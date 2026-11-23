module NativeSideStorySpec (nativeSideStorySpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist (..))
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation
import Arkham.Message (Message (..))
import Arkham.Question
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Key qualified as K
import Data.Map.Strict qualified as Map
import Data.UUID qualified as UUID
import Data.ByteString.Lazy.Char8 qualified as BLC
import Data.Text qualified as T
import Data.Either (isLeft,isRight)
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label) else putStrLn ("FAIL " <> label) >> exitFailure
right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure
typed :: FromJSON a => Value -> IO a
typed = either (\e -> P.putStrLn e >> exitFailure) pure . Aeson.parseEither parseJSON
valueAs :: FromJSON a => Value -> a
valueAs value=case fromJSON value of Aeson.Success x -> x; Aeson.Error e -> error $ fromString e
field :: Text -> Value -> Value
field name (Object o) = fromMaybe Null $ KM.lookup (K.fromText name) o
field _ _ = Null
setField :: Text -> Value -> Value -> Value
setField name value (Object o)=Object $ KM.insert (K.fromText name) value o
setField _ _ _=error "Expected object in declared QA fixture"
array :: Value -> [Value]
array (Array xs)=toList xs
array _=[]
emit :: Text -> Value -> IO ()
emit prefix value = BLC.putStrLn $ BLC.pack (T.unpack prefix) <> encode value

nativeSideStorySpecs :: PlayerId -> PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeSideStorySpecs p1 p2 choose = do
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e -> P.putStrLn e >> exitFailure) pure
  jimDeck <- eitherDecodeFileStrict @ArkhamDBDecklist "arkham-core/spec/fixtures/jim-02004.json" >>= either (\e -> P.putStrLn e >> exitFailure) pure
  let p3=PlayerId $ fromJustNote "p3" $ UUID.fromString "00000000-0000-4000-8000-000000000003"
      p4=PlayerId $ fromJustNote "p4" $ UUID.fromString "00000000-0000-4000-8000-000000000004"
      campaignOf s=field "This" $ toJSON $ gameMode $ snapshotGame s
      pick s=fromJustNote "actual pending player" $ headMay $ Map.keys $ gameQuestion $ snapshotGame s
      continuation s=field "contents" $ field "step" $ campaignOf s
      setCampaign s c=s {snapshotGame=(snapshotGame s) {gameMode=valueAs $ object ["This" .= c]}}
      actors s=toJSON $ (gameEntities $ snapshotGame s).investigators
      xp s=Map.map (valueAs @Int . field "xp") (valueAs @(Map Text Value) $ actors s)
      balances f s=
        let rewritten=Map.mapWithKey (\iid -> setField "xp" (toJSON $ f iid)) (valueAs @(Map Text Value) $ actors s)
            entities=valueAs $ setField "investigators" (toJSON rewritten) $ toJSON $ gameEntities $ snapshotGame s
         in s {snapshotGame=(snapshotGame s) {gameEntities=entities}}
      nextAnswer s=do
        offered <- right $ offeredContinuation $ gameMode $ snapshotGame s
        typed $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= field "nextStep" (toJSON offered)]
      respond choices players s=case unwrapQuestion $ fromJustNote "actual question" $ Map.lookup (pick s) $ gameQuestion $ snapshotGame s of
        ContinueCampaign -> nextAnswer s
        _ -> choose (pick s) (fromJustNote "preset per actual seat" $ lookup (pick s) $ zip players choices) s
      untilContinuation choices players budget s = case offeredContinuation $ gameMode $ snapshotGame s of
        Right offered | field "canChooseSideStory" (toJSON offered) == Bool True && field "That" (toJSON $ gameMode $ snapshotGame s) == Null -> pure s
        _ | budget <= (0 :: Int) -> error "Natural initial campaign continuation not reached"
          | otherwise -> do
            answer <- respond choices players s
            (next,_) <- applyAnswer s (pick s) (snapshotRevision s) answer >>= right
            untilContinuation choices players (budget-1) next
      untilReady choices players budget s
        | not (gameInSetup $ snapshotGame s), field "That" (toJSON $ gameMode $ snapshotGame s) /= Null = pure s
        | budget <= (0 :: Int) = error "Side-story actual Setup budget exhausted"
        | otherwise = do
            answer <- respond choices players s
            (next,_) <- applyAnswer s (pick s) (snapshotRevision s) answer >>= right
            untilReady choices players (budget-1) next
      ask sid=BeginSideStoryAnswer $ SideStoryRequest sid
      actual sid s=do
        (chosen,_) <- applyAnswer s (pick s) (snapshotRevision s) (ask sid) >>= right
        next <- nextAnswer chosen
        (entered,_) <- applyAnswer chosen (pick chosen) (snapshotRevision chosen) next >>= right
        pure (chosen,entered)
      runControlled s messages=do
        env <- makeEnv (snapshotGame s) $ ClearUI : messages
        advance env
        fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s) env
      noSides= setField "chooseSideStory" (Bool False) . setField "canChooseSideStory" (Bool False)
      step tag contents=object ["tag" .= (tag :: Text),"contents" .= contents]
  for_ [1,2,4] $ \seats -> do
    let players=take seats [p1,p2,p3,p4]
    (created,_) <- createSession (CreateRequest (Just "01") Nothing 171019 players Standard False False) >>= right
    natural <- untilContinuation decks players 50 created
    let manager=pick natural
        funded=balances (const 5) natural
        before=exportSnapshot funded
        original=continuation funded
        returning=step "ContinueCampaignStep" $ noSides original
    assert ("side-story natural continuation before first scenario seats " <> tshow seats) $ isRight (campaignSideStoryContinuation (snapshotGame natural) manager) && all (== 0) (xp natural)
    poor <- applyAnswer natural manager (snapshotRevision natural) (ask "81001")
    assert ("side-story cannot silently truncate unpaid XP seats " <> tshow seats) $ isLeft poor
    (chosen,entered) <- actual "81001" funded
    let selected=continuation chosen
    assert ("side-story selection does not charge or start scenario seats " <> tshow seats) $ xp chosen == xp funded && field "That" (toJSON $ gameMode $ snapshotGame chosen) == Null
    assert ("side-story two explicit continuations preserve exact original return lead flags seats " <> tshow seats) $
      field "nextStep" selected == step "StandaloneScenarioStep" (toJSON [toJSON ("81001" :: ScenarioId),returning]) && field "canChooseSideStory" selected == Bool False && field "chooseSideStory" selected == Bool False && field "lead" selected == field "lead" original
    assert ("side-story actual runner charges each investigator exactly once seats " <> tshow seats) $ xp entered == fmap (subtract 1) (xp funded)
    assert ("side-story input remains byte immutable seats " <> tshow seats) $ exportSnapshot funded == before
    imported <- importSnapshot before >>= right
    (chosenAgain,enteredAgain) <- actual "81001" imported
    assert ("side-story two-step replay whole Game queue RNG exact seats " <> tshow seats) $ exportSnapshot chosenAgain == exportSnapshot chosen && exportSnapshot enteredAgain == exportSnapshot entered
    nested <- applyAnswer chosen manager (snapshotRevision chosen) (ask "82001")
    stale <- applyAnswer funded manager (snapshotRevision funded+1) (ask "81001")
    wrongRecipient <- applyAnswer funded (if manager == p2 then p1 else p2) (snapshotRevision funded) (ask "81001")
    assert ("side-story nesting stale and nonrecipient rejected seats " <> tshow seats) $ all isLeft [nested,stale,wrongRecipient]
    let depleted=balances (const 0) chosen
    next <- nextAnswer depleted
    secondPoor <- applyAnswer depleted manager (snapshotRevision depleted) next
    assert ("side-story second Continue rechecks changed balances seats " <> tshow seats) $ isLeft secondPoor
    assert ("side-story direct arbitrary wrapper remains rejected seats " <> tshow seats) $ isLeft $ validateAnswer (snapshotGame funded) manager next
    let pending=field "pendingEntry" $ sideStoryView (snapshotGame chosen) manager
    assert ("side-story chosen pending entry retains second-confirmation cost seats " <> tshow seats) $ field "available" pending == Bool True && field "cost" pending == toJSON (1 :: Int)
    when (seats == 2) $ do
      let onePoor=balances (\iid -> if iid == headEx (Map.keys $ xp funded) then 5 else 0) funded
      rejected <- applyAnswer onePoor manager (snapshotRevision onePoor) (ask "81001")
      assert "side-story wealthy manager cannot pay for insolvent teammate" $ isLeft rejected
    when (seats == 1) $ do
      let entries=array $ field "entries" $ sideStoryView (snapshotGame funded) manager
          expectedCosts=[("81001",1),("82001",3),("83001",2),("83016",2),("85001",2),("86001",3),("87001",2),("70001",2),("84001",3),("88001",3),("71001",2),("72001",3),("90004",3),("90011",3),("90020",3),("90032",3),("90041",3),("90054",3),("90065",3),("90094",2),(":against-the-wendigo:001",2),(":consternation-on-the-constellation:001",2),(":the-symphony-of-erich-zann:001",2)] :: [(ScenarioId,Int)]
      assert "side-story registry exact23 unique IDs and original/homebrew costs" $ [(field "scenarioId" row,valueAs @Int $ field "baseCost" row) | row <- entries] == [(toJSON sid,cost) | (sid,cost) <- expectedCosts] && length (nub $ map (field "scenarioId") entries) == 23
      assert "side-story registry all23 have real registered engine scenarios" $ all (\row -> field "reason" row /= String "Side story has no registered scenario implementation") entries
      for_ [False,True] $ \options -> do
        let end=object ["tag" .= ("EpilogueStep" :: Text)]
            opts=object ["scenarioOptionsStandalone" .= False,"scenarioOptionsPerformTarotReading" .= False,"scenarioOptionsLeadInvestigator" .= Null]
            completed=step (if options then "StandaloneScenarioStepWithOptions" else "StandaloneScenarioStep") $ toJSON ([toJSON ("83016" :: ScenarioId),end] <> [opts | options])
            already=setCampaign funded $ setField "completedSteps" (toJSON [completed]) $ campaignOf funded
        assert ("side-story completed both constructors and backwards two-part order rejected " <> tshow options) $ isLeft (sideStoryQuote (snapshotGame already) manager True "83016") && isLeft (sideStoryQuote (snapshotGame already) manager True "83001")
      assert "side-story challenge investigator absence rejected" $ isLeft $ sideStoryQuote (snapshotGame funded) manager True "90054"
      assert "side-story unknown id rejected" $ isLeft $ sideStoryQuote (snapshotGame funded) manager True "01104"
      let iid=headEx $ Map.keys $ xp funded
          setXp=object ["operation" .= ("setExperience" :: Text),"investigatorId" .= iid,"amount" .= (7 :: Int)]
          token=object ["operation" .= ("adjustTokens" :: Text),"target" .= object ["tag" .= ("InvestigatorTarget" :: Text),"contents" .= iid],"token" .= ("Resource" :: Text),"amount" .= (3 :: Int)]
      disabledDebug <- applyDebug funded manager (snapshotRevision funded) False setXp
      staleDebug <- applyDebug funded manager (snapshotRevision funded+1) True setXp
      forgedDebug <- applyDebug funded manager (snapshotRevision funded) True $ setField "messages" (toJSON [ClearUI]) setXp
      assert "debug disabled stale and arbitrary-message fields reject unchanged basis" $ all isLeft [disabledDebug,staleDebug,forgedDebug] && exportSnapshot funded == before
      (debugXp,_) <- applyDebug funded manager (snapshotRevision funded) True setXp >>= right
      assert "debug actual SetXP bypasses no seat answer and preserves parked question and queue" $ Map.lookup iid (xp debugXp) == Just 7 && toJSON (gameQuestion $ snapshotGame debugXp) == toJSON (gameQuestion $ snapshotGame funded) && toJSON (snapshotQueue debugXp) == toJSON (snapshotQueue funded)
      (debugTokens,_) <- applyDebug debugXp manager (snapshotRevision debugXp) True token >>= right
      let tokenCount key actor = fromMaybe 0 $ lookup key $ valueAs @[(Text,Int)] $ field "tokens" actor
          resource s = maybe 0 (tokenCount "Resource") $ Map.lookup iid (valueAs @(Map Text Value) $ actors s)
      assert "debug actual finite resource edit changes selected real investigator" $ resource debugTokens == resource debugXp+3 && Map.lookup iid (xp debugTokens) == Just 7
      let damage=setField "token" (String "Damage") $ setField "amount" (toJSON (99 :: Int)) token
      (debugDamage,_) <- applyDebug debugTokens manager (snapshotRevision debugTokens) True damage >>= right
      assert "debug direct lethal-sized token edit does not dispatch defeat or resurrect questions" $ toJSON (gameQuestion $ snapshotGame debugDamage) == toJSON (gameQuestion $ snapshotGame debugTokens) && toJSON (snapshotQueue debugDamage) == toJSON (snapshotQueue debugTokens) && gameSeed (snapshotGame debugDamage) == gameSeed (snapshotGame debugTokens)
      let editedActor=fromJustNote "edited actor" $ Map.lookup iid $ valueAs @(Map Text Value) $ actors debugDamage
      assert "debug direct damage policy explicit no automatic defeated flag" $ field "defeated" editedActor == Bool False && tokenCount "Damage" editedActor == 99
      negative <- applyDebug debugTokens manager (snapshotRevision debugTokens) True $ setField "amount" (toJSON (-99 :: Int)) token
      assert "debug direct token result cannot underflow below zero" $ isLeft negative
      debugRestored <- importSnapshot before >>= right
      (debugXpReplay,_) <- applyDebug debugRestored manager (snapshotRevision debugRestored) True setXp >>= right
      (debugTokensReplay,_) <- applyDebug debugXpReplay manager (snapshotRevision debugXpReplay) True token >>= right
      assert "debug numeric whole-save restore replay exact" $ exportSnapshot debugTokensReplay == exportSnapshot debugTokens
      emit "DEBUG_NUMERIC_FIXTURE " $ object ["before" .= funded,"setExperienceCommand" .= setXp,"afterExperience" .= debugXp,"tokenCommand" .= token,"afterTokens" .= debugTokens]
      -- A single representative scenario uses the explicit debug shortcut;
      -- this is not a natural victory or a per-scenario completion matrix.
      gatheringAnswer <- nextAnswer natural
      (gatheringStarted,_) <- applyAnswer natural manager (snapshotRevision natural) gatheringAnswer >>= right
      let untilStable budget s
            | not (null $ array $ field "resolutionChoices" $ debugView (snapshotGame s) (pick s)) = pure s
            | budget <= (0 :: Int) = error "Representative Gathering stable window not reached"
            | otherwise = do
                response <- respond decks players s
                (following,_) <- applyAnswer s (pick s) (snapshotRevision s) response >>= right
                untilStable (budget-1) following
      stable <- untilStable 120 gatheringStarted
      let stableManager=pick stable
          resolution=object ["operation" .= ("resolveGathering" :: Text),"resolution" .= (2 :: Int)]
          inAction=stable {snapshotGame=(snapshotGame stable) {gameInAction=True}}
      unsafe <- applyDebug inAction stableManager (snapshotRevision inAction) True resolution
      assert "debug representative finish rejects in-action context" $ isLeft unsafe
      (resolved,_) <- applyDebug stable stableManager (snapshotRevision stable) True resolution >>= right
      resumed <- untilContinuation decks players 60 resolved
      stableImport <- importSnapshot (exportSnapshot stable) >>= right
      (resolveReplay,_) <- applyDebug stableImport stableManager (snapshotRevision stableImport) True resolution >>= right
      resumedReplay <- untilContinuation decks players 60 resolveReplay
      assert "debug representative real Gathering resolution and return replay exact" $ exportSnapshot resumed == exportSnapshot resumedReplay
      assert "debug representative original resolution actually records house standing" $ object ["tag" .= ("NightOfTheZealotKey" :: Text),"contents" .= ("YourHouseIsStillStanding" :: Text)] `elem` array (field "recorded" $ field "log" $ campaignOf resumed)
      emit "DEBUG_RESOLUTION_FIXTURE " $ object ["debugFinishNotNaturalVictory" .= True,"before" .= stable,"command" .= resolution,"resolved" .= resolved,"returned" .= resumed]
      let scenario=field "That" $ toJSON $ gameMode $ snapshotGame entered
          standalone=funded {snapshotGame=(snapshotGame funded) {gameMode=valueAs $ object ["That" .= scenario]}}
          local=funded {snapshotGame=(snapshotGame funded) {gameMode=valueAs $ object ["This" .= campaignOf funded,"That" .= setField "campaignStep" (step "ContinueCampaignStep" original) scenario]}}
      assert "side-story standalone and scenario-local These continuations cannot insert campaign stories" $ isLeft (offeredSideStory (snapshotGame standalone) manager $ SideStoryRequest "81001") && isLeft (offeredSideStory (snapshotGame local) manager $ SideStoryRequest "81001")
      let disabled=setCampaign funded $ setField "step" (step "ContinueCampaignStep" $ noSides original) $ campaignOf funded
          must=setCampaign funded $ setField "step" (step "ContinueCampaignStep" $ setField "chooseSideStory" (Bool True) original) $ campaignOf funded
      assert "side-story explicit disabled continuation rejected" $ isLeft $ offeredSideStory (snapshotGame disabled) manager $ SideStoryRequest "81001"
      skip <- nextAnswer must
      assert "side-story required choice cannot bypass via original next step" $ isLeft $ validateAnswer (snapshotGame must) manager skip
      (scarletCreated,_) <- createSession (CreateRequest (Just "09") Nothing 171019 [p1] Standard False False) >>= right
      (circusCreated,_) <- createSession (CreateRequest (Just ":circus-ex-mortis") Nothing 171019 [p1] Standard False False) >>= right
      let scarlet elapsed = setCampaign funded $ setField "step" (step "ContinueCampaignStep" original) $ setField "log" (object ["recorded" .= ([] :: [Value]),"crossedOut" .= ([] :: [Value]),"recordedCounts" .= [[object ["tag" .= ("TheScarletKeysKey" :: Text),"contents" .= ("Time" :: Text)],toJSON (elapsed :: Int)]],"recordedSets" .= ([] :: [Value]),"orderedKeys" .= ([] :: [Value]),"options" .= ([] :: [Value]),"partners" .= object []]) $ campaignOf scarletCreated
          circus = setCampaign funded $ setField "step" (step "ContinueCampaignStep" original) $ setField "completedSteps" (toJSON [step "ScenarioStep" $ toJSON (":circus-ex-mortis:040" :: ScenarioId)]) $ campaignOf circusCreated
      timeQuote <- right $ sideStoryQuote (snapshotGame $ scarlet 34) manager True "81001"
      assert "side-story Scarlet time quote uses base1 no XP charge and admits exact35" $ quoteUsesTime timeQuote && quoteCost timeQuote == 1 && quoteRemainingTime timeQuote == Just 1
      assert "side-story Scarlet old UI admission rejects excess time" $ isLeft $ sideStoryQuote (snapshotGame $ scarlet 34) manager True "83001"
      assert "side-story Scarlet zero XP does not restrict time purchase" $ isRight $ sideStoryQuote (snapshotGame $ balances (const 0) $ scarlet 34) manager True "81001"
      freeQuote <- right $ sideStoryQuote (snapshotGame $ balances (const 0) circus) manager True "81001"
      assert "side-story actual Circus campaign instance overlay yields free quote" $ not (quoteUsesTime freeQuote) && quoteCost freeQuote == 0 && quoteOverlay freeQuote == Just "Circus Ex Mortis"
      (freeSelected,freeEntered) <- actual "81001" $ balances (const 0) circus
      assert "side-story actual Circus overlay zero charged once by runner" $ xp freeSelected == xp freeEntered && all (== 0) (xp freeEntered)
      for_ [":against-the-wendigo:001",":consternation-on-the-constellation:001",":the-symphony-of-erich-zann:001"] $ \sid -> do
        let homeFunded=balances (const 2) natural
        (homeSelected,homeEntered) <- actual sid homeFunded
        homeReady <- untilReady decks players 200 homeEntered
        assert ("side-story actual homebrew shared cost2 and own scenario " <> tshow sid) $ all (== 2) (xp homeSelected) && all (== 0) (xp homeReady) && field "id" (field "That" $ toJSON $ gameMode $ snapshotGame homeReady) == toJSON sid
        when (sid == ":consternation-on-the-constellation:001") $ do
          let game=toJSON $ snapshotGame homeReady
              scenario=field "That" $ field "gameMode" game
              entities=field "gameEntities" game
              onBoard=filter ((== String "c:consternation-on-the-constellation:029") . field "cardCode") $ Map.elems (valueAs @(Map Text Value) $ field "enemies" entities)
              deck=filter ((== String "c:consternation-on-the-constellation:029") . field "cardCode") $ array $ field "encounterDeck" scenario
          assert "side-story Consternation one Enforcer spawned and one remains physical deck" $ length onBoard == 1 && length deck == 1 && field "cardId" (headEx onBoard) /= field "id" (headEx deck)
          -- Only the changed single-card extraction is an integration regression.
          -- Original spawn/engage placement is owned by the unchanged rule engine;
          -- do not require it to remain AtLocation after Setup has fully advanced.
        homeRestore <- importSnapshot (exportSnapshot homeFunded) >>= right
        (_,homeReplayEntered) <- actual sid homeRestore
        homeReplay <- untilReady decks players 200 homeReplayEntered
        assert ("side-story actual homebrew entire Setup replay exact " <> tshow sid) $ exportSnapshot homeReplay == exportSnapshot homeReady
        emit "SIDESTORY_HOMEBREW_FIXTURE " $ object ["scenarioId" .= sid,"controlledExperienceOnly" .= True,"before" .= homeFunded,"selected" .= homeSelected,"ready" .= homeReady]
      ready <- untilReady decks players 200 entered
      -- Explicit controlled finish, NOT natural victory or full scenario play.
      returned <- runControlled ready [EndOfScenario Nothing]
      assert "side-story real EndOfScenario returns exact original next flags and lead" $ continuation returned == noSides original
      emit "SIDESTORY_RETURN_FIXTURE " $ object ["controlledEndOfScenario" .= True,"naturalVictory" .= False,"before" .= funded,"selected" .= chosen,"entered" .= entered,"ready" .= ready,"returned" .= returned]
    emit "SIDESTORY_SEATS_FIXTURE " $ object ["seats" .= seats,"controlledExperienceOnly" .= True,"natural" .= natural,"funded" .= funded,"selected" .= chosen,"entered" .= entered]
  let challengeDecks=jimDeck:decks
      players=[p1,p2]
      isJim iid=iid == "c02004"
  (jimCreated,_) <- createSession (CreateRequest (Just "01") Nothing 171019 players Standard False False) >>= right
  jimNatural <- untilContinuation challengeDecks players 50 jimCreated
  let challenge=balances (\iid -> if isJim iid then 3 else 1) jimNatural
      manager=pick challenge
  assert "side-story real ordinary Jim qualifies for parallel-content Encore" $ isRight $ sideStoryQuote (snapshotGame $ balances (const 2) jimNatural) manager True "90094"
  for_ [("Jim",\iid -> if isJim iid then 2 else 1),("teammate",\iid -> if isJim iid then 3 else 0)] $ \(label,balance) -> do
    rejected <- applyAnswer (balances balance jimNatural) manager (snapshotRevision jimNatural) (ask "90054")
    assert ("side-story challenge rejects insolvent actual " <> label) $ isLeft rejected
  (jimSelected,jimEntered) <- actual "90054" challenge
  assert "side-story actual Jim challenge charges required3 and teammate1 exactly once" $ xp jimSelected == xp challenge && all (== 0) (xp jimEntered) && Map.size (xp jimEntered) == 2
  restoredJim <- importSnapshot (exportSnapshot challenge) >>= right
  (reselectedJim,reenteredJim) <- actual "90054" restoredJim
  assert "side-story actual challenge entire two-step replay exact" $ exportSnapshot reselectedJim == exportSnapshot jimSelected && exportSnapshot reenteredJim == exportSnapshot jimEntered
  emit "SIDESTORY_CHALLENGE_FIXTURE " $ object ["controlledExperienceOnly" .= True,"natural" .= jimNatural,"before" .= challenge,"selected" .= jimSelected,"entered" .= jimEntered,"scope" .= ("Real ordinary Jim+Tommy entry charging, not full Spirit setup or victory" :: Text)]
  let request sid fields=object ["tag" .= ("BeginSideStoryAnswer" :: Text),"contents" .= object (["scenarioId" .= (sid :: Value)] <> fields)]
  for_ [("cost",["cost" .= (0 :: Int)]),("return",["returnStep" .= object ["tag" .= ("EpilogueStep" :: Text)]]),("flags",["canChooseSideStory" .= True])] $ \(label,extra) ->
    assert ("side-story strict request rejects forged " <> label) $ isLeft $ Aeson.parseEither (parseJSON @Answer) $ request (toJSON ("81001" :: ScenarioId)) extra
  assert "side-story strict canonical scenario identity roundtrip accepted" $ isRight $ Aeson.parseEither (parseJSON @Answer) $ request (toJSON ("81001" :: ScenarioId)) []
