module NativeStandaloneSetupSpec (nativeStandaloneSetupSpecs) where

import Arkham.Prelude hiding (assert, count, pairs)
import Arkham.Game (Game (..), GameMode)
import Arkham.Id
import Arkham.Question
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Env (runCore)
import Arkham.Native.Events (eventJSON)
import Arkham.Helpers.Modifiers (getModifiers)
import Arkham.Message (Message (..))
import Arkham.Native.Validation (indexedChoices,validateAnswer)
import Arkham.Native.Settings.Engine qualified as Engine
import Arkham.Native.Settings.Types
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.Either (isLeft)
import Data.ByteString.Lazy.Char8 qualified as BLC
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn $ "PASS " <> label else putStrLn ("FAIL " <> label) >> exitFailure
right :: Either Text a -> IO a
right = either (\reason -> putStrLn reason >> exitFailure) pure
typed :: FromJSON a => Value -> IO a
typed = either (\reason -> P.putStrLn reason >> exitFailure) pure . Aeson.parseEither parseJSON
field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null
setField :: Text -> Value -> Value -> Value
setField key value (Object o) = Object $ KM.insert (K.fromText key) value o
setField _ _ _ = error "Spec requires an object"
facingWire :: Bool -> DestinyDrawing -> Value
facingWire reversed drawing =
  let value = toJSON drawing
   in setField "tarot" (setField "facing" (String $ if reversed then "Reversed" else "Upright") $ field "tarot" value) value
array :: Value -> [Value]
array (Array xs) = toList xs
array _ = []
pairs :: Value -> [(Text,Value)]
pairs (Object o) = [(K.toText k,v) | (k,v) <- KM.toList o]
pairs _ = []
emit :: Text -> Value -> IO ()
emit prefix value = BLC.putStrLn $ BLC.pack (T.unpack prefix) <> encode value
saved :: Snapshot -> IO Value
saved = right . first T.pack . eitherDecode . exportSnapshot
scenario :: Snapshot -> Value
scenario = field "That" . toJSON . gameMode . snapshotGame
logOf :: Snapshot -> Value
logOf = field "standaloneCampaignLog" . scenario
count :: Text -> Snapshot -> Int
count name s = sum [fromMaybe 0 $ Aeson.parseMaybe parseJSON n | pair <- array $ field "recordedCounts" $ logOf s
  ,[key,n] <- [array pair],field "contents" key == String name]
recorded :: Text -> Snapshot -> Bool
recorded name = any ((== String name) . field "contents") . array . field "recorded" . logOf
hasCount :: Text -> Snapshot -> Bool
hasCount name s = any (\pair -> case array pair of [key,_] -> field "contents" key == String name; _ -> False) $ array $ field "recordedCounts" $ logOf s
tokenCount :: Text -> Snapshot -> Int
tokenCount name = length . filter ((== String name) . field "chaosTokenFace") . array . field "chaosTokens" . field "chaosBag" . scenario
recordSet :: Text -> Snapshot -> [Value]
recordSet name s = concat [array values | pair <- array $ field "recordedSets" $ logOf s
  ,[key,values] <- [array pair],field "contents" key == String name]
unwrap :: Question a -> Question a
unwrap = \case QuestionLabel _ _ q -> unwrap q; PayCostQuestion _ q -> unwrap q; QuestionWithSource _ _ q -> unwrap q; q -> q
choiceKey :: Value -> Text
choiceKey v = case field "label" v of String text -> lastEx $ T.splitOn "." $ T.takeWhile (/= ' ') text; _ -> ""
unwrappedValue :: Value -> Value
unwrappedValue value
  | field "tag" value `elem` map String ["QuestionLabel","PayCostQuestion","QuestionWithSource"] = unwrappedValue $ field "question" value
  | otherwise = value

-- Test-only finite wire adapter. Do not expose arbitrary rule messages or add
-- a production ToJSON instance merely to serialize QA commands.
answerWire :: Answer -> IO Value
answerWire = \case
  Answer (QuestionResponse choice player version) -> pure $ object
    ["tag" .= ("Answer" :: Text),"contents" .= object ["choice" .= choice,"playerId" .= player,"questionVersion" .= version]]
  DeckListAnswer deck player -> pure $ object
    ["tag" .= ("DeckListAnswer" :: Text),"deckList" .= deck,"playerId" .= player]
  AmountsAnswer (AmountsResponse amounts version player) -> pure $ object
    ["tag" .= ("AmountsAnswer" :: Text),"contents" .= object ["amounts" .= amounts,"questionVersion" .= version,"playerId" .= player]]
  PickDestinyAnswer drawings -> pure $ object
    ["tag" .= ("PickDestinyAnswer" :: Text),"contents" .= drawings]
  _ -> assert "native setup explicit finite test wire adapter required" False >> pure Null

-- This table is independent of Engine's allowlist. Source review is frozen in
-- phase5-native-setup-review-r1; 05284 explicitly remains Declared-empty.
cases :: [(Text,Text,Text)]
cases = [("51020","02118","fixed-bag"),("51025","02159","fixed-bag"),("51032","02195","fixed-bag")
  ,("51041","02236","fixed-log"),("51047","02274","fixed-log"),("51053","02311","fixed-bag")
  ,("52014","03043","fixed-bag"),("52021","03061","random-bag"),("52028","03120","random-bag")
  ,("52034","03159","random-bag"),("52040","03200","conviction-doubt"),("52048","03240","fixed-log")
  ,("52054","03274","random-bag"),("52059","03316","conviction-doubt-neither")
  ,("53016","04043","fixed-bag"),("53017","04054","fixed-log"),("53028","04113","relic-custody")
  ,("53038","04161","fixed-bag"),("53045","04205a","paths"),("53053","04237","fixed-bag")
  ,("53059","04277","fury"),("53061","04314","faction"),("54016","05043","prologue")
  ,("54017","05050","fixed-bag"),("54024","05065","fixed-log"),("54029","05120","fixed-bag")
  ,("54034","05161","fixed-bag"),("54042","05197","membership"),("54049","05284","finale-enemy")]

defaultBranch :: Text -> Text
defaultBranch = \case
  "52040" -> "conviction"; "52059" -> "conviction"; "53028" -> "gaveCustodyToAlejandro"
  "53045" -> "0"; "53059" -> "0"; "53061" -> "standaloneIchtacaSetAgainst"
  "54016" -> "playNormally"; "54042" -> "members"; "54049" -> "anetteMasonIsPossessedByEvil"
  _ -> "default"

expectedKeys :: Text -> [Text]
expectedKeys = \case
  "52040" -> ["conviction","doubt"]; "52059" -> ["conviction","doubt","neither"]
  "53028" -> ["gaveCustodyToAlejandro","gaveCustodyToHarlan"]
  "53061" -> ["standaloneIchtacaSetAgainst","standaloneAlejandroSetAgainst","standaloneBothSetAgainst"]
  "54016" -> ["returnToSkip","playNormally"]; "54042" -> ["members","notMembers"]
  "54049" -> ["anetteMasonIsPossessedByEvil","carlSanfordPossessesTheSecretsOfTheUniverse"]
  _ -> []

nativeStandaloneSetupSpecs :: PlayerId -> PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeStandaloneSetupSpecs player other ordinary = do
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e -> P.putStrLn e >> exitFailure) pure
  let create sid players = do
        request <- typed $ object ["scenario" .= (sid :: Text),"seed" .= (201020 :: Int),"players" .= players]
        fst <$> (createSession request >>= right)
      emptyLog = object $ [key .= ([] :: [Value]) | key <- ["recorded","crossedOut","recordedCounts","recordedSets","orderedKeys","options"]] <> ["partners" .= object []]
      schemaFor s = right $ Engine.resolveSchema (snapshotGame s) player
      formFor schema = SettingsFormRequest (Engine.schemaIdentity schema) mempty
  gathering <- create "01104" [player] >>= schemaFor
  (campaign,_) <- (typed (object ["campaign" .= ("01" :: Text),"seed" .= (201020 :: Int),"players" .= [player]]) >>= createSession) >>= right
  actual <- for cases $ \(target,base,family) -> do
    before <- create target [player]
    initialSave <- saved before
    emit "NATIVE_SETUP_INITIAL_FIXTURE " $ object ["target" .= target,"save" .= initialSave]
    schema <- schemaFor before
    let form = formFor schema
        context = field "context" $ Engine.schemaView schema
    preview <- right $ Engine.previewForm schema form
    logValue <- right $ Engine.compileForm schema form
    assert ("native setup exact manifest target definition family presence " <> target) $
      field "scenario" context == String target && field "definitionScenario" context == String base
        && field "nativeSetupFamily" context == String family
        && field "definitionPresence" context == String (if target == "54049" then "declared" else "source-omitted")
        && field "aliasPolicy" context == String "reviewed-native-standalone-initialization-v1"
    assert ("native setup zero front form does not invent history " <> target) $
      null (array $ field "rows" preview) && field "canSubmit" preview == Bool True && toJSON logValue == emptyLog
    assert ("native setup foreign and invented front rows reject " <> target) $
      isLeft (Engine.compileForm schema $ formFor gathering)
        && isLeft (Engine.previewForm schema $ form {valuesByRowId = Map.singleton "invented" $ FormBoolean True})
    combined <- typed @GameMode $ object ["This" .= field "This" (toJSON $ gameMode $ snapshotGame campaign),"That" .= scenario before]
    assert ("native setup controlled These cannot borrow standalone manifest " <> target) $
      isLeft $ Engine.resolveSchema ((snapshotGame before) {gameMode=combined}) player
    pure (target,base,family,before,schema,form)
  for_ (zip actual $ drop 1 actual <> take 1 actual) $ \((target,_,_,_,schema,_),(_,_,_,_,_,foreignForm)) ->
    assert ("native setup cross-target empty schema rejected " <> target) $ isLeft $ Engine.compileForm schema foreignForm

  let drive target branch players = loop (180 :: Int) [] []
       where
        loop budget seen trace s
          | any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrap $ Map.elems $ gameQuestion $ snapshotGame s) = pure (s,reverse seen,reverse trace)
          | budget <= 0 = emit "NATIVE_SETUP_UNHANDLED " (object ["target" .= target,"questions" .= gameQuestion (snapshotGame s)]) >> assert ("native setup bounded " <> target) False >> pure (s,seen,trace)
          | otherwise = do
            let questions = Map.toList $ gameQuestion $ snapshotGame s
            assert ("native setup actual recipient exists " <> target) $ not (null questions)
            let hasActionable wrapped = case indexedChoices (unwrap wrapped) of
                  Nothing -> True
                  Just cs -> any (\v -> field "tag" v `notElem` map String ["Info","InvalidLabel"]) $ map toJSON cs
                selectable = filter (hasActionable . snd) questions
            when (null selectable) $ emit "NATIVE_SETUP_NO_ACTIONABLE_PROMPT " $ object ["target" .= target,"questions" .= gameQuestion (snapshotGame s)]
            assert ("native setup real actionable recipient exists " <> target) $ not (null selectable)
            let (who,wrapped) = headEx selectable
                q = unwrap wrapped
                choices = fromMaybe [] $ indexedChoices q
                values = map toJSON choices
                keys = map choiceKey values
                expected = expectedKeys target
                isBranch = not (null expected) && all (`elem` keys) expected
                alternateKey = if branch == "takeCards" then "takeCards" else "bullshit"
                isAlternateFate = target == "54017" && branch `elem` ["takeCards","rejectFate"] && alternateKey `elem` keys
                isPaths = target == "53045" && length choices == 6 && all (== "number") keys
                prologueIds = map String ["c05046","c05047","c05048","c05049"]
                isPrologueCards = target == "54016" && not (null choices) && all (\v -> field "tag" v == String "CardLabel" && field "cardCode" v `elem` prologueIds) values
                deck = if who == other then decks P.!! 1 else headEx decks
                tag = field "tag" $ toJSON q
                parseInteger text = either (const Nothing) Just $ eitherDecodeStrict' @Int $ encodeUtf8 text
                labelHasCount desired value = case field "label" value of
                  String text -> case [number | token <- T.words text,Just number <- [T.stripPrefix "count=i:" token]] of
                    [number] -> isJust (parseInteger desired) && parseInteger number == parseInteger desired
                    _ -> False
                  _ -> False
            when (length selectable < length questions) $ emit "NATIVE_SETUP_WAITING_PROMPT " $
              object ["target" .= target,"branch" .= branch,"questions" .= gameQuestion (snapshotGame s),"selectedRecipient" .= who]
            (answer,marker) <- if isAlternateFate
              then do
                let selected = [i | (i,v) <- zip [0..] values,choiceKey v == alternateKey]
                assert ("destiny alternative actual offered branch " <> branch) $ length selected == 1
                pure (Answer $ QuestionResponse (headEx selected) (Just who) Nothing,Just branch)
              else if isBranch || isPaths
              then do
                assert ("native setup actual family choice cardinality " <> target) $ length choices == if isPaths then 6 else length expected
                let selected = [i | (i,v) <- zip [0..] values,if isPaths then labelHasCount branch v else choiceKey v == branch]
                assert ("native setup exact requested offered branch " <> target <> "/" <> branch) $ length selected == 1
                emit "NATIVE_SETUP_BRANCH_PROMPT " $ object ["target" .= target,"branch" .= branch,"recipient" .= who,"question" .= wrapped,"selectedIndex" .= headEx selected]
                when (length players > 1) $ do
                  let wrong = headEx $ filter (/= who) players
                  rejected <- applyAnswer s wrong (snapshotRevision s) (Answer $ QuestionResponse (headEx selected) (Just wrong) Nothing)
                  assert ("native setup actual branch rejects wrong seat " <> target) $ isLeft rejected
                pure (Answer $ QuestionResponse (headEx selected) (Just who) Nothing,Just branch)
              else case q of
                PickDestiny drawings -> do
                  assert ("native setup actual offered Destiny scopes unique " <> target) $
                    not (null drawings) && length drawings == length (nub [scope | DestinyDrawing scope _ <- drawings])
                  invalid <- applyAnswer s who (snapshotRevision s) $ PickDestinyAnswer $ headEx drawings : drawings
                  assert ("native setup actual Destiny duplicate rejected " <> target) $ isLeft invalid
                  rotated <- typed @[DestinyDrawing] $ toJSON $ zipWith facingWire
                    [i < (length drawings + 1) `div` 2 | i <- [0 .. length drawings-1]] drawings
                  emit "NATIVE_SETUP_DESTINY_PROMPT " $ object ["target" .= target,"recipient" .= who,"question" .= wrapped,"selected" .= rotated]
                  pure (PickDestinyAnswer rotated,Nothing)
                ChooseAmounts "Fury" (MaxAmountTarget 9000) [AmountChoice key "Fury" 0 9000] _ | target == "53059" -> do
                  let amount = if branch == "6" then 6 else 0
                  emit "NATIVE_SETUP_FURY_PROMPT " $ object ["target" .= target,"amount" .= amount,"recipient" .= who,"question" .= wrapped]
                  pure (AmountsAnswer $ AmountsResponse (Map.singleton key amount) (Just $ gameScenarioSteps $ snapshotGame s) (Just who),Just branch)
                ChooseDeck -> (,Nothing) <$> ordinary who deck s
                ChooseAmounts _ (TotalAmountTarget {}) _ _ -> (,Nothing) <$> ordinary who deck s
                -- Use the production finite index adapter, including genuine
                -- ChooseN starting-location discards. This is QA selection,
                -- not an application auto-answer or arbitrary message path.
                _ | isJust (indexedChoices q) -> do
                  when isPrologueCards $ do
                    let taken = [field "id" inv | (_,inv) <- pairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame s,field "id" inv `elem` prologueIds]
                        offeredIds = map (field "cardCode") values
                    assert "native setup prologue offered special cards exclude already chosen investigators" $
                      length offeredIds == 4 - length taken && all (`notElem` taken) offeredIds
                    emit "NATIVE_SETUP_PROLOGUE_CARD_PROMPT " $ object ["target" .= target,"recipient" .= who,"question" .= wrapped,"alreadyTaken" .= taken]
                  assert ("native setup offers actual actionable choice " <> target) $ any (\v -> field "tag" v `notElem` map String ["Info","InvalidLabel"]) values
                  (,Nothing) <$> ordinary who deck s
                _ -> do
                  emit "NATIVE_SETUP_UNHANDLED " $ object ["target" .= target,"question" .= wrapped]
                  assert ("native setup explicit question driver " <> target) False
                  ordinary who deck s <&> (,Nothing)
            let parent = exportSnapshot s
            wire <- answerWire answer
            decoded <- typed @Answer wire
            assert ("native setup exact typed answer wire roundtrip " <> target) $ show decoded == show answer
            (next,_) <- applyAnswer s who (snapshotRevision s) decoded >>= right
            assert ("native setup each answer leaves old state immutable " <> target) $ exportSnapshot s == parent
            loop (budget-1) (maybe seen (:seen) marker)
              (object ["tag" .= tag,"recipient" .= who,"revision" .= snapshotRevision s,"question" .= wrapped,"answer" .= wire] : trace) next

      run target branch players before schema form = do
        let bytes = exportSnapshot before
        (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
        imported <- importSnapshot bytes >>= right
        (submittedReplay,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer form) >>= right
        assert ("native setup actual form submission exact replay " <> target) $ exportSnapshot submitted == exportSnapshot submittedReplay && exportSnapshot before == bytes
        (ready,seen,trace) <- drive target branch players submitted
        (replayed,replaySeen,replayTrace) <- drive target branch players submittedReplay
        beforeSave <- saved before
        submittedSave <- saved submitted
        readySave <- saved ready
        replaySave <- saved replayed
        emit "NATIVE_SETUP_READY_FIXTURE " $ object ["target" .= target,"branch" .= branch,"schema" .= Engine.schemaView schema,"form" .= form
          ,"beforeSave" .= beforeSave,"submittedSave" .= submittedSave,"readySave" .= readySave,"replaySave" .= replaySave,"seen" .= seen,"trace" .= trace]
        assert ("native setup exact entire Game queue RNG natural replay " <> target <> "/" <> branch) $
          exportSnapshot ready == exportSnapshot replayed && seen == replaySeen && trace == replayTrace
        assert ("native setup actual true Return-To scenario and local bag " <> target) $
          field "id" (scenario ready) == String ("c" <> target)
            && not (null $ array $ field "chaosTokens" $ field "chaosBag" $ scenario ready)
        assert ("native setup actual initialization ended " <> target) $ not $ gameInSetup $ snapshotGame ready
        unless (branch == "default") $ assert ("native setup branch actually reached not defaulted " <> target <> "/" <> branch) $ branch `elem` seen
        pure ready

  for_ actual $ \(target,base,_,before,schema,form) -> do
    ready <- run target (defaultBranch target) [player] before schema form
    let reference = T.dropWhileEnd (\c -> c == 'a' || c == 'b') base
    assert ("native setup wrapper reference retained " <> target) $ field "reference" (scenario ready) == String ("c" <> reference)
    when (target == "51041") $ assert "native setup Undimensioned real standalone sacrificed Armitage" $
      any (\v -> field "tag" (field "recordVal" v) == String "Recorded" && field "contents" (field "recordVal" v) == String "c02040") $ recordSet "SacrificedToYogSothoth" ready
    when (target == "51047") $ assert "native setup WhereDoomAwaits original noBrood history installed" $ recorded "NoBroodEscapedIntoTheWild" ready
    when (target == "52048") $ assert "native setup PallidMask original Nigel and soloCatacomb history installed" $
      recorded "YouFoundNigelsHome" ready && recorded "YouEnteredTheCatacombsOnYourOwn" ready
    when (target == "53017") $ assert "native setup DoomOfEztli original clearedPath history installed" $ recorded "TheInvestigatorsClearedAPathToTheEztliRuins" ready
    when (target == "54024") $ do
      let missing = map (field "recordVal") $ recordSet "MissingPersons" ready
      assert "native setup AtDeathsDoorstep original four crossed-out missing persons" $
        length missing == 4 && all ((== String "CrossedOut") . field "tag") missing
          && all (\code -> any ((== String code) . field "contents") missing) ["c05046","c05047","c05048","c05049"]
    when (target == "52040") $ assert "native setup default Conviction actually increments by1" $
      count "Conviction" ready == count "Conviction" before + 1 && count "Doubt" ready == count "Doubt" before
    when (target == "52059") $ assert "native setup default DimCarcosa Conviction actually increments by8" $
      count "Conviction" ready == count "Conviction" before + 8 && count "Doubt" ready == count "Doubt" before
    when (target == "53028") $ assert "native setup default custody records Alejandro only" $
      recorded "TheInvestigatorsGaveCustodyOfTheRelicToAlejandro" ready && not (recorded "TheInvestigatorsGaveCustodyOfTheRelicToHarlanEarnstone" ready)
    when (target == "53045") $ assert "native setup paths actual recorded lower endpoint0" $ hasCount "PathsAreKnownToYou" ready && count "PathsAreKnownToYou" ready == 0
    when (target == "53059") $ assert "native setup Fury0 actual recorded and first agenda" $
      hasCount "YigsFury" ready && count "YigsFury" ready == 0
        && any ((== String "c04278") . field "id" . snd) (pairs $ field "agendas" $ field "gameEntities" $ toJSON $ snapshotGame ready)
    when (target == "53061") $ assert "native setup default Ichtaca faction flags and three Tablets" $
      recorded "IchtacaIsSetAgainstYou" ready && not (recorded "AlejandroIsSetAgainstYou" ready)
        && tokenCount "Tablet" ready == 3 && tokenCount "Cultist" ready == 1 && tokenCount "ElderThing" ready == 1
    when (target == "54042") $ assert "native setup default Lodge membership recorded" $ recorded "TheInvestigatorsAreMembersOfTheLodge" ready
    when (target == "54049") $ assert "native setup default Anette not Carl recorded" $
      recorded "AnetteMasonIsPossessedByEvil" ready && not (recorded "CarlSanfordPossessesTheSecretsOfTheUniverse" ready)
  for_ [("52040","doubt"),("52059","doubt"),("52059","neither"),("53028","gaveCustodyToHarlan")
       ,("53045","5"),("53059","6"),("53061","standaloneAlejandroSetAgainst"),("53061","standaloneBothSetAgainst")
       ,("54042","notMembers"),("54049","carlSanfordPossessesTheSecretsOfTheUniverse")] $ \(target,branch) -> do
    let (_,_,_,before,schema,form) = fromJustNote "actual reviewed native setup case" $ find (\(t,_,_,_,_,_) -> t == target) actual
    ready <- run target branch [player] before schema form
    when (target == "52040") $ assert "native setup doubt branch increments original log by1" $ count "Doubt" ready == count "Doubt" before + 1
    when (target == "52059") $ assert ("native setup DimCarcosa actual selected count " <> branch) $
      count "Doubt" ready == count "Doubt" before + (if branch == "doubt" then 8 else 0)
        && count "Conviction" ready == count "Conviction" before
    when (target == "53028") $ assert "native setup actual relic custody Harlan not Alejandro" $
      recorded "TheInvestigatorsGaveCustodyOfTheRelicToHarlanEarnstone" ready && not (recorded "TheInvestigatorsGaveCustodyOfTheRelicToAlejandro" ready)
    when (target == "53045") $ assert "native setup paths upper endpoint5 recorded" $ count "PathsAreKnownToYou" ready == 5
    when (target == "53059") $ do
      assert "native setup actual Fury6 recorded" $ count "YigsFury" ready == 6
      assert "native setup Fury6 starts second actual agenda" $ any ((== String "c04279") . field "id" . snd) $ pairs $ field "agendas" $ field "gameEntities" $ toJSON $ snapshotGame ready
    when (target == "53061") $ assert ("native setup exact faction flags and token effects " <> branch) $
      if branch == "standaloneAlejandroSetAgainst"
        then not (recorded "IchtacaIsSetAgainstYou" ready) && recorded "AlejandroIsSetAgainstYou" ready
          && tokenCount "Cultist" ready == 4 && tokenCount "Tablet" ready == 0 && tokenCount "ElderThing" ready == 1
        else recorded "IchtacaIsSetAgainstYou" ready && recorded "AlejandroIsSetAgainstYou" ready
          && tokenCount "Cultist" ready == 1 && tokenCount "Tablet" ready == 0 && tokenCount "ElderThing" ready == 3
    when (target == "54042") $ assert "native setup notMembers does not record Lodge membership" $ not $ recorded "TheInvestigatorsAreMembersOfTheLodge" ready
    when (target == "54049") $ assert "native setup Carl not Anette actual recorded" $
      recorded "CarlSanfordPossessesTheSecretsOfTheUniverse" ready && not (recorded "AnetteMasonIsPossessedByEvil" ready)
  for_ ["53028","54016"] $ \target -> do
    before <- create target [player,other]
    schema <- schemaFor before
    ready <- run target (defaultBranch target) [player,other] before schema (formFor schema)
    when (target == "54016") $ do
      let investigators = map snd $ pairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame ready
      assert "native setup two actual prologue investigators and correct seat owners" $
        length investigators == 2 && all (\i -> field "id" i `elem` map String ["c05046","c05047","c05048","c05049"]) investigators
          && all (\who -> length (filter ((== toJSON who) . field "playerId") investigators) == 1) [player,other]

  -- Correct original Vue rotation contract, independently of the still-pending
  -- standalone rule-effect issue. The old bad-permutation reproduction remains
  -- immutable in phase5-core-destiny-repro-r1, not in the new accepted contract.
  let (_,_,_,destinyBefore,_,destinyForm) = fromJustNote "actual54017 case" $ find (\(t,_,_,_,_,_) -> t == "54017") actual
      untilDestiny budget s = case unwrap <$> Map.lookup player (gameQuestion $ snapshotGame s) of
        Just (PickDestiny drawings) -> pure (s,drawings)
        _ | budget > (0 :: Int) -> do
          answer <- ordinary player (headEx decks) s
          (next,_) <- applyAnswer s player (snapshotRevision s) answer >>= right
          untilDestiny (budget-1) next
        _ -> assert "destiny reproduction reaches genuine pending question" False >> pure (s,[])
  (destinySubmitted,_) <- applyAnswer destinyBefore player (snapshotRevision destinyBefore) (SettingsFormAnswer destinyForm) >>= right
  (destinyPending,drawings) <- untilDestiny 80 destinySubmitted
  assert "destiny defect reproduction has eight offered scopes without campaign" $
    length drawings == 8 && field "This" (toJSON $ gameMode $ snapshotGame destinyPending) == Null
  let scopes = [scope | DestinyDrawing scope _ <- drawings]
      cards = [card | DestinyDrawing _ card <- drawings]
      rearranged = zipWith DestinyDrawing scopes (drop 1 cards <> take 1 cards)
      rotateWith mask = typed @[DestinyDrawing] $ toJSON $ zipWith facingWire mask drawings
      originalPendingBytes = exportSnapshot destinyPending
  chosenA <- rotateWith $ replicate 4 True <> replicate 4 False
  chosenB <- rotateWith $ replicate 4 False <> replicate 4 True
  for_ (zip [0 :: Int ..] $ sequence $ replicate 8 [False,True]) $ \(index,mask) -> do
    rotated <- rotateWith mask
    assert ("destiny rotation exact original half-facing contract mask " <> tshow index) $
      isLeft (validateAnswer (snapshotGame destinyPending) player $ PickDestinyAnswer rotated) == (length (filter id mask) /= 4)
  for_ [("all upright",drawings),("swapped arcana",rearranged),("duplicate scope",headEx chosenA : headEx chosenA : drop 2 chosenA)
       ,("missing scope",drop 1 chosenA),("too many scopes",chosenA <> take 1 chosenA)] $ \(label,invalid) -> do
    outcome <- applyAnswer destinyPending player (snapshotRevision destinyPending) $ PickDestinyAnswer invalid
    assert ("destiny rotation actual submission rejects " <> label) $ isLeft outcome && exportSnapshot destinyPending == originalPendingBytes
  wrongSeat <- applyAnswer destinyPending other (snapshotRevision destinyPending) $ PickDestinyAnswer chosenA
  stale <- applyAnswer destinyPending player (snapshotRevision destinyPending+1) $ PickDestinyAnswer chosenA
  assert "destiny rotation genuine submission preserves recipient and revision guards" $ isLeft wrongSeat && isLeft stale
  assert "destiny effect genuine unique bare CheckDestiny after StandaloneSetup before investigator setup" $
    length (filter (== CheckDestiny) $ snapshotQueue destinyPending) == 1
      && case dropWhile (/= StandaloneSetup) (snapshotQueue destinyPending) of
        StandaloneSetup : CheckDestiny : SetupInvestigators : _ -> True
        _ -> False
  for_ [("missing",filter (/= CheckDestiny) $ snapshotQueue destinyPending)
       ,("duplicate",snapshotQueue destinyPending <> [CheckDestiny])
       ,("grouped",map (\m -> if m == CheckDestiny then Run [m] else m) $ snapshotQueue destinyPending)
       ,("wrapped",map (\m -> if m == CheckDestiny then Do m else m) $ snapshotQueue destinyPending)
       ,("bare plus meaningful wrapper",snapshotQueue destinyPending <> [Do CheckDestiny])
       ,("bare plus nested semantic wrapper",snapshotQueue destinyPending <> [When $ Run [Do CheckDestiny]])] $ \(label,queue) -> do
    let bad = destinyPending {snapshotQueue=queue}
        original = exportSnapshot bad
    rejected <- applyAnswer bad player (snapshotRevision bad) (PickDestinyAnswer chosenA)
    assert ("destiny effect malformed checkpoint rejected without state queue RNG mutation " <> label) $ isLeft rejected && exportSnapshot bad == original
  let missingDrawings = drop 1 drawings
  missingAnswer <- typed @[DestinyDrawing] $ toJSON $ zipWith facingWire [True,True,True,True,False,False,False] missingDrawings
  let missingScope = destinyPending {snapshotGame=(snapshotGame destinyPending) {gameQuestion=Map.singleton player $ PickDestiny missingDrawings}}
      missingBytes = exportSnapshot missingScope
  missingResult <- applyAnswer missingScope player (snapshotRevision missingScope) (PickDestinyAnswer missingAnswer)
  assert "destiny effect no-current-scope rejected without mutation" $ isLeft missingResult && exportSnapshot missingScope == missingBytes
  (destinyA,eventsA) <- applyAnswer destinyPending player (snapshotRevision destinyPending) (PickDestinyAnswer chosenA) >>= right
  (destinyB,eventsB) <- applyAnswer destinyPending player (snapshotRevision destinyPending) (PickDestinyAnswer chosenB) >>= right
  assert "destiny rotation two valid fixed-pair four-reversed submissions accepted" $ snapshotRevision destinyA == snapshotRevision destinyPending+1 && snapshotRevision destinyB == snapshotRevision destinyPending+1
  assert "destiny effect actual facing variants produce distinct saved rule states" $ exportSnapshot destinyA /= exportSnapshot destinyB
  (readyA,_,_) <- drive "54017" "destiny-defect-A" [player] destinyA
  (readyB,_,_) <- drive "54017" "destiny-defect-B" [player] destinyB
  let destinyMap selected = Map.fromList [(scope,card) | DestinyDrawing scope card <- selected]
      current selected = field "tarot" $ headEx $ array $ toJSON selected
      expectedTarot card = toJSON [(object ["tag" .= ("GlobalTarot" :: Text)],[card])]
      previousDeck = array $ field "tarotDeck" $ scenario destinyPending
      expectedDeck = filter (/= String "TemperanceXIV") previousDeck
  for_ [("reversed",chosenA,readyA,eventsA,-1 :: Int),("upright",chosenB,readyB,eventsB,1)] $ \(label,selected,ready,events,amount) -> do
    assert ("destiny effect exact current Temperance only at natural ready " <> label) $
      field "tarotCards" (scenario ready) == expectedTarot (current selected) && not (gameInSetup $ snapshotGame ready)
    assert ("destiny effect removes exactly current arcana preserving all other deck order " <> label) $
      field "tarotDeck" (scenario ready) == toJSON expectedDeck && length previousDeck == 22 && length expectedDeck == 21
    assert ("destiny effect exactly one corresponding tarot event " <> label) $
      filter ((== String "tarot") . field "type") (map eventJSON events) == [object ["type" .= ("tarot" :: Text),"value" .= [current selected]]]
    iid <- typed @InvestigatorId $ field "id" $ snd $ headEx $ pairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame ready
    env <- makeEnv (snapshotGame ready) []
    modifiers <- runCore env $ getModifiers iid
    assert ("destiny effect actual computed Sanity modifier " <> label) $
      object ["tag" .= ("SanityModifier" :: Text),"contents" .= amount] `elem` map toJSON modifiers
  assert "destiny effect distinct natural ready states retain identical RNG" $ exportSnapshot readyA /= exportSnapshot readyB && gameSeed (snapshotGame readyA) == gameSeed (snapshotGame readyB)
  let barrier = Ask player $ ChooseOne [Label "controlled-destiny-queue-barrier" [Noop]]
      controlled based queue = (boundary (gameCustomCards $ snapshotGame based) do
        env <- makeEnv ((snapshotGame based) {gameQuestion=mempty,gameRetainedQuestion=False}) queue
        advance env
        finish (snapshotHistoricalBootstrap based) (snapshotRevision based) env) >>= right
      mapped card = map (\m -> if m == CheckDestiny then ResolveDestiny card else m) $ snapshotQueue destinyPending
  (staged,stagedEvents) <- controlled destinyPending $ SetDestiny (destinyMap chosenA) : barrier : snapshotQueue destinyPending
  assert "destiny effect controlled barrier preserves exact queue except original checkpoint" $
    toJSON (snapshotQueue staged) == toJSON (mapped $ case headEx chosenA of DestinyDrawing _ card -> card)
  assert "destiny effect not active before original checkpoint and no premature tarot event" $
    field "tarotCards" (scenario staged) == field "tarotCards" (scenario destinyPending)
      && field "tarotDeck" (scenario staged) == field "tarotDeck" (scenario destinyPending)
      && null (filter ((== String "tarot") . field "type") $ map eventJSON stagedEvents)
  stagedImported <- importSnapshot (exportSnapshot staged) >>= right
  let barrierAnswer = Answer $ QuestionResponse 0 (Just player) Nothing
  (stagedFollowing,_) <- applyAnswer staged player (snapshotRevision staged) barrierAnswer >>= right
  (stagedReplay,_) <- applyAnswer stagedImported player (snapshotRevision stagedImported) barrierAnswer >>= right
  (stagedReady,_,_) <- drive "54017" "controlled-destiny-stage" [player] stagedFollowing
  (stagedReadyReplay,_,_) <- drive "54017" "controlled-destiny-stage" [player] stagedReplay
  assert "destiny effect export import at deferred checkpoint exact whole continuation" $ exportSnapshot stagedReady == exportSnapshot stagedReadyReplay
  assert "destiny effect deferred checkpoint installs same one card exactly once" $ field "tarotCards" (scenario stagedReady) == expectedTarot (current chosenA) && field "tarotDeck" (scenario stagedReady) == toJSON expectedDeck
  campaignRequest <- typed @CreateRequest $ object ["campaign" .= ("54" :: Text),"scenario" .= ("54017" :: Text),"seed" .= (201020 :: Int),"players" .= [player]]
  (realCampaign,_) <- createSession campaignRequest >>= right
  let campaignValue = field "This" $ toJSON $ gameMode $ snapshotGame realCampaign
  combinedMode <- typed @GameMode $ object ["This" .= campaignValue,"That" .= scenario destinyPending]
  let combined = destinyPending {snapshotGame=(snapshotGame destinyPending) {gameMode=combinedMode}}
  (campaignStaged,_) <- controlled combined $ SetDestiny (destinyMap chosenA) : barrier : snapshotQueue destinyPending
  assert "destiny effect controlled actual campaign54 stores all8 and leaves original checkpoint" $
    field "destiny" (field "This" $ toJSON $ gameMode $ snapshotGame campaignStaged) == toJSON (destinyMap chosenA)
      && toJSON (snapshotQueue campaignStaged) == toJSON (snapshotQueue destinyPending)
      && field "tarotCards" (field "That" $ toJSON $ gameMode $ snapshotGame campaignStaged) == field "tarotCards" (scenario destinyPending)
  (campaignChecked,_) <- controlled combined [SetDestiny $ destinyMap chosenB,CheckDestiny,barrier]
  assert "destiny effect controlled campaign still installs via original CheckDestiny" $
    field "tarotCards" (field "That" $ toJSON $ gameMode $ snapshotGame campaignChecked) == expectedTarot (current chosenB)
  let checkedCombined = combined {snapshotGame=(snapshotGame campaignChecked) {gameQuestion=mempty,gameRetainedQuestion=False}}
  (campaignResolveIgnored,_) <- controlled checkedCombined [ResolveDestiny $ case headEx chosenA of DestinyDrawing _ card -> card,barrier]
  assert "destiny effect ResolveDestiny does not overwrite existing campaign tarot or deck" $
    field "tarotCards" (field "That" $ toJSON $ gameMode $ snapshotGame campaignResolveIgnored) == field "tarotCards" (field "That" $ toJSON $ gameMode $ snapshotGame campaignChecked)
      && field "tarotDeck" (field "That" $ toJSON $ gameMode $ snapshotGame campaignResolveIgnored) == field "tarotDeck" (field "That" $ toJSON $ gameMode $ snapshotGame campaignChecked)
  for_ ["takeCards","rejectFate"] $ \branch -> do
    (alternative,seen,trace) <- drive "54017" branch [player] destinySubmitted
    (alternativeReplay,_,_) <- drive "54017" branch [player] destinySubmitted
    assert ("destiny effect natural alternative actually chosen " <> branch) $ branch `elem` seen
    assert ("destiny effect natural alternative has no Destiny submission or activation " <> branch) $
      not (any ((== String "PickDestiny") . field "tag") trace)
        && field "tarotCards" (scenario alternative) == toJSON ([] :: [Value])
    assert ("destiny effect natural alternative entire replay exact " <> branch) $ exportSnapshot alternative == exportSnapshot alternativeReplay
    alternativeSave <- saved alternative
    alternativeReplaySave <- saved alternativeReplay
    emit "DESTINY_ALTERNATIVE_FIXTURE " $ object ["branch" .= branch,"readySave" .= alternativeSave,"replaySave" .= alternativeReplaySave,"trace" .= trace]
  optionalRequest <- typed @CreateRequest $ object ["scenario" .= ("54017" :: Text),"seed" .= (201020 :: Int),"players" .= [player],"tarot" .= True]
  (optionalBefore,_) <- createSession optionalRequest >>= right
  optionalSchema <- schemaFor optionalBefore
  (optionalSubmitted,_) <- applyAnswer optionalBefore player (snapshotRevision optionalBefore) (SettingsFormAnswer $ formFor optionalSchema) >>= right
  (optionalReady,_,optionalTrace) <- drive "54017" "rejectFate" [player] optionalSubmitted
  (optionalReplay,_,_) <- drive "54017" "rejectFate" [player] optionalSubmitted
  assert "destiny effect normal optional Chaos reading remains actionable and active" $
    any (\entry -> field "tag" entry == String "ChooseOne" && any ((== "tarotChaos") . choiceKey) (array $ field "choices" $ unwrappedValue $ field "question" entry)) optionalTrace
      && length (array $ field "tarotCards" $ scenario optionalReady) == 1
  assert "destiny effect normal optional reading entire replay exact" $ exportSnapshot optionalReady == exportSnapshot optionalReplay
  (optionalDestinyReady,_,optionalDestinyTrace) <- drive "54017" "default" [player] optionalSubmitted
  (optionalDestinyReplay,_,_) <- drive "54017" "default" [player] optionalSubmitted
  let optionalOffers = [entry | entry <- optionalDestinyTrace,field "tag" entry == String "PickDestiny"]
      optionalCards = [field "tarot" row | entry <- optionalOffers,row <- array $ field "contents" $ field "answer" entry
        ,field "scenario" row == String "theWitchingHour"]
      optionalChaos = [entry | entry <- optionalDestinyTrace,field "tag" entry == String "ChooseOne"
        ,any ((== "tarotChaos") . choiceKey) $ array $ field "choices" $ unwrappedValue $ field "question" entry]
  assert "destiny effect enabled optional reading then Destiny suppresses extra reading prompt" $
    length optionalOffers == 1 && null optionalChaos
  assert "destiny effect enabled optional reading then Destiny exact one current card" $
    length optionalCards == 1 && field "tarotCards" (scenario optionalDestinyReady) == toJSON [(object ["tag" .= ("GlobalTarot" :: Text)],[headEx optionalCards])]
      && length (array $ field "tarotDeck" $ scenario optionalDestinyReady) == 21
  assert "destiny effect enabled optional reading then Destiny whole replay exact" $
    exportSnapshot optionalDestinyReady == exportSnapshot optionalDestinyReplay
  optionalDestinyValue <- saved optionalDestinyReady
  optionalDestinyReplayValue <- saved optionalDestinyReplay
  emit "DESTINY_OPTIONAL_COMBINATION_FIXTURE " $ object
    ["readySave" .= optionalDestinyValue,"replaySave" .= optionalDestinyReplayValue,"trace" .= optionalDestinyTrace]
  restoredPending <- importSnapshot originalPendingBytes >>= right
  (replayedA,_) <- applyAnswer restoredPending player (snapshotRevision restoredPending) (PickDestinyAnswer chosenA) >>= right
  assert "destiny defect reproduction exact import submission replay and old snapshot immutable" $
    exportSnapshot replayedA == exportSnapshot destinyA && exportSnapshot destinyPending == originalPendingBytes
  pendingValue <- saved destinyPending
  afterA <- saved destinyA
  afterB <- saved destinyB
  finalA <- saved readyA
  finalB <- saved readyB
  stagedValue <- saved staged
  stagedFinalValue <- saved stagedReady
  stagedReplayValue <- saved stagedReadyReplay
  campaignStagedValue <- saved campaignStaged
  campaignCheckedValue <- saved campaignChecked
  campaignIgnoredValue <- saved campaignResolveIgnored
  optionalValue <- saved optionalReady
  optionalReplayValue <- saved optionalReplay
  emit "DESTINY_EFFECT_FIXTURE " $ object
    ["target" .= ("54017" :: Text),"scope" .= ("Natural standalone facing effects; separate explicitly controlled queue-timing and These-campaign message contracts, NOT a complete natural campaign playthrough" :: Text)
    ,"pendingSave" .= pendingValue,"originalAnswer" .= chosenA,"permutedAnswer" .= chosenB
    ,"afterOriginalSave" .= afterA,"afterPermutedSave" .= afterB,"readyOriginalSave" .= finalA,"readyPermutedSave" .= finalB
    ,"originalEvents" .= map eventJSON eventsA,"permutedEvents" .= map eventJSON eventsB
    ,"controlledStagedSave" .= stagedValue,"controlledReadySave" .= stagedFinalValue,"controlledReplaySave" .= stagedReplayValue
    ,"controlledCampaignStagedSave" .= campaignStagedValue,"controlledCampaignCheckedSave" .= campaignCheckedValue,"controlledCampaignIgnoredResolveSave" .= campaignIgnoredValue
    ,"optionalReadySave" .= optionalValue,"optionalReplaySave" .= optionalReplayValue,"optionalTrace" .= optionalTrace]
