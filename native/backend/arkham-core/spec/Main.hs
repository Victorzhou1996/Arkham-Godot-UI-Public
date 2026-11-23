module Main where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..), GameMode)
import Arkham.Id
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation
import Arkham.Native.Env
import Arkham.Native.Events (eventJSONForPlayer)
import Arkham.Helpers.Playable
import Arkham.Helpers.Modifiers
import Arkham.Card.CustomCard (CustomCard (..), missingCustomCardDef, customCardRegistry)
import Arkham.Card.CardType (CardType (AssetType))
import Arkham.Question
import Arkham.Message (Message (..), pattern PlaceTokens, pattern MoveTokens, uiToRun)
import Control.Exception qualified as E
import Data.Either (isLeft, isRight)
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.Data (gmapQ, typeOf)
import Data.UUID qualified as UUID
import System.Exit (exitFailure)
import System.Environment qualified as CLI
import qualified Prelude as P
import Data.ByteString.Lazy.Char8 qualified as BLC
import SettingsDefinitionSpec (settingsDefinitionSpecs)
import SettingsEngineSpec (settingsEngineSpecs,omittedStandaloneSettingsSpecs,heartPart2SettingsSpecs)
import NativeStandaloneSetupSpec (nativeStandaloneSetupSpecs)
import NativeSideStorySpec (nativeSideStorySpecs)
import NativeDebugActionsSpec (nativeDebugActionsSpecs)
import NativeWorkbenchSpec (nativeWorkbenchSpecs)
import NativeAutomaticTriggersSpec (nativeAutomaticTriggersSpecs, remoteResumeFixture)
import NativeUndoMetadataSpec (nativeUndoMetadataSpecs)
import NativeHistoryFoundationSpec (nativeHistoryFoundationSpecs)
import NativeHistoryBootstrapSpec (nativeHistoryBootstrapSpecs)
import NativeScenarioDebugSpec (nativeScenarioDebugSpecs)
import NativeAuthorSpec (nativeAuthorSpecs)
import DeckBuildSelectionsSpec (deckBuildSelectionSpecs)
import DeckBuildUsesSpec (deckBuildUsesSpecs)
import DeckBuildConstraintsSpec (deckBuildConstraintsSpecs)
import DeckBuildTagsSpec (deckBuildTagsSpecs)
import DeckBuildCustomizationsSpec (deckBuildCustomizationsSpecs)
import DeckBuildInvestigatorsSpec (deckBuildInvestigatorsSpecs)
import DeckBuildTaboosSpec (deckBuildTaboosSpecs)
import DeckBuildOwnedSpec (deckBuildOwnedSpecs)
import DeckBuildExtraSpec (deckBuildExtraSpecs)
import DeckBuildAttachmentsSpec (deckBuildAttachmentsSpecs)
import DeckBuildUpgradeQuoteSpec (deckBuildUpgradeQuoteSpecs)
import NativeLabyrinthMiniSpec (labyrinthMiniSpecs)
import NativeEpicSpec (epicSpecs)
import NativeNetworkSpec (networkSpecs)
import NativeDeckMetadataSpec (deckMetadataSpecs)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn $ "PASS " <> label else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure

field :: Text -> Value -> Value
field key (Object fields) = fromMaybe Null $ KM.lookup (Key.fromText key) fields
field _ _ = Null

setField :: Text -> Value -> Value -> Value
setField key value (Object fields) = Object $ KM.insert (Key.fromText key) value fields
setField _ _ _ = error "Test fixture expected an object"

typed :: FromJSON a => Value -> IO a
typed value = either (\e -> P.putStrLn e >> exitFailure) pure $ Aeson.parseEither parseJSON value

fixtureValue :: FromJSON a => Value -> a
fixtureValue value = case fromJSON value of Aeson.Success result -> result; Aeson.Error reason -> error $ "Invalid typed contract fixture: " <> fromString reason

nextAnswer :: Snapshot -> IO Answer
nextAnswer snapshot = do
  let campaign = field "This" $ toJSON $ gameMode $ snapshotGame snapshot
      next = field "nextStep" $ field "contents" $ field "step" campaign
  typed $ object ["tag" .= ("CampaignStepAnswer" :: Text), "contents" .= next]

chooseRealAnswer :: PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer
chooseRealAnswer player deck snapshot = do
  let question = fromJustNote "recipient question" $ Map.lookup player $ gameQuestion $ snapshotGame snapshot
  case unwrapQuestion question of
    ChooseDeck -> pure $ DeckListAnswer deck player
    ChooseUpgradeDeck -> pure $ DeckListAnswer deck player
    ChooseJoinDeck {} -> pure $ DeckListAnswer deck player
    PickScenarioSettings -> pure $ StandaloneSettingsAnswer []
    PickCampaignSettings -> pure $ CampaignSettingsAnswer $ CampaignSettings [] mempty mempty []
    ChooseAmounts _ (TotalAmountTarget total) choices _ -> do
      let ranges = [(key,lo,hi) | AmountChoice key _ lo hi <- choices]
          combinations = product [toInteger hi - toInteger lo + 1 | (_,lo,hi) <- ranges]
      when (any (\(_,lo,hi) -> lo > hi) ranges || combinations > 10000
        || Map.size (Map.fromList [(key,()) | (key,_,_) <- ranges]) /= length ranges) $
        error "Amount test driver requires distinct IDs and a bounded valid range"
      let candidates = sequence [[(key,n) | n <- [lo..hi]] | (key,lo,hi) <- ranges]
          selected = listToMaybe [rows | rows <- candidates, sum (map (toInteger . snd) rows) == toInteger total]
      case selected of
        Nothing -> error "No legal total allocation in actual question"
        Just rows -> pure $ AmountsAnswer $ AmountsResponse (Map.fromList rows)
          (Just $ gameScenarioSteps $ snapshotGame snapshot) (Just player)
    other -> case indexedChoices other of
      Just choices -> case listToMaybe [i | (i,choice) <- zip [0..] choices, case choice of InvalidLabel {} -> False; Info {} -> False; _ -> True] of
        Just i -> pure $ Answer $ QuestionResponse i (Just player) Nothing
        Nothing -> error "No actionable choice in real initial flow"
      Nothing -> error $ "Add a real typed test answer for question: " <> show other

traumaTotal :: Snapshot -> IO Int
traumaTotal snapshot = case Map.elems (gameEntities (snapshotGame snapshot)).investigators of
  [investigator] -> case Aeson.parseEither (withObject "actual investigator" \o ->
      (+) <$> o .: "physicalTrauma" <*> o .: "mentalTrauma") (toJSON investigator) of
    Left reason -> P.putStrLn reason >> exitFailure
    Right total -> pure total
  _ -> error "Actual single-seat fixture must have exactly one investigator"

eventProjectionContracts :: PlayerId -> PlayerId -> IO ()
eventProjectionContracts player other = do
  let public = object ["tag" .= ("Everyone" :: Text)]
      private who = object ["tag" .= ("OnlyPlayer" :: Text),"contents" .= who]
      entry audience text children = object
        ["seq" .= (0 :: Int),"step" .= Null,"kind" .= ("Mechanic" :: Text)
        ,"body" .= [object ["tag" .= ("LogText" :: Text),"contents" .= (text :: Text)]]
        ,"source" .= Null,"children" .= (children :: [Value]),"audience" .= audience
        ,"context" .= object ["round" .= Null,"phase" .= Null,"turn" .= Null]
        ,"tone" .= Null,"group" .= Null,"tag" .= Null]
      logEvent value = object ["type" .= ("log" :: Text),"entry" .= value]
      mixed = logEvent $ entry public "public parent" [entry (private player) "own secret" [],entry (private other) "other secret" [],entry public "public child" []]
      project who value = eventJSONForPlayer who value
      delivered value = case value of Right (Just result) -> result; _ -> Null
      kids value = field "children" $ field "entry" $ delivered value
      textEvent = object ["type" .= ("text" :: Text),"text" .= ("public text" :: Text)]
  assert "events actual Aeson Everyone is visible" $ isJust $ join $ either (const Nothing) Just $ project player $ logEvent $ entry public "public" []
  assert "events actual OnlyPlayer own entry visible" $ isRight (project player $ logEvent $ entry (private player) "own" []) && delivered (project player $ logEvent $ entry (private player) "own" []) /= Null
  assert "events actual OnlyPlayer other entry absent" $ project player (logEvent $ entry (private other) "other secret" []) == Right Nothing
  assert "events recursive filtering removes only other seat children" $ length (fromMaybe [] $ Aeson.parseMaybe parseJSON $ kids $ project player mixed :: [Value]) == 2
  assert "events nested private body absent from complete recipient encoding" $ not $ "other secret" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode $ delivered $ project player mixed)
  assert "events other seat keeps its private child not ours" $ not ("own secret" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode $ delivered $ project other mixed)) && "other secret" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode $ delivered $ project other mixed)
  assert "events hidden parent never promotes public descendants" $ project player (logEvent $ entry (private other) "private parent" [entry public "public child" []]) == Right Nothing
  assert "events projection preserves input immutably" $ field "children" (field "entry" mixed) /= kids (project player mixed)
  assert "events unknown audience is explicit failure not public" $ isLeft $ project player $ logEvent $ entry (object ["tag" .= ("FutureSecretAudience" :: Text)]) "secret" []
  assert "events missing audience is explicit failure not public" $ isLeft $ project player $ logEvent $ setField "audience" Null $ entry public "secret" []
  assert "events malformed seat is explicit failure" $ isLeft $ project player $ setField "seat" Null textEvent
  assert "events conflicting outer seat cannot override public audience" $ project player (setField "seat" (toJSON other) mixed) == Right Nothing
  assert "events own outer seat cannot override private inner audience" $ project player (setField "seat" (toJSON player) $ logEvent $ entry (private other) "secret" []) == Right Nothing
  assert "events public text envelope unchanged" $ project player textEvent == Right (Just textEvent)
  let draw = object ["type" .= ("draw" :: Text),"text" .= ("draw" :: Text),"cards" .= ([] :: [Value]),"seat" .= player]
  assert "events draw own seat passes exact envelope" $ project player draw == Right (Just draw)
  assert "events draw other seat absent" $ project other draw == Right Nothing
  assert "events draw missing recipient rejected" $ isLeft $ project player $ object ["type" .= ("draw" :: Text)]
  assert "events future envelope fails explicitly" $ isLeft $ project player $ object ["type" .= ("FuturePrivateEvent" :: Text)]
  assert "events nonobject envelope rejected" $ isLeft $ project player Null
  BLC.putStrLn $ "EVENT_FIXTURE " <> encode (object ["input" .= mixed,"player" .= player,"other" .= other,"forPlayer" .= delivered (project player mixed),"forOther" .= delivered (project other mixed)])

main :: IO ()
main = do
  args <- CLI.getArgs
  case args of
    [] -> fullSpecs
    ["--integration-remote-fixtures"] -> do
      let player=PlayerId $ fromJustNote "remote fixture uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason -> P.putStrLn reason >> exitFailure) pure
      travel <- remoteTravelFixture player $ fromJustNote "actual preset" $ listToMaybe decks
      BLC.putStrLn $ "REMOTE_SEAT_FIXTURE " <> encode (object
        ["name" .= ("travel"::Text),"player" .= player,"snapshot" .= travel
        ,"scope" .= ("Fresh normal campaign09 through original prologue; explicit test-only embark queue executed by original handler. Travel not executed; not natural preceding scenario completion."::Text)])
      resume <- remoteResumeFixture player chooseRealAnswer
      BLC.putStrLn $ "REMOTE_SEAT_FIXTURE " <> encode (object
        ["name" .= ("resume"::Text),"player" .= player,"snapshot" .= resume
        ,"scope" .= ("Fresh normal01104 investigator; explicit controlled optional window and original saved Stop preference, then imported eligible pause. Final Resume not executed; not natural ability-window coverage."::Text)])
      putStrLn "REMOTE_FIXTURES_COMPLETED"
    ["--integration-deck-metadata"] -> do
      deckMetadataSpecs
      putStrLn "DECK_METADATA_INTEGRATION_COMPLETED"
    ["--integration-undo-metadata"] -> do
      let player=PlayerId $ fromJustNote "undo metadata integration uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason -> P.putStrLn reason >> exitFailure) pure
      nativeUndoMetadataSpecs chooseRealAnswer player $ headEx decks
    ["--integration-network"] -> do
      networkSpecs
      putStrLn "NETWORK_INTEGRATION_COMPLETED"
    ["--integration-epic"] -> do
      epicSpecs
      putStrLn "EPIC_INTEGRATION_COMPLETED"
    ["--integration-labyrinth-mini"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      labyrinthMiniSpecs player chooseRealAnswer
      putStrLn "LABYRINTH_MINI_INTEGRATION_COMPLETED"
    ["--integration-heart-part2"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      heartPart2SettingsSpecs player chooseRealAnswer
      putStrLn "HEART_PART2_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-taboos"] -> do
      deckBuildTaboosSpecs
      putStrLn "DECKBUILD_TABOOS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-owned"] -> do
      deckBuildOwnedSpecs
      putStrLn "DECKBUILD_OWNED_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-extra"] -> do
      deckBuildExtraSpecs
      putStrLn "DECKBUILD_EXTRA_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-attachments"] -> do
      deckBuildAttachmentsSpecs
      putStrLn "DECKBUILD_ATTACHMENTS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-upgrade-quote"] -> deckBuildUpgradeQuoteSpecs
    ["--integration-deckbuild-investigators"] -> do
      deckBuildInvestigatorsSpecs
      putStrLn "DECKBUILD_INVESTIGATORS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-customizations"] -> do
      deckMetadataSpecs
      deckBuildSelectionSpecs
      deckBuildTagsSpecs
      deckBuildCustomizationsSpecs
      putStrLn "DECKBUILD_CUSTOMIZATIONS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-tags"] -> do
      deckBuildTagsSpecs
      putStrLn "DECKBUILD_TAGS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-constraints"] -> do
      deckBuildUsesSpecs
      deckBuildConstraintsSpecs
      putStrLn "DECKBUILD_CONSTRAINTS_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-uses"] -> do
      deckBuildUsesSpecs
      putStrLn "DECKBUILD_USES_INTEGRATION_COMPLETED"
    ["--integration-deckbuild-selections"] -> do
      deckBuildSelectionSpecs
      putStrLn "DECKBUILD_SELECTIONS_INTEGRATION_COMPLETED"
    ["--integration-set-aside-regions"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      setAsideRegionContracts player
      putStrLn "SET_ASIDE_REGIONS_INTEGRATION_COMPLETED"
    ["--integration-omitted-standalone-settings"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      omittedStandaloneSettingsSpecs player
      putStrLn "OMITTED_STANDALONE_SETTINGS_INTEGRATION_COMPLETED"
    ["--integration-author"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeAuthorSpecs player chooseRealAnswer
    ["--integration-scenario-debug"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeScenarioDebugSpecs player chooseRealAnswer
    ["--integration-history-bootstrap"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeHistoryBootstrapSpecs player chooseRealAnswer
    ["--integration-history-foundation"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeHistoryFoundationSpecs player chooseRealAnswer
    ["--integration-automatic-triggers"] -> do
      let player=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
          other=PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000002"
      nativeAutomaticTriggersSpecs player other chooseRealAnswer
    ["--integration-workbench"] -> do
      let player = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeWorkbenchSpecs player chooseRealAnswer
    ["--integration-debug-actions"] -> do
      let player = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      nativeDebugActionsSpecs player chooseRealAnswer
    ["--integration-sidestory-debug"] -> do
      let p1 = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
          p2 = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000002"
      nativeSideStorySpecs p1 p2 chooseRealAnswer
      putStrLn "SIDESTORY_DEBUG_INTEGRATION_COMPLETED"
    _ -> putStrLn "Unsupported explicit spec suite" >> exitFailure

fullSpecs :: IO ()
fullSpecs = do
  settingsDefinitionSpecs
  let p1 = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
      p2 = PlayerId $ fromJustNote "test uuid" $ UUID.fromString "00000000-0000-4000-8000-000000000002"
      request = CreateRequest (Just "01") Nothing 171019 [p1] Standard False False
  settingsEngineSpecs p1 p2 chooseRealAnswer
  nativeStandaloneSetupSpecs p1 p2 chooseRealAnswer
  nativeSideStorySpecs p1 p2 chooseRealAnswer
  eventProjectionContracts p1 p2
  (initial, _) <- createSession request >>= right
  assert "actual campaign creates a player question" $ notNull $ gameQuestion $ snapshotGame initial
  assert "actual campaign retains the player" $ gamePlayers (snapshotGame initial) == [p1]
  assert "real initial ask advances persisted RNG seed" $ gameSeed (snapshotGame initial) /= createSeed request
  (initialAgain, _) <- createSession request >>= right
  assert "same seed creates identical Game + pending queue" $
    encode (snapshotGame initial, snapshotQueue initial) == encode (snapshotGame initialAgain, snapshotQueue initialAgain)
  let bytes = exportSnapshot initial
  roundtrip <- importSnapshot bytes >>= right
  assert "full game + pending queue snapshot roundtrip" $ exportSnapshot roundtrip == bytes
  assert "read-only serialize does not alter RNG seed" $ gameSeed (snapshotGame roundtrip) == gameSeed (snapshotGame initial)
  stale <- applyAnswer initial p1 1 $ Answer $ QuestionResponse 0 (Just p1) Nothing
  assert "stale revision rejected" $ isLeft stale
  foreignSeat <- applyAnswer initial p2 0 $ Answer $ QuestionResponse 0 (Just p2) Nothing
  assert "unknown seat rejected" $ isLeft foreignSeat
  wrongSeat <- applyAnswer initial p1 0 $ Answer $ QuestionResponse 0 (Just p2) Nothing
  assert "embedded player mismatch rejected" $ isLeft wrongSeat
  assert "rejected operations preserve full snapshot" $ exportSnapshot initial == bytes
  duplicate <- createSession $ request {createPlayers = [p1,p1]}
  assert "duplicate players rejected" $ isLeft duplicate
  nilSeat <- createSession $ request {createPlayers = [PlayerId UUID.nil]}
  assert "nil player rejected" $ isLeft nilSeat
  unknownCampaign <- createSession $ request {createCampaign = Just "not-a-real-campaign"}
  assert "unknown campaign becomes a rejected operation" $ isLeft unknownCampaign
  lazyFault <- applyAnswer initial p1 0 (error "intentional lazy answer fault")
  assert "validation-time exception stays inside native boundary" $ isLeft lazyFault
  invalidSnapshot <- importSnapshot "{\"snapshotFormat\":999}"
  assert "unknown snapshot schema rejected" $ isLeft invalidSnapshot
  registryBefore <- readIORef customCardRegistry
  let sentinel = Map.singleton "custom-native-qa" $ CustomCard (missingCustomCardDef AssetType "custom-native-qa") Nothing
  E.bracket_ (writeIORef customCardRegistry sentinel) (writeIORef customCardRegistry registryBefore) do
    exceptionResult <- boundary mempty do
      writeIORef customCardRegistry mempty
      E.evaluate (error "intentional native rule fault" :: Int)
    registryAfter <- readIORef customCardRegistry
    assert "actual rule exception converted into rejection" $ isLeft exceptionResult
    assert "exception restores actual registry values" $ registryAfter == sentinel
  assert "negative amount rejected" $ isLeft $ validateAmounts (Just $ TotalAmountTarget 1) [(unPlayerId p1,0,2)] $ Map.singleton (unPlayerId p1) (-1)
  assert "over-limit amount rejected" $ isLeft $ validateAmounts Nothing [(unPlayerId p1,0,2)] $ Map.singleton (unPlayerId p1) 1000000
  assert "wrong total rejected" $ isLeft $ validateAmounts (Just $ TotalAmountTarget 2) [(unPlayerId p1,0,2)] $ Map.singleton (unPlayerId p1) 1
  assert "unknown amount choice rejected" $ isLeft $ validateAmounts Nothing [(unPlayerId p1,0,2)] $ Map.singleton (unPlayerId p2) 1
  decks <- eitherDecodeFileStrict' @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json"
    >>= either (\e -> P.putStrLn e >> exitFailure) pure
  let firstDeck = fromJustNote "five presets" $ listToMaybe decks
  operation <- chooseRealAnswer p1 firstDeck initial
  (next, _) <- applyAnswer initial p1 0 operation >>= right
  assert "real answer increments revision" $ snapshotRevision next == 1
  assert "real answer advances persisted RNG seed" $ gameSeed (snapshotGame next) /= gameSeed (snapshotGame initial)
  assert "real deck selection installs one investigator" $ Map.size (gameEntities (snapshotGame next)).investigators == 1
  let implementation snapshot = concatMap (gmapQ (\a -> show $ typeOf a)) $ Map.elems (gameEntities $ snapshotGame snapshot).investigators
  assert "official preset uses actual TommyMuldoon2 implementation" $ implementation next == ["TommyMuldoon2"]
  assert "successful candidate preserves previous state" $ exportSnapshot initial == bytes
  restoredNext <- importSnapshot $ exportSnapshot next
  assert "successful candidate has durable next snapshot" $ isRight restoredNext
  importedInvestigator <- right restoredNext
  assert "snapshot import retains actual TommyMuldoon2 implementation" $ implementation importedInvestigator == ["TommyMuldoon2"]
  assert "real answer changes the game or pending queue" $
    encode (snapshotGame next, snapshotQueue next) /= encode (snapshotGame initial, snapshotQueue initial)
  continuation <- chooseRealAnswer p1 firstDeck next
  traumaBefore <- traumaTotal next
  (direct,_) <- applyAnswer next p1 1 continuation >>= right
  traumaAfter <- traumaTotal direct
  assert "actual trauma allocation applies two trauma" $ traumaAfter - traumaBefore == 2
  assert "actual continuation advances Game or queue" $
    encode (snapshotGame direct,snapshotQueue direct) /= encode (snapshotGame next,snapshotQueue next)
  restored <- importSnapshot (exportSnapshot next) >>= right
  (replayed,_) <- applyAnswer restored p1 1 continuation >>= right
  assert "restore then continue matches direct Game + queue" $
    encode (snapshotGame direct,snapshotQueue direct) == encode (snapshotGame replayed,snapshotQueue replayed)
  (repeated,_) <- applyAnswer next p1 1 continuation >>= right
  assert "same snapshot and answer give deterministic rule state" $
    encode (snapshotGame direct,snapshotQueue direct) == encode (snapshotGame repeated,snapshotQueue repeated)
  let beforeContinue = exportSnapshot direct
  offered <- nextAnswer direct
  assert "actual trauma flow reaches ContinueCampaign" $ any ((== ContinueCampaign) . unwrapQuestion) $ Map.elems $ gameQuestion $ snapshotGame direct
  (story,_) <- applyAnswer direct p1 (snapshotRevision direct) offered >>= right
  assert "real offered continuation reaches actual story Read" $ any (\q -> case unwrapQuestion q of Read {} -> True; _ -> False) $ Map.elems $ gameQuestion $ snapshotGame story
  restoredContinue <- importSnapshot beforeContinue >>= right
  (restoredStory,_) <- applyAnswer restoredContinue p1 (snapshotRevision direct) offered >>= right
  assert "restore and offered continuation preserves exact Game queue RNG" $ exportSnapshot story == exportSnapshot restoredStory
  for_ [ ("arbitrary scenario", object ["tag" .= ("ScenarioStep" :: Text), "contents" .= ("99999" :: Text)])
       , ("epilogue", object ["tag" .= ("EpilogueStep" :: Text)])
       , ("unoffered upgrade wrapper", object ["tag" .= ("UpgradeDeckStep" :: Text), "contents" .= object ["tag" .= ("PrologueStep" :: Text)]])
       ] \(name,step) -> do
    altered <- typed $ object ["tag" .= ("CampaignStepAnswer" :: Text), "contents" .= step]
    rejected <- applyAnswer direct p1 (snapshotRevision direct) altered
    assert ("continuation rejects " <> name) $ isLeft rejected
  rejectedStale <- applyAnswer direct p1 (snapshotRevision direct + 1) offered
  assert "continuation rejects future mismatched outer revision" $ isLeft rejectedStale
  rejectedOld <- applyAnswer direct p1 (snapshotRevision direct - 1) offered
  assert "continuation rejects old outer revision" $ isLeft rejectedOld
  repeatedOld <- applyAnswer story p1 (snapshotRevision direct) offered
  assert "completed continuation cannot be replayed with old revision" $ isLeft repeatedOld
  rejectedForeign <- applyAnswer direct p2 (snapshotRevision direct) offered
  assert "continuation rejects nonrecipient player" $ isLeft rejectedForeign
  forOther <- typed $ object ["tag" .= ("CampaignStepAnswerFor" :: Text), "contents" .= [toJSON p2,toJSON False,object ["tag" .= ("PrologueStep" :: Text)]]]
  rejectedFor <- applyAnswer direct p1 (snapshotRevision direct) forOther
  assert "continuation cannot answer for another player" $ isLeft rejectedFor
  case offered of
    CampaignStepAnswer step -> do
      (ownFor,_) <- applyAnswer direct p1 (snapshotRevision direct) (CampaignStepAnswerFor p1 False step) >>= right
      assert "matching For recipient reaches identical actual story" $ exportSnapshot ownFor == exportSnapshot story
    _ -> error "Expected typed offered campaign answer"
  rejectedRaw <- applyAnswer direct p1 (snapshotRevision direct) $ Raw Noop
  assert "continuation still rejects raw messages" $ isLeft rejectedRaw
  assert "all failed continuations preserve source Game queue RNG" $ exportSnapshot direct == beforeContinue
  (standalone,_) <- createSession (request {createCampaign=Nothing,createScenario=Just "01104"}) >>= right
  let campaignJson=field "This" $ toJSON $ gameMode $ snapshotGame direct
      scenarioJson=field "That" $ toJSON $ gameMode $ snapshotGame standalone
      continuing step=object ["tag" .= ("ContinueCampaignStep" :: Text),"contents" .= object ["nextStep" .= step,"canUpgradeDecks" .= False,"chooseSideStory" .= False,"lead" .= Null,"canChooseSideStory" .= False]]
      prologue=object ["tag" .= ("PrologueStep" :: Text)]
      epilogue=object ["tag" .= ("EpilogueStep" :: Text)]
      fakeGame mode=(snapshotGame direct) {gameMode=mode}
  campaignAnswer <- typed $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= prologue]
  scenarioAnswer <- typed $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= epilogue]
  both <- typed @GameMode $ object ["This" .= campaignJson,"That" .= setField "campaignStep" (continuing epilogue) scenarioJson]
  assert "pure These fixture retains actual scenario campaignStep" $ field "campaignStep" (field "That" $ toJSON both) == continuing epilogue
  assert "pure These prioritizes scenario continuation" $ isRight $ validateAnswer (fakeGame both) p1 scenarioAnswer
  assert "pure These rejects campaign step when scenario asks" $ isLeft $ validateAnswer (fakeGame both) p1 campaignAnswer
  that <- typed @GameMode $ object ["That" .= setField "campaignStep" (continuing epilogue) scenarioJson]
  assert "pure That fixture retains actual scenario campaignStep" $ field "campaignStep" (field "That" $ toJSON that) == continuing epilogue
  assert "pure That accepts only scenario continuation" $ isRight (validateAnswer (fakeGame that) p1 scenarioAnswer) && isLeft (validateAnswer (fakeGame that) p1 campaignAnswer)
  wrapped <- typed @GameMode $ object ["This" .= setField "step" (object ["tag" .= ("StandaloneScenarioStep" :: Text),"contents" .= [toJSON ("01104" :: Text),continuing prologue]]) campaignJson]
  assert "pure standalone campaign wrapper uses actual next step" $ isRight (validateAnswer (fakeGame wrapped) p1 campaignAnswer) && isLeft (validateAnswer (fakeGame wrapped) p1 scenarioAnswer)
  absent <- typed @GameMode $ object ["This" .= setField "step" prologue campaignJson]
  assert "pure no continuation cannot succeed as empty operation" $ isLeft $ validateAnswer (fakeGame absent) p1 campaignAnswer
  let options=object ["scenarioOptionsStandalone" .= False,"scenarioOptionsPerformTarotReading" .= False]
      optionsStep=object ["tag" .= ("ScenarioStepWithOptions" :: Text),"contents" .= [toJSON ("01104" :: Text),options]]
  blocked <- typed @GameMode $ object ["This" .= campaignJson,"That" .= setField "campaignStep" optionsStep scenarioJson]
  assert "pure options fixture retains scenario campaignStep tag" $ field "tag" (field "campaignStep" $ field "That" $ toJSON blocked) == toJSON ("ScenarioStepWithOptions" :: Text)
  assert "pure scenario options cannot fall back to campaign continuation" $ isLeft $ validateAnswer (fakeGame blocked) p1 campaignAnswer
  invalidKind <- applyAnswer initial p1 0 $ CampaignSpecificAnswer "not-offered" Null
  assert "unoffered answer type rejected" $ isLeft invalidKind
  raw <- applyAnswer initial p1 0 $ Raw Noop
  assert "arbitrary raw messages rejected" $ isLeft raw
  (twoSeats,_) <- createSession (request {createPlayers=[p1,p2]}) >>= right
  assert "real multiplayer game has both seats" $ length (gamePlayers $ snapshotGame twoSeats) == 2
  let untilContinuation budget snapshot
        | any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = error "Bounded multiplayer continuation test did not reach the actual ask"
        | otherwise = do
            let (recipient,_) = fromJustNote "pending multiplayer ask" $ listToMaybe $ Map.toList $ gameQuestion $ snapshotGame snapshot
                deck = if recipient == p1 then firstDeck else fromJustNote "second actual preset" $ decks !!? 1
            reply <- chooseRealAnswer recipient deck snapshot
            (following,_) <- applyAnswer snapshot recipient (snapshotRevision snapshot) reply >>= right
            untilContinuation (budget-1) following
  multiplayerContinue <- untilContinuation 12 twoSeats
  let recipient = fst $ fromJustNote "actual continuation recipient" $ find ((== ContinueCampaign) . unwrapQuestion . snd) $ Map.toList $ gameQuestion $ snapshotGame multiplayerContinue
      notAsked = fromJustNote "existing teammate" $ find (/= recipient) $ gamePlayers $ snapshotGame multiplayerContinue
      preserved = exportSnapshot multiplayerContinue
  multiOffered <- nextAnswer multiplayerContinue
  wrongExistingSeat <- applyAnswer multiplayerContinue notAsked (snapshotRevision multiplayerContinue) multiOffered
  assert "existing teammate cannot answer lead-only continuation" $ isLeft wrongExistingSeat
  assert "wrong existing teammate preserves full game queue RNG" $ exportSnapshot multiplayerContinue == preserved
  questionContracts p1 p2 next multiplayerContinue
  strictNativeContracts p1 p2 next
  triggerContracts p1 p2 next multiplayerContinue
  fastPlayContracts p1 next multiplayerContinue
  scarletCheckpoint p1 firstDeck
  upgradeContracts p1 p2 firstDeck multiplayerContinue
  campaignContracts decks multiplayerContinue
  regionsContracts p1 p2 firstDeck direct multiplayerContinue
  putStrLn "Core source rule specs completed (not full campaign coverage)."

-- Controlled typed public-region projection, not a scenario-playthrough test.
setAsideRegionContracts :: PlayerId -> IO ()
setAsideRegionContracts player = do
  request <- typed $ object ["scenario" .= ("01104" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (snapshot,_) <- createSession request >>= right
  let enc ident code down flipped = object
        ["tag" .= ("EncounterCard" :: Text),"contents" .= object
          ["id" .= (ident :: Text),"cardCode" .= (code :: Text),"originalCardCode" .= code
          ,"facedown" .= down,"isFlipped" .= flipped,"addedPeril" .= False,"owner" .= Null]]
      a=enc "20000000-0000-4000-8000-000000000001" "01111" False False
      b=enc "20000000-0000-4000-8000-000000000002" "01111" False False
      hidden=enc "20000000-0000-4000-8000-000000000003" "01111" True False
      revenge=object ["tag" .= ("VengeanceCard" :: Text),"contents" .= enc "20000000-0000-4000-8000-000000000005" "01111" False False]
      unknownFace=enc "20000000-0000-4000-8000-000000000004" "05178a" False True
      cards=[a,b,hidden,revenge,unknownFace]
      scenario=field "That" $ toJSON $ gameMode $ snapshotGame snapshot
      game=(snapshotGame snapshot) {gameMode=fixtureValue $ object
        ["That" .= setField "setAsideCards" (toJSON cards) scenario]}
      rows value=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "zones" value) :: [Value]
      zoneOf kind value=fromJustNote "expected zone" $ find ((== String kind) . field "kind") $ rows value
      values value=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "items" value) :: [Value]
      before=encode game
  projected <- regionsView game player
  repeated <- regionsView game player
  let zone=zoneOf "setAside" projected
      shown=values zone
  assert "set-aside is ordinary supported inspection with exact physical count" $
    field "status" zone==String "supported" && field "count" zone==toJSON (5 :: Int) && length shown==5
  assert "set-aside preserves duplicate printed cards as distinct physical instances in order" $
    map (field "cardId") (take 2 shown)==map (field "id" . field "contents") [a,b]
  assert "set-aside visible cards permit read-only inspection" $
    all ((== Bool True) . field "inspectAllowed") $ take 2 shown
  assert "set-aside face-down vengeance and unknown reverse reveal no identity" $
    all (\item -> field "visibility" item==String "back" && field "inspectAllowed" item==Bool False
      && all (\key -> field key item==Null) ["cardId","cardCode","name","art"]) $ drop 2 shown
  assert "set-aside unknown reverse retains explicit unsupported face reason" $
    field "reasonKey" (lastEx shown)==String "unrecognizedFlippedCardFace"
  assert "set-aside inspection does not reveal encounter deck" $
    null $ values $ zoneOf "encounterDeck" projected
  assert "set-aside repeated inspection leaves game unchanged" $
    projected==repeated && before==encode game
  denied <- regionsView game $ PlayerId UUID.nil
  assert "set-aside unknown recipient receives no zones" $
    field "supported" denied==Bool False && null (rows denied)

regionsContracts :: PlayerId -> PlayerId -> ArkhamDBDecklist -> Snapshot -> Snapshot -> IO ()
regionsContracts player other deck story team = do
  let locationValues g=fixtureValue @(Map LocationId Value) $ field "locations" $ toJSON $ gameEntities g
      hasHand value=case field "hand" $ toJSON value of Array cards -> notNull cards; _ -> False
      untilCards budget snapshot
        | any hasHand (Map.elems (gameEntities $ snapshotGame snapshot).investigators)
          && notNull (locationValues $ snapshotGame snapshot) && not (gameInSetup $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = error "Natural setup did not reach cards and locations"
        | otherwise = do
            response <- if Map.lookup player (gameQuestion $ snapshotGame snapshot)==Just ContinueCampaign then nextAnswer snapshot else chooseRealAnswer player deck snapshot
            (following,_) <- applyAnswer snapshot player (snapshotRevision snapshot) response >>= right
            untilCards (budget-1) following
  natural <- untilCards 80 story
  let game=snapshotGame natural;before=exportSnapshot natural
  naturalSchema <- regionsView game player
  naturalAgain <- regionsView game player
  assert "regions actual setup reaches hand and locations without a checkpoint" $ notNull $ locationValues game
  assert "regions actual projection and refresh preserve complete snapshot queue RNG" $ before == exportSnapshot natural && naturalSchema == naturalAgain
  let rows schema=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "zones" schema) :: [Value]
      items z=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "items" z) :: [Value]
      findZone kind schema=fromJustNote "expected shared region" $ find ((== String kind) . field "kind") $ rows schema
      oneZone ident schema=fromJustNote "expected region id" $ find ((== String ident) . field "id") $ rows schema
      ownActors=[(iid,toJSON actor) | (iid,actor) <- Map.toList (gameEntities $ snapshotGame team).investigators,field "playerId" (toJSON actor) == toJSON player]
      (iid,actor)=fromJustNote "region own actual actor" $ listToMaybe ownActors
      otherIid=fst $ fromJustNote "region second actor" $ find ((/= iid) . fst) $ Map.toList (gameEntities $ snapshotGame team).investigators
      naturalActor=toJSON $ fromJustNote "natural own actor" $ find ((== toJSON player) . field "playerId" . toJSON) $ Map.elems (gameEntities game).investigators
      originalCards=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "deck" naturalActor) :: [Value]
      originalCard=fromJustNote "region actual player card" $ listToMaybe originalCards
      card ident down=setField "id" (String ident) $ setField "facedown" (Bool down) originalCard
      a=card "10000000-0000-4000-8000-000000000001" False
      b=card "10000000-0000-4000-8000-000000000002" False
      hidden=card "10000000-0000-4000-8000-000000000003" True
      teammate=card "10000000-0000-4000-8000-000000000004" False
      wrap c=object ["tag" .= ("PlayerCard" :: Text),"contents" .= c]
      others=Map.mapWithKey (\key value -> if key==iid then setField "deck" (toJSON originalCards) $ setField "hand" (toJSON [wrap a,wrap b]) $ setField "discard" (toJSON [b,a]) $ setField "cardsUnderneath" (toJSON [wrap hidden,wrap a]) value
        else if key==otherIid then setField "hand" (toJSON [wrap teammate]) value else value) $ Map.map toJSON (gameEntities $ snapshotGame team).investigators
      fixture=(snapshotGame team){gameInSetup=gameInSetup game,gameEntities=fixtureValue $ setField "investigators" (toJSON others) $ toJSON $ gameEntities $ snapshotGame team}
      prefix="investigator:" <> (case toJSON iid of String t -> t; _ -> error "iid") <> ":"
  schema <- regionsView fixture player
  otherSchema <- regionsView fixture other
  assert "regions own hand preserves order and duplicate printed codes as physical instances" $ map (field "cardId") (items $ oneZone (prefix <> "hand") schema) == map (field "id") [a,b]
  assert "regions discard retains original physical ordering" $ map (field "cardId") (items $ oneZone (prefix <> "discard") schema) == map (field "id") [b,a]
  assert "regions other seat cannot see our hand identity" $ not $ "10000000-0000-4000-8000-000000000001" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode otherSchema)
  assert "regions own seat cannot see teammate hand identity" $ not $ "10000000-0000-4000-8000-000000000004" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode schema)
  let under=items $ oneZone (prefix <> "under") schema;back=headEx under
  assert "regions face down item strips card id code name art and entity reference" $ field "visibility" back == String "back" && all ((== Null) . (`field` back)) ["cardId","cardCode","name","art","entityRef"] && field "inspectAllowed" back == Bool False
  assert "regions hidden physical identity absent from entire serialized schema" $ not $ "10000000-0000-4000-8000-000000000003" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode schema)
  assert "regions ordinary visible under card remains inspectable" $ field "cardId" (lastEx under)==field "id" a && field "inspectAllowed" (lastEx under)==Bool True
  assert "regions ordinary deck exposes count but no order identifiers" $ null (items $ oneZone (prefix <> "deck") schema) && field "count" (oneZone (prefix <> "deck") schema)==toJSON (length originalCards)
  unknown <- regionsView fixture $ PlayerId UUID.nil
  assert "regions unknown recipient cannot inspect any zone" $ field "supported" unknown==Bool False && null (rows unknown)
  -- Load the actual Norman implementation. Cached gameModifiers are rebuilt
  -- by makeEnv, so forging that cache would not test effective permissions.
  normanDeck <- typed @ArkhamDBDecklist $ setField "investigator_code" (String "08004") $ setField "meta" Null $ toJSON deck
  (normanInitial,_) <- createSession (CreateRequest (Just "01") Nothing 201020 [player] Standard False False) >>= right
  (normanLoaded,_) <- applyAnswer normanInitial player 0 (DeckListAnswer normanDeck player) >>= right
  normanReady <- untilCards 80 normanLoaded
  let normanGame=snapshotGame normanReady
      (normanIid,normanActor)=fromJustNote "actual Norman actor" $ listToMaybe $ Map.toList (gameEntities normanGame).investigators
      normanCards=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "deck" $ toJSON normanActor) :: [Value]
      normanPrefix="investigator:" <> (case toJSON normanIid of String t -> t; _ -> error "Norman iid") <> ":"
  revealed <- regionsView normanGame player
  assert "regions effective actual Norman reveal exposes only one real top card" $ notNull normanCards && map (field "cardId") (items $ oneZone (normanPrefix <> "deck") revealed)==map (field "id") (take 1 normanCards)
  noReveal <- regionsView fixture player
  assert "regions ordinary investigator does not inherit Norman reveal permissions" $ null $ items $ oneZone (prefix <> "deck") noReveal
  let scenarioJson=field "That" $ toJSON $ gameMode game
      encounters=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "encounterDeck" scenarioJson) :: [Value]
      encounter=fromJustNote "actual natural encounter deck card" $ listToMaybe encounters
      ec ident=setField "id" (String ident) $ setField "facedown" (Bool False) encounter
      enc=object ["tag" .= ("EncounterCard" :: Text),"contents" .= ec "20000000-0000-4000-8000-000000000001"]
      vengeance=object ["tag" .= ("VengeanceCard" :: Text),"contents" .= enc]
      modifiedScenario=setField "discard" (toJSON [ec "20000000-0000-4000-8000-000000000002"]) $ setField "victoryDisplay" (toJSON [vengeance]) scenarioJson
      withScenario=fixture {gameMode=fixtureValue $ object ["This" .= field "This" (toJSON $ gameMode fixture),"That" .= modifiedScenario]}
  scenarioSchema <- regionsView withScenario player
  assert "regions actual encounter discard mapped separately from player discard" $ field "cardId" (headEx $ items $ findZone "encounterDiscard" scenarioSchema)==String "20000000-0000-4000-8000-000000000002"
  let revenge=headEx $ items $ findZone "victory" scenarioSchema
  assert "regions victory vengeance stays back without wrapped front identity" $ field "vengeance" revenge==Bool True && field "visibility" revenge==String "back" && all ((== Null) . (`field` revenge)) ["cardCode","cardId","art","name"]
  assert "regions set aside uses ordinary protected inspection" $ field "status" (findZone "setAside" scenarioSchema)==String "supported"
  let encounterFace code flipped ident=object ["tag" .= ("EncounterCard" :: Text),"contents" .= setField "cardCode" (String code) (setField "originalCardCode" (String code) $ setField "isFlipped" (Bool flipped) $ ec ident)]
      faceCards=[encounterFace "01111" True "21000000-0000-4000-8000-000000000001"
                ,encounterFace "05178b" False "21000000-0000-4000-8000-000000000002"
                ,encounterFace "05178b" True "21000000-0000-4000-8000-000000000003"
                ,encounterFace "05178a" True "21000000-0000-4000-8000-000000000004"]
  faceSchema <- regionsView (withScenario {gameRemovedFromPlay=fixtureValue $ toJSON faceCards}) player
  let faceItems=items $ findZone "removed" faceSchema
  assert "regions numeric double sided reverse resolves actual b art" $ field "art" (headEx faceItems)==String "01111b" && field "face" (headEx faceItems)==String "reverse"
  assert "regions Unfinished Business unflipped selects paired front" $ field "art" (faceItems P.!! 1)==String "05178a"
  assert "regions Unfinished Business flipped selects exact named reverse not bb" $ field "art" (faceItems P.!! 2)==String "05178b"
  assert "regions unknown suffixed reverse explicitly unsupported without guessing" $ field "status" (faceItems P.!! 3)==String "unsupported" && field "visibility" (faceItems P.!! 3)==String "back" && field "cardId" (faceItems P.!! 3)==Null
  let (lid,location)=fromJustNote "natural actual location" $ listToMaybe $ Map.toList $ locationValues game
      locationWith cards code=setField "cardsUnderneath" (toJSON cards) $ setField "cardCode" code location
      atLocation loc=withScenario {gameEntities=fixtureValue $ setField "locations" (toJSON $ Map.singleton lid loc) $ toJSON $ gameEntities withScenario}
  locationSchema <- regionsView (atLocation $ locationWith [enc,wrap a] $ field "cardCode" location) player
  let locationRows=filter ((== String "Location") . field "kind" . field "owner") $ rows locationSchema
  assert "regions ordinary location allows only ordinary player under cards" $ length locationRows==1 && map (field "cardId") (items $ headEx locationRows)==[field "id" a]
  hiddenLocation <- regionsView (atLocation $ locationWith [enc,wrap hidden,wrap a] $ field "cardCode" location) player
  let hiddenRows=filter ((== String "Location") . field "kind" . field "owner") $ rows hiddenLocation
  assert "regions location facedown guard is count-only not an unsupported fake permission" $ field "status" (headEx hiddenRows)==String "supported" && field "access" (headEx hiddenRows)==String "countOnly" && null (items $ headEx hiddenRows)
  shop <- regionsView (atLocation $ locationWith [enc,wrap a] $ toJSON ("c11685" :: Text)) player
  assert "regions shuffled shop has explicit unresolved inspection policy" $ any ((== String "shuffledShopStackInspectionPolicyPending") . field "reasonKey") $ rows shop
  concealedLocation <- regionsView (atLocation $ setField "revealed" (Bool False) $ locationWith [wrap a] $ field "cardCode" location) player
  let concealedOwner=field "owner" $ headEx $ filter ((== String "Location") . field "kind" . field "owner") $ rows concealedLocation
  assert "regions unrevealed location owner omits title and searchable code" $ field "title" concealedOwner==Null && field "cardCode" concealedOwner==Null
  -- Controlled CreateAssetAt messages construct genuine implementations and
  -- their real metadata; this is not a claim of purchasing these cards in play.
  let assetCard code ident=wrap $ setField "owner" (toJSON iid) $ setField "cardCode" (String code) $ setField "originalCardCode" (String code) $ card ident False
      marketId="30000000-0000-4000-8000-000000000001" :: Text
      beyondId="30000000-0000-4000-8000-000000000002" :: Text
      eyeId="30000000-0000-4000-8000-000000000003" :: Text
      allyId="30000000-0000-4000-8000-000000000004" :: Text
      createAsset ident code=object ["tag" .= ("CreateAssetAt" :: Text),"contents" .= [String ident,assetCard code ident,object ["tag" .= ("InPlayArea" :: Text),"contents" .= iid]]]
  commands <- typed @[Message] $ toJSON
    [object ["tag" .= ("ClearUI" :: Text)]
    ,createAsset marketId "09077",createAsset beyondId "90052",createAsset eyeId "11073",createAsset allyId "01020"
    ,object ["tag" .= ("CreateEventAt" :: Text),"contents" .= [toJSON iid,assetCard "01088" "31000000-0000-4000-8000-000000000001",object ["tag" .= ("InThreatArea" :: Text),"contents" .= iid]]]
    ,toJSON $ Ask player $ ChooseOne [Label "regions-controlled-assets" [Noop]]]
  let assetSource=withScenario {gameQuestion=mempty,gameRetainedQuestion=False}
  (createdAssets,_) <- boundary (gameCustomCards assetSource) (do
    env <- makeEnv assetSource commands
    advance env
    finish Nothing 1 env) >>= right
  let assetGame=snapshotGame createdAssets
      assetObjects=Map.map toJSON (gameEntities assetGame).assets
      marketKey=fixtureValue $ String marketId
      beyondKey=fixtureValue $ String beyondId
      eyeKey=fixtureValue $ String eyeId
      allyKey=fixtureValue $ String allyId
      mutateAsset ident fn g=g {gameEntities=fixtureValue $ setField "assets" (toJSON $ Map.adjust fn ident $ Map.map toJSON (gameEntities g).assets) $ toJSON $ gameEntities g}
      market=fromJustNote "actual constructed Market" $ Map.lookup marketKey assetObjects
      beyond=fromJustNote "actual constructed Beyond" $ Map.lookup beyondKey assetObjects
      withMarket=mutateAsset marketKey (setField "marketDeck" (toJSON [wrap hidden,wrap b,wrap a]) . setField "knownMarketDeck" (toJSON [wrap b,wrap a])) assetGame
      withSpirit=mutateAsset beyondKey (setField "spiritDeck" $ toJSON [enc,wrap hidden,wrap a]) withMarket
      marketPrefix="asset:" <> marketId <> ":"
      beyondPrefix="asset:" <> beyondId <> ":"
  assert "regions controlled creation reaches exact sentinel after all entry windows" $ Map.lookup player (gameQuestion assetGame)==Just (ChooseOne [Label "regions-controlled-assets" [Noop]])
  assert "regions controlled creation has all four actual assets" $ all (`Map.member` assetObjects) [marketKey,beyondKey,eyeKey,allyKey]
  assert "regions genuine Market constructor exposes top-level deck metadata" $ field "marketDeck" market==toJSON ([] :: [Value]) && field "knownMarketDeck" market==toJSON ([] :: [Value])
  -- The real entry handler populates this from the actual preset side deck;
  -- this validates its protocol, not the false assumption it stays empty.
  assert "regions genuine Beyond constructor exposes top-level spirit metadata" $ case field "spiritDeck" beyond of Array _ -> field "selectedSpirit" beyond==Null && field "selectedEnemySpirit" beyond==Null; _ -> False
  assetSchema <- regionsView withSpirit player
  teammateAssetSchema <- regionsView withSpirit other
  assert "regions Market unknown deck is count-only without hidden card ids" $ field "count" (oneZone (marketPrefix <> "market") assetSchema)==toJSON (3 :: Int) && null (items $ oneZone (marketPrefix <> "market") assetSchema)
  assert "regions Market known bottom preserves exact physical order" $ field "order" (oneZone (marketPrefix <> "knownMarket") assetSchema)==String "bottomKnownSequence" && map (field "cardId") (items $ oneZone (marketPrefix <> "knownMarket") assetSchema)==map (field "id") [b,a]
  assert "regions public asset owner title is explicitly projected by core" $ field "title" (field "owner" $ oneZone (marketPrefix <> "under") assetSchema)/=Null && field "cardCode" (field "owner" $ oneZone (marketPrefix <> "under") assetSchema)==String "c09077"
  assert "regions teammate cannot inspect controller Market known bottom" $ not $ any ((== String (marketPrefix <> "knownMarket")) . field "id") $ rows teammateAssetSchema
  assert "regions Beyond only exposes actual encounter top not remaining spirits" $ map (field "cardId") (items $ oneZone (beyondPrefix <> "spirit") assetSchema)==[String "20000000-0000-4000-8000-000000000001"]
  playerSpirit <- regionsView (mutateAsset beyondKey (setField "spiritDeck" $ toJSON [wrap hidden,enc]) withSpirit) player
  assert "regions Beyond player top does not expose name code or identity" $ null $ items $ oneZone (beyondPrefix <> "spirit") playerSpirit
  assert "regions hidden metadata identity absent from complete asset projection" $ not $ "10000000-0000-4000-8000-000000000003" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode assetSchema)
  assert "regions actual event in threat area remains in threat browsing" $ any ((== String "31000000-0000-4000-8000-000000000001") . field "cardId") $ items $ oneZone (prefix <> "threat") assetSchema
  assert "regions actual Eye controller effect reveals exactly encounter top" $ length (items $ findZone "encounterDeck" assetSchema)==1 && field "cardId" (headEx $ items $ findZone "encounterDeck" assetSchema)==field "id" (headEx encounters)
  let withoutEye=assetGame {gameEntities=fixtureValue $ setField "assets" (toJSON $ Map.delete eyeKey $ Map.map toJSON (gameEntities assetGame).assets) $ toJSON $ gameEntities assetGame}
  eyeRemoved <- regionsView withoutEye player
  assert "regions removing actual Eye recalculates permissions and hides encounter top" $ null $ items $ findZone "encounterDeck" eyeRemoved
  let hiddenAsset=mutateAsset marketKey (setField "flipped" (Bool True) . setField "cardsUnderneath" (toJSON [wrap hidden])) withSpirit
  flippedSchema <- regionsView hiddenAsset player
  assert "regions flipped assets do not expose private underneath or metadata decks" $ field "status" (oneZone (marketPrefix <> "under") flippedSchema)==String "unsupported" && not (any ((== String (marketPrefix <> "market")) . field "id") $ rows flippedSchema)
  assert "regions flipped asset owner cannot restore secret title or front code" $ field "title" (field "owner" $ oneZone (marketPrefix <> "under") flippedSchema)==Null && field "cardCode" (field "owner" $ oneZone (marketPrefix <> "under") flippedSchema)==Null
  let attached=mutateAsset allyKey (setField "placement" $ object ["tag" .= ("AttachedToAsset" :: Text),"contents" .= [String beyondId,toJSON $ object ["tag" .= ("InPlayArea" :: Text),"contents" .= iid]]]) withSpirit
  attachedSchema <- regionsView attached player
  assert "regions actual asset attached spirit has separate relationship zone" $ map (field "cardId") (items $ oneZone (beyondPrefix <> "attachments") attachedSchema)==[String allyId]
  BLC.putStrLn $ "REGIONS_FIXTURE " <> encode (object ["player" .= player,"other" .= other,"schema" .= schema,"otherSchema" .= otherSchema,"revealedSchema" .= revealed,"scenarioSchema" .= scenarioSchema,"naturalSchema" .= naturalSchema,"assetSchema" .= assetSchema,"attachedSchema" .= attachedSchema])
  BLC.putStrLn $ "REGIONS_SAVE_FIXTURE " <> encode (object ["player" .= player,"natural" .= natural,"controlledAssets" .= (createdAssets {snapshotGame=withSpirit})
    ,"scope" .= ("Natural campaign01 setup and separately controlled CreateAssetAt/metadata region checkpoint, not natural purchases or completed campaign" :: Text)])

-- A controlled upgrade question on a real, fully initialised campaign.
-- It is not a claim that the preceding scenario was naturally completed.
upgradeContracts :: PlayerId -> PlayerId -> ArkhamDBDecklist -> Snapshot -> IO ()
upgradeContracts player other deck campaign = do
  let game = snapshotGame campaign
      iid = fst $ fromJustNote "actual upgrade actor" $ find
        (\(_,actor) -> field "playerId" (toJSON actor) == toJSON player)
        $ Map.toList (gameEntities game).investigators
      otherIid = fst $ fromJustNote "other upgrade actor" $ find
        (\(_,actor) -> field "playerId" (toJSON actor) == toJSON other)
        $ Map.toList (gameEntities game).investigators
      actors = (gameEntities game).investigators
      oldActor = fromJustNote "old upgrade actor" $ Map.lookup iid actors
      actor = fixtureValue $ setField "physicalTrauma" (toJSON (2 :: Int)) $ setField "mentalTrauma" (toJSON (0 :: Int))
        $ setField "xp" (toJSON (13 :: Int)) $ setField "spentXp" (toJSON (3 :: Int)) $ toJSON oldActor
      entities = fixtureValue $ setField "investigators" (toJSON $ Map.insert iid actor actors) $ toJSON $ gameEntities game
      pending = game {gameQuestion=Map.fromList [(player,ChooseUpgradeDeck),(other,ChooseUpgradeDeck)],gameRetainedQuestion=False
        ,gameGameState=fixtureValue $ object ["tag" .= ("IsChooseDecks" :: Text),"contents" .= [player,other]],gameEntities=entities}
      basis = campaign {snapshotGame=pending,snapshotQueue=[Ask player $ ChooseOne [Label "upgrade-finished" [Noop]]]}
      ownRequest chosen = UpgradeDeckAnswer $ UpgradeDeckRequest iid chosen
      getActor snapshot target = toJSON $ fromJustNote "upgrade result actor" $ Map.lookup target (gameEntities $ snapshotGame snapshot).investigators
      rejected name source who revision answer = do
        let before=exportSnapshot source
        result <- applyAnswer source who revision answer
        assert ("upgrade rejects " <> name) $ isLeft result && exportSnapshot source == before
  reply <- handleAnswerPure pending player $ DeckListAnswer deck player
  assert "upgrade routes through UpgradeDecklist, never LoadDecklist or InitDeck" $ case reply of
    Handled messages -> any (\case UpgradeDecklist target _ -> target == iid; _ -> False) messages
      && not (any (\case LoadDecklist {} -> True; InitDeck {} -> True; _ -> False) messages)
    _ -> False
  assert "upgrade legacy deck payload and explicit target share validation" $ isRight (validateAnswer pending player $ DeckListAnswer deck player)
    && isRight (validateAnswer pending player $ ownRequest $ Just deck)
  assert "upgrade resolver preserves other seat exact question after its own work" $ case reply of
    Handled messages -> encode messages == encode ([UpgradeDecklist iid deck] <> reAskOthers pending player)
    _ -> False
  let metadata = deckView pending player
      original = exportSnapshot basis
  assert "upgrade metadata locks actual investigator and exposes explicit keep path" $
    field "investigatorId" metadata == toJSON iid && field "canKeepDeck" metadata == Bool True
    && field "supported" metadata == Bool True && field "replacementSupported" metadata == Bool True
    && field "purchaseBudgetValidated" metadata == Bool False
  assert "upgrade readonly metadata keeps exact whole state queue RNG" $ deckView pending player == metadata && exportSnapshot basis == original
  (upgraded,_) <- applyAnswer basis player (snapshotRevision basis) (ownRequest $ Just deck) >>= right
  assert "upgrade actual revision increments once and source remains immutable" $ snapshotRevision upgraded == snapshotRevision basis+1 && exportSnapshot basis == original
  for_ ["physicalTrauma","mentalTrauma","xp","settings","playerId","id","hand","deck","damage","horror"] \key ->
    assert ("upgrade real same investigator preserves " <> key) $ field key (getActor upgraded iid) == field key (toJSON actor)
  assert "upgrade actual rules mark spent XP without resetting accumulated XP" $ field "spentXp" (getActor upgraded iid) == toJSON (13 :: Int)
  assert "same-person upgrade does not stamp replacement history" $ field "replacedInvestigators" (field "This" $ toJSON $ gameMode $ snapshotGame upgraded)==toJSON ([] :: [InvestigatorId])
  assert "upgrade other investigator full attrs unchanged" $ getActor upgraded otherIid == toJSON (fromJustNote "other original actor" $ Map.lookup otherIid actors)
  assert "upgrade real queue waits for the other seat before later continuation" $
    Map.lookup other (gameQuestion $ snapshotGame upgraded) == Just ChooseUpgradeDeck
    && not (Map.member player $ gameQuestion $ snapshotGame upgraded)
  restored <- importSnapshot original >>= right
  (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (ownRequest $ Just deck) >>= right
  assert "upgrade restore then real execution exact whole Game queue integer RNG" $ exportSnapshot upgraded == exportSnapshot replayed
  (legacy,_) <- applyAnswer basis player (snapshotRevision basis) (DeckListAnswer deck player) >>= right
  assert "upgrade old deck wire form now reaches exact real upgrade state" $ exportSnapshot legacy == exportSnapshot upgraded
  keepReply <- handleAnswerPure pending player $ ownRequest Nothing
  assert "upgrade keeping deck emits only seat completion not any deck message" $ case keepReply of
    Handled messages -> encode messages == encode (reAskOthers pending player)
    _ -> False
  (kept,_) <- applyAnswer basis player (snapshotRevision basis) (ownRequest Nothing) >>= right
  assert "upgrade keeping original deck never marks spent XP or recreates investigator" $ getActor kept iid == toJSON actor
  assert "upgrade keeping deck preserves original campaign deck bytes" $
    encode (campaignDeck (snapshotGame kept) iid) == encode (campaignDeck pending iid)
  (keptAgain,_) <- applyAnswer restored player (snapshotRevision restored) (ownRequest Nothing) >>= right
  assert "keeping a deck does not stamp replacement history" $ field "replacedInvestigators" (field "This" $ toJSON $ gameMode $ snapshotGame kept)==toJSON ([] :: [InvestigatorId])
  assert "upgrade keeping deck restore replay preserves exact whole Game queue RNG" $ exportSnapshot keptAgain == exportSnapshot kept
  rejected "foreign investigator" basis player (snapshotRevision basis) $ UpgradeDeckAnswer $ UpgradeDeckRequest otherIid Nothing
  rejected "stale revision" basis player (snapshotRevision basis+1) $ ownRequest Nothing
  rejected "embedded wrong seat legacy" basis player (snapshotRevision basis) $ DeckListAnswer deck other
  let wrongQuestion = basis {snapshotGame=pending {gameQuestion=Map.singleton player $ ChooseOne [Label "not upgrade" [Noop]]}}
      wrongState = basis {snapshotGame=pending {gameGameState=fixtureValue $ object ["tag" .= ("IsActive" :: Text)]}}
      wrongOwner = basis {snapshotGame=pending {gameEntities=fixtureValue $ setField "investigators"
        (toJSON $ Map.insert otherIid (fixtureValue $ setField "playerId" (toJSON player) $ toJSON $ fromJustNote "other duplicate owner" $ Map.lookup otherIid actors) $ (gameEntities pending).investigators) $ toJSON $ gameEntities pending}}
      noDeckMode = fixtureValue @GameMode $ setField "This"
        (setField "decks" (object []) $ field "This" $ toJSON $ gameMode pending) $ toJSON $ gameMode pending
  rejected "unoffered question" wrongQuestion player (snapshotRevision basis) $ ownRequest Nothing
  rejected "active game not deck state" wrongState player (snapshotRevision basis) $ ownRequest Nothing
  rejected "two investigators share same seat" wrongOwner player (snapshotRevision basis) $ ownRequest Nothing
  rejected "missing original campaign deck" (basis {snapshotGame=pending {gameMode=noDeckMode}}) player (snapshotRevision basis) $ ownRequest Nothing
  replacement <- typed @ArkhamDBDecklist $ setField "investigator_code" (toJSON otherIid) $ setField "meta" Null $ toJSON deck
  rejected "unsafe replacement remains explicit not silently initialised" basis player (snapshotRevision basis) $ ownRequest $ Just replacement
  for_ [("negative",object ["01001" .= (-1 :: Int)]),("huge count",object ["01001" .= (1000000 :: Int)]),("unknown",object ["not-real" .= (1 :: Int)]),("encounter",object ["01111" .= (1 :: Int)])] \(label,slots) -> do
    bad <- typed @ArkhamDBDecklist $ setField "slots" slots $ toJSON deck
    rejected label basis player (snapshotRevision basis) $ ownRequest $ Just bad
  badMeta <- typed @ArkhamDBDecklist $ setField "meta" (String "{\"extra_deck\":\"unknown-native\"}") $ toJSON deck
  rejected "unknown metadata extra deck" basis player (snapshotRevision basis) $ ownRequest $ Just badMeta
  for_ ["", "01033,", ",01033"] \codes -> do
    emptyExtra <- typed @ArkhamDBDecklist $ setField "meta" (String $ "{\"extra_deck\":" <> decodeUtf8 (toStrict $ encode codes) <> "}") $ toJSON deck
    rejected "empty extra-deck token" basis player (snapshotRevision basis) $ ownRequest $ Just emptyExtra
  let malformed=object ["investigatorId" .= iid,"deck" .= Null,"messages" .= ([] :: [Value])]
      malformedAnswer=object ["tag" .= ("UpgradeDeckAnswer" :: Text),"contents" .= malformed]
  assert "upgrade finite request rejects extra messages" $ isLeft $ Aeson.parseEither (parseJSON @Answer) malformedAnswer
  -- New real purchase effect: Spiritual Healing heals trauma through the
  -- actual campaign handler. Re-submitting an unchanged deck must not heal again.
  healing <- typed @ArkhamDBDecklist $ setField "slots" (setField "11098" (toJSON (1 :: Int)) $ field "slots" $ toJSON deck) $ toJSON deck
  (healed,_) <- applyAnswer basis player (snapshotRevision basis) (ownRequest $ Just healing) >>= right
  let beforeTrauma=fromJustNote "original trauma number" (Aeson.parseMaybe parseJSON $ field "physicalTrauma" $ toJSON actor) :: Int
      afterTrauma=fromJustNote "healed trauma number" (Aeson.parseMaybe parseJSON $ field "physicalTrauma" $ getActor healed iid) :: Int
  assert "upgrade actual new purchase executes Spiritual Healing not InitDeck trauma" $ beforeTrauma > 0 && afterTrauma == beforeTrauma-1
  let healAgain = healed {snapshotGame=(snapshotGame healed){gameQuestion=Map.singleton player ChooseUpgradeDeck}}
  (notHealedAgain,_) <- applyAnswer healAgain player (snapshotRevision healAgain) (ownRequest $ Just healing) >>= right
  assert "upgrade unchanged deck does not re-run added-card healing effect" $ field "physicalTrauma" (getActor notHealedAgain iid) == toJSON afterTrauma
  assert "upgrade official alternate art follows original same-investigator identity" $
    sameDeckInvestigator "01501" (fixtureValue $ setField "investigator_code" (String "01001") $ setField "meta" Null $ toJSON deck)
    && not (sameDeckInvestigator "01002" $ fixtureValue $ setField "investigator_code" (String "01001") $ setField "meta" Null $ toJSON deck)
  -- A deliberately minimal card-loading fixture, not a legal Build deck. It
  -- isolates official alternate-art ownership on actual generated main/side cards.
  minimal <- typed @ArkhamDBDecklist $ object ["investigator_code" .= ("01001" :: Text),"slots" .= object ["01006" .= (1 :: Int),"01007" .= (1 :: Int),"01088" .= (2 :: Int)],"sideSlots" .= object []]
  (aliasInitial,_) <- createSession (CreateRequest (Just "01") Nothing 201020 [player] Standard False False) >>= right
  (aliasLoaded,_) <- applyAnswer aliasInitial player 0 (DeckListAnswer minimal player) >>= right
  let aliasGame=snapshotGame aliasLoaded
      actualAliasTarget=fst $ fromJustNote "Roland alias fixture actor" $ listToMaybe $ Map.toList (gameEntities aliasGame).investigators
      aliasPending=aliasLoaded {snapshotGame=aliasGame {gameQuestion=Map.singleton player ChooseUpgradeDeck
        ,gameGameState=fixtureValue $ object ["tag" .= ("IsChooseDecks" :: Text),"contents" .= [player]]},snapshotQueue=[Ask player $ ChooseOne [Label "alias-finished" [Noop]]]}
  aliasDeck <- typed @ArkhamDBDecklist $ setField "investigator_code" (String "01501") $ setField "sideSlots" (object ["01089" .= (1 :: Int)]) $ toJSON minimal
  (aliasUpgraded,_) <- applyAnswer aliasPending player (snapshotRevision aliasPending) (UpgradeDeckAnswer $ UpgradeDeckRequest actualAliasTarget $ Just aliasDeck) >>= right
  let mainCards=field "decks" $ field "This" $ toJSON $ gameMode $ snapshotGame aliasUpgraded
      actorWithSide=getActor aliasUpgraded actualAliasTarget
      loadedMain=fromJustNote "alias main cards" $ Aeson.parseMaybe parseJSON $ field (fromJustNote "iid string" $ Aeson.parseMaybe parseJSON $ toJSON actualAliasTarget) mainCards :: [Value]
      loadedSide=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "sideDeck" actorWithSide) :: [Value]
  assert "upgrade alternate-art actual main cards retain original target owner" $ notNull loadedMain && all (\card -> field "owner" card == toJSON actualAliasTarget) loadedMain
  assert "upgrade alternate-art actual side cards retain original target owner" $ length loadedSide == 1 && all (\card -> field "owner" card == toJSON actualAliasTarget) loadedSide
  assert "upgrade alternate art never replaces actual investigator entity" $ Map.keys (gameEntities $ snapshotGame aliasUpgraded).investigators == [actualAliasTarget]

-- Controlled pending-window contracts on actual constructed native games.
-- These are not claims that every natural campaign window has been reached.
campaignContracts :: [ArkhamDBDecklist] -> Snapshot -> IO ()
campaignContracts decks original = do
  let game=snapshotGame original
      manager=fst $ fromJustNote "campaign continuation manager" $ find ((== ContinueCampaign) . unwrapQuestion . snd) $ Map.toList $ gameQuestion game
      other=fromJustNote "campaign other seat" $ find (/=manager) $ gamePlayers game
      actorFor g pid=fst $ fromJustNote "campaign owned actor" $ find (\(_,actor) -> field "playerId" (toJSON actor)==toJSON pid) $ Map.toList (gameEntities g).investigators
      managerIid=actorFor game manager
      otherIid=actorFor game other
      actors=(gameEntities game).investigators
      otherActor=fromJustNote "campaign other actor" $ Map.lookup otherIid actors
      participating=fixtureValue $ setField "xp" (toJSON (2 :: Int)) $ setField "physicalTrauma" (toJSON (1 :: Int)) $ toJSON otherActor
      entities=fixtureValue $ setField "investigators" (toJSON $ Map.insert otherIid participating actors) $ toJSON $ gameEntities game
      pending=original {snapshotGame=game {gameEntities=entities,gameActivePlayerId=other,gameActiveInvestigatorId=otherIid
        ,gameLeadInvestigatorId=managerIid,gamePlayerOrder=[managerIid,otherIid]}}
      newPlayer n=PlayerId $ fromJustNote "new campaign seat uuid" $ UUID.fromString $ "00000000-0000-4000-8000-00000000000"<>show n
      joinAnswer pid=JoinLocalCampaignAnswer $ LocalJoinRequest pid
      recipient snapshot=fst $ fromJustNote "campaign manager after operation" $ find ((== ContinueCampaign) . unwrapQuestion . snd) $ Map.toList $ gameQuestion $ snapshotGame snapshot
      rejected label source who answer = do
        let bytes=exportSnapshot source
        result <- applyAnswer source who (snapshotRevision source) answer
        assert ("campaign rejects " <> label) $ isLeft result && exportSnapshot source==bytes
      finish budget deck snapshot
        | any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = error "Bounded real join did not return to campaign continuation"
        | otherwise = do
            let who=fst $ fromJustNote "pending join subquestion" $ listToMaybe $ Map.toList $ gameQuestion $ snapshotGame snapshot
            answer <- chooseRealAnswer who deck snapshot
            (next,_) <- applyAnswer snapshot who (snapshotRevision snapshot) answer >>= right
            finish (budget-1) deck next
  assert "campaign shared metadata exposes true manager and capacity" $
    field "manager" (campaignView (snapshotGame pending) manager)==toJSON manager && field "canJoin" (campaignView (snapshotGame pending) manager)==Bool True
  assert "campaign other seat sees management read-only" $ field "canManage" (campaignView (snapshotGame pending) other)==Bool False
  rejected "other seat team mutation" pending other $ RetireInvestigatorAnswer managerIid
  rejected "nil new seat" pending manager $ joinAnswer $ PlayerId UUID.nil
  rejected "existing new seat" pending manager $ joinAnswer manager
  rejected "unknown active retire target" pending manager $ RetireInvestigatorAnswer "native-unknown"
  (retired,_) <- applyAnswer pending manager (snapshotRevision pending) (RetireInvestigatorAnswer otherIid) >>= right
  let rg=snapshotGame retired
  assert "campaign retirement removes actual player and active investigator" $ other `notElem` gamePlayers rg && Map.notMember otherIid (gameEntities rg).investigators && gamePlayerCount rg==1
  assert "campaign retirement preserves complete participating investigator attrs" $ fmap toJSON (Map.lookup otherIid $ gameRetiredInvestigators rg)==Just (toJSON participating)
  assert "campaign retiring old active does not resurrect absent player" $ gameActivePlayerId rg==manager && gameActiveInvestigatorId rg==managerIid
  assert "campaign retirement returns actual lead continuation" $ Map.lookup manager (gameQuestion rg)==Just ContinueCampaign
  rejected "last investigator cannot leave" retired manager $ RetireInvestigatorAnswer managerIid
  rejected "retired seat pid cannot be reused for new join" retired manager $ joinAnswer other
  rejected "unknown retired target" retired manager $ RejoinInvestigatorAnswer "native-unknown"
  (rejoined,_) <- applyAnswer retired manager (snapshotRevision retired) (RejoinInvestigatorAnswer otherIid) >>= right
  let rjg=snapshotGame rejoined
  assert "campaign rejoin restores original player and exact investigator attrs" $ other `elem` gamePlayers rjg && fmap toJSON (Map.lookup otherIid (gameEntities rjg).investigators)==Just (toJSON participating)
  assert "campaign rejoin increments count once and removes retired entry" $ gamePlayerCount rjg==2 && Map.notMember otherIid (gameRetiredInvestigators rjg)
  rejected "rejoining again" rejoined (recipient rejoined) $ RejoinInvestigatorAnswer otherIid
  let occupied=retired {snapshotGame=rg {gamePlayers=gamePlayers rg<>[other]}}
  rejected "retired original pid occupied" occupied manager $ RejoinInvestigatorAnswer otherIid
  (selfRetired,_) <- applyAnswer pending manager (snapshotRevision pending) (RetireInvestigatorAnswer managerIid) >>= right
  assert "campaign manager leaving transfers lead active and real question" $
    gameLeadInvestigatorId (snapshotGame selfRetired)==otherIid && gameActivePlayerId (snapshotGame selfRetired)==other
    && Map.lookup other (gameQuestion $ snapshotGame selfRetired)==Just ContinueCampaign
  let p3=newPlayer (3 :: Int);p4=newPlayer (4 :: Int);p5=newPlayer (5 :: Int)
  (joining,_) <- applyAnswer pending manager (snapshotRevision pending) (joinAnswer p3) >>= right
  let jg=snapshotGame joining
  assert "campaign join creates distinct actual player and count before deck selection" $ p3 `elem` gamePlayers jg && gamePlayerCount jg==3 && Map.size (gameEntities jg).investigators==2
  assert "campaign join asks actual new seat ChooseJoinDeck" $ case unwrapQuestion <$> Map.lookup p3 (gameQuestion jg) of Just ChooseJoinDeck {} -> True; _ -> False
  rejected "used investigator when joining" joining p3 $ DeckListAnswer (headEx decks) p3
  let third=fromJustNote "third distinct preset" $ decks !!? 2
      fourth=fromJustNote "fourth distinct preset" $ decks !!? 3
  (loadedThird,_) <- applyAnswer joining p3 (snapshotRevision joining) (DeckListAnswer third p3) >>= right
  three <- finish 12 third loadedThird
  assert "campaign new seat barrier resolves all real setup and returns manager" $ length (gamePlayers $ snapshotGame three)==3 && Map.size (gameEntities $ snapshotGame three).investigators==3
  (joiningFourth,_) <- applyAnswer three (recipient three) (snapshotRevision three) (joinAnswer p4) >>= right
  (loadedFourth,_) <- applyAnswer joiningFourth p4 (snapshotRevision joiningFourth) (DeckListAnswer fourth p4) >>= right
  four <- finish 12 fourth loadedFourth
  assert "campaign fourth seat joins through actual core barrier" $ length (gamePlayers $ snapshotGame four)==4 && gamePlayerCount (snapshotGame four)==4 && Map.size (gameEntities $ snapshotGame four).investigators==4
  rejected "fifth local seat" four (recipient four) $ joinAnswer p5
  assert "campaign full roster metadata disables join" $ field "canJoin" (campaignView (snapshotGame four) $ recipient four)==Bool False
  restored <- importSnapshot (exportSnapshot pending) >>= right
  (joiningReplay,_) <- applyAnswer restored manager (snapshotRevision restored) (joinAnswer p3) >>= right
  assert "campaign join restored execution exact whole Game queue integer RNG" $ exportSnapshot joiningReplay==exportSnapshot joining
  stale <- applyAnswer pending manager (snapshotRevision pending+1) (joinAnswer p3)
  assert "campaign stale join rejects before any rules mutation" $ isLeft stale
  assert "campaign used-investigator check includes official alternate art" $ isJust $ joinDeckRejection
    (game {gameQuestion=Map.singleton manager $ ChooseJoinDeck ["01001"]}) manager "01501"
  standaloneRequest <- typed @CreateRequest $ object ["scenario" .= ("01104" :: Text),"seed" .= (201020 :: Int),"players" .= [manager]]
  (standalone,_) <- createSession standaloneRequest >>= right
  let standaloneContinue=standalone {snapshotGame=(snapshotGame standalone){gameQuestion=Map.singleton manager ContinueCampaign}}
  rejected "standalone scenario masquerading as campaign manager" standaloneContinue manager $ joinAnswer p3
  -- Controlled replacement question on the real campaign; not natural death.
  let replacementPending=pending {snapshotGame=(snapshotGame pending){gameQuestion=Map.fromList [(manager,ChooseUpgradeDeck),(other,ChooseUpgradeDeck)]
        ,gameGameState=fixtureValue $ object ["tag" .= ("IsChooseDecks" :: Text),"contents" .= [manager,other]]},snapshotQueue=[Ask manager $ ChooseOne [Label "replacement-finished" [Noop]]]}
      replacement=ReplaceInvestigatorAnswer $ UpgradeDeckRequest managerIid $ Just third
  (replaced,_) <- applyAnswer replacementPending manager (snapshotRevision replacementPending) replacement >>= right
  let newIid=actorFor (snapshotGame replaced) manager
  assert "campaign replacement keeps player but installs distinct actual investigator" $ newIid/=managerIid && gamePlayers (snapshotGame replaced)==gamePlayers (snapshotGame pending)
  assert "campaign replacement repairs actual lead reference" $ gameLeadInvestigatorId (snapshotGame replaced)==newIid
  assert "campaign replacement repairs actual player order references" $ gamePlayerOrder (snapshotGame replaced)==[newIid,otherIid]
  assert "campaign replacement never overwrites other investigator" $ fmap toJSON (Map.lookup otherIid (gameEntities $ snapshotGame replaced).investigators)==Just (toJSON participating)
  let ownDeck=fromJustNote "manager original preset" $ find (sameDeckInvestigator managerIid) decks
      otherDeck=fromJustNote "other original preset" $ find (sameDeckInvestigator otherIid) decks
  rejected "replacement of same investigator must use upgrade" replacementPending manager $ ReplaceInvestigatorAnswer $ UpgradeDeckRequest managerIid $ Just ownDeck
  rejected "replacement overwriting another active investigator" replacementPending manager $ ReplaceInvestigatorAnswer $ UpgradeDeckRequest managerIid $ Just otherDeck
  rejected "replacement without full deck" replacementPending manager $ ReplaceInvestigatorAnswer $ UpgradeDeckRequest managerIid Nothing
  let retiredCollision=replacementPending {snapshotGame=(snapshotGame replacementPending){gameRetiredInvestigators=Map.singleton third.investigator (fromJustNote "replacement actor" $ Map.lookup newIid (gameEntities $ snapshotGame replaced).investigators)}}
  rejected "replacement cannot initialise a retired identity" retiredCollision manager replacement
  (replaceReplay,_) <- applyAnswer replacementPending manager (snapshotRevision replacementPending) replacement >>= right
  assert "campaign replacement deterministic full Game queue RNG" $ exportSnapshot replaced==exportSnapshot replaceReplay
  let history snapshot=fixtureValue @[InvestigatorId] $ field "replacedInvestigators" $ field "This" $ toJSON $ gameMode $ snapshotGame snapshot
  assert "campaign replacement stamps old identity independently of removed playable deck" $ managerIid `elem` history replaced && isNothing (campaignDeck (snapshotGame replaced) managerIid)
  historyRestored <- importSnapshot (exportSnapshot replaced) >>= right
  assert "campaign replaced identity survives exact snapshot roundtrip" $ history historyRestored==history replaced && exportSnapshot historyRestored==exportSnapshot replaced
  let historyContinue=replaced {snapshotGame=(snapshotGame replaced){gameQuestion=Map.singleton manager ContinueCampaign},snapshotQueue=[]}
  (historyJoining,_) <- applyAnswer historyContinue manager (snapshotRevision historyContinue) (joinAnswer p3) >>= right
  assert "campaign actual joining metadata includes replaced identity" $ managerIid `elem` (fixtureValue @[InvestigatorId] $ field "usedInvestigators" $ deckView (snapshotGame historyJoining) p3)
  rejected "joining previously replaced investigator" historyJoining p3 $ DeckListAnswer ownDeck p3
  let staleJoin=historyJoining {snapshotGame=(snapshotGame historyJoining){gameQuestion=Map.singleton p3 $ ChooseJoinDeck []}}
  rejected "stale joining question cannot omit durable replacement history" staleJoin p3 $ DeckListAnswer ownDeck p3
  assert "campaign stale joining metadata uses same durable replacement history" $ managerIid `elem` (fixtureValue @[InvestigatorId] $ field "usedInvestigators" $ deckView (snapshotGame staleJoin) p3)
  let secondPending=replaced {snapshotGame=(snapshotGame replaced){gameQuestion=Map.fromList [(manager,ChooseUpgradeDeck),(other,ChooseUpgradeDeck)]
        ,gameGameState=fixtureValue $ object ["tag" .= ("IsChooseDecks" :: Text),"contents" .= [manager,other]]}
        ,snapshotQueue=[Ask manager $ ChooseOne [Label "history-chain-finished" [Noop]]]}
      fourthHistory=fromJustNote "fourth distinct history preset" $ decks !!? 3
  rejected "replacement cannot return to previously replaced investigator" secondPending manager $ ReplaceInvestigatorAnswer $ UpgradeDeckRequest newIid $ Just ownDeck
  (chain,_) <- applyAnswer secondPending manager (snapshotRevision secondPending) (ReplaceInvestigatorAnswer $ UpgradeDeckRequest newIid $ Just fourthHistory) >>= right
  assert "campaign replacement chain retains both earlier identities" $ all (`elem` history chain) [managerIid,newIid] && length (history chain)==2
  chainRestored <- importSnapshot (exportSnapshot secondPending) >>= right
  (chainReplay,_) <- applyAnswer chainRestored manager (snapshotRevision chainRestored) (ReplaceInvestigatorAnswer $ UpgradeDeckRequest newIid $ Just fourthHistory) >>= right
  assert "campaign replacement chain replay exact Game queue RNG and history" $ exportSnapshot chainReplay==exportSnapshot chain
  let withRolandHistory=historyJoining {snapshotGame=(snapshotGame historyJoining){gameMode=fixtureValue $ setField "This"
        (setField "replacedInvestigators" (toJSON (["01001"] :: [InvestigatorId])) $ field "This" $ toJSON $ gameMode $ snapshotGame historyJoining) $ toJSON $ gameMode $ snapshotGame historyJoining}}
  for_ ["01001","01501","98004"] \alias ->
    assert "campaign durable history rejects official identity alias" $ isJust $ joinDeckRejection (snapshotGame withRolandHistory) p3 alias
  assert "campaign history does not infer parallel identity from display name" $ not $ sameInvestigatorIdentity "01001" "90024"
  let attrs=field "This" $ toJSON $ gameMode $ snapshotGame replaced
      withoutHistory=case attrs of Object fields -> Object $ KM.delete "replacedInvestigators" fields; _ -> error "Campaign attrs object required"
      legacyMode=fixtureValue @GameMode $ setField "This" withoutHistory $ toJSON $ gameMode $ snapshotGame replaced
  assert "legacy CampaignAttrs parser defaults missing replacement field empty not reconstructed" $ field "replacedInvestigators" (field "This" $ toJSON legacyMode)==toJSON ([] :: [InvestigatorId])
  let historyScenario=field "That" $ toJSON $ gameMode $ snapshotGame standalone
      theseHistory=withRolandHistory {snapshotGame=(snapshotGame withRolandHistory){gameMode=fixtureValue $ object
        ["This" .= field "This" (toJSON $ gameMode $ snapshotGame withRolandHistory),"That" .= historyScenario]}}
      theseReplacement=secondPending {snapshotGame=(snapshotGame secondPending){gameMode=fixtureValue $ object
        ["This" .= field "This" (toJSON $ gameMode $ snapshotGame secondPending),"That" .= historyScenario]}}
  assert "These mode joining uses actual campaign replacement history" $ isJust $ joinDeckRejection (snapshotGame theseHistory) p3 "01501"
  rejected "These mode replacement cannot reuse prior investigator" theseReplacement manager $ ReplaceInvestigatorAnswer $ UpgradeDeckRequest newIid $ Just ownDeck
  assert "retirement and explicit rejoin do not invent replacement history" $ null (history retired) && null (history rejoined)
  -- Two actual initial decks without the purchase-trauma permanent. This is
  -- a bounded card-loading fixture, not a complete deck-building legality test.
  let withoutPurchase deck=fixtureValue @ArkhamDBDecklist $ setField "slots"
        (case field "slots" (toJSON deck) of Object values -> Object $ KM.delete "c08125" $ KM.delete "08125" values; _ -> error "Slots object required") $ toJSON deck
      cleanDeck=withoutPurchase ownDeck
      cleanOtherDeck=withoutPurchase otherDeck
      settleUnused budget snapshot
        | any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot)=pure snapshot
        | budget <= (0 :: Int)=error "Unused investigator fixture did not reach continuation"
        | otherwise=do
            let who=fst $ fromJustNote "unused fixture actual question" $ listToMaybe $ Map.toList $ gameQuestion $ snapshotGame snapshot
            operation <- chooseRealAnswer who (if who==manager then cleanDeck else cleanOtherDeck) snapshot
            (next,_) <- applyAnswer snapshot who (snapshotRevision snapshot) operation >>= right
            settleUnused (budget-1) next
  (unusedStart,_) <- createSession (CreateRequest (Just "01") Nothing 201021 [manager,other] Standard False False) >>= right
  unusedReady <- settleUnused 20 unusedStart
  let unusedIid=actorFor (snapshotGame unusedReady) manager
  (unusedLeft,_) <- applyAnswer unusedReady (recipient unusedReady) (snapshotRevision unusedReady) (RetireInvestigatorAnswer unusedIid) >>= right
  assert "unused investigator leaves without retirement or replacement history" $ Map.notMember unusedIid (gameRetiredInvestigators $ snapshotGame unusedLeft) && unusedIid `notElem` history unusedLeft && isNothing (campaignDeck (snapshotGame unusedLeft) unusedIid)
  (unusedJoin,_) <- applyAnswer unusedLeft (recipient unusedLeft) (snapshotRevision unusedLeft) (joinAnswer p3) >>= right
  assert "unused leaver may be chosen by a new seat without a false historical ban" $ isNothing $ joinDeckRejection (snapshotGame unusedJoin) p3 unusedIid
  (unusedReloaded,_) <- applyAnswer unusedJoin p3 (snapshotRevision unusedJoin) (DeckListAnswer cleanDeck p3) >>= right
  assert "unused investigator actual new seat reload succeeds" $ actorFor (snapshotGame unusedReloaded) p3==unusedIid
  -- Finite campaign overlay, intentionally separate from purchase/upgrade.
  let overlayPayload changes=object ["tag" .= ("ApplyOverlayAnswer" :: Text),"investigatorId" .= managerIid,"overlay" .= changes]
      overlayBase=object ["investigator" .= Null,"swaps" .= object [],"add" .= object ["11098" .= (1 :: Int)],"remove" .= object []]
  overlay <- typed @Answer $ overlayPayload overlayBase
  (overlaid,_) <- applyAnswer pending manager (snapshotRevision pending) overlay >>= right
  let beforeActor=toJSON $ fromJustNote "overlay old actor" $ Map.lookup managerIid (gameEntities $ snapshotGame pending).investigators
      afterActor=toJSON $ fromJustNote "overlay new actor" $ Map.lookup managerIid (gameEntities $ snapshotGame overlaid).investigators
  assert "campaign custom overlay is not purchase healing or XP mutation" $ beforeActor==afterActor
  let overlayCards=fromJustNote "overlay deck array" (Aeson.parseMaybe parseJSON $ toJSON $ campaignDeck (snapshotGame overlaid) managerIid) :: [Value]
      healingCards=filter (\card -> field "cardCode" card==String "c11098") overlayCards
  assert "campaign overlay actual added card has target owner" $ length healingCards==1 && field "owner" (headEx healingCards)==toJSON managerIid
  for_ [("unknown add",setField "add" (object ["native-unknown" .= (1 :: Int)]) overlayBase)
       ,("negative add",setField "add" (object ["11098" .= (-1 :: Int)]) overlayBase)
       ,("excessive add",setField "add" (object ["11098" .= (1000000 :: Int)]) overlayBase)
       ,("investigator replacement field",setField "investigator" (toJSON otherIid) overlayBase)
       ,("missing swap source",setField "swaps" (object ["native-unknown" .= ("01088" :: Text)]) overlayBase)
       ,("missing removal",setField "remove" (object ["native-unknown" .= (1 :: Int)]) overlayBase)] \(label,bad) -> do
    answer <- typed @Answer $ overlayPayload bad
    rejected ("overlay "<>label) pending manager answer
  rejected "other seat cannot edit team overlay" pending other overlay
  emptyOverlay <- typed @Answer $ overlayPayload $ object []
  rejected "empty custom overlay" pending manager emptyOverlay
  assert "campaign overlay rejects unknown message fields" $ isLeft $ Aeson.parseEither (parseJSON @Answer)
    $ overlayPayload $ setField "messages" (toJSON [object ["tag" .= ("Noop" :: Text)]]) overlayBase
  let deckCards snapshot=fromJustNote "actual campaign deck list" (Aeson.parseMaybe parseJSON $ toJSON $ campaignDeck (snapshotGame snapshot) managerIid) :: [Value]
      countCode code snapshot=length $ filter (\card -> field "cardCode" card==String code) $ deckCards snapshot
      applyOverlay snapshot changes=do
        answer <- typed @Answer $ overlayPayload changes
        fst <$> (applyAnswer snapshot manager (snapshotRevision snapshot) answer >>= right)
      plainOverlay adds removes swaps=object ["add" .= adds,"remove" .= removes,"swaps" .= swaps]
      initialGuts=countCode "c01088" pending
  primed <- applyOverlay pending $ plainOverlay (object ["01088" .= (100-initialGuts)]) (object []) (object [])
  assert "campaign overlay bounded fixture reaches exactly one hundred copies" $ countCode "c01088" primed==100
  removeThenAdd <- applyOverlay primed $ plainOverlay (object ["01088" .= (100 :: Int)]) (object ["01088" .= (100 :: Int)]) (object [])
  assert "campaign overlay validates actual remove-before-add result not false two hundred" $ countCode "c01088" removeThenAdd==100
  let aCount=countCode "c01088" pending;bCount=countCode "c01089" pending;cCount=countCode "c01090" pending
  prepared <- applyOverlay pending $ plainOverlay (object ["01088" .= (2 :: Int),"01089" .= (3 :: Int)]) (object []) (object [])
  chained <- applyOverlay prepared $ plainOverlay (object []) (object []) (object ["01088" .= ("01089" :: Text),"01089" .= ("01090" :: Text)])
  assert "campaign overlay simultaneous swaps never re-swap newly added cards" $
    countCode "c01088" chained==0 && countCode "c01089" chained==aCount+2 && countCode "c01090" chained==cCount+bCount+3
  facePrepared <- applyOverlay pending $ plainOverlay (object ["08011a" .= (1 :: Int)]) (object []) (object [])
  faceSwapped <- applyOverlay facePrepared $ plainOverlay (object []) (object []) (object ["08011b" .= ("01088" :: Text)])
  assert "campaign overlay shared CardCode Eq plan accepts paired face source" $
    countCode "c08011a" faceSwapped==0 && countCode "c01088" faceSwapped==aCount+1
  let tabooActors=Map.adjust (\actor -> fixtureValue $ setField "taboo" (String "TabooList24") $ toJSON actor) managerIid (gameEntities $ snapshotGame pending).investigators
      tabooEntities=fixtureValue $ setField "investigators" (toJSON tabooActors) $ toJSON $ gameEntities $ snapshotGame pending
      tabooPending=pending {snapshotGame=(snapshotGame pending){gameEntities=tabooEntities}}
  tabooOverlaid <- applyOverlay tabooPending $ plainOverlay (object ["01088" .= (1 :: Int)]) (object []) (object [])
  let generated=[card | card <- deckCards tabooOverlaid,field "cardCode" card==String "c01088",field "tabooList" card==String "TabooList24"]
  assert "campaign overlay new card inherits actual investigator taboo and owner" $ length generated==1 && field "owner" (headEx generated)==toJSON managerIid
  overlayRestored <- importSnapshot (exportSnapshot pending) >>= right
  (overlayReplay,_) <- applyAnswer overlayRestored manager (snapshotRevision overlayRestored) overlay >>= right
  assert "campaign overlay restored execution exact Game queue integer RNG" $ exportSnapshot overlayReplay==exportSnapshot overlaid
  assert "campaign management metadata is readonly" $ exportSnapshot pending==exportSnapshot restored
  -- Explicit checkpoint after a completed scenario, not a natural playthrough.
  let originalMode=toJSON $ gameMode $ snapshotGame pending
      campaignJson=field "This" originalMode
      nextScenario=object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c01120" :: Text)]
      continuation=object ["nextStep" .= nextScenario,"canUpgradeDecks" .= True,"chooseSideStory" .= False,"lead" .= managerIid,"canChooseSideStory" .= True]
      continueStep=object ["tag" .= ("ContinueCampaignStep" :: Text),"contents" .= continuation]
      newCampaign=setField "completedSteps" (toJSON [object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c01104" :: Text)]]) $ setField "step" continueStep campaignJson
      between=pending {snapshotGame=(snapshotGame pending){gameMode=fixtureValue $ object ["This" .= newCampaign]},snapshotQueue=[]}
  assert "campaign begin upgrade metadata uses same offered qualification" $ field "canUpgrade" (campaignView (snapshotGame between) manager)==Bool True
  beginReply <- handleAnswerPure (snapshotGame between) manager BeginUpgradeAnswer
  assert "campaign begin upgrade constructs exact continuation wrapper in core" $ case beginReply of
    Handled [message] -> encode message==encode (NextCampaignStep $ Just $ fixtureValue $ object ["tag" .= ("UpgradeDeckStep" :: Text),"contents" .= continueStep])
    _ -> False
  (upgrading,_) <- applyAnswer between manager (snapshotRevision between) BeginUpgradeAnswer >>= right
  assert "campaign begin upgrade reaches actual two seat ChooseUpgradeDeck" $ Map.size (gameQuestion $ snapshotGame upgrading)==2 && all ((== ChooseUpgradeDeck) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame upgrading)
  let keepEveryone budget snapshot
        | any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot)=pure snapshot
        | budget <= (0 :: Int)=error "Keep all did not restore continuation"
        | otherwise=do
            let who=fst $ fromJustNote "actual upgrade recipient" $ listToMaybe $ Map.toList $ gameQuestion $ snapshotGame snapshot
            target <- right $ upgradeInvestigator (snapshotGame snapshot) who
            (next,_) <- applyAnswer snapshot who (snapshotRevision snapshot) (UpgradeDeckAnswer $ UpgradeDeckRequest target Nothing) >>= right
            keepEveryone (budget-1) next
  returned <- keepEveryone 6 upgrading
  assert "campaign keeping all decks returns exact complete continuation not next scenario" $ field "step" (field "This" $ toJSON $ gameMode $ snapshotGame returned)==continueStep
  rejected "manual upgrading before first scenario" pending manager BeginUpgradeAnswer
  let noHistory=between {snapshotGame=(snapshotGame between){gameMode=fixtureValue $ object ["This" .= setField "completedSteps" (toJSON ([] :: [Value])) newCampaign]}}
  rejected "upgrade flag alone before first scenario insufficient" noHistory manager BeginUpgradeAnswer
  rejected "other seat beginning upgrade" between other BeginUpgradeAnswer
  let deniedCampaign=setField "step" (setField "contents" (setField "canUpgradeDecks" (Bool False) continuation) continueStep) newCampaign
      denied=between {snapshotGame=(snapshotGame between){gameMode=fixtureValue $ object ["This" .= deniedCampaign]}}
  rejected "campaign disabled upgrade flag" denied manager BeginUpgradeAnswer
  rejected "standalone cannot begin campaign upgrade" standaloneContinue manager BeginUpgradeAnswer
  let scenarioJson=field "That" $ toJSON $ gameMode $ snapshotGame standalone
      scenarioFirst=between {snapshotGame=(snapshotGame between){gameMode=fixtureValue $ object ["This" .= newCampaign,"That" .= setField "campaignStep" continueStep scenarioJson]}}
  rejected "scenario continuation takes priority over campaign upgrade" scenarioFirst manager BeginUpgradeAnswer

triggerContracts :: PlayerId -> PlayerId -> Snapshot -> Snapshot -> IO ()
triggerContracts player other single team = do
  let iid = fromJustNote "trigger actor" $ listToMaybe $ Map.keys (gameEntities $ snapshotGame single).investigators
      tag name = object ["tag" .= (name :: Text)]
      ab kind index = fixtureValue $ object
        ["source" .= object ["tag" .= ("InvestigatorSource" :: Text),"contents" .= iid]
        ,"cardCode" .= ("c01033" :: Text),"index" .= (index :: Int),"type" .= kind
        ,"limit" .= tag "NoLimit","window" .= tag "AnyWindow","criteria" .= tag "NoRestriction"
        ,"doesNotProvokeAttacksOfOpportunity" .= Null,"canBeCancelled" .= True,"basic" .= False,"additionalCosts" .= ([] :: [Value])]
      optional = AbilityLabel iid (ab (object ["tag" .= ("FastAbility'" :: Text),"cost" .= tag "Free"]) 1) [] [] []
      optional2 = AbilityLabel iid (ab (object ["tag" .= ("ReactionAbility" :: Text),"window" .= tag "AnyWindow"]) 2) [] [] []
      skip = SkipTriggersButton iid
      basis choices = single {snapshotGame=(snapshotGame single){gameQuestion=Map.singleton player $ WindowChooseOne choices,gameRetainedQuestion=False},snapshotQueue=[Ask player $ ChooseOne [Label "trigger-sentinel" [Noop]]]}
      pending = basis [Info mempty,optional,InvalidLabel "unavailable",skip]
      pref code index mode = fixtureValue @Answer $ object ["tag" .= ("SetTriggerModeAnswer" :: Text),"contents" .= object
        ["investigatorId" .= iid,"cardCode" .= (code :: Text),"abilityIndex" .= (index :: Int),"mode" .= (mode :: Text)]]
      saveMode snapshot mode = applyAnswer snapshot player (snapshotRevision snapshot) (pref "c01033" 1 mode) >>= right
      view snapshot = boundary (gameCustomCards $ snapshotGame snapshot) (triggerView (snapshotGame snapshot) player) >>= right
      autoIndex snapshot = field "automaticSkipIndex" <$> view snapshot
      rejected label snapshot who revision answer = do
        let old = exportSnapshot snapshot
        result <- applyAnswer snapshot who revision answer
        assert ("trigger rejects " <> label) $ isLeft result && exportSnapshot snapshot == old
  let original = exportSnapshot pending
  registry <- readIORef customCardRegistry
  firstView <- view pending
  secondView <- view pending
  assert "trigger default is ask without automatic skip" $ field "automaticSkipIndex" firstView == Null
  assert "trigger readonly context preserves whole input queue RNG and registry" . (&& exportSnapshot pending == original && firstView == secondView) . (== registry) =<< readIORef customCardRegistry
  (stopped,events) <- saveMode pending "AbilityAutoSkip"
  assert "trigger preference increments revision once with no events" $ snapshotRevision stopped == snapshotRevision pending+1 && null events
  assert "trigger preference retains exact pending question queue and RNG" $
    gameQuestion (snapshotGame stopped) == gameQuestion (snapshotGame pending) && snapshotQueue stopped == snapshotQueue pending && gameSeed (snapshotGame stopped) == gameSeed (snapshotGame pending)
  let restoreSettings candidate =
        let game = snapshotGame candidate
            entities = gameEntities game
            oldActors = (gameEntities $ snapshotGame pending).investigators
            reset key actor = setField "settings" (field "settings" $ toJSON $ fromJustNote "old actor" $ Map.lookup key oldActors) $ toJSON actor
         in game {gameEntities=fixtureValue $ setField "investigators" (toJSON $ Map.mapWithKey reset entities.investigators) $ toJSON entities}
  assert "trigger preference changes only specified investigator settings in whole Game" $ encode (restoreSettings stopped) == encode (snapshotGame pending)
  assert "trigger stop never hides the manual optional choice" $ gameQuestion (snapshotGame stopped) == gameQuestion (snapshotGame pending)
  assert "trigger actual original skip index includes Info Invalid offsets" . (== toJSON (3 :: Int)) =<< autoIndex stopped
  rejected "duplicate mode" stopped player (snapshotRevision stopped) $ pref "c01033" 1 "AbilityAutoSkip"
  rejected "wrong source" stopped player (snapshotRevision stopped) $ pref "c01034" 1 "AbilityAlwaysAsk"
  rejected "wrong face" stopped player (snapshotRevision stopped) $ pref "c01033b" 1 "AbilityAlwaysAsk"
  rejected "wrong index" stopped player (snapshotRevision stopped) $ pref "c01033" 99 "AbilityAlwaysAsk"
  rejected "wrong seat" stopped other (snapshotRevision stopped) $ pref "c01033" 1 "AbilityAlwaysAsk"
  rejected "stale setting" stopped player (snapshotRevision pending) $ pref "c01033" 1 "AbilityAlwaysAsk"
  for_ [ ("no skip",[optional]),("duplicate skip",[optional,skip,skip]),("only skip",[skip])
       , ("unknown actionable",[optional,Label "ordinary" [Noop],skip])
       , ("mixed ask",[optional,optional2,skip])
       , ("forced",[AbilityLabel iid (ab (object ["tag" .= ("ForcedAbility" :: Text),"window" .= tag "AnyWindow"]) 3) [] [] [],skip])
       , ("action",[AbilityLabel iid (ab (object ["tag" .= ("ActionAbility" :: Text),"actions" .= ([] :: [Value]),"cost" .= tag "Free"]) 4) [] [] [],skip])
       ] \(label,choices) -> do
        let candidate = stopped {snapshotGame=(snapshotGame stopped){gameQuestion=Map.singleton player $ WindowChooseOne choices}}
        assert ("trigger blocks automatic " <> label) . (== Null) =<< autoIndex candidate
        rejected ("automatic " <> label) candidate player (snapshotRevision candidate) $ AutoSkipTriggersAnswer $ length choices-1
  let payment = stopped {snapshotGame=(snapshotGame stopped){gameQuestion=Map.singleton player $ ChooseOne [optional,skip]}}
  assert "trigger nonwindow question never auto submits" . (== Null) =<< autoIndex payment
  rejected "wrong auto index" stopped player (snapshotRevision stopped) $ AutoSkipTriggersAnswer 1
  (skipped,_) <- applyAnswer stopped player (snapshotRevision stopped) (AutoSkipTriggersAnswer 3) >>= right
  assert "trigger automatic answer executes real Skip and reaches queued sentinel" $ Map.lookup player (gameQuestion $ snapshotGame skipped) == Just (ChooseOne [Label "trigger-sentinel" [Noop]]) && snapshotRevision skipped == snapshotRevision stopped+1
  let removed = stopped {snapshotGame=(snapshotGame stopped){gameQuestion=mempty}}
  (changedNoQuestion,_) <- saveMode removed "AbilityAlwaysAsk"
  assert "trigger recorded preference editable without consuming a question" $ null (gameQuestion $ snapshotGame changedNoQuestion) && snapshotQueue changedNoQuestion == snapshotQueue removed
  (ownerOnly,_) <- saveMode stopped "AbilityOwnerOnly"
  let ownActive = ownerOnly {snapshotGame=(snapshotGame ownerOnly){gameActiveInvestigatorId=iid}}
  assert "trigger owner mode asks for own active investigator" . (== Null) =<< autoIndex ownActive
  let teamGame = snapshotGame team
      actorFor p = fst <$> find (\(_,actor) -> field "playerId" (toJSON actor) == toJSON p) (Map.toList $ (gameEntities teamGame).investigators)
      theirs = fromJustNote "other actual teammate" $ actorFor other
      mergedActors = Map.union (gameEntities $ snapshotGame ownerOnly).investigators (gameEntities teamGame).investigators
      otherActive = ownerOnly {snapshotGame=(snapshotGame ownerOnly){gamePlayers=gamePlayers teamGame,gameEntities=fixtureValue $ setField "investigators" (toJSON mergedActors) $ toJSON (gameEntities teamGame),gameActiveInvestigatorId=theirs}}
  assert "trigger owner mode skips only when another investigator is active" . (== toJSON (3 :: Int)) =<< autoIndex otherActive
  rejected "existing other seat cannot set actor preference" otherActive other (snapshotRevision otherActive) $ pref "c01033" 1 "AbilityAlwaysAsk"
  rejected "existing other seat cannot execute actor skip" otherActive other (snapshotRevision otherActive) $ AutoSkipTriggersAnswer 3
  for_ [ setField "extra" (Bool True) $ field "contents" $ object ["contents" .= object ["investigatorId" .= iid,"cardCode" .= ("c01033" :: Text),"abilityIndex" .= (1 :: Int),"mode" .= ("AbilityAutoSkip" :: Text)]]
       , object ["investigatorId" .= iid,"cardCode" .= ("cc01033" :: Text),"abilityIndex" .= (1 :: Int),"mode" .= ("AbilityAutoSkip" :: Text)]
       , object ["investigatorId" .= iid,"cardCode" .= ("c01033" :: Text),"abilityIndex" .= (1 :: Int),"mode" .= ("UnknownMode" :: Text)]
       ] \payload -> assert "trigger strict finite wire payload rejects extra normalization and unknown mode" $ isLeft $ Aeson.parseEither (parseJSON @Answer) $ object ["tag" .= ("SetTriggerModeAnswer" :: Text),"contents" .= payload]
  assert "trigger earlier snapshot remains immutable after all operations" $ exportSnapshot pending == original

fastPlayContracts :: PlayerId -> Snapshot -> Snapshot -> IO ()
fastPlayContracts player single team = do
  let iid = fromJustNote "fast actor" $ listToMaybe $ Map.keys (gameEntities $ snapshotGame single).investigators
      tag name = object ["tag" .= (name :: Text)]
      args name value = object ["tag" .= (name :: Text),"contents" .= value]
      readyActor actor = setField "remainingActions" (toJSON (3 :: Int)) $ setField "tokens" (toJSON [("Resource" :: Text,5 :: Int)]) $ toJSON actor
      game0 = snapshotGame single
      game = game0 {gameEntities=fixtureValue $ setField "investigators" (toJSON $ Map.map readyActor (gameEntities game0).investigators) $ toJSON (gameEntities game0),gameActiveInvestigatorId=iid}
      during = [fixtureValue $ object ["windowTiming" .= ("When" :: Text),"windowType" .= args "DuringTurn" (toJSON iid)]]
      nonfast = [fixtureValue $ object ["windowTiming" .= ("When" :: Text),"windowType" .= tag "NonFast"]]
      fastWindow = [fixtureValue $ object ["windowTiming" .= ("When" :: Text),"windowType" .= tag "FastPlayerWindow"]]
      card code = fixtureValue $ args "PlayerCard" $ object ["id" .= unPlayerId player,"owner" .= iid,"cardCode" .= (code :: Text),"originalCardCode" .= code,"customizations" .= ([] :: [Value])]
      ordinary = card "c01088"
      printed = card "c02266"
      register state target = state {gameCards=Map.insert (fixtureValue $ toJSON $ unPlayerId player) target $ gameCards state}
      anyCard = tag "AnyCard"
      matcherMismatch = args "CardWithCardCode" $ toJSON ("c01021" :: Text)
      becomes = fixtureValue $ args "BecomesFast" $ tag "AnyWindow"
      canFast = fixtureValue $ args "CanBecomeFast" anyCard
      chuck = fixtureValue $ args "ChuckFergus2Modifier" $ toJSON [anyCard,toJSON (2 :: Int)]
      context :: [ModifierType] -> ModifierType
      context mods = fixtureValue $ args "PlayableModifierContexts" $ toJSON [(anyCard,toJSON mods)]
      evaluateCase label state target who windows actorMods cardMods expected = do
        env <- makeEnv (register state target) []
        before <- finish Nothing 0 env
        (summary,legacy) <- runCore env $ withModifiersOf who who actorMods $ withModifiersOf target who cardMods do
          summary <- getCardPlayability who who (fixtureValue $ args "UnpaidCost" $ toJSON ("NeedsAction" :: Text)) windows target
          legacy <- getIsPlayable who who (fixtureValue $ args "UnpaidCost" $ toJSON ("NeedsAction" :: Text)) windows target
          pure (summary,legacy)
        after <- finish Nothing 0 env
        assert ("fast shared " <> label) $ (playabilityPlayable summary,playabilityFast summary) == expected && legacy == fst expected
        assert ("fast readonly " <> label) $ exportSnapshot (fst before) == exportSnapshot (fst after) && null (snd before) && null (snd after)
      normal label windows = evaluateCase label game ordinary iid windows [] [] (True,False)
  normal "ordinary DuringTurn is not Fast" during
  normal "ordinary NonFast is not Fast" nonfast
  evaluateCase "printed Fast actual window" game printed iid during [] [] (True,True)
  evaluateCase "printed Fast wrong window" game printed iid fastWindow [] [] (False,False)
  evaluateCase "card BecomesFast" game ordinary iid fastWindow [] [becomes] (True,True)
  evaluateCase "investigator CanBecomeFast" game ordinary iid during [canFast] [] (True,True)
  evaluateCase "investigator CanBecomeFast mismatch" game ordinary iid during [fixtureValue $ args "CanBecomeFast" matcherMismatch] [] (True,False)
  evaluateCase "matching context makes card legal Fast" game ordinary iid fastWindow [context [becomes]] [] (True,True)
  evaluateCase "ordinary legal base cannot borrow failed Fast context" game ordinary iid during [context [becomes,fixtureValue $ args "AdditionalCost" $ args "ResourceCost" $ toJSON (99 :: Int)]] [] (True,False)
  evaluateCase "ignored modifier contexts" game ordinary iid fastWindow [context [becomes],IgnorePlayableModifierContexts] [] (False,False)
  evaluateCase "Chuck affordable uses dynamic Fast" game ordinary iid during [chuck] [] (True,True)
  let taboo value =
        let actors = (gameEntities game).investigators
            updated = Map.adjust (\actor -> fixtureValue $ setField "taboo" value $ toJSON actor) iid actors
         in game {gameEntities=fixtureValue $ setField "investigators" (toJSON updated) $ toJSON (gameEntities game),gameTurnPlayerInvestigatorId=Just iid}
  evaluateCase "Chuck pre24 fast window without action window" (taboo Null) ordinary iid fastWindow [chuck] [] (True,True)
  evaluateCase "Chuck taboo24 cannot bypass action window" (taboo $ toJSON ("TabooList24" :: Text)) ordinary iid fastWindow [chuck] [] (False,False)
  evaluateCase "Chuck taboo24 actual DuringTurn still Fast" (taboo $ toJSON ("TabooList24" :: Text)) ordinary iid during [chuck] [] (True,True)
  let teamGame = snapshotGame team
      otherIid = fromJustNote "second actual actor" $ find ((/= toJSON iid) . toJSON) $ Map.keys (gameEntities teamGame).investigators
      scoped = teamGame {gameEntities=fixtureValue $ setField "investigators" (toJSON $ Map.map readyActor (gameEntities teamGame).investigators) $ toJSON (gameEntities teamGame),gameActiveInvestigatorId=otherIid}
  evaluateCase "asActive other seat sees its own dynamic Fast" scoped ordinary iid during [canFast] [] (True,True)
  let actor = fromJustNote "actor" $ Map.lookup iid (gameEntities game).investigators
      expensive = card "c01021"
      poor = fixtureValue $ setField "tokens" (toJSON [("Resource" :: Text,2 :: Int)]) $ toJSON actor
      poorGame = game {gameEntities=fixtureValue $ setField "investigators" (toJSON $ Map.insert iid poor (gameEntities game).investigators) $ toJSON (gameEntities game)}
  evaluateCase "Chuck required discount cannot also grant Fast" poorGame expensive iid during [chuck] [] (True,False)
  let costStatus = fixtureValue $ args "UnpaidCost" $ toJSON ("NoAction" :: Text)
  env <- makeEnv (register game ordinary) []
  noAction <- runCore env $ getCardPlayability iid iid costStatus fastWindow ordinary
  assert "fast no-action exception is not Fast" $ playabilityPlayable noAction && not (playabilityFast noAction)
  let targetOf = fixtureValue $ args "CardIdTarget" $ toJSON (unPlayerId player)
      play target windows usesAction tailMessages = TargetLabel targetOf $ [InitiatePlayCardWithWindows iid target Nothing (fixtureValue $ tag "NoPayment") windows usesAction] <> tailMessages
      offered choices = (foldl' (\g choice -> case choice of TargetLabel _ (InitiatePlayCardWithWindows _ c _ _ _ _:_) -> register g c; _ -> g) game choices) {gameQuestion=Map.singleton player $ WindowChooseOne choices}
      hasFast choices = do
        value <- boundary (gameCustomCards game) (triggerView (offered choices) player) >>= right
        pure $ case field "entries" value of Array xs -> any ((== toJSON (-1 :: Int)) . field "abilityIndex") xs; _ -> False
  assert "fast offered single-message identifies only actual Fast" =<< hasFast [play printed during True []]
  assert "fast offered ordinary action rejected" . not =<< hasFast [play ordinary during True []]
  assert "fast offered exact PlayerWindow continuation" =<< hasFast [play printed during False [PlayerWindow iid [] True False]]
  assert "fast offered inconsistent continuation action flag rejected" . not =<< hasFast [play printed during True [PlayerWindow iid [] True False]]
  assert "fast offered other investigator continuation rejected" . not =<< hasFast [play printed during True [PlayerWindow otherIid [] False False]]
  assert "fast offered unknown tail rejected" . not =<< hasFast [play printed during True [Noop]]
  let wrongId = TargetLabel (fixtureValue $ args "CardIdTarget" $ toJSON (UUID.nil)) [InitiatePlayCardWithWindows iid printed Nothing (fixtureValue $ tag "NoPayment") during True]
  assert "fast offered wrong target ID rejected" . not =<< hasFast [wrongId]
  let pendingFast = single {snapshotGame=offered [play printed during True [],SkipTriggersButton iid],snapshotQueue=[]}
      setMode = fixtureValue @Answer $ args "SetTriggerModeAnswer" $ object ["investigatorId" .= iid,"cardCode" .= ("c02266" :: Text),"abilityIndex" .= (-1 :: Int),"mode" .= ("AbilityAutoSkip" :: Text)]
  (stoppedFast,_) <- applyAnswer pendingFast player (snapshotRevision pendingFast) setMode >>= right
  fastView <- boundary (gameCustomCards game) (triggerView (snapshotGame stoppedFast) player) >>= right
  assert "fast actual Fast entry permits explicit skip only" $ field "automaticSkipIndex" fastView == toJSON (1 :: Int)
  let ordinaryOldKey = stoppedFast {snapshotGame=(snapshotGame stoppedFast){gameQuestion=Map.singleton player $ WindowChooseOne [play printed fastWindow True [],SkipTriggersButton iid]}}
  noFastView <- boundary (gameCustomCards game) (triggerView (snapshotGame ordinaryOldKey) player) >>= right
  assert "fast historical reserved key cannot authorize currently invalid Fast window" $ field "automaticSkipIndex" noFastView == Null
  rejected <- applyAnswer ordinaryOldKey player (snapshotRevision ordinaryOldKey) $ AutoSkipTriggersAnswer 1
  assert "fast historical reserved key cannot execute invalid automatic skip" $ isLeft rejected
  let actorWithOldFast actor =
        let settings = field "settings" $ toJSON actor
            perCard = field "perCardSettings" settings
            ordinarySettings = case perCard of
              Object rows -> case KM.elems rows of
                one:_ -> Object $ KM.singleton "01088" one
                _ -> error "Expected saved Fast setting"
              _ -> error "Expected per-card map"
         in setField "settings" (setField "perCardSettings" ordinarySettings settings) $ toJSON actor
      ordinaryHistory = stoppedFast {snapshotGame=(register (snapshotGame stoppedFast) ordinary){gameQuestion=Map.singleton player $ WindowChooseOne [play ordinary during True [],SkipTriggersButton iid],gameEntities=fixtureValue $ setField "investigators" (toJSON $ Map.map actorWithOldFast (gameEntities $ snapshotGame stoppedFast).investigators) $ toJSON (gameEntities $ snapshotGame stoppedFast)}}
  ordinaryView <- boundary (gameCustomCards game) (triggerView (snapshotGame ordinaryHistory) player) >>= right
  assert "fast historical ordinary reserved key remains editable but cannot grant skip" $ field "automaticSkipIndex" ordinaryView == Null && case field "entries" ordinaryView of Array entries -> any (\row -> field "cardCode" row == toJSON ("c01088" :: Text) && field "abilityIndex" row == toJSON (-1 :: Int)) entries; _ -> False

-- Dedicated QA only: advance a real Scarlet campaign to its prologue, then
-- replace the pending queue with an explicit embark checkpoint. This is not
-- evidence of naturally finishing the preceding scenario or entire campaign.
remoteTravelFixture :: PlayerId -> ArkhamDBDecklist -> IO Snapshot
remoteTravelFixture player deck = do
  (initial,_) <- createSession (CreateRequest (Just "09") Nothing 943011 [player] Standard False False) >>= right
  let toStory budget snapshot
        | budget <= (0 :: Int) = error "Scarlet QA did not reach prologue"
        | any (\q -> case unwrapQuestion q of Read {} -> True; _ -> False) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | otherwise = do
            answer <- if Map.lookup player (gameQuestion $ snapshotGame snapshot) == Just ContinueCampaign then nextAnswer snapshot else chooseRealAnswer player deck snapshot
            (next,_) <- applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            toStory (budget-1) next
  story <- toStory 12 initial
  assert "specific actual Scarlet prologue has real investigator and initialized meta" $ Map.size (gameEntities $ snapshotGame story).investigators == 1
  commands <- typed @[Message] $ toJSON
    [object ["tag" .= ("ClearUI" :: Text)]
    ,object ["tag" .= ("CampaignStep" :: Text),"contents" .= object ["tag" .= ("CampaignSpecificStep" :: Text),"contents" .= [String "embark",Null]]]]
  let predecessor=exportSnapshot story
  (checkpoint,_) <- boundary (gameCustomCards $ snapshotGame story) (do
    env <- makeEnv (snapshotGame story) commands
    advance env
    finish (snapshotHistoricalBootstrap story) (snapshotRevision story+1) env) >>= right
  let schema=interactionView (snapshotGame checkpoint) player
  assert "specific actual Scarlet handler produces full travel table" $ field "kind" schema == String "embark" && case field "destinations" schema of Array rows -> length rows == 36; _ -> False
  assert "remote travel preparation preserves original prologue snapshot" $ exportSnapshot story == predecessor
  pure checkpoint

scarletCheckpoint :: PlayerId -> ArkhamDBDecklist -> IO ()
scarletCheckpoint player deck = do
  (initial,_) <- createSession (CreateRequest (Just "09") Nothing 943011 [player] Standard False False) >>= right
  let toStory budget snapshot
        | budget <= (0 :: Int) = error "Scarlet QA did not reach prologue"
        | any (\q -> case unwrapQuestion q of Read {} -> True; _ -> False) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | otherwise = do
            answer <- if Map.lookup player (gameQuestion $ snapshotGame snapshot) == Just ContinueCampaign then nextAnswer snapshot else chooseRealAnswer player deck snapshot
            (next,_) <- applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            toStory (budget-1) next
  story <- toStory 12 initial
  assert "specific actual Scarlet prologue has real investigator and initialized meta" $ Map.size (gameEntities $ snapshotGame story).investigators == 1
  commands <- typed @[Message] $ toJSON
    [object ["tag" .= ("ClearUI" :: Text)]
    ,object ["tag" .= ("CampaignStep" :: Text),"contents" .= object ["tag" .= ("CampaignSpecificStep" :: Text),"contents" .= [String "embark",Null]]]]
  let predecessor=exportSnapshot story
  (checkpoint,_) <- boundary (gameCustomCards $ snapshotGame story) (do
    env <- makeEnv (snapshotGame story) commands
    advance env
    finish (snapshotHistoricalBootstrap story) (snapshotRevision story+1) env) >>= right
  let schema=interactionView (snapshotGame checkpoint) player
  assert "specific actual Scarlet handler produces full travel table" $ field "kind" schema == String "embark" && case field "destinations" schema of Array rows -> length rows == 36; _ -> False
  let answer=CampaignSpecificAnswer "travelVia" $ String "Alexandria"
  (travelled,_) <- applyAnswer checkpoint player (snapshotRevision checkpoint) answer >>= right
  let afterCampaign=field "This" $ toJSON $ gameMode $ snapshotGame travelled
  BLC.putStrLn $ "SPECIFIC_POST_TRAVEL " <> encode (object ["question" .= Map.lookup player (gameQuestion $ snapshotGame travelled),"campaign" .= afterCampaign])
  assert "specific actual Scarlet via handler moves campaign to offered location" $ field "currentLocation" (field "meta" afterCampaign) == String "Alexandria"
  imported <- importSnapshot (exportSnapshot checkpoint) >>= right
  (replayed,_) <- applyAnswer imported player (snapshotRevision imported) answer >>= right
  assert "specific actual Scarlet restore then travel exact Game queue RNG" $ exportSnapshot replayed == exportSnapshot travelled
  assert "specific actual Scarlet controlled setup preserves predecessor" $ exportSnapshot story == predecessor
  BLC.putStrLn $ "SPECIFIC_SAVE_FIXTURE " <> encode (object
    ["name" .= ("scarlet-controlled-embark" :: Text),"player" .= player,"snapshot" .= checkpoint
    ,"predecessor" .= story,"injectedMessages" .= commands,"afterTravel" .= travelled
    ,"scope" .= ("Real campaign09 and rule-generated embark; explicit test-only replacement queue after prologue, not natural preceding scenario completion" :: Text)])

-- Controlled genuine Haskell Question/resolver/queue contracts, not naturally
-- encountered campaign situations. No fixture is installed in a user's save.
questionContracts :: PlayerId -> PlayerId -> Snapshot -> Snapshot -> IO ()
questionContracts player other single team = do
  let iid = fromJustNote "actual single investigator" $ listToMaybe $ Map.keys (gameEntities $ snapshotGame single).investigators
      teamIds = Map.keys (gameEntities $ snapshotGame team).investigators
      a = fromJustNote "first actual teammate" $ teamIds !!? 0
      b = fromJustNote "second actual teammate" $ teamIds !!? 1
      sourceOf who = fixtureValue $ object ["tag" .= ("InvestigatorSource" :: Text),"contents" .= who]
      targetOf who = fixtureValue $ object ["tag" .= ("InvestigatorTarget" :: Text),"contents" .= who]
      resources = fixtureValue $ toJSON ("Resource" :: Text)
      clues = fixtureValue $ toJSON ("Clue" :: Text)
      src = sourceOf iid
      gain n = PlaceTokens src (targetOf iid) resources n
      label n = Label ("option-" <> tshow n) [gain n]
      sentinel = ChooseOne [Label "contract-finished" [Noop]]
      basis snapshot question tailQueue = snapshot {snapshotGame=(snapshotGame snapshot){gameQuestion=Map.singleton player question,gameRetainedQuestion=False},snapshotQueue=tailQueue}
      resource snapshot who =
        let attrs = toJSON $ fromJustNote "existing investigator" $ Map.lookup who (gameEntities $ snapshotGame snapshot).investigators
         in case field "tokens" attrs of
              Array pairs -> sum [n | pair <- toList pairs, Aeson.Success (name,n) <- [fromJSON @(Text,Int) pair], name == "Resource"]
              _ -> error "Actual token map encoding required"
      execute name question response = do
        let input = basis single question [Ask player sentinel]
            old = exportSnapshot input
        reply <- typed @Answer response
        assert ("contract validated " <> name) $ isRight $ validateAnswer (snapshotGame input) player reply
        (result,_) <- applyAnswer input player (snapshotRevision input) reply >>= right
        assert ("contract observable sentinel " <> name) $ Map.lookup player (gameQuestion $ snapshotGame result) == Just sentinel && snapshotRevision result == snapshotRevision input + 1
        assert ("contract immutable input " <> name) $ exportSnapshot input == old
        -- Generated by the actual core serializers. Consumers must bind this
        -- stdout record to the successful source-frozen spec receipt.
        BLC.putStrLn $ "CONTRACT_FIXTURE " <> encode (object ["caseId" .= name,"question" .= question,"answer" .= response,"resultQuestion" .= sentinel])
        pure result
      indexed index = object ["tag" .= ("Answer" :: Text),"contents" .= object ["choice" .= index,"playerId" .= player,"questionVersion" .= gameScenarioSteps (snapshotGame single)]]
  wizard <- execute "wizard" (ChooseOneWizard mempty [WizardChoice "first" mempty [gain 1],WizardChoice "second" mempty [gain 2]] "confirm" "back") $ indexed (1 :: Int)
  assert "contract wizard executes selected original choice only" $ resource wizard iid == resource single iid + 2
  dropdown <- execute "dropdown" (DropDown [("first",gain 1),("second",gain 2)]) $ indexed (0 :: Int)
  assert "contract dropdown executes original selected message" $ resource dropdown iid == resource single iid + 1
  supplies <- execute "supplies" (PickSupplies 3 [] [label 1,label 2] False) $ indexed (1 :: Int)
  assert "contract supplies chosen message observed not invented point deduction" $ resource supplies iid == resource single iid + 2
  let groups = [[Info mempty,InvalidLabel "unavailable",label 1],[label 2],[label 3,label 4,label 5]]
      fromEach = ChooseOneFromEach groups
      fromBasis = basis single fromEach []
      expectedGroups = [fromJustNote "first group" $ listToMaybe groups,fromJustNote "last group" $ groups !!? 2]
  (selectedGroup,_) <- applyAnswer fromBasis player (snapshotRevision fromBasis) (Answer $ QuestionResponse 3 (Just player) Nothing) >>= right
  assert "contract FromEach removes the selected entire group" $ Map.lookup player (gameQuestion $ snapshotGame selectedGroup) == Just (ChooseOneFromEach expectedGroups)
  assert "contract FromEach selected distinct message observed" $ resource selectedGroup iid == resource single iid + 2
  BLC.putStrLn $ "CONTRACT_FIXTURE " <> encode (object ["caseId" .= ("fromEach" :: Text),"question" .= fromEach,"answer" .= indexed (3 :: Int),"resultQuestion" .= ChooseOneFromEach expectedGroups])
  for_ [0,1] \i -> do
    blocked <- applyAnswer fromBasis player (snapshotRevision fromBasis) (Answer $ QuestionResponse i (Just player) Nothing)
    assert ("contract FromEach rejects informational unavailable index " <> tshow i) $ isLeft blocked
  let payment = ChoosePaymentAmounts "payment" Nothing [PaymentAmountChoice (unPlayerId player) iid (-1) 3 "resource units" (gain 1)]
      paymentJson n = object ["tag" .= ("PaymentAmountsAnswer" :: Text),"contents" .= object ["amounts" .= Map.singleton (unPlayerId player) n,"playerId" .= player,"questionVersion" .= gameScenarioSteps (snapshotGame single)]]
  paid <- execute "payment" payment $ paymentJson (3 :: Int)
  assert "contract payment repeats generic selected message exactly n times" $ resource paid iid == resource single iid + 3
  paidZero <- execute "paymentZero" payment $ paymentJson (0 :: Int)
  assert "contract null payment target permits zero without invented total" $ resource paidZero iid == resource single iid
  for_ [-1,4] \n -> do
    answer <- typed @Answer $ paymentJson (n :: Int)
    assert ("contract payment range rejected " <> tshow n) $ isLeft $ validateAnswer (snapshotGame $ basis single payment []) player answer
  let teamSrc = sourceOf a
      exchange = ChooseExchangeAmounts teamSrc a 3 b 2 resources
      exchangeGame = snapshotGame $ basis team exchange []
  for_ [-2,0,3] \n -> do
    let response = ExchangeAmountsAnswer teamSrc a b resources n
        expected = if n < 0 then MoveTokens teamSrc (sourceOf b) (targetOf a) resources (abs n) else MoveTokens teamSrc (sourceOf a) (targetOf b) resources n
    assert ("contract signed exchange validation " <> tshow n) $ isRight $ validateAnswer exchangeGame player response
    resolved <- handleAnswerPure exchangeGame player response
    assert ("contract signed exchange exact direction " <> tshow n) $ case resolved of Handled messages -> encode messages == encode [expected]; _ -> False
  let mismatch = [ExchangeAmountsAnswer teamSrc b a resources 1,ExchangeAmountsAnswer (sourceOf b) a b resources 1,ExchangeAmountsAnswer teamSrc a b clues 1,ExchangeAmountsAnswer teamSrc a b resources 4,ExchangeAmountsAnswer teamSrc a b resources (-3)]
  assert "contract exchange rejects changed identities source token and range" $ all (isLeft . validateAnswer exchangeGame player) mismatch
  -- Execution uses real distinct investigators and explicitly seeded test-only
  -- token messages, not a fabricated second ID or a self-transfer.
  let seedQuestion = ChooseOne [Label "seed fixture resources" [PlaceTokens teamSrc (targetOf a) resources 3,PlaceTokens teamSrc (targetOf b) resources 2]]
      seedBasis = basis team seedQuestion [Ask player exchange]
  (seeded,_) <- applyAnswer seedBasis player (snapshotRevision seedBasis) (Answer $ QuestionResponse 0 (Just player) Nothing) >>= right
  assert "contract exchange starts with two actual exact resource balances" $ resource seeded a == 3 && resource seeded b == 2
  for_ [-2,0,3] \n -> do
    let input=seeded {snapshotQueue=[Ask player sentinel]}
    (result,_) <- applyAnswer input player (snapshotRevision input) (ExchangeAmountsAnswer teamSrc a b resources n) >>= right
    assert ("contract actual exchange balances and conservation " <> tshow n) $ resource result a == 3-n && resource result b == 2+n && resource result a + resource result b == 5
  BLC.putStrLn $ "CONTRACT_FIXTURE " <> encode (object ["caseId" .= ("exchange" :: Text),"question" .= exchange,"answer" .= object ["tag" .= ("ExchangeAmountsAnswer" :: Text),"source" .= teamSrc,"fromInvestigator" .= a,"toInvestigator" .= b,"token" .= field "token" (toJSON exchange),"amount" .= (1 :: Int)]])
  let safe = basis single (QuestionLabel "wrapper" Nothing $ ChooseOne [label 1]) []
      old = exportSnapshot safe
  wrongRevision <- applyAnswer safe player (snapshotRevision safe+1) (Answer $ QuestionResponse 0 (Just player) Nothing)
  wrongPlayer <- applyAnswer safe other (snapshotRevision safe) (Answer $ QuestionResponse 0 (Just other) Nothing)
  wrongVersion <- applyAnswer safe player (snapshotRevision safe) (Answer $ QuestionResponse 0 (Just player) (Just $ gameScenarioSteps (snapshotGame safe)+1))
  assert "contract wrappers retain revision recipient and inner version rejection" $ isLeft wrongRevision && isLeft wrongPlayer && isLeft wrongVersion && exportSnapshot safe == old

strictNativeContracts :: PlayerId -> PlayerId -> Snapshot -> IO ()
strictNativeContracts player other single = do
  let withQuestion q = single {snapshotGame=(snapshotGame single) {gameQuestion=Map.singleton player q,gameRetainedQuestion=False},snapshotQueue=[]}
      ordinary n = Answer $ QuestionResponse n (Just player) Nothing
      ordered xs = OrderedAnswer $ OrderedResponse xs (Just player) Nothing
      actionable n = Label ("action-" <> tshow n) [Noop]
      rejected label q answer = do
        let input=withQuestion q; bytes=exportSnapshot input
        result <- applyAnswer input player (snapshotRevision input) answer
        assert label $ isLeft result && exportSnapshot input == bytes
  for_ [0,1,2] \position -> for_ [Info mempty,InvalidLabel "unavailable"] \bad -> do
    let choices=take position [actionable 0,actionable 1] <> [bad] <> drop position [actionable 0,actionable 1]
        kind=case bad of Info {} -> "Info"; _ -> "Invalid"
        firstValid=fromJustNote "valid index" $ find (\n -> n /= position) [0..2]
    rejected ("strict ordered rejects " <> kind <> " at original position " <> tshow position) (ChooseOneAtATime choices) (ordered [2,0,1])
    rejected ("strict auto rejects " <> kind <> " at original position " <> tshow position) (ChooseOneAtATimeWithAuto "auto" choices) (ordinary 0)
    let game=snapshotGame $ withQuestion $ ChooseOneAtATimeWithAuto "auto" choices
    assert ("strict valid ordinary option keeps auto offset around " <> kind <> tshow position) $ isRight $ validateAnswer game player (ordinary $ firstValid+1)
  let allActions=[actionable 0,actionable 1,actionable 2]
  assert "strict actionable complete ordering accepted" $ isRight $ validateAnswer (snapshotGame $ withQuestion $ ChooseOneAtATime allActions) player (ordered [2,0,1])
  assert "strict actionable automatic batch accepted" $ isRight $ validateAnswer (snapshotGame $ withQuestion $ ChooseOneAtATimeWithAuto "auto" allActions) player (ordinary 0)
  for_ [[],[0,1],[0,1,1],[-1,0,1],[0,1,3]] \indexes -> rejected ("strict batch rejects nonpermutation " <> tshow indexes) (ChooseOneAtATime allActions) (ordered indexes)
  rejected "strict empty batch not an automatic progress operation" (ChooseOneAtATimeWithAuto "auto" []) (ordinary 0)
  let message n = Ask player $ ChooseOne [Label ("ordered-" <> tshow n) [Noop]]
      distinct=[Label "first" [message 1],Label "second" [message 2],Label "third" [message 3]]
      orderedGame=snapshotGame $ withQuestion $ ChooseOneAtATime distinct
  resolved <- handleAnswerPure orderedGame player $ ordered [2,0,1]
  assert "strict ordered resolver preserves requested exact Run message order" $ case resolved of Handled messages -> encode messages == encode [Run [message 3],Run [message 1],Run [message 2]]; _ -> False
  let embark current available ticket = object
        ["current" .= (current :: Text),"available" .= (available :: [Text]),"hasTicket" .= ticket
        ,"locations" .= [[toJSON ("London" :: Text),object ["travel" .= (0 :: Int)]],[toJSON ("Alexandria" :: Text),object ["travel" .= (2 :: Int)]],[toJSON ("Arkham" :: Text),object ["travel" .= (1 :: Int)]],[toJSON ("Tokyo" :: Text),object ["travel" .= (3 :: Int)]],[toJSON ("Moscow" :: Text),object ["travel" .= Null]],[toJSON ("Tunguska" :: Text),object ["travel" .= Null]]]]
      travelQuestion=PickCampaignSpecific "embark" $ embark "London" ["Alexandria","Arkham","London"] True
      travel operation destination=CampaignSpecificAnswer operation $ toJSON (destination :: Text)
      accepted label q answer=assert label $ isRight $ validateAnswer (snapshotGame $ withQuestion q) player answer
  accepted "strict travel accepts offered unlocked destination" travelQuestion $ travel "travel" "Alexandria"
  accepted "strict travel via permits passing locked destination" travelQuestion $ travel "travelVia" "Tokyo"
  accepted "strict ticket route accepted with ticket" travelQuestion $ travel "travelWithTicket" "Alexandria"
  accepted "strict green location adds actual extra day for ticket threshold" travelQuestion $ travel "travelWithTicket" "Arkham"
  for_ [("travel","Tokyo"),("travel","Moscow"),("travelVia","Moscow"),("travel","London"),("setCurrent","Alexandria"),("unlock","Tokyo"),("embark","Alexandria"),("statusReport","Alexandria"),("travel","NotARealMapLocation")] \(operation,destination) -> rejected ("strict embark rejects " <> operation <> " " <> destination) travelQuestion $ travel operation destination
  rejected "strict ticket cannot be forged when absent" (PickCampaignSpecific "embark" $ embark "London" ["Alexandria"] False) $ travel "travelWithTicket" "Alexandria"
  let finale=PickCampaignSpecific "embark" $ embark "Tunguska" ["Tunguska"] False
  accepted "strict terminal Tunguska original offer remains usable" finale $ travel "travel" "Tunguska"
  rejected "strict Tunguska exception does not authorize travelVia" finale $ travel "travelVia" "Tunguska"
  rejected "specific terminal offer cannot escape via another destination" finale $ travel "travelVia" "Alexandria"
  rejected "strict singleton-current London is not terminal exception" (PickCampaignSpecific "embark" $ embark "London" ["London"] True) $ travel "travel" "London"
  rejected "strict unknown Specific discriminator never becomes public message" (PickCampaignSpecific "setCurrent" $ object []) $ travel "setCurrent" "Alexandria"
  let codes=map toJSON (["c01033","c01034","c01035","c01036","c01037","c01038","c01039","c01040","c01041","c01042"] :: [Text])
      spirit=PickScenarioSpecific "laidToRest.buildSpiritDeck" $ object ["cardCodes" .= codes,"count" .= (9 :: Int),"fixed" .= (["c90050"] :: [Text])]
      spiritAnswer selected=ScenarioSpecificAnswer "laidToRest.buildSpiritDeck" $ object ["cardCodes" .= selected]
  accepted "strict spirit deck accepts exact nine unique offered entries" spirit $ spiritAnswer $ take 9 codes
  for_ [("eight",take 8 codes),("ten",codes),("duplicate",replicate 9 $ headEx codes),("foreign",take 8 codes <> [toJSON ("c99999" :: Text)]),("fixed",take 8 codes <> [toJSON ("c90050" :: Text)]),("alternateface",take 8 codes <> [toJSON ("c01041b" :: Text)])] \(label,selected) -> rejected ("strict spirit deck rejects " <> label) spirit $ spiritAnswer selected
  rejected "strict spirit deck rejects extra administrative field" spirit $ ScenarioSpecificAnswer "laidToRest.buildSpiritDeck" $ object ["cardCodes" .= take 9 codes,"fixed" .= ([] :: [Text])]
  for_ [("duplicate candidates",codes <> take 1 codes,9,[]),("duplicate fixed",codes,9,["c90050","c90050"]),("overlapping fixed",codes,9,["c01033"]),("zero count",codes,0,[])] \(label,candidates,total,fixed) -> do
    let malformed=PickScenarioSpecific "laidToRest.buildSpiritDeck" $ object ["cardCodes" .= candidates,"count" .= (total :: Int),"fixed" .= (fixed :: [Text])]
    rejected ("strict spirit deck rejects malformed offer " <> label) malformed $ spiritAnswer $ take total codes
  rejected "strict unknown scenario discriminator rejected even same key" (PickScenarioSpecific "future" Null) $ ScenarioSpecificAnswer "future" Null
  for_ [("travel",travelQuestion),("finale",finale)] \(label,q) -> do
    let g=snapshotGame $ withQuestion $ QuestionLabel "specific wrapper" Nothing q
        before=encode g
        metadata=interactionView g player
    assert ("specific schema projects own wrapped question " <> label) $ field "supported" metadata == Bool True && field "kind" metadata == String "embark"
    assert ("specific schema absent for nonrecipient " <> label) $ interactionView g other == Null
    routes <- typed @[Value] $ field "destinations" metadata
    for_ routes \route -> do
      actions <- typed @[Value] $ field "actions" route
      let destination=field "id" route
      for_ ["travel","travelVia","travelWithTicket","setCurrent"] \operation -> do
        let listed=any (\a -> field "operation" a == String operation) actions
            legal=isRight $ validateAnswer g player $ CampaignSpecificAnswer operation destination
        assert ("specific metadata exact validator membership " <> label <> " " <> tshow destination <> " " <> operation) $ listed == legal
      for_ actions \action -> case field "operation" action of
        String "travelWithTicket" -> assert "specific ticket metadata exact executed one day and consumed ticket" $ field "days" action == toJSON (1 :: Int) && field "consumesTicket" action == Bool True
        _ -> pure ()
    assert ("specific read only projection leaves whole Game unchanged " <> label) $ encode g == before
    let expected=object ["tag" .= ("CampaignSpecificAnswer" :: Text),"contents" .= [String "travel",String $ if label == "finale" then "Tunguska" else "Alexandria"]]
    answer <- typed @Answer expected
    assert ("specific canonical travel payload validates " <> label) $ isRight $ validateAnswer g player answer
    resolvedSpecific <- handleAnswerPure g player answer
    assert ("specific exact travel message no client messages " <> label) $ case resolvedSpecific of Handled messages -> encode messages == encode [CampaignSpecific "travel" $ String $ if label == "finale" then "Tunguska" else "Alexandria"]; _ -> False
    when (label == "finale") $ assert "specific finale cost explicitly unknown not old distance or zero" $ all (\r -> field "id" r /= String "Tunguska" || case field "actions" r of Array xs -> all (\a -> field "days" a == Null) xs; _ -> False) routes
    BLC.putStrLn $ "SPECIFIC_FIXTURE " <> encode (object ["name" .= label,"schema" .= metadata,"answer" .= expected])
  let spiritGame=snapshotGame $ withQuestion spirit
      spiritSchema=interactionView spiritGame player
      expectedSpirit=object ["tag" .= ("ScenarioSpecificAnswer" :: Text),"contents" .= [String "laidToRest.buildSpiritDeck",object ["cardCodes" .= take 9 codes]]]
  assert "specific spirit schema preserves exact candidate fixed and count contract" $ field "cardCodes" spiritSchema == toJSON codes && field "count" spiritSchema == toJSON (9 :: Int) && field "fixed" spiritSchema == toJSON (["c90050"] :: [Text])
  spiritWire <- typed @Answer expectedSpirit
  assert "specific canonical spirit payload validates" $ isRight $ validateAnswer spiritGame player spiritWire
  resolvedSpirit <- handleAnswerPure spiritGame player spiritWire
  assert "specific exact spirit message no client messages" $ case resolvedSpirit of Handled messages -> encode messages == encode [ScenarioSpecific "laidToRest.buildSpiritDeck" $ object ["cardCodes" .= take 9 codes]]; _ -> False
  assert "specific unrelated ordinary question has no metadata" $ interactionView (snapshotGame $ withQuestion $ ChooseOne [actionable 0]) player == Null
  assert "specific unknown question metadata explicitly unsupported" $ field "supported" (interactionView (snapshotGame $ withQuestion $ PickCampaignSpecific "unknown" Null) player) == Bool False
  BLC.putStrLn $ "SPECIFIC_FIXTURE " <> encode (object ["name" .= ("spirit" :: Text),"schema" .= spiritSchema,"answer" .= expectedSpirit])
  let toggle k b=object ["type" .= ("ToggleKey" :: Text),"key" .= (k :: Text),"content" .= b]
      lita b=object ["type" .= ("ToggleOption" :: Text),"key" .= ("AddLitaChantler" :: Text),"content" .= b]
      house k=object ["type" .= ("PickKey" :: Text),"content" .= (k :: Text)]
      settings xs=object ["tag" .= ("StandaloneSettingsAnswer" :: Text),"contents" .= xs]
      midnight=[toggle "LitaWasForcedToFindOthersToHelpHerCause" False,house "YourHouseHasBurnedToTheGround",toggle "GhoulPriestIsStillAlive" True,lita False]
      cultists=["01137","01138","01139","01140","01141","01121b"] :: [Text]
      crossed xs=object ["type" .= ("ToggleCrossedOut" :: Text),"key" .= ("CultistsWhoGotAway" :: Text),"recordable" .= ("RecordableCardCode" :: Text),"content" .= [object ["key" .= code,"content" .= (i == (0 :: Int))] | (i,code) <- zip [0..] xs]]
      devourer=[toggle "ItIsPastMidnight" True,toggle "GhoulPriestIsStillAlive" False,crossed cultists,lita True]
  for_ [("01104",[]),("01120",midnight),("01142",devourer)] \(sid,rows) -> do
    request <- typed @CreateRequest $ object ["scenario" .= (sid :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
    (initial,_) <- createSession request >>= right
    let schema=settingsView (snapshotGame initial) player
    assert ("settings real standalone offered shared schema " <> sid) $ field "supported" schema == Bool True && length (fromMaybe [] $ Aeson.parseMaybe parseJSON $ field "rows" schema :: [Value]) == length rows
    answer <- typed @Answer $ settings rows
    (result,_) <- applyAnswer initial player (snapshotRevision initial) answer >>= right
    assert ("settings actual accepted schema reaches native deck choice " <> sid) $ any ((== ChooseDeck) . unwrapQuestion) $ Map.elems $ gameQuestion $ snapshotGame result
    assert ("settings native commit leaves input immutable " <> sid) $ snapshotRevision initial == 0 && snapshotRevision result == 1
    let metaRows=fromMaybe [] $ Aeson.parseMaybe parseJSON $ field "rows" schema :: [Value]
        canonicalRow meta row = case field "type" row of
          String "PickKey" ->
            let choices=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "choices" meta) :: [Value]
                selected=fromJustNote "actual offered canonical key" $ find (\choice -> field "contents" choice == field "content" row) choices
             in object ["type" .= ("PickKey" :: Text),"content" .= selected]
          String "ToggleCrossedOut" ->
            let nativeEntries=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "entries" meta) :: [Value]
                actualEntries=fromMaybe [] (Aeson.parseMaybe parseJSON $ field "content" row) :: [Value]
             in object ["type" .= field "type" row,"key" .= field "key" meta,"recordable" .= field "recordable" meta,"content" .= [object ["key" .= field "key" native,"content" .= field "content" chosen] | (native,chosen) <- zip nativeEntries actualEntries]]
          _ -> object ["type" .= field "type" row,"key" .= field "key" meta,"content" .= field "content" row]
        canonicalPayload=settings $ zipWith canonicalRow metaRows rows
    canonicalAnswer <- typed @Answer canonicalPayload
    (canonicalResult,_) <- applyAnswer initial player 0 canonicalAnswer >>= right
    assert ("settings canonical metadata keys produce exact same Game queue RNG " <> sid) $ exportSnapshot result == exportSnapshot canonicalResult
    BLC.putStrLn $ "SETTINGS_FIXTURE " <> encode (object ["scenario" .= sid,"schema" .= schema,"answer" .= canonicalPayload,"originalRawAnswer" .= settings rows])
    when (sid == "01120") do
      for_ [("missing",[]),("extra",midnight <> [toggle "ItIsPastMidnight" True]),("wrongrow",[toggle "ItIsPastMidnight" True] <> drop 1 midnight),("mutual",take 1 midnight <> [house "GhoulPriestIsStillAlive"] <> drop 2 midnight),("nested",[object ["type" .= ("Group" :: Text),"content" .= midnight]]),("swapped",[midnight P.!! 1,headEx midnight,midnight P.!! 2,lastEx midnight]),("duplicate row",[headEx midnight,headEx midnight,midnight P.!! 2,lastEx midnight])] \(label,bad) -> do
        badAnswer <- typed @Answer $ settings bad
        rejectedResult <- applyAnswer initial player 0 badAnswer
        assert ("settings rejects malformed standalone " <> label) $ isLeft rejectedResult
    when (sid == "01142") do
      for_ [("duplicate",replicate 6 ("01137" :: Text)),("missing",take 5 cultists),("wrongface",take 5 cultists <> ["01121a"])] \(label,bad) -> do
        badAnswer <- typed @Answer $ settings [headEx devourer,devourer P.!! 1,crossed bad,lastEx devourer]
        rejectedResult <- applyAnswer initial player 0 badAnswer
        assert ("settings rejects recorded card " <> label) $ isLeft rejectedResult
    notRecipient <- applyAnswer initial other 0 answer
    stale <- applyAnswer initial player 1 answer
    assert ("settings actual wrong seat and stale revision rejected " <> sid) $ isLeft notRecipient && isLeft stale
  decks <- eitherDecodeFileStrict "arkham-core/data/community-starter-decks.json" >>= either (\reason -> P.putStrLn reason >> exitFailure) pure
  let firstDeck=fromJustNote "actual default deck" $ listToMaybe decks
      untilSettings budget snapshot
        | any ((== PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = error "Bounded campaign start did not reach actual settings"
        | otherwise = do
            response <- chooseRealAnswer player firstDeck snapshot
            (following,_) <- applyAnswer snapshot player (snapshotRevision snapshot) response >>= right
            untilSettings (budget-1) following
  campaignRequest <- typed @CreateRequest $ object ["campaign" .= ("01" :: Text),"scenario" .= ("01120" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (campaignInitial,_) <- createSession campaignRequest >>= right
  campaignSettings <- untilSettings 8 campaignInitial
  let emptyCampaign=object ["tag" .= ("CampaignSettingsAnswer" :: Text),"contents" .= object ["keys" .= ([] :: [Text]),"counts" .= ([] :: [Value]),"sets" .= ([] :: [Value]),"options" .= ([] :: [Text])]]
  assert "settings actual NOTZ campaign start selects explicit empty schema" $ field "supported" (settingsView (snapshotGame campaignSettings) player) == Bool True
  emptyAnswer <- typed @Answer emptyCampaign
  (continued,_) <- applyAnswer campaignSettings player (snapshotRevision campaignSettings) emptyAnswer >>= right
  assert "settings actual empty campaign pair arrays advance without guessing step" $ snapshotRevision continued == snapshotRevision campaignSettings+1 && all ((/= PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame continued)
  nonempty <- typed @Answer $ object ["tag" .= ("CampaignSettingsAnswer" :: Text),"contents" .= object ["keys" .= (["GhoulPriestIsStillAlive"] :: [Text]),"counts" .= ([] :: [Value]),"sets" .= ([] :: [Value]),"options" .= ([] :: [Text])]]
  badCampaign <- applyAnswer campaignSettings player (snapshotRevision campaignSettings) nonempty
  assert "settings empty campaign schema cannot inject a valid but unoffered key" $ isLeft badCampaign
