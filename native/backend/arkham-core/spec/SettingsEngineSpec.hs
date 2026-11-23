module SettingsEngineSpec (settingsEngineSpecs,omittedStandaloneSettingsSpecs,heartPart2SettingsSpecs,complete,completeFrom) where

import Arkham.Prelude hiding (assert)
import Arkham.Game (Game (..), GameMode)
import Arkham.Id
import Arkham.Question
import Arkham.Message (Message (..), pattern SetChaosTokens)
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation (offeredContinuation,indexedChoices)
import Arkham.Native.Settings.Engine qualified as Engine
import Arkham.Native.Settings.Types
import Arkham.Native.StrictJSON (validateJsonProperties)
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.Either (isLeft,isRight)
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

array :: Value -> [Value]
array (Array xs) = toList xs
array _ = []

-- A bounded UI-like driver: only selects actual offered entry IDs from the
-- core preview, never composes arbitrary log keys, cards or raw messages.
complete :: Engine.ResolvedSchema -> IO SettingsFormRequest
complete schema = completeFrom schema $ SettingsFormRequest (Engine.schemaIdentity schema) mempty

completeFrom :: Engine.ResolvedSchema -> SettingsFormRequest -> IO SettingsFormRequest
completeFrom schema initial = loop (40 :: Int) initial
 where
  loop budget draft = do
    preview <- right $ Engine.previewForm schema draft
    canonical <- typed $ field "submission" preview
    if field "canSubmit" preview == Bool True then pure canonical else do
      let edits = mapMaybe fill $ filter ((== String "visible") . field "visibility") $ array $ field "rows" preview
          next = canonical {valuesByRowId = Map.union (Map.fromList edits) $ valuesByRowId canonical}
      when (budget <= 0 || next == draft) $ do
        BLC.putStrLn $ "SETTINGS_ENGINE_INCOMPLETE " <> encode preview
        assert "settings engine bounded offered-only driver completes" False
      loop (budget-1) next
  fill row = do
    String ident <- Just $ field "id" row
    String kind <- Just $ field "kind" row
    let available = [entry | entry <- array $ field "choices" row,field "visibility" entry == String "visible"]
        entryIds = [ident' | entry <- available,String ident' <- [field "id" entry]]
        current = Aeson.parseMaybe parseJSON $ field "value" row
    value <- case kind of
      "ChooseNum" -> case current of Nothing -> Just $ FormInteger $ fromMaybe 0 $ Aeson.parseMaybe parseJSON $ field "minimum" row; _ -> Nothing
      _ | kind `elem` ["PickKey","ChooseKey","ChooseOption","ChooseRecordable","SetPartnerKilled"] ->
          case current of
            Just (FormChoice (Just _)) -> Nothing
            _ -> FormChoice . Just <$> listToMaybe entryIds
      _ | kind `elem` ["Record","CrossOut","ToggleRecords","ToggleCrossedOut","ChooseRecordables"] ->
          let entries = case current of Just (FormEntries e) -> e; _ -> mempty
              added = Map.union entries $ Map.fromList [(k,False) | k <- entryIds]
           in if current == Just (FormEntries added) then Nothing else Just $ FormEntries added
      _ -> Nothing
    pure (ident,value)

-- Focused initialization adapter only, not Part1 completion or a rule matrix.
heartPart2SettingsSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
heartPart2SettingsSpecs player chooseActual = do
  request <- typed $ object ["scenario" .= ("53048" :: Text),"seed" .= (171048 :: Int),"players" .= [player]]
  original <- fst <$> (createSession request >>= right)
  let game = snapshotGame original
      before = exportSnapshot original
      replace key val (Object o) = Object $ KM.insert (K.fromText key) val o
      replace _ _ _ = error "Heart fixture requires object"
  schema <- right $ Engine.resolveSchema game player
  let form = SettingsFormRequest (Engine.schemaIdentity schema) mempty
  preview <- right $ Engine.previewForm schema form
  assert "heart2 exact empty schema keeps actual target and base identity" $
    field "scenario" (field "context" $ Engine.schemaView schema) == String "53048"
      && field "definitionScenario" (field "context" $ Engine.schemaView schema) == String "04205b"
      && null (array $ field "rows" preview) && field "canSubmit" preview == Bool True
  messages <- right $ Engine.compileSubmission schema form
  let expectedFaces = map String
        ["PlusOne","Zero","Zero","Zero","MinusOne","MinusTwo","MinusTwo","MinusThree"
        ,"MinusFive","Skull","Skull","Cultist","Tablet","ElderThing","AutoFail","ElderSign"]
      bagFaces snapshot = map (field "chaosTokenFace") $ array $ field "chaosTokens" $
        field "chaosBag" $ field "That" $ toJSON $ gameMode $ snapshotGame snapshot
  assert "heart2 settings compile only log; original bag is deferred past StartScenario" $ case messages of
    [SetCampaignLog _] -> True
    _ -> False
  assert "heart2 nonempty form and absent/nonsetup question are rejected" $
    isLeft (Engine.compileSubmission schema $ form {valuesByRowId=Map.singleton "paths" (FormInteger 3)})
      && isLeft (Engine.resolveSchema (game {gameQuestion=mempty}) player)
      && isLeft (Engine.resolveSchema (game {gameInSetup=False}) player)
  campaignRequest <- typed $ object ["campaign" .= ("53" :: Text),"seed" .= (171048 :: Int),"players" .= [player]]
  campaign <- fst <$> (createSession campaignRequest >>= right)
  combined <- typed @GameMode $ object
    ["This" .= field "This" (toJSON $ gameMode $ snapshotGame campaign)
    ,"That" .= field "That" (toJSON $ gameMode game)]
  assert "heart2 controlled These cannot use standalone initialization" $
    isLeft $ Engine.resolveSchema (game {gameMode=combined}) player
  let scenario = field "That" $ toJSON $ gameMode game
      changedBag = replace "forceDraw" (toJSON [String "Zero"]) $ field "chaosBag" scenario
  occupied <- typed @GameMode $ object ["That" .= replace "chaosBag" changedBag scenario]
  assert "heart2 existing bag state cannot be overwritten even with empty token list" $
    isLeft $ Engine.resolveSchema (game {gameMode=occupied}) player
  restored <- importSnapshot before >>= right
  assert "heart2 preview compile and import leave original snapshot exact" $
    exportSnapshot original == before && exportSnapshot restored == before
  (submitted,_) <- applyAnswer original player (snapshotRevision original) (SettingsFormAnswer form) >>= right
  assert "heart2 real submit reaches ChooseDeck without prematurely installing bag or Paths" $
    null (bagFaces submitted)
      && any ((== ChooseDeck) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame submitted)
      && field "This" (toJSON $ gameMode $ snapshotGame submitted) == Null
  (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer form) >>= right
  assert "heart2 imported pending submit replays entire snapshot exactly" $
    exportSnapshot replayed == exportSnapshot submitted && exportSnapshot original == before
  stale <- applyAnswer submitted player (snapshotRevision original) (SettingsFormAnswer form)
  assert "heart2 stale submission cannot reinstall bag" $ isLeft stale
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json"
    >>= either (\reason -> P.putStrLn reason >> exitFailure) pure
  let ready budget current
        | not (gameInSetup $ snapshotGame current)
        , any (\case PlayerWindowChooseOne {} -> True; _ -> False)
            (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame current) = pure current
        | budget <= (0 :: Int) = error "Heart2 actual setup did not reach PlayerWindow"
        | otherwise = do
            response <- chooseActual player (headEx decks) current
            (next,_) <- applyAnswer current player (snapshotRevision current) response >>= right
            ready (budget-1) next
  atWindow <- ready 30 submitted
  assert "heart2 original StartScenario and natural setup retain all sixteen original tokens" $
    sort (map encode $ bagFaces atWindow) == sort (map encode expectedFaces)
  submittedRestored <- importSnapshot (exportSnapshot submitted) >>= right
  atWindowAgain <- ready 30 submittedRestored
  assert "heart2 post-deck startup replay preserves complete game queue RNG and bag identities" $
    exportSnapshot atWindow == exportSnapshot atWindowAgain
  windowRestored <- importSnapshot (exportSnapshot atWindow) >>= right
  assert "heart2 importing completed setup never reinitializes its bag" $
    exportSnapshot windowRestored == exportSnapshot atWindow

-- Focused adapter checks only: no scenario completion or old rule matrix.
omittedStandaloneSettingsSpecs :: PlayerId -> IO ()
omittedStandaloneSettingsSpecs player = do
  let create sid = do
        request <- typed $ object ["scenario" .= (sid :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
        fst <$> (createSession request >>= right)
      offered snapshot = right $ Engine.resolveSchema (snapshotGame snapshot) player
      blank schema = SettingsFormRequest (Engine.schemaIdentity schema) mempty
  original <- create "01104"
  originalSchema <- offered original
  originalEmpty <- right $ Engine.compileForm originalSchema $ blank originalSchema
  war <- create "86001"
  fortune <- create "88001"
  warSchema <- offered war
  fortuneSchema <- offered fortune
  for_ [(war,warSchema),(fortune,fortuneSchema)] $ \(snapshot,schema) -> do
    let request = blank schema
        invalid = request {valuesByRowId=Map.singleton "invented-row" (FormBoolean True)}
        before = exportSnapshot snapshot
    preview <- right $ Engine.previewForm schema request
    assert "omitted exact standalone preview has empty rows and permits empty submit" $
      null (array $ field "rows" preview) && field "canSubmit" preview == Bool True
    messages <- right $ Engine.compileSubmission schema request
    assert "omitted standalone compiles only original empty SetCampaignLog" $ case messages of
      [SetCampaignLog logValue] -> toJSON logValue == toJSON originalEmpty
      _ -> False
    assert "omitted standalone rejects nonempty caller values" $ isLeft $ Engine.compileSubmission schema invalid
    assert "omitted preview and compilation leave snapshot unchanged" $ exportSnapshot snapshot == before
    assert "omitted resolver still requires actual settings question" $ isLeft $
      Engine.resolveSchema ((snapshotGame snapshot) {gameQuestion=mempty}) player
  assert "omitted schemas remain bound to exact scenario" $
    Engine.schemaIdentity warSchema /= Engine.schemaIdentity fortuneSchema
      && isLeft (Engine.compileSubmission fortuneSchema $ blank warSchema)
  campaignRequest <- typed $ object ["campaign" .= ("01" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  campaign <- fst <$> (createSession campaignRequest >>= right)
  -- Explicit controlled context predicate, not a claim of natural These flow.
  combined <- typed @GameMode $ object
    ["This" .= field "This" (toJSON $ gameMode $ snapshotGame campaign)
    ,"That" .= field "That" (toJSON $ gameMode $ snapshotGame war)]
  assert "omitted standalone policy rejects controlled These context" $ isLeft $
    Engine.resolveSchema ((snapshotGame war) {gameMode=combined}) player
  assert "omitted standalone policy rejects campaign-only context" $ isLeft $
    Engine.resolveSchema ((snapshotGame war) {gameMode=gameMode $ snapshotGame campaign}) player
  declared <- create "07056"
  declaredSchema <- offered declared
  declaredPreview <- right $ Engine.previewForm declaredSchema $ blank declaredSchema
  assert "declared settings retain nonempty original controls" $
    not $ null $ array $ field "rows" declaredPreview
  declaredForm <- complete declaredSchema
  let row = "scenario:07056/0"
      selected = declaredForm {valuesByRowId=Map.insert row
        (FormEntries $ Map.singleton (row <> "/entry/0") True) (valuesByRowId declaredForm)}
  declaredLog <- right $ Engine.compileForm declaredSchema selected
  assert "declared typed choice still records original memory rather than empty fallback" $
    "ADecisionToStickTogether" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode declaredLog)
      && toJSON declaredLog /= toJSON originalEmpty

settingsEngineSpecs :: PlayerId -> PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
settingsEngineSpecs player other chooseActual = do
  let rawDecode text = do
        let bytes = encodeUtf8 text
        validateJsonProperties bytes
        first (const "Invalid JSON") $ eitherDecodeStrict' @Value bytes
  for_ (["{}","[]","null","true","-1","9007199254740993","{\"a\":{},\"b\":[1,{\"a\":2}]}","{\"a\":\"braces { } \\\"quoted\\\"\",\"b\":false}","{\"名称\":\"中文\",\"key\":1}"] :: [Text]) $ \text ->
    assert "settings JSON property guard valid structure" $ isRight $ rawDecode text
  for_ (["{\"a\":1,\"a\":2}","{\"a\":1,\"\\u0061\":2}","{\"draft\":{\"rowId\":1,\"rowId\":2}}","[{\"a\":1,\"a\":2}]","{\"a\":1,}","[1,]","{\"a\" 1}","true false","\"unterminated","{\"a\":[1,,2]}"] :: [Text]) $ \text ->
    assert "settings JSON property guard malformed/duplicate rejected" $ isLeft $ rawDecode text
  let tooDeep = P.replicate 130 '[' <> "0" <> P.replicate 130 ']'
  assert "settings JSON property guard explicit nesting bound" $ isLeft $ rawDecode $ fromString tooDeep
  assert "settings JSON property guard exact Int64 value preserved" $ rawDecode "9007199254740993" == Right (toJSON (9007199254740993 :: Int))
  let create sid = do
        req <- typed $ object ["scenario" .= (sid :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
        fst <$> (createSession req >>= right)
      offered snapshot = right $ Engine.resolveSchema (snapshotGame snapshot) player
      scenarios = ["01104","01120","01142","05238","05325","06119","07056","07123","07163","07198","07231","07274","07311","08501b","08501c","08549","08596","08621","08648a","08648b"]
  completed <- for scenarios $ \sid -> do
    snapshot <- create sid
    let bytes = exportSnapshot snapshot
    schema <- offered snapshot
    form <- complete schema
    logValue <- right $ Engine.compileForm schema form
    preview <- right $ settingsPreview snapshot player (snapshotRevision snapshot) form
    assert ("settings engine actual offered standalone compiles " <> sid) $ field "canSubmit" preview == Bool True
    assert ("settings engine preview exact whole snapshot queue RNG unchanged " <> sid) $ exportSnapshot snapshot == bytes
    let rejected draft = isLeft $ Engine.compileForm schema draft
    assert ("settings engine rejects foreign schema " <> sid) $ rejected $ form {formSchemaId = "foreign"}
    assert ("settings engine rejects future/unknown row " <> sid) $ rejected $ form {valuesByRowId = Map.insert "foreign-future-secret" (FormBoolean False) $ valuesByRowId form}
    let forcedIds = [ident | row <- array $ field "rows" preview,field "valueSource" row == String "forced",String ident <- [field "id" row]]
    for_ (filter (`notElem` forcedIds) $ Map.keys $ valuesByRowId form) $ \ident ->
      assert ("settings engine missing active row never implicit default " <> ident) $ rejected $ form {valuesByRowId = Map.delete ident $ valuesByRowId form}
    assert ("settings engine known wrong recipient rejected " <> sid) $ isLeft $ Engine.resolveSchema (snapshotGame snapshot) other
    assert ("settings engine stale preview rejected " <> sid) $ isLeft $ settingsPreview snapshot player (snapshotRevision snapshot+1) form
    direct <- handleAnswerPure (snapshotGame snapshot) player $ SettingsFormAnswer form
    assert ("settings engine direct unprepared answer rejected " <> sid) $ case direct of Unhandled {} -> True; _ -> False
    wireAnswer <- typed @Answer $ object ["tag" .= ("SettingsFormAnswer" :: Text),"contents" .= form]
    (after,_) <- applyAnswer snapshot player (snapshotRevision snapshot) wireAnswer >>= right
    assert ("settings engine ACTUAL form submission advances to deck " <> sid) $ snapshotRevision after == 1
      && any ((== ChooseDeck) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame after)
    assert ("settings engine actual submission preserves original " <> sid) $ exportSnapshot snapshot == bytes
    imported <- importSnapshot (exportSnapshot after) >>= right
    assert ("settings engine submitted full snapshot import exact " <> sid) $ exportSnapshot imported == exportSnapshot after
    assert ("settings JSON guard accepts actual entire snapshot " <> sid) $ isRight $ validateJsonProperties $ BLC.toStrict $ exportSnapshot after
    assert ("settings engine no form outside actual question " <> sid) $ isLeft $ Engine.resolveSchema (snapshotGame after) player
    BLC.putStrLn $ "SETTINGS_ENGINE_FIXTURE " <> encode (object ["scenario" .= sid,"schema" .= Engine.schemaView schema,"form" .= form,"log" .= logValue])
    pure (sid,snapshot,schema,form,toJSON logValue)
  let get sid = fromJustNote "actual completed settings context" $ find (\(s,_,_,_,_) -> s == sid) completed
      (_,midnight,midSchema,midForm,_) = get "01120"
      (_,_,devSchema,devForm,devLog) = get "01142"
      (_,_,numSchema,numForm,_) = get "05325"
      change ident value form = form {valuesByRowId = Map.insert ident value $ valuesByRowId form}
      previewSubmit schema draft = do
        preview <- right $ Engine.previewForm schema draft
        typed $ field "submission" preview
  assert "settings engine numeric effective default min zero rejects negative" $ isLeft $ Engine.compileForm numSchema $ change "scenario:05325/0" (FormInteger (-1)) numForm
  assert "settings engine actual explicit max rejects seven" $ isLeft $ Engine.compileForm numSchema $ change "scenario:05325/0" (FormInteger 7) numForm
  assert "settings engine wrong form type rejected" $ isLeft $ Engine.compileForm midSchema $ change "scenario:01120/0" (FormInteger 1) midForm
  let (_,_,lateSchema,lateForm,_) = get "07311"
  -- Use an actual unbounded Innsmouth memory form to ensure no invented cap;
  -- the numeric-specific high-Int64 case below selects the actual count row.
  for_ [(schema,form) | (_,_,schema,form,_) <- completed] $ \(schema,form) -> do
    preview <- right $ Engine.previewForm schema form
    for_ [r | r <- array $ field "rows" preview,field "visibility" r == String "visible",field "kind" r == String "ChooseNum",field "maximum" r == Null] $ \r -> do
      ident <- typed $ field "id" r
      big <- right $ Engine.compileForm schema $ change ident (FormInteger 9007199254740993) form
      assert "settings engine unbounded Int64 count preserved above double range" $ "9007199254740993" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode big)
  assert "settings engine CrossOut unchecked records all six exact faces" $ length (concatMap (array . snd) $ objectPairs $ field "recordedSets" devLog) == 6
  let devEntries = fromJustNote "actual cultist entries" $ Map.lookup "scenario:01142/2" $ valuesByRowId devForm
  case devEntries of
    FormEntries entries -> do
      let ident = fst $ headEx $ Map.toAscList entries
      crossed <- right $ Engine.compileForm devSchema $ change "scenario:01142/2" (FormEntries $ Map.insert ident True entries) devForm
      assert "settings engine checked cultist crossed not duplicate recorded" $ "CrossedOut" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode crossed)
      assert "settings engine missing visible entry rejected not unchecked" $ isLeft $ Engine.compileForm devSchema $ change "scenario:01142/2" (FormEntries $ Map.delete ident entries) devForm
      assert "settings engine unknown entry rejected" $ isLeft $ Engine.compileForm devSchema $ change "scenario:01142/2" (FormEntries $ Map.insert "01121a" True entries) devForm
    _ -> assert "settings engine actual record form kind" False
  let nonQuestion = (snapshotGame midnight) {gameQuestion = mempty}
  assert "settings engine no synthetic authority without pending question" $ isLeft $ Engine.resolveSchema nonQuestion player
  -- Positive non-default fields, not merely parsing hidden partner defaults.
  let (_,partnerSnapshot,partnerSchema,partnerForm,_) = get "08501c"
      (_,crashSnapshot,crashSchema,crashForm,_) = get "08501b"
      (_,_,memorySchema,memoryForm,_) = get "07056"
  partners <- completeFrom partnerSchema $ change "scenario:08501c/3" (FormBoolean True) partnerForm
  partnerPreview <- right $ Engine.previewForm partnerSchema partners
  let amyRow = "scenario:08501c/4/0"
  resolute <- typed @FormValue $ object ["kind" .= ("partner" :: Text),"damage" .= (1 :: Int),"horror" .= (3 :: Int),"status" .= ("Resolute" :: Text)]
  partnerLog <- right $ Engine.compileForm partnerSchema $ change amyRow resolute partners
  let partnerRows = map snd $ objectPairs $ field "partners" $ toJSON partnerLog
  assert "settings engine real nine-member partner group editable" $ length partnerRows == 9 && field "canSubmit" partnerPreview == Bool True
  assert "settings engine Resolute max-bound damage/horror retained" $ any (\p -> field "status" p == String "Resolute" && field "damage" p == toJSON (1 :: Int) && field "horror" p == toJSON (3 :: Int)) partnerRows
  tooMuch <- typed @FormValue $ object ["kind" .= ("partner" :: Text),"damage" .= (2 :: Int),"horror" .= (3 :: Int),"status" .= ("Safe" :: Text)]
  assert "settings engine partner role exact damage bound rejects overflow" $ isLeft $ Engine.compileForm partnerSchema $ change amyRow tooMuch partners
  let hiddenPartners = change "scenario:08501c/3" (FormBoolean False) partners
  assert "settings engine hidden group rejects stale partner values" $ isLeft $ Engine.compileForm partnerSchema hiddenPartners
  prunedPartners <- previewSubmit partnerSchema hiddenPartners
  noPartners <- right $ Engine.compileForm partnerSchema prunedPartners
  assert "settings engine preview prunes hidden group without effects" $ null $ objectPairs $ field "partners" $ toJSON noPartners
  victim <- completeFrom crashSchema $ change "scenario:08501b/2" (FormBoolean True) crashForm
  victimLog <- right $ Engine.compileForm crashSchema victim
  assert "settings engine selected crash victim creates exactly one eliminated role" $ case map snd $ objectPairs $ field "partners" $ toJSON victimLog of
    [p] -> field "status" p == String "Eliminated" && field "damage" p == toJSON (0 :: Int) && field "horror" p == toJSON (0 :: Int)
    _ -> False
  assert "settings engine crash victim exact typed record written" $ "WasKilledInThePlaneCrash" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode victimLog)
  for_ [(partnerSnapshot,change amyRow resolute partners),(crashSnapshot,victim)] $ \(before,submitted) -> do
    let bytes = exportSnapshot before
    (_,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer submitted) >>= right
    assert "settings engine positive partner/crash ACTUAL submit old snapshot unchanged" $ exportSnapshot before == bytes
  camp <- completeFrom crashSchema $ change "scenario:08501b/0" (FormBoolean True) crashForm
  campLog <- right $ Engine.compileForm crashSchema camp
  assert "settings engine enabled manual Camp explicit offered choice recorded" $ length (array $ field "recorded" $ toJSON campLog) == 1
  let memoryRow = "scenario:07056/0"
      recovered = change memoryRow (FormEntries $ Map.singleton (memoryRow <> "/entry/0") True) memoryForm
  memoryLog <- right $ Engine.compileForm memorySchema recovered
  assert "settings engine checked memory is typed Recorded not CrossedOut" $ "ADecisionToStickTogether" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode memoryLog)
    && not ("CrossedOut" `isInfixOf` decodeUtf8 (BLC.toStrict $ encode memoryLog))
  -- 53048 now has dedicated fresh-standalone guards in heartPart2SettingsSpecs.
  for_ ["53066"] $ \sid -> do
    snapshot <- create sid
    assert ("settings engine Return-To remains explicitly pending " <> sid) $ isLeft $ Engine.resolveSchema (snapshotGame snapshot) player
  -- Full campaign starts follow the real deck/trauma/lead/continue prompts.
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason -> P.putStrLn reason >> exitFailure) pure
  let deck = headEx decks
      untilSettings budget snapshot
        | any ((== PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = assert "settings engine real campaign reaches settings" False >> pure snapshot
        | otherwise = do
            answer <- chooseActual player deck snapshot
            (next,_) <- applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            untilSettings (budget-1) next
  for_ [("50025","01120"),("50032","01142"),("54046","05238"),("54056","05325")] $ \(target,base) -> do
    original <- create target
    aliasSchema <- offered original
    aliasForm <- complete aliasSchema
    let (_,_,baseSchema,baseForm,_) = get base
        context = field "context" $ Engine.schemaView aliasSchema
        originalBytes = exportSnapshot original
    assert ("settings reviewed Return-To keeps actual target and definition identity " <> target) $
      field "scenario" context == String target && field "definitionScenario" context == String base
        && Engine.schemaIdentity aliasSchema /= Engine.schemaIdentity baseSchema
    assert ("settings reviewed Return-To rejects base schema " <> target) $ isLeft $ Engine.compileForm aliasSchema baseForm
    assert ("settings reviewed Return-To rejects target schema in base " <> target) $ isLeft $ Engine.compileForm baseSchema aliasForm
    baseLog <- right $ Engine.compileForm baseSchema baseForm
    aliasLog <- right $ Engine.compileForm aliasSchema aliasForm
    assert ("settings reviewed Return-To default effects exact base definition " <> target) $ toJSON aliasLog == toJSON baseLog
    (submitted,_) <- applyAnswer original player (snapshotRevision original) (SettingsFormAnswer aliasForm) >>= right
    assert ("settings reviewed Return-To actual settings submission reaches ChooseDeck " <> target) $
      any ((== ChooseDeck) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame submitted)
    restored <- importSnapshot originalBytes >>= right
    (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer aliasForm) >>= right
    assert ("settings reviewed Return-To import replay entire save exact " <> target) $ exportSnapshot replayed == exportSnapshot submitted
    assert ("settings reviewed Return-To parent immutable " <> target) $ exportSnapshot original == originalBytes
    let steedQuestion = \case
          QuestionLabel label Nothing (ChooseOne [Label yes yesMessages,Label no []]) ->
            "$" `T.isPrefixOf` label && ".nightgauntSteed" `T.isSuffixOf` label
              && ".yes" `T.isSuffixOf` yes && ".no" `T.isSuffixOf` no && not (null yesMessages)
          _ -> False
        reachSetup budget current = do
          let questions = Map.elems $ gameQuestion $ snapshotGame current
          if any steedQuestion questions then pure (current,True)
          else if any (\case PlayerWindowChooseOne {} -> True; _ -> False) $ map unwrapQuestion questions then pure (current,False)
          else if budget <= (0 :: Int) then assert ("settings reviewed Return-To natural setup bounded " <> target) False >> pure (current,False)
          else do
            answer <- chooseActual player deck current
            (next,_) <- applyAnswer current player (snapshotRevision current) answer >>= right
            reachSetup (budget-1) next
    (ready,defaultSteed) <- reachSetup 100 submitted
    assert ("settings reviewed Return-To natural setup reaches player window " <> target) $ not defaultSteed
    let actualLog current = field "standaloneCampaignLog" $ field "That" $ toJSON $ gameMode $ snapshotGame current
        hasKey key value = any ((== String key) . field "contents") $ array $ field "recorded" value
        records key value = concat [array entries | pair <- array $ field "recordedSets" value
          ,[recordKey,entries] <- [array pair],field "contents" recordKey == String key]
        exactRecord code state record = field "recordType" record == String "RecordableCardCode"
          && field "tag" (field "recordVal" record) == String state
          && field "contents" (field "recordVal" record) == String code
        submitSetup form = do
          (next,_) <- applyAnswer original player (snapshotRevision original) (SettingsFormAnswer form) >>= right
          fst <$> reachSetup 100 next
        emitEffect kind current = do
          saved <- right $ first T.pack $ eitherDecode @Value $ exportSnapshot current
          BLC.putStrLn $ "SETTINGS_RETURNTO_EFFECT_FIXTURE " <> encode (object
            ["scenario" .= target,"kind" .= (kind :: Text),"save" .= saved])
    when (target == "50025") $ do
      let chosen = change "scenario:01120/2" (FormBoolean True) $
            change "scenario:01120/1" (FormChoice $ Just "scenario:01120/1/choice/1") aliasForm
      nondefault <- submitSetup chosen
      let gameValue = toJSON $ snapshotGame nondefault
          entities = field "gameEntities" gameValue
          houses = [location | (_,location) <- objectPairs $ field "locations" entities,field "cardCode" location == String "c01124"]
          placements = [field "contents" $ field "placement" investigator | (_,investigator) <- objectPairs $ field "investigators" entities]
          encounter = array $ field "encounterDeck" $ field "That" $ field "gameMode" gameValue
      assert "settings reviewed50025 nondefault ghoul house typed log retained after Setup" $
        hasKey "GhoulPriestIsStillAlive" (actualLog nondefault) && hasKey "YourHouseIsStillStanding" (actualLog nondefault)
          && not (hasKey "YourHouseHasBurnedToTheGround" $ actualLog nondefault)
      assert "settings reviewed50025 real Setup places investigator in exact YourHouse01124" $
        length houses == 1 && all (== field "id" (headEx houses)) placements && not (null placements)
      assert "settings reviewed50025 GhoulPriest01116 enters encounter deck not spawned enemy" $
        any ((== String "c01116") . field "cardCode") encounter
      emitEffect "nondefault" nondefault
    when (target == "50032") $ do
      let entryRow = "scenario:01142/2"
          chosen = case Map.lookup entryRow $ valuesByRowId aliasForm of
            Just (FormEntries entries) -> change "scenario:01142/0" (FormBoolean True) $
              change entryRow (FormEntries $ Map.insert (entryRow <> "/entry/5") True entries) aliasForm
            _ -> aliasForm
      nondefault <- submitSetup chosen
      let cultists = records "CultistsWhoGotAway" $ actualLog nondefault
          agendas = map snd $ objectPairs $ field "agendas" $ field "gameEntities" $ toJSON $ snapshotGame nondefault
          doom value = fromMaybe (-1) $ Aeson.parseMaybe parseJSON $ field "doom" value :: Int
      assert "settings reviewed50032 exact01121b CrossedOut plus five Recorded retained after Setup" $
        length cultists == 6 && length (filter (exactRecord "c01121b" "CrossedOut") cultists) == 1
          && all (\code -> any (exactRecord code "Recorded") cultists) ["c01137","c01138","c01139","c01140","c01141"]
          && hasKey "ItIsPastMidnight" (actualLog nondefault)
      assert "settings reviewed50032 real Setup agenda doom is three" $ sum (map doom agendas) == 3
      emitEffect "nondefault" nondefault
      midnightControl <- submitSetup $ change "scenario:01142/0" (FormBoolean False) chosen
      emitEffect "midnight-control" midnightControl
    when (target == "54046") $ do
      let defaultPersons = records "MissingPersons" $ actualLog ready
      assert "settings reviewed54046 default nonempty log exact four CrossedOut and Josef after Setup" $
        length defaultPersons == 4 && all (\code -> any (exactRecord code "CrossedOut") defaultPersons) ["c05046","c05047","c05048","c05049"]
          && hasKey "JosefIsAliveAndWell" (actualLog ready)
      let row = "scenario:05238/0"
          changedPerson = case Map.lookup row $ valuesByRowId aliasForm of
            Just (FormEntries entries) -> change row (FormEntries $ Map.insert (row <> "/entry/0") False entries) aliasForm
            _ -> aliasForm
      normalized <- previewSubmit aliasSchema $ change "scenario:05238/10" (FormBoolean False) changedPerson
      chosen <- previewSubmit aliasSchema $ change "scenario:05238/1" (FormChoice $ Just "scenario:05238/1/entry/0") $
        change "scenario:05238/2" (FormBoolean True) normalized
      nondefault <- submitSetup chosen
      let persons = records "MissingPersons" $ actualLog nondefault
          fates = records "WasTakenByTheWatcher" $ actualLog nondefault
      assert "settings reviewed54046 nondefault typed person and fate survive natural Setup" $
        length persons == 4 && any (exactRecord "c05046" "Recorded") persons
          && all (\code -> any (exactRecord code "CrossedOut") persons) ["c05047","c05048","c05049"]
          && length fates == 1 && any (exactRecord "c05046" "Recorded") fates
      assert "settings reviewed54046 nonempty user log not overwritten with Josef default" $
        hasKey "TheInvestigatorsAreOnGavriella'sTrail" (actualLog nondefault) && not (hasKey "JosefIsAliveAndWell" $ actualLog nondefault)
      assert "settings reviewed54046 legacy empty public answer remains rejected" . isLeft =<<
        applyAnswer original player (snapshotRevision original) (StandaloneSettingsAnswer [])
      emitEffect "nondefault" nondefault
    when (target == "54056") $ do
      assert "settings reviewed54056 count7 rejected" $ isLeft $ Engine.compileForm aliasSchema $ change "scenario:05325/0" (FormInteger 7) aliasForm
      one <- submitSetup $ change "scenario:05325/0" (FormInteger 1) aliasForm
      assert "settings reviewed54056 count1 has no steed question" $ not $ any steedQuestion $ Map.elems $ gameQuestion $ snapshotGame one
      emitEffect "count1" one
      let chosen = change "scenario:05325/0" (FormInteger 2) aliasForm
      (withCount,_) <- applyAnswer original player (snapshotRevision original) (SettingsFormAnswer chosen) >>= right
      (steed,offeredSteed) <- reachSetup 100 withCount
      assert "settings reviewed Return-To path count2 naturally offers nightgauntSteed" offeredSteed
      assert "settings reviewed Return-To steed prompt not auto answered" $ any steedQuestion $ Map.elems $ gameQuestion $ snapshotGame steed
      BLC.putStrLn $ "SETTINGS_RETURNTO_STEED_QUESTION " <> encode (toJSON $ gameQuestion $ snapshotGame steed)
      emitEffect "count2-pending" steed
    readySave <- right $ first T.pack $ eitherDecode @Value $ exportSnapshot ready
    BLC.putStrLn $ "SETTINGS_RETURNTO_FIXTURE " <> encode (object ["scenario" .= target,"base" .= base
      ,"schema" .= Engine.schemaView aliasSchema,"form" .= aliasForm,"log" .= aliasLog,"readySave" .= readySave])
  -- Explicit no-prior-input Return-To standalone paths. The raw catalog still
  -- says source-omitted; these aliases still require their wrapper/context
  -- policy. The generic exact-definition fallback does not authorize aliases.
  predicateRequest <- typed $ object ["campaign" .= ("02" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (predicateCampaign,_) <- createSession predicateRequest >>= right
  let emptyTargets = [("50011","01104"),("51015","02062"),("51012","02041")]
      -- Explicit expected JSON of the seven-field empty CampaignLog. Keep
      -- its original module private rather than expanding the public core
      -- API solely to satisfy a test's convenience import.
      emptyLog = object $ [key .= ([] :: [Value]) | key <- ["recorded","crossedOut","recordedCounts","recordedSets","orderedKeys","options"]]
        <> ["partners" .= object []]
  for_ emptyTargets $ \(target,base) -> do
    original <- create target
    schema <- offered original
    form <- complete schema
    preview <- right $ Engine.previewForm schema form
    logValue <- right $ Engine.compileForm schema form
    let context = field "context" $ Engine.schemaView schema
        originalBytes = exportSnapshot original
        (_,_,gatheringSchema,gatheringForm,_) = get "01104"
    assert ("settings reviewed-empty exact target base policy " <> target) $
      field "scenario" context == String target && field "definitionScenario" context == String base
        && field "aliasPolicy" context == String "reviewed-empty-standalone-v1"
    assert ("settings reviewed-empty actual zero-row canonical offered " <> target) $
      null (valuesByRowId form) && null (array $ field "rows" preview) && field "canSubmit" preview == Bool True
    assert ("settings reviewed-empty no invented recorded flags or sets " <> target) $ toJSON logValue == emptyLog
    let foreignRow = form {valuesByRowId = Map.singleton "foreign-historical-input" $ FormBoolean True}
    assert ("settings reviewed-empty extra row rejects preview and compile " <> target) $
      isLeft (Engine.previewForm schema foreignRow) && isLeft (Engine.compileForm schema foreignRow)
    assert ("settings reviewed-empty foreign ordinary empty schema rejected " <> target) $ isLeft $ Engine.compileForm schema gatheringForm
    assert ("settings reviewed-empty target schema rejected by foreign ordinary empty schema " <> target) $ isLeft $ Engine.compileForm gatheringSchema form
    crossForms <- for (filter ((/= target) . fst) emptyTargets) $ \(otherTarget,_) -> create otherTarget >>= offered >>= complete
    assert ("settings reviewed-empty cross-target empty schema rejected " <> target) $ all (isLeft . Engine.compileForm schema) crossForms
    -- Controlled typed predicate fixture only. Retains the actual offered
    -- question and uses real created campaign/scenario attrs; no queue writes,
    -- applyAnswer or claim that this is a naturally reached campaign state.
    predicateMode <- typed @GameMode $ object
      ["This" .= field "This" (toJSON $ gameMode $ snapshotGame predicateCampaign)
      ,"That" .= field "That" (toJSON $ gameMode $ snapshotGame original)]
    assert ("settings reviewed-empty controlled These predicate rejects standalone-only policy " <> target) $
      isLeft $ Engine.resolveSchema ((snapshotGame original) {gameMode=predicateMode}) player
    for_ [other] $ \notRecipient ->
      assert ("settings reviewed-empty foreign player rejected " <> target) $ isLeft $ Engine.resolveSchema (snapshotGame original) notRecipient
    (submitted,_) <- applyAnswer original player (snapshotRevision original) (SettingsFormAnswer form) >>= right
    assert ("settings reviewed-empty actual submit reaches deck " <> target) $
      any ((== ChooseDeck) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame submitted)
    imported <- importSnapshot originalBytes >>= right
    (replayed,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer form) >>= right
    assert ("settings reviewed-empty import replay exact whole snapshot " <> target) $ exportSnapshot replayed == exportSnapshot submitted
    assert ("settings reviewed-empty parent queue Game RNG immutable " <> target) $ exportSnapshot original == originalBytes
    let reachPlayer budget current
          | any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame current) = pure current
          | budget <= (0 :: Int) = assert ("settings reviewed-empty natural setup bounded " <> target) False >> pure current
          | otherwise = do
              answer <- chooseActual player deck current
              (next,_) <- applyAnswer current player (snapshotRevision current) answer >>= right
              reachPlayer (budget-1) next
    ready <- reachPlayer 100 submitted
    replayReady <- reachPlayer 100 replayed
    let value = toJSON $ snapshotGame ready
        scenario = field "That" $ field "gameMode" value
        entities = field "gameEntities" value
        locations = map snd $ objectPairs $ field "locations" entities
        investigators = map snd $ objectPairs $ field "investigators" entities
        enemies = map snd $ objectPairs $ field "enemies" entities
        setAside = array $ field "setAsideCards" scenario
        asideCodes = [field "cardCode" $ field "contents" card | card <- setAside,field "tag" card == String "EncounterCard"]
        codes = map (field "cardCode")
        actualLog = field "standaloneCampaignLog" scenario
        at code = let matching = filter ((== String code) . field "cardCode") locations
           in length matching == 1 && not (null investigators)
             && all ((== object ["tag" .= ("AtLocation" :: Text),"contents" .= field "id" (headEx matching)]) . field "placement") investigators
    assert ("settings reviewed-empty natural setup retains true scenario " <> target) $ field "id" scenario == String ("c" <> target)
    assert ("settings reviewed-empty full natural setup replay Game queue RNG exact " <> target) $ exportSnapshot ready == exportSnapshot replayReady
    assert ("settings reviewed-empty no invented outcome after Setup " <> target) $ actualLog == emptyLog
    -- Retain the exact actual state even if a following expected-effect
    -- assertion fails. Failed-run snapshots are diagnostic, never acceptance.
    saved <- right $ first T.pack $ eitherDecode @Value $ exportSnapshot ready
    BLC.putStrLn $ "SETTINGS_RETURNTO_EMPTY_FIXTURE " <> encode (object
      ["scenario" .= target,"base" .= base,"schema" .= Engine.schemaView schema,"form" .= form,"readySave" .= saved])
    when (target == "50011") $ do
      assert "settings reviewed-empty50011 actual MysteriousGateway50012" $
        any ((== String "c50012") . field "id") (map snd $ objectPairs $ field "acts" entities)
      assert "settings reviewed-empty50011 actual Study50013 and investigator placement" $ at "c50013"
      assert "settings reviewed-empty50011 actual GuestHall Bedroom Bathroom" $
        all (`elem` codes locations) $ map String ["c50014","c50015","c50016"]
    when (target == "51015") $ do
      assert "settings reviewed-empty51015 actual LaBellaLuna investigator placement" $ at "c02070"
      let lounges = filter (\l -> field "cardCode" l `elem` map String ["c02071","c51016"]) locations
          bosses = filter ((== String "c02078") . field "cardCode") enemies
      assert "settings reviewed-empty51015 exactly one sampled Lounge" $ length lounges == 1
      assert "settings reviewed-empty51015 actual PitBoss at sampled Lounge" $ length bosses == 1
        && field "placement" (headEx bosses) == object ["tag" .= ("AtLocation" :: Text),"contents" .= field "id" (headEx lounges)]
      assert "settings reviewed-empty51015 ReturnToStage51017 set aside" $ String "c51017" `elem` asideCodes
    when (target == "51012") $ do
      assert "settings reviewed-empty51012 actual Quad investigator placement" $ at "c02048"
      assert "settings reviewed-empty51012 early FacultyOffices only set aside" $
        String "c02054" `elem` asideCodes && String "c02055" `notElem` asideCodes
      assert "settings reviewed-empty51012 no completed-campaign EnthralledGuard" $ String "c51014" `notElem` codes enemies
      assert "settings reviewed-empty51012 Library Observatory mutually exclusive" $
        length (filter (`elem` map String ["c02050","c51013"]) $ codes locations) == 1
        && not (any (`elem` asideCodes) $ map String ["c02050","c51013"])
      let unchosen = if String "c02050" `elem` codes locations then "c51013" else "c02050"
      assert "settings reviewed-empty51012 unchosen Library Observatory absent from encounter deck" $
        String unchosen `notElem` codes (array $ field "encounterDeck" scenario)
  -- TCU's actual prologue overrides StartCampaign; it must NOT be forced into
  -- the generic deck/settings barrier just to fit this test's expectation.
  prologueRequest <- typed $ object ["campaign" .= ("05" :: Text),"scenario" .= ("05043" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (prologueInitial,_) <- createSession prologueRequest >>= right
  let untilContinue budget snapshot
        | any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot) = pure snapshot
        | budget <= (0 :: Int) = assert "settings engine actual prologue reaches continuation" False >> pure snapshot
        | otherwise = do
            answer <- chooseActual player deck snapshot
            (next,_) <- applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            untilContinue (budget-1) next
  prologue <- untilContinue 6 prologueInitial
  assert "settings engine actual TCU prologue has continuation NOT synthetic settings" $ isLeft $ Engine.resolveSchema (snapshotGame prologue) player
  for_ [("01","01120"),("02","02062"),("02","02236"),("02","02311"),("05","05050"),("05","05197"),("05","05325")] $ \(cid,sid) -> do
    req <- typed $ object ["campaign" .= (cid :: Text),"scenario" .= (sid :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
    (initial,_) <- createSession req >>= right
    snapshot <- untilSettings 12 initial
    schema <- offered snapshot
    form <- complete schema
    compiled <- right $ Engine.compileForm schema form
    let bytes = exportSnapshot snapshot
    preview <- right $ settingsPreview snapshot player (snapshotRevision snapshot) form
    assert ("settings engine campaign form preview actual start " <> cid <> "/" <> sid) $ field "canSubmit" preview == Bool True && exportSnapshot snapshot == bytes
    (after,_) <- applyAnswer snapshot player (snapshotRevision snapshot) (SettingsFormAnswer form) >>= right
    assert ("settings engine campaign form actual submission " <> cid <> "/" <> sid) $ snapshotRevision after == snapshotRevision snapshot+1
      && all ((/= PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame after)
    assert ("settings engine campaign submission original immutable " <> cid <> "/" <> sid) $ exportSnapshot snapshot == bytes
    let ordered = array $ field "orderedKeys" $ toJSON compiled
        keys = array $ field "recorded" $ toJSON compiled
    assert ("settings engine campaign keeps declared key order and exact membership " <> cid <> "/" <> sid) $
      length ordered == length (nub ordered) && sort ordered == sort keys
    when (cid == "05" && sid == "05325") $ do
      let firstFate = "campaign:05/step/0/1"
          secondFate = "campaign:05/step/0/2"
          setFate row form' = case Map.lookup row $ valuesByRowId form' of
            Just (FormEntries entries) -> change row (FormEntries $ Map.insert (row <> "/entry/0") True entries) form'
            _ -> form'
          both = setFate secondFate $ setFate firstFate form
      assert "settings engine actual TCU two fates rejected no traversal winner" $ isLeft $ Engine.compileForm schema both
      let hidePerson = case Map.lookup "campaign:05/step/0/0" $ valuesByRowId form of
            Just (FormEntries entries) -> change "campaign:05/step/0/0" (FormEntries $ Map.insert "campaign:05/step/0/0/entry/0" True entries) $ setFate firstFate form
            _ -> form
      assert "settings engine hidden true fate cannot authorize a log" $ isLeft $ Engine.compileForm schema hidePerson
      onePreview <- right $ Engine.previewForm schema $ setFate firstFate form
      one <- typed $ field "submission" onePreview
      assert "settings engine TCU single fate valid after inactive false draft pruning" $ isRight $ Engine.compileForm schema one
      let setChecked row checked form' = case Map.lookup row $ valuesByRowId form' of
            Just (FormEntries entries) -> change row (FormEntries $ Map.insert (row <> "/entry/0") checked entries) form'
            _ -> form'
          withdrawn = setChecked firstFate False one
      assert "settings engine sparse withdrawn draft final compile remains strict" $ isLeft $ Engine.compileForm schema withdrawn
      withdrawnPreview <- right $ Engine.previewForm schema withdrawn
      withdrawnCanonical <- typed $ field "submission" withdrawnPreview
      assert "settings engine reactivated entry defaults restore submit after withdrawal" $ field "canSubmit" withdrawnPreview == Bool True
      assert "settings engine default restoration returns exact original all-false form" $ withdrawnCanonical == form
      assert "settings engine restored canonical final compiler accepts" $ isRight $ Engine.compileForm schema withdrawnCanonical
      for_ [firstFate,secondFate,"campaign:05/step/0/3","campaign:05/step/0/4"] $ \fate ->
        assert ("settings engine reactivated explicit false retained " <> fate) $
          case Map.lookup fate $ valuesByRowId withdrawnCanonical of
            Just (FormEntries entries) -> Map.lookup (fate <> "/entry/0") entries == Just False
            _ -> False
      let bothWithdrawn = setChecked secondFate False $ setChecked firstFate False both
      bothWithdrawnPreview <- right $ Engine.previewForm schema bothWithdrawn
      bothWithdrawnCanonical <- typed $ field "submission" bothWithdrawnPreview
      assert "settings engine two explicit withdrawals restore canonical all false" $ field "canSubmit" bothWithdrawnPreview == Bool True && bothWithdrawnCanonical == form
      assert "settings engine preview defaults preserve explicit true and conflict" $ either (const False) ((== Bool False) . field "canSubmit") $ Engine.previewForm schema both
      case Map.lookup firstFate $ valuesByRowId one of
        Just (FormEntries entries) -> assert "settings engine TCU unrelated omitted person is not auto false" $ isLeft $ Engine.compileForm schema $
          change firstFate (FormEntries $ Map.delete (firstFate <> "/entry/1") entries) one
        _ -> assert "settings engine TCU selected fate retains own form" False
      assert "settings engine TCU simultaneous conflict preview cannot submit" $ either (const True) ((== Bool False) . field "canSubmit") $ Engine.previewForm schema both
      assert "settings engine stale inactive false entry final rejected" $ isLeft $ Engine.compileForm schema $ setFate firstFate form
      let absentNumber = form {valuesByRowId = Map.delete "campaign:05/step/5/0" $ valuesByRowId form}
      assert "settings engine missing number cannot unlock inverse forced memento" $ isLeft $ Engine.compileForm schema absentNumber
      missingPreview <- right $ Engine.previewForm schema absentNumber
      assert "settings engine unanswered count keeps forced memento pending" $ any (\r -> field "id" r == String "campaign:05/step/5/1" && field "visibility" r == String "pending") $ array $ field "rows" missingPreview
      let inject = change "campaign:05/step/3/1" (FormBoolean True) form
      assert "settings engine user cannot inject forced row" $ isLeft $ Engine.compileForm schema inject
      optionalPreview <- right $ Engine.previewForm schema $ change "campaign:05/step/1/2" (FormBoolean True) form
      optional <- typed $ field "submission" optionalPreview
      assert "settings engine original optional recordable explicitly none accepted" $ isRight $ Engine.compileForm schema $
        change "campaign:05/step/1/3" (FormChoice Nothing) optional
      assert "settings engine original optional recordable field still cannot be omitted" $ isLeft $ Engine.compileForm schema $
        optional {valuesByRowId = Map.delete "campaign:05/step/1/3" $ valuesByRowId optional}
      mementos <- completeFrom schema $ change "campaign:05/step/4/1" (FormBoolean True) $
        change "campaign:05/step/1/2" (FormBoolean True) form
      mementoLog <- right $ Engine.compileForm schema mementos
      let text = decodeUtf8 $ BLC.toStrict $ encode mementoLog
      assert "settings engine additive mementos from separate chapters do not overwrite" $ all (`isInfixOf` text) ["MesmerizingFlute","Gilman'sJournal","WispOfSpectralMist"]
    BLC.putStrLn $ "SETTINGS_ENGINE_CAMPAIGN_FIXTURE " <> encode (object ["campaign" .= cid,"start" .= sid,"schema" .= Engine.schemaView schema,"form" .= form,"log" .= compiled])
  -- Regression: a campaign setting is not implemented merely because its
  -- option is recorded. The real rule handler must grant an owned story card.
  riceRequest <- typed $ object ["campaign" .= ("02" :: Text),"scenario" .= ("02062" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (riceInitial,_) <- createSession riceRequest >>= right
  riceSettings <- untilSettings 12 riceInitial
  riceSchema <- offered riceSettings
  riceDefault <- complete riceSchema
  riceForm <- completeFrom riceSchema $ change "campaign:02/step/0/1" (FormBoolean True) $
    change "campaign:02/step/0/0" (FormChoice $ Just "campaign:02/step/0/0/entry/1") riceDefault
  riceLog <- right $ Engine.compileForm riceSchema riceForm
  assert "settings reward actual Rice option selected in compiled log" $
    any ((== String "TakeWarrenRice") . field "tag") $ array $ field "options" $ toJSON riceLog
  (riceGranted,_) <- applyAnswer riceSettings player (snapshotRevision riceSettings) (SettingsFormAnswer riceForm) >>= right
  riceSaved <- right $ first T.pack $ eitherDecode @Value $ exportSnapshot riceGranted
  BLC.putStrLn $ "SETTINGS_REWARD_RICE_FIXTURE " <> encode (object ["save" .= riceSaved,"form" .= riceForm])
  let riceCampaign = field "This" $ toJSON $ gameMode $ snapshotGame riceGranted
      riceCards = concatMap (array . snd) $ objectPairs $ field "storyCards" riceCampaign
      riceOwned = [field "contents" card | card <- riceCards,field "tag" card == String "PlayerCard",field "cardCode" (field "contents" card) == String "c02061"]
  let investigatorIds s = map (field "id" . snd) $ objectPairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame s
      campaignOf s = field "This" $ toJSON $ gameMode $ snapshotGame s
      ownedStory code s = [field "contents" card | (_,cards) <- objectPairs $ field "storyCards" $ campaignOf s
        ,card <- array cards,field "tag" card == String "PlayerCard",field "cardCode" (field "contents" card) == String code]
      heldStory code s = [(field "id" inv,card) | (_,inv) <- objectPairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame s
        ,card <- array (field "deck" inv) <> array (field "discard" inv)
          <> [field "contents" c | c <- array $ field "hand" inv,field "tag" c == String "PlayerCard"]
        ,field "cardCode" card == String code]
      emit prefix value = BLC.putStrLn $ prefix <> encode value
      reachPlayer budget s
        | any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame s) = pure s
        | budget <= (0 :: Int) = assert "settings campaign51 actual natural Setup bounded" False >> pure s
        | otherwise = do
            response <- chooseActual player deck s
            (next,_) <- applyAnswer s player (snapshotRevision s) response >>= right
            reachPlayer (budget-1) next
      createCampaign cid sid players = do
        request <- typed $ object ["campaign" .= (cid :: Text),"scenario" .= (sid :: Text),"seed" .= (171019 :: Int),"players" .= players]
        fst <$> (createSession request >>= right)
      decodeSave s = right $ first T.pack $ eitherDecode @Value $ exportSnapshot s
  assert "settings reward ACTUAL Rice granted once with investigator owner" $ case riceOwned of
    [card] -> investigatorIds riceGranted == [field "owner" card]
    _ -> False
  riceImported <- importSnapshot (exportSnapshot riceSettings) >>= right
  (riceReplay,_) <- applyAnswer riceImported player (snapshotRevision riceImported) (SettingsFormAnswer riceForm) >>= right
  assert "settings reward Rice actual immutable parent and exact replay" $ exportSnapshot riceSettings == exportSnapshot riceImported
    && exportSnapshot riceReplay == exportSnapshot riceGranted
  riceStale <- applyAnswer riceGranted player (snapshotRevision riceSettings) (SettingsFormAnswer riceForm)
  assert "settings reward no stale resubmit duplicates Rice" $ isLeft riceStale
  riceReady <- reachPlayer 100 riceGranted
  assert "settings reward Rice survives actual Setup once" $ length (ownedStory "c02061" riceReady) == 1
  assert "settings reward Rice physically present once in real hand deck or discard" $ case heldStory "c02061" riceReady of
    [(iid,card)] -> field "owner" card == iid
    _ -> False
  riceReadyReplay <- reachPlayer 100 riceReplay
  assert "settings reward Rice entire natural setup replay exact" $ exportSnapshot riceReady == exportSnapshot riceReadyReplay
  riceReadySave <- decodeSave riceReady
  riceReadyReplaySave <- decodeSave riceReadyReplay
  emit "SETTINGS_REWARD_RICE_READY_FIXTURE " $ object ["readySave" .= riceReadySave,"readyReplaySave" .= riceReadyReplaySave]
  let starts = [("51012","02041",[1]),("51015","02062",[0]),("51020","02118",[0..2]),("51025","02159",[0..3])
        ,("51032","02195",[0..4]),("51041","02236",[0..6]),("51047","02274",[0..7]),("51053","02311",[0..8])]
  campaignCases <- for starts $ \(target,base,chapters) -> do
    initial <- createCampaign "51" target [player]
    before <- untilSettings 12 initial
    schema <- offered before
    form <- complete schema
    preview <- right $ Engine.previewForm schema form
    baseInitial <- createCampaign "02" base [player]
    baseBefore <- untilSettings 12 baseInitial
    baseSchema <- offered baseBefore
    baseForm <- complete baseSchema
    basePreview <- right $ Engine.previewForm baseSchema baseForm
    let context = field "context" $ Engine.schemaView schema
        rows = array $ field "rows" preview
        rowIds = map (field "id") rows
        scopePrefixes = ["campaign:02/step/" <> tshow (n :: Int) <> "/" | n <- chapters]
        hasPrefix prefix row = maybe False (T.isPrefixOf prefix) $ Aeson.parseMaybe parseJSON $ field "id" row
        bytes = exportSnapshot before
    assert ("settings campaign51 exact actual and definition context " <> target) $
      field "campaign" context == String "51" && field "start" context == String target
        && field "definitionCampaign" context == String "02" && field "definitionStart" context == String base
        && field "aliasPolicy" context == String "reviewed-return-to-dunwich-starts-v1"
        && field "id" (campaignOf before) == String "51"
    assert ("settings campaign51 exact all prior chapter rows " <> target) $
      rowIds == map (field "id") (array $ field "rows" basePreview)
        && all (\row -> any (`hasPrefix` row) scopePrefixes) rows
        && all (\prefix -> any (hasPrefix prefix) rows) scopePrefixes
    assert ("settings campaign51 rejects foreign base schema " <> target) $
      isLeft (Engine.compileForm schema baseForm) && isLeft (Engine.compileForm baseSchema form)
    (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
    imported <- importSnapshot bytes >>= right
    (replay,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer form) >>= right
    assert ("settings campaign51 parent Game queue RNG immutable " <> target) $ exportSnapshot before == bytes
    assert ("settings campaign51 submitted import replay exact " <> target) $ exportSnapshot submitted == exportSnapshot replay
    ready <- reachPlayer 100 submitted
    readyReplay <- reachPlayer 100 replay
    assert ("settings campaign51 natural setup exact whole replay " <> target) $ exportSnapshot ready == exportSnapshot readyReplay
    assert ("settings campaign51 no fabricated completedSteps " <> target) $
      all ((== toJSON ([] :: [Value])) . field "completedSteps" . campaignOf) [before,submitted,ready]
    assert ("settings campaign51 actual target retained after natural setup " <> target) $
      field "id" (campaignOf ready) == String "51"
        && field "id" (field "That" $ toJSON $ gameMode $ snapshotGame ready) == String ("c" <> target)
    saves <- traverse decodeSave [before,submitted,replay,ready,readyReplay]
    emit "SETTINGS_CAMPAIGN51_FIXTURE " $ object ["target" .= target,"base" .= base,"chapters" .= chapters,"schema" .= Engine.schemaView schema
      ,"form" .= form,"rowIds" .= rowIds,"beforeSave" .= (saves P.!! 0),"submittedSave" .= (saves P.!! 1),"replayedSave" .= (saves P.!! 2)
      ,"readySave" .= (saves P.!! 3),"readyReplaySave" .= (saves P.!! 4)]
    pure (target,before,schema,form,ready)
  for_ campaignCases $ \(target,_,schema,form,_) ->
    assert ("settings campaign51 foreign base and other target schema rejected " <> target) $
      all (\(otherTarget,_,otherSchema,otherForm,_) -> target == otherTarget ||
        (isLeft (Engine.compileForm schema otherForm) && isLeft (Engine.compileForm otherSchema form))) campaignCases
  let caseFor target = fromJustNote "actual campaign51 case" $ find (\(t,_,_,_,_) -> target == t) campaignCases
      runVariant target label edit = do
        let (_,before,schema,form,_) = caseFor target
            beforeBytes = exportSnapshot before
        canonical <- completeFrom schema $ edit form
        (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer canonical) >>= right
        ready <- reachPlayer 100 submitted
        imported <- importSnapshot beforeBytes >>= right
        (replayed,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer canonical) >>= right
        readyReplay <- reachPlayer 100 replayed
        assert ("settings campaign51 nondefault parent immutable and whole replay " <> target <> "/" <> label) $
          exportSnapshot before == beforeBytes && exportSnapshot ready == exportSnapshot readyReplay
        saved <- decodeSave ready
        replaySave <- decodeSave readyReplay
        emit "SETTINGS_CAMPAIGN51_VARIANT_FIXTURE " $ object ["target" .= target,"label" .= label,"form" .= canonical,"readySave" .= saved,"readyReplaySave" .= replaySave]
        pure ready
      agendaDoom code s = [field "doom" agenda | (_,agenda) <- objectPairs $ field "agendas" $ field "gameEntities" $ toJSON $ snapshotGame s,field "id" agenda == String code]
  delayed <- runVariant "51032" "delayed" $ change "campaign:02/step/4/5" (FormBoolean True)
  let (_,_,_,_,notDelayed) = caseFor "51032"
  assert "settings campaign51 actual delayed train adds one agenda doom" $ agendaDoom "c02196" delayed == [toJSON (1 :: Int)] && agendaDoom "c02196" notDelayed == [toJSON (0 :: Int)]
  baseBloodInitial <- createCampaign "02" "02195" [player]
  baseBloodBefore <- untilSettings 12 baseBloodInitial
  baseBloodSchema <- offered baseBloodBefore
  baseBloodDefault <- complete baseBloodSchema
  for_ [False,True] $ \isDelayed -> do
    form <- completeFrom baseBloodSchema $ change "campaign:02/step/4/5" (FormBoolean isDelayed) baseBloodDefault
    (submitted,_) <- applyAnswer baseBloodBefore player (snapshotRevision baseBloodBefore) (SettingsFormAnswer form) >>= right
    ready <- reachPlayer 100 submitted
    imported <- importSnapshot (exportSnapshot baseBloodBefore) >>= right
    (replayed,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer form) >>= right
    readyReplay <- reachPlayer 100 replayed
    assert ("settings delayed base02195 actual doom and whole replay " <> tshow isDelayed) $
      agendaDoom "c02196" ready == [toJSON (if isDelayed then 1 else 0 :: Int)] && exportSnapshot ready == exportSnapshot readyReplay
    saved <- decodeSave ready
    replaySave <- decodeSave readyReplay
    emit "SETTINGS_DELAYED_BASE_FIXTURE " $ object ["delayed" .= isDelayed,"readySave" .= saved,"readyReplaySave" .= replaySave]
  brood <- runVariant "51047" "twoBrood" $ change "campaign:02/step/7/2" (FormInteger 2) . change "campaign:02/step/7/1" (FormBoolean False)
  noBrood <- runVariant "51047" "noBrood" $ change "campaign:02/step/7/1" (FormBoolean True)
  assert "settings campaign51 actual escaped Brood controls initial agenda doom" $ agendaDoom "c02275" brood == [toJSON (2 :: Int)] && agendaDoom "c02275" noBrood == [toJSON (0 :: Int)]
  let (_,survivorBefore,survivorSchema,survivorDefault,_) = caseFor "51041"
      nonRitual = change "campaign:02/step/5/0" (FormChoice $ Just "campaign:02/step/5/0/entry/1")
  ritualPreview <- right $ Engine.previewForm survivorSchema survivorDefault
  assert "settings campaign51 ritual completed suppresses all Survivors rows" $
    all ((== String "inactive") . field "visibility")
      [row | row <- array $ field "rows" ritualPreview,maybe False (T.isPrefixOf "campaign:02/step/6/") $ Aeson.parseMaybe parseJSON $ field "id" row]
  survivorForm <- completeFrom survivorSchema $ change "campaign:02/step/2/2" (FormBoolean True) $
    change "campaign:02/step/6/1" (FormBoolean True) $ nonRitual survivorDefault
  survivorLog <- right $ Engine.compileForm survivorSchema survivorForm
  survivorMessages <- right $ Engine.compileSubmission survivorSchema survivorForm
  let tagCount name = length . filter ((== String name) . field "tag")
  assert "settings campaign51 two chapter Armitage true options compile once and dispatch once" $
    tagCount "TakeArmitage" (array $ field "options" $ toJSON survivorLog) == 1
      && length [m | m <- map toJSON survivorMessages,field "tag" m == String "HandleOption",field "tag" (field "contents" m) == String "TakeArmitage"] == 1
  assert "settings campaign51 nonritual forces actual unsacrificed survivor history" $
    any ((== String "DrHenryArmitageSurvivedTheDunwichLegacy") . field "contents") $ array $ field "recorded" $ toJSON survivorLog
  (survivorGranted,_) <- applyAnswer survivorBefore player (snapshotRevision survivorBefore) (SettingsFormAnswer survivorForm) >>= right
  assert "settings campaign51 repeated Armitage options grant one actual owned card" $ case ownedStory "c02040" survivorGranted of
    [card] -> investigatorIds survivorGranted == [field "owner" card]
    _ -> False
  lateUnchecked <- completeFrom survivorSchema $ change "campaign:02/step/6/1" (FormBoolean False) survivorForm
  (lateUncheckedGranted,_) <- applyAnswer survivorBefore player (snapshotRevision survivorBefore) (SettingsFormAnswer lateUnchecked) >>= right
  assert "settings campaign51 later false does not erase earlier reward true" $ exportSnapshot lateUncheckedGranted == exportSnapshot survivorGranted
  let (_,_,earlySchema,_,earlyReady) = caseFor "51012"
      earlyScenario = field "That" $ toJSON $ gameMode $ snapshotGame earlyReady
      earlyAside = [field "cardCode" $ field "contents" c | c <- array $ field "setAsideCards" earlyScenario]
  assert "settings campaign51 forceHouse chapter does not forge completed campaign Setup" $
    String "c02054" `elem` earlyAside && String "c02055" `notElem` earlyAside
      && all ((/= String "c51014") . field "cardCode" . snd) (objectPairs $ field "enemies" $ field "gameEntities" $ toJSON $ snapshotGame earlyReady)
  for_ [("52","03043"),("51","01104")] $ \(cid,sid) -> do
    initial <- createCampaign cid sid [player]
    before <- untilSettings 12 initial
    assert ("settings campaign51 no generic unreviewed campaign fallback " <> cid <> "/" <> sid) $ isLeft $ Engine.resolveSchema (snapshotGame before) player
  assert "settings campaign51 true51 schema distinct from base02 foreign start" $ Engine.schemaIdentity earlySchema /= Engine.schemaIdentity riceSchema
  cheated <- runVariant "51012" "cheated" $ change "campaign:02/step/1/4" (FormBoolean True)
  let (_,cheatBefore,_,_,_) = caseFor "51012"
      elderCount s = length $ filter (== String "ElderThing") $ array $ field "chaosBag" $ campaignOf s
      scenarioElders s = length [token | token <- array $ field "chaosTokens" $ field "chaosBag" $ field "That" $ toJSON $ gameMode $ snapshotGame s,field "chaosTokenFace" token == String "ElderThing"]
  assert "settings reward Cheated exactly one new ElderThing survives natural scenario Setup" $
    elderCount cheated == elderCount cheatBefore + 1 && scenarioElders cheated == elderCount cheated
  -- A normal start is NOT a fabricated advanced-start settings form. Select
  -- the actual two offered prologue labels, not arbitrary Read indices.
  normalRequest <- typed $ object ["campaign" .= ("51" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (normalInitial,_) <- createSession normalRequest >>= right
  let realAnswer who chosenDeck s = case unwrapQuestion <$> Map.lookup who (gameQuestion $ snapshotGame s) of
        Just ContinueCampaign -> do
          continuation <- right $ offeredContinuation $ gameMode $ snapshotGame s
          typed $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= field "nextStep" (toJSON continuation)]
        _ -> chooseActual who chosenDeck s
      runNormal desired = do
        imported <- importSnapshot (exportSnapshot normalInitial) >>= right
        let loop budget chosen s = do
              assert "settings campaign51 normal intermediate has no synthetic settings" $
                all ((/= PickCampaignSettings) . unwrapQuestion) $ Map.elems $ gameQuestion $ snapshotGame s
              if any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame s)
                then assert "settings campaign51 normal prologue actual offered branch selected" chosen >> pure s
                else do
                  when (budget <= (0 :: Int)) $ do
                    emit "SETTINGS_CAMPAIGN51_NORMAL_UNHANDLED " $ toJSON $ gameQuestion $ snapshotGame s
                    assert "settings campaign51 normal prologue bounded" False
                  let q = fromJustNote "normal actual recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s
                      choices = fromMaybe [] $ indexedChoices $ unwrapQuestion q
                      labelOf choice = field "label" $ toJSON choice
                      labels = map labelOf choices
                      matches suffix (String label) = suffix `T.isSuffixOf` label
                      matches _ _ = False
                      branches = [(i,choice) | (i,choice) <- zip [0..] choices,matches desired $ labelOf choice]
                      isRead = case unwrapQuestion q of Read {} -> True; _ -> False
                      prologue = isRead && length choices == 2 && any (matches "extracurricularActivity") labels && any (matches "theHouseAlwaysWins") labels
                  response <- if prologue
                    then case branches of
                      [(i,_)] -> pure $ Answer $ QuestionResponse i (Just player) Nothing
                      _ -> assert "settings campaign51 exact prologue labels" False >> realAnswer player deck s
                    else realAnswer player deck s
                  (next,_) <- applyAnswer s player (snapshotRevision s) response >>= right
                  loop (budget-1) (chosen || prologue) next
        loop 100 False imported
  normalA <- runNormal "extracurricularActivity"
  normalB <- runNormal "theHouseAlwaysWins"
  let actualScenario s = field "id" $ field "That" $ toJSON $ gameMode $ snapshotGame s
  assert "settings campaign51 both natural prologue choices without synthetic settings" $
    actualScenario normalA == String "c51012" && actualScenario normalB == String "c51015"
  for_ [("extracurricularActivity",normalA),("theHouseAlwaysWins",normalB)] $ \(branch,s) -> do
    saved <- decodeSave s
    emit "SETTINGS_CAMPAIGN51_NORMAL_FIXTURE " $ object ["branch" .= (branch :: Text),"readySave" .= saved]
  -- Both seats really finish their own deck/trauma before settings. A reward
  -- is assigned by its offered PortraitLabel ID, never by player-seat index.
  -- Lead is not yet appointed at this barrier (stored lead can be c00000).
  twoInitial <- createCampaign "51" "51015" [other,player]
  let secondDeck = fromJustNote "second actual investigator preset" $ decks !!? 1
      driveTwo budget completed s
        | any ((== PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame s) = do
            assert "settings campaign51 settings waits for both actual decks and no pending peer setup" $
              sort completed == sort [player,other] && Map.size (gameQuestion $ snapshotGame s) == 1
            pure s
        | budget <= (0 :: Int) = assert "settings campaign51 actual two seat setup bounded" False >> pure s
        | otherwise = do
            let questions = gameQuestion $ snapshotGame s
                who = if Map.member other questions then other else player
                q = fromJustNote "actual two seat recipient" $ Map.lookup who questions
                selectedDeck = if who == player then deck else secondDeck
            response <- realAnswer who selectedDeck s
            (next,_) <- applyAnswer s who (snapshotRevision s) response >>= right
            let completed' = if unwrapQuestion q == ChooseDeck then who:completed else completed
            when (length completed' < 2) $
              assert "settings campaign51 no premature settings at first seat deck barrier" $
                all ((/= PickCampaignSettings) . unwrapQuestion) $ Map.elems $ gameQuestion $ snapshotGame next
            driveTwo (budget-1) completed' next
  twoBefore <- driveTwo 24 [] twoInitial
  let settingsRecipients = [who | (who,q) <- Map.toList $ gameQuestion $ snapshotGame twoBefore,unwrapQuestion q == PickCampaignSettings]
  assert "settings campaign51 exact unique offered settings recipient" $ length settingsRecipients == 1
  let settingsWho = headEx settingsRecipients
  twoSchema <- right $ Engine.resolveSchema (snapshotGame twoBefore) settingsWho
  twoDefault <- complete twoSchema
  twoForm <- completeFrom twoSchema $ change "campaign:02/step/0/1" (FormBoolean True) $
    change "campaign:02/step/0/0" (FormChoice $ Just "campaign:02/step/0/0/entry/1") twoDefault
  (twoPrompt,_) <- applyAnswer twoBefore settingsWho (snapshotRevision twoBefore) (SettingsFormAnswer twoForm) >>= right
  let ricePrompts = [(who,q) | (who,q@(QuestionLabel _ (Just code) _)) <- Map.toList $ gameQuestion $ snapshotGame twoPrompt,toJSON code == String "c02061"]
  assert "settings campaign51 exact unique Rice card-labelled recipient" $ length ricePrompts == 1
  let (recipient,prompt) = headEx ricePrompts
      portraits = fromMaybe [] $ indexedChoices $ unwrapQuestion prompt
      actualOwners = [(field "id" inv,field "playerId" inv) | (_,inv) <- objectPairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame twoPrompt]
      targets = [(i,iid) | (i,PortraitLabel iid _) <- zip [0..] portraits,Just owner <- [lookup (toJSON iid) actualOwners]
        ,owner `elem` map toJSON [player,other],owner /= toJSON recipient]
  promptSave <- decodeSave twoPrompt
  emit "SETTINGS_CAMPAIGN51_TWO_SEAT_PROMPT_FIXTURE " $ object ["recipient" .= recipient,"save" .= promptSave]
  assert "settings campaign51 actual two portrait reward prompt" $ length portraits == 2 && length targets == 1
  let (targetIndex,targetIid) = headEx targets
      wrongRecipient = if recipient == player then other else player
      targetAnswer = Answer $ QuestionResponse targetIndex (Just recipient) Nothing
      promptBytes = exportSnapshot twoPrompt
  wrong <- applyAnswer twoPrompt wrongRecipient (snapshotRevision twoPrompt) (Answer $ QuestionResponse targetIndex (Just wrongRecipient) Nothing)
  assert "settings campaign51 reward refuses wrong actual recipient without mutation" $ isLeft wrong && exportSnapshot twoPrompt == promptBytes
  (twoGranted,_) <- applyAnswer twoPrompt recipient (snapshotRevision twoPrompt) targetAnswer >>= right
  assert "settings campaign51 two seats deck barrier and reward recipient" $ case ownedStory "c02061" twoGranted of
    [card] -> field "owner" card == toJSON targetIid && maybe False (/= toJSON recipient) (lookup (toJSON targetIid) actualOwners)
    _ -> False
  targetKey <- typed @Text $ toJSON targetIid
  assert "settings campaign51 actual reward archived under chosen investigator" $
    length [c | c <- array $ field targetKey $ field "storyCards" $ campaignOf twoGranted,field "cardCode" (field "contents" c) == String "c02061"] == 1
  twoSave <- decodeSave twoGranted
  emit "SETTINGS_CAMPAIGN51_TWO_SEAT_FIXTURE " $ object ["settingsRecipient" .= settingsWho,"rewardRecipient" .= recipient,"chosenInvestigator" .= targetIid,"save" .= twoSave]
  -- Shared submission must also repair the already supported TCU campaign.
  tcuInitial <- createCampaign "05" "05325" [player]
  tcuBefore <- untilSettings 12 tcuInitial
  tcuSchema <- offered tcuBefore
  tcuDefault <- complete tcuSchema
  for_ [("BlackBook","c05150","c05228",change "campaign:05/step/4/4" (FormBoolean True))
       ,("PuzzleBox","c05228","c05150",change "campaign:05/step/6/1" (FormBoolean True) .
          change "campaign:05/step/6/0" (FormChoice $ Just "campaign:05/step/6/0/entry/1"))] $ \(label,code,absent,edit) -> do
    form <- completeFrom tcuSchema $ edit tcuDefault
    (granted,_) <- applyAnswer tcuBefore player (snapshotRevision tcuBefore) (SettingsFormAnswer form) >>= right
    imported <- importSnapshot (exportSnapshot tcuBefore) >>= right
    (replayed,_) <- applyAnswer imported player (snapshotRevision imported) (SettingsFormAnswer form) >>= right
    assert ("settings reward actual TCU owned story card and no unselected reward " <> label) $
      case ownedStory code granted of [card] -> investigatorIds granted == [field "owner" card] && null (ownedStory absent granted); _ -> False
    assert ("settings reward actual TCU exact whole replay " <> label) $ exportSnapshot granted == exportSnapshot replayed && exportSnapshot imported == exportSnapshot tcuBefore
    saved <- decodeSave granted
    emit "SETTINGS_TCU_REWARD_FIXTURE " $ object ["label" .= (label :: Text),"code" .= code,"form" .= form,"save" .= saved]
  -- Isolated handler contract: real created game/decks/step, but a TEST-ONLY
  -- disposable queue. NOT a natural settings route or client Raw capability.
  -- Base versus Return-To isolates the Powder step guard from dispatch itself.
  powderMessage <- typed @Message $ object ["tag" .= ("HandleOption" :: Text),"contents" .= object ["tag" .= ("TakePowderOfIbnGhazi" :: Text)]]
  for_ [("02","02236",True),("51","51053",False),("51","51041",True)] $ \(cid,sid,grants) -> do
    initial <- createCampaign cid sid [player]
    before <- untilSettings 12 initial
    env <- makeEnv (snapshotGame before) [ClearUI,powderMessage]
    advance env
    (after,_) <- finish (snapshotHistoricalBootstrap before) (snapshotRevision before) env
    let cards = ownedStory "c02219" after
    saved <- decodeSave after
    emit "SETTINGS_POWDER_HANDLER_FIXTURE " $ object ["campaign" .= cid,"start" .= sid,"controlledHandlerOnly" .= True,"save" .= saved]
    assert (if grants then "settings reward controlled Powder handler owned card " <> cid <> "/" <> sid else "settings reward controlled Powder handler wrong start does not grant") $
      if grants then case cards of [card] -> investigatorIds after == [field "owner" card]; _ -> False else null cards
  -- Now the independently isolated handler guard has been checked, test the
  -- natural settings -> reward -> real Setup route, not a raw queue fixture.
  powderForm <- completeFrom survivorSchema $ change "campaign:02/step/6/10" (FormBoolean True) $ nonRitual survivorDefault
  powderLog <- right $ Engine.compileForm survivorSchema powderForm
  assert "settings reward natural Powder compiled option explicitly selected" $
    any ((== String "TakePowderOfIbnGhazi") . field "tag") $ array $ field "options" $ toJSON powderLog
  let powderBeforeBytes = exportSnapshot survivorBefore
  (powderGranted,_) <- applyAnswer survivorBefore player (snapshotRevision survivorBefore) (SettingsFormAnswer powderForm) >>= right
  let powderAssets s = [asset | (_,asset) <- objectPairs $ field "assets" $ field "gameEntities" $ toJSON $ snapshotGame s
        ,field "cardCode" asset == String "c02219"]
      reachPowder shouldPlay budget chosen s
        | any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame s) = do
            assert "settings reward actual Powder starting-hand play choice reached" chosen
            pure s
        | budget <= (0 :: Int) = assert "settings reward Powder natural branch bounded" False >> pure s
        | otherwise = do
            let question = fromJustNote "actual Powder setup recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s
                choices = fromMaybe [] $ indexedChoices $ unwrapQuestion question
                key choice = case field "label" $ toJSON choice of String label -> T.takeWhile (/= ' ') label; _ -> ""
                keys = map key choices
                isPowder = keys == ["$label.playName","$label.doNotPlayName"]
            response <- if isPowder
              then do
                emit "SETTINGS_POWDER_STARTING_HAND_PROMPT " $ toJSON question
                pure $ Answer $ QuestionResponse (if shouldPlay then 0 else 1) (Just player) Nothing
              else chooseActual player deck s
            (next,_) <- applyAnswer s player (snapshotRevision s) response >>= right
            reachPowder shouldPlay (budget-1) (chosen || isPowder) next
  powderReady <- reachPowder True 100 False powderGranted
  powderImported <- importSnapshot powderBeforeBytes >>= right
  (powderReplayed,_) <- applyAnswer powderImported player (snapshotRevision powderImported) (SettingsFormAnswer powderForm) >>= right
  powderReadyReplay <- reachPowder True 100 False powderReplayed
  powderReadySave <- decodeSave powderReady
  powderReadyReplaySave <- decodeSave powderReadyReplay
  powderGrantedSave <- decodeSave powderGranted
  powderReplayedSave <- decodeSave powderReplayed
  emit "SETTINGS_POWDER_NATURAL_FIXTURE " $ object ["form" .= powderForm,"readySave" .= powderReadySave,"readyReplaySave" .= powderReadyReplaySave
    ,"submittedSave" .= powderGrantedSave,"submittedReplaySave" .= powderReplayedSave]
  assert "settings reward natural Powder selected only at real current chapter" $
    field "id" (campaignOf powderReady) == String "51" && actualScenario powderReady == String "c51041"
      && null (heldStory "c02219" powderReady) && case (ownedStory "c02219" powderReady,powderAssets powderReady) of
        ([archived],[asset]) -> field "id" archived == field "cardId" asset
          && field "owner" archived == field "owner" asset && field "controller" asset == field "owner" archived
        _ -> False
  assert "settings reward natural Powder parent immutable and whole Game queue RNG replay" $
    exportSnapshot survivorBefore == powderBeforeBytes && exportSnapshot powderGranted == exportSnapshot powderReplayed
      && exportSnapshot powderReady == exportSnapshot powderReadyReplay
  powderDeclined <- reachPowder False 100 False powderGranted
  powderDeclinedReplay <- reachPowder False 100 False powderReplayed
  declinedSave <- decodeSave powderDeclined
  declinedReplaySave <- decodeSave powderDeclinedReplay
  emit "SETTINGS_POWDER_DECLINED_FIXTURE " $ object ["readySave" .= declinedSave,"readyReplaySave" .= declinedReplaySave]
  assert "settings reward actual declined Powder remains physical card not played asset" $
    null (powderAssets powderDeclined) && case (ownedStory "c02219" powderDeclined,heldStory "c02219" powderDeclined) of
      ([archived],[(iid,card)]) -> field "id" archived == field "id" card && field "owner" archived == iid && field "owner" card == iid
      _ -> False
  assert "settings reward declined Powder exact whole natural replay" $ exportSnapshot powderDeclined == exportSnapshot powderDeclinedReplay
  uncheckedPowder <- runVariant "51041" "powderUnchecked" nonRitual
  uncheckedForm <- completeFrom survivorSchema $ nonRitual survivorDefault
  uncheckedLog <- right $ Engine.compileForm survivorSchema uncheckedForm
  let (_,_,latePowderSchema,latePowderDefault,_) = caseFor "51053"
  latePowderForm <- completeFrom latePowderSchema $ change "campaign:02/step/6/10" (FormBoolean True) $ nonRitual latePowderDefault
  latePowderLog <- right $ Engine.compileForm latePowderSchema latePowderForm
  assert "settings reward Powder unchecked versus late selected remain distinct compiled options" $
    not (any ((== String "TakePowderOfIbnGhazi") . field "tag") $ array $ field "options" $ toJSON uncheckedLog)
      && any ((== String "TakePowderOfIbnGhazi") . field "tag") (array $ field "options" $ toJSON latePowderLog)
  latePowder <- runVariant "51053" "powderLateIgnored" $ change "campaign:02/step/6/10" (FormBoolean True) . nonRitual
  assert "settings reward natural unselected and wrong chapter Powder not granted" $
    all (\s -> null (ownedStory "c02219" s) && null (heldStory "c02219" s)) [uncheckedPowder,latePowder]
  -- Deliberately keep lateSchema/lateForm evaluated as a distinct actual scope.
  assert "settings engine later standalone schema not an earlier NOTZ alias" $ Engine.schemaIdentity lateSchema /= Engine.schemaIdentity midSchema && formSchemaId lateForm /= formSchemaId midForm

  -- Exact reviewed campaign50 policy, not a generic omitted-form fallback.
  -- These test commands select actual questions; no fabricated completedSteps,
  -- campaign log, chaos bag or arbitrary public rule message is supplied.
  let notzStarts = [("50011","01104"),("50025","01120"),("50032","01142")]
      notzWire = \case
        Answer (QuestionResponse choice who version) -> pure $ object
          ["tag" .= ("Answer" :: Text),"contents" .= object ["choice" .= choice,"playerId" .= who,"questionVersion" .= version]]
        DeckListAnswer selected who -> pure $ object ["tag" .= ("DeckListAnswer" :: Text),"deckList" .= selected,"playerId" .= who]
        AmountsAnswer (AmountsResponse amounts version who) -> pure $ object
          ["tag" .= ("AmountsAnswer" :: Text),"contents" .= object ["amounts" .= amounts,"questionVersion" .= version,"playerId" .= who]]
        CampaignStepAnswer step -> pure $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= step]
        PickDestinyAnswer drawings -> pure $ object ["tag" .= ("PickDestinyAnswer" :: Text),"contents" .= drawings]
        _ -> assert "settings campaign50 finite actual test command adapter" False >> pure Null
      runNotz = runWithPrologue "playNormally"
      runWithPrologue = runWithStops False
      runToContinuation = runWithStops True "playNormally" False True
      runWithStops stopAtContinuation prologueChoice stopAtSettings forbidSettings = loop (120 :: Int) []
       where
        loop budget trace s
          | stopAtContinuation && any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame s) = pure (s,reverse trace)
          | stopAtSettings && any ((== PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame s) = pure (s,reverse trace)
          | any (\case PlayerWindowChooseOne {} -> True; _ -> False) (map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame s) = pure (s,reverse trace)
          | budget <= 0 = assert "settings campaign50 bounded actual initialization" False >> pure (s,reverse trace)
          | otherwise = do
              let hasAction q = case indexedChoices $ unwrapQuestion q of
                    Nothing -> True
                    Just cs -> any (\case Info {} -> False; InvalidLabel {} -> False; _ -> True) cs
                  offeredQuestions = filter (hasAction . snd) $ Map.toList $ gameQuestion $ snapshotGame s
              assert "settings campaign50 actual actionable recipient exists" $ not $ null offeredQuestions
              let (who,wrapped) = headEx offeredQuestions
                  q = unwrapQuestion wrapped
                  selectedDeck = if who == other then decks P.!! 1 else deck
                  choices = fromMaybe [] $ indexedChoices q
                  choiceKey choice = case field "label" $ toJSON choice of
                    String label -> lastEx $ T.splitOn "." $ T.takeWhile (/= ' ') label
                    _ -> ""
                  prologueKeys = map choiceKey choices
              when forbidSettings $ assert "settings campaign50 natural normal route never asks synthetic settings" $ q /= PickCampaignSettings
              response <- case q of
                _ | actualScenario s == String "c54016" && prologueKeys == ["returnToSkip","playNormally"] -> do
                  let selected = [i | (i,key) <- zip [0..] prologueKeys,key == prologueChoice]
                  assert ("settings campaign54 actual requested prologue offered branch " <> prologueChoice) $ length selected == 1
                  pure $ Answer $ QuestionResponse (headEx selected) (Just who) Nothing
                PickSupplies {} -> do
                  let value = toJSON q
                      provisions = [i | (i,choice) <- zip [0..] choices,String label <- [field "label" $ toJSON choice]
                        ,".supplies.provisions.name" `T.isSuffixOf` label]
                      firstSupply = null $ array $ field "chosenSupplies" value
                  assert "settings campaign53 actual supply selection finite original offered choices" $
                    not (null choices) && (not firstSupply || length provisions == 1)
                  when (not firstSupply) $ assert "settings campaign53 actual Done index0 label verified" $
                    field "label" (toJSON $ headEx choices) == String "$done"
                  pure $ Answer $ QuestionResponse (if firstSupply then headEx provisions else 0) (Just who) Nothing
                PickDestiny drawings -> do
                  rotated <- typed @[DestinyDrawing] $ toJSON
                    [object ["scenario" .= field "scenario" value,"tarot" .= object ["arcana" .= field "arcana" (field "tarot" value)
                      ,"facing" .= (if i < (length drawings+1) `div` 2 then "Reversed" else "Upright" :: Text)]]
                    | (i,drawing) <- zip [0..] drawings,let value = toJSON drawing]
                  pure $ PickDestinyAnswer rotated
                ContinueCampaign -> do
                  continuation <- right $ offeredContinuation $ gameMode $ snapshotGame s
                  typed $ object ["tag" .= ("CampaignStepAnswer" :: Text),"contents" .= field "nextStep" (toJSON continuation)]
                _ -> chooseActual who selectedDeck s
              wire <- notzWire response
              decoded <- typed @Answer wire
              assert "settings campaign50 finite wire command roundtrip" $ show decoded == show response
              let oldBytes = exportSnapshot s
              result <- applyAnswer s who (snapshotRevision s) decoded
              when (isLeft result) do
                saved <- decodeSave s
                emit "SETTINGS_CAMPAIGN_DRIVER_FAILURE " $ object ["beforeSave" .= saved,"recipient" .= who,"question" .= wrapped,"answer" .= wire,"reason" .= either Just (const Nothing) result]
              (next,_) <- right result
              assert "settings campaign50 each command preserves old snapshot" $ exportSnapshot s == oldBytes
              loop (budget-1) (object ["recipient" .= who,"revision" .= snapshotRevision s,"question" .= wrapped,"answer" .= wire
                ,"beforeCampaignLog" .= field "log" (campaignOf s)]:trace) next
  notzCases <- for notzStarts $ \(target,base) -> do
    initial <- createCampaign "50" target [player]
    (before,deckTrace) <- runNotz True False initial
    schema <- offered before
    form <- complete schema
    preview <- right $ Engine.previewForm schema form
    compiled <- right $ Engine.compileForm schema form
    baseInitial <- createCampaign "01" base [player]
    (baseBefore,_) <- runNotz True False baseInitial
    baseSchema <- offered baseBefore
    baseForm <- complete baseSchema
    standalone <- create target >>= offered >>= complete
    let context = field "context" $ Engine.schemaView schema
        emptyLog = object ([key .= ([] :: [Value]) | key <- ["recorded","crossedOut","recordedCounts","recordedSets","orderedKeys","options"]] <> ["partners" .= object []])
        beforeBytes = exportSnapshot before
    assert ("settings campaign50 exact distinct actual and definition identities " <> target) $
      field "campaign" context == String "50" && field "start" context == String target
        && field "definitionCampaign" context == String "01" && field "definitionStart" context == String base
        && field "aliasPolicy" context == String "reviewed-empty-return-to-notz-starts-v1"
        && field "id" (campaignOf before) == String "50"
    assert ("settings campaign50 canonical empty no invented history " <> target) $
      null (array $ field "rows" preview) && field "canSubmit" preview == Bool True
        && Map.null (valuesByRowId form) && toJSON compiled == emptyLog
    assert ("settings campaign50 foreign base and standalone schemas rejected " <> target) $
      isLeft (Engine.compileForm schema baseForm) && isLeft (Engine.compileForm baseSchema form)
        && isLeft (Engine.compileForm schema standalone)
    assert ("settings campaign50 unknown nonempty row rejected " <> target) $
      isLeft $ Engine.compileForm schema $ form {valuesByRowId=Map.singleton "invented" $ FormBoolean True}
    (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
    restored <- importSnapshot beforeBytes >>= right
    (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer form) >>= right
    assert ("settings campaign50 actual form submission replay entire snapshot " <> target) $
      exportSnapshot submitted == exportSnapshot replayed && exportSnapshot before == beforeBytes
    (ready,trace) <- runNotz False True submitted
    (readyReplay,_) <- runNotz False True replayed
    assert ("settings campaign50 actual natural Setup entire replay " <> target) $ exportSnapshot ready == exportSnapshot readyReplay
    assert ("settings campaign50 actual wrapper target and no invented completed history " <> target) $
      field "id" (campaignOf ready) == String "50" && actualScenario ready == String ("c" <> target)
        && all ((== toJSON ([] :: [Value])) . field "completedSteps" . campaignOf) [before,submitted,ready]
    saves <- traverse decodeSave [initial,before,submitted,replayed,ready,readyReplay]
    emit "SETTINGS_CAMPAIGN50_FIXTURE " $ object ["target" .= target,"base" .= base,"schema" .= Engine.schemaView schema,"form" .= form
      ,"initialSave" .= (saves P.!! 0),"beforeSave" .= (saves P.!! 1),"submittedSave" .= (saves P.!! 2),"replayedSave" .= (saves P.!! 3)
      ,"readySave" .= (saves P.!! 4),"readyReplaySave" .= (saves P.!! 5),"deckTrace" .= deckTrace,"setupTrace" .= trace]
    pure (target,schema,form)
  for_ notzCases $ \(target,schema,form) -> assert ("settings campaign50 cross-target schema isolation " <> target) $
    all (\(otherTarget,otherSchema,otherForm) -> target == otherTarget ||
      (isLeft (Engine.compileForm schema otherForm) && isLeft (Engine.compileForm otherSchema form))) notzCases
  for_ [("50","01104"),("50","51012"),("52","03043"),("53","04043")] $ \(cid,target) -> do
    initial <- createCampaign cid target [player]
    before <- untilSettings 12 initial
    assert ("settings campaign50 no generic alias fallback " <> cid <> "/" <> target) $ isLeft $ Engine.resolveSchema (snapshotGame before) player
  normal50Request <- typed $ object ["campaign" .= ("50" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (normal50Initial,_) <- createSession normal50Request >>= right
  (normal50Ready,normal50Trace) <- runNotz False True normal50Initial
  normal50Restored <- importSnapshot (exportSnapshot normal50Initial) >>= right
  (normal50Replay,_) <- runNotz False True normal50Restored
  assert "settings campaign50 normal actual deck trauma prologue Continue to ReturnToGathering" $
    actualScenario normal50Ready == String "c50011"
      && any ((== String "Read") . field "tag" . field "question") normal50Trace
      && any ((== String "ContinueCampaign") . field "tag" . field "question") normal50Trace
  assert "settings campaign50 normal whole Game queue RNG replay" $ exportSnapshot normal50Ready == exportSnapshot normal50Replay
  normal50Saves <- traverse decodeSave [normal50Initial,normal50Ready,normal50Replay]
  emit "SETTINGS_CAMPAIGN50_NORMAL_FIXTURE " $ object ["initialSave" .= (normal50Saves P.!! 0),"readySave" .= (normal50Saves P.!! 1)
    ,"replaySave" .= (normal50Saves P.!! 2),"trace" .= normal50Trace]
  two50Initial <- createCampaign "50" "50025" [player,other]
  let untilTwoSettings budget chosen s
        | any ((== PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame s) = do
            assert "settings campaign50 actual two seats barrier complete" $ length (nub chosen) == 2
            pure s
        | budget <= (0 :: Int) = assert "settings campaign50 two seats bounded barrier" False >> pure s
        | otherwise = do
            let candidates = [(who,unwrapQuestion q) | (who,q) <- Map.toList $ gameQuestion $ snapshotGame s
                  ,case indexedChoices (unwrapQuestion q) of Nothing -> True; Just cs -> any (\case Info {} -> False; InvalidLabel {} -> False; _ -> True) cs]
            assert "settings campaign50 actual two seats actionable recipient" $ not $ null candidates
            let (who,q) = headEx candidates
            response <- chooseActual who (if who == other then decks P.!! 1 else deck) s
            (next,_) <- applyAnswer s who (snapshotRevision s) response >>= right
            let chosen' = if q == ChooseDeck then who:chosen else chosen
            when (length (nub chosen') < 2) $ assert "settings campaign50 first seat cannot bypass peer deck barrier" $
              all ((/= PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame next)
                && actualScenario next == Null
            untilTwoSettings (budget-1) chosen' next
  two50Before <- untilTwoSettings 20 [] two50Initial
  two50Save <- decodeSave two50Before
  emit "SETTINGS_CAMPAIGN50_TWO_SEAT_FIXTURE " $ object ["save" .= two50Save]

  -- Reuse05 form definitions only at these eight actual settings starts.
  -- The special54016/prologue route is verified separately below.
  let tcuStarts = [("54017","05050"),("54024","05065"),("54029","05120"),("54034","05161")
        ,("54042","05197"),("54046","05238"),("54049","05284"),("54056","05325")]
  tcuCases <- for tcuStarts $ \(target,base) -> do
    initial <- createCampaign "54" target [player]
    (before,deckTrace) <- runNotz True False initial
    schema <- offered before
    form <- complete schema
    preview <- right $ Engine.previewForm schema form
    baseInitial <- createCampaign "05" base [player]
    (baseBefore,_) <- runNotz True False baseInitial
    baseSchema <- offered baseBefore
    baseForm <- complete baseSchema
    basePreview <- right $ Engine.previewForm baseSchema baseForm
    standalone <- create target >>= offered >>= complete
    let context = field "context" $ Engine.schemaView schema
        beforeBytes = exportSnapshot before
    assert ("settings campaign54 exact actual and definition scope " <> target) $
      context == object ["mode" .= ("campaign" :: Text),"campaign" .= ("54" :: Text),"start" .= target
        ,"definitionCampaign" .= ("05" :: Text),"definitionStart" .= base,"aliasPolicy" .= ("reviewed-return-to-tcu-starts-v1" :: Text)]
        && field "id" (campaignOf before) == String "54"
    assert ("settings campaign54 exact prior base chapter rows including conditional interludes " <> target) $
      map (field "id") (array $ field "rows" preview) == map (field "id") (array $ field "rows" basePreview)
    assert ("settings campaign54 base and standalone form isolation " <> target) $
      isLeft (Engine.compileForm schema baseForm) && isLeft (Engine.compileForm baseSchema form)
        && isLeft (Engine.compileForm schema standalone)
    (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
    restored <- importSnapshot beforeBytes >>= right
    (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer form) >>= right
    (ready,trace) <- runNotz False True submitted
    (readyReplay,_) <- runNotz False True replayed
    assert ("settings campaign54 whole actual submission and natural Setup replay " <> target) $
      exportSnapshot submitted == exportSnapshot replayed && exportSnapshot ready == exportSnapshot readyReplay && exportSnapshot before == beforeBytes
    assert ("settings campaign54 actual ReturnTo scenario no forged completed history " <> target) $
      field "id" (campaignOf ready) == String "54" && actualScenario ready == String ("c" <> target)
        && all ((== toJSON ([] :: [Value])) . field "completedSteps" . campaignOf) [before,submitted,ready]
    when (target == "54017") do
      let offers = [entry | entry <- trace,field "tag" (field "question" entry) == String "PickDestiny"]
          actualDestiny = field "destiny" $ campaignOf ready
          currentCard = field "theWitchingHour" actualDestiny
          active = field "tarotCards" $ field "That" $ toJSON $ gameMode $ snapshotGame ready
      assert "settings campaign54 natural Destiny stores all8 campaign scope-card pairs" $
        length offers == 1 && case actualDestiny of Object values -> KM.size values == 8; _ -> False
      assert "settings campaign54 original campaign CheckDestiny applies only current scope card" $
        active == toJSON [(object ["tag" .= ("GlobalTarot" :: Text)],[currentCard])]
    saves <- traverse decodeSave [initial,before,submitted,replayed,ready,readyReplay]
    emit "SETTINGS_CAMPAIGN54_FIXTURE " $ object ["target" .= target,"base" .= base,"schema" .= Engine.schemaView schema,"form" .= form
      ,"basePreview" .= basePreview,"preview" .= preview,"initialSave" .= (saves P.!! 0),"beforeSave" .= (saves P.!! 1)
      ,"submittedSave" .= (saves P.!! 2),"replayedSave" .= (saves P.!! 3),"readySave" .= (saves P.!! 4),"readyReplaySave" .= (saves P.!! 5)
      ,"deckTrace" .= deckTrace,"setupTrace" .= trace]
    pure (target,before,schema,form,ready)
  for_ tcuCases $ \(target,_,schema,form,_) -> assert ("settings campaign54 cross-target form isolation " <> target) $
    all (\(otherTarget,_,otherSchema,otherForm,_) -> target == otherTarget ||
      (isLeft (Engine.compileForm schema otherForm) && isLeft (Engine.compileForm otherSchema form))) tcuCases
  let tcuCase target = fromJustNote "actual campaign54 settings start" $ find (\(t,_,_,_,_) -> t == target) tcuCases
      (_,tcu54Before,tcu54Schema,tcu54Default,_) = tcuCase "54056"
  for_ [("BlackBook","c05150","c05228",change "campaign:05/step/4/4" (FormBoolean True))
       ,("PuzzleBox","c05228","c05150",change "campaign:05/step/6/1" (FormBoolean True) .
          change "campaign:05/step/6/0" (FormChoice $ Just "campaign:05/step/6/0/entry/1"))] $ \(label,code,absent,edit) -> do
    form <- completeFrom tcu54Schema $ edit tcu54Default
    (submitted,_) <- applyAnswer tcu54Before player (snapshotRevision tcu54Before) (SettingsFormAnswer form) >>= right
    restored <- importSnapshot (exportSnapshot tcu54Before) >>= right
    (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer form) >>= right
    assert ("settings campaign54 option really grants one owned story reward " <> label) $
      case ownedStory code submitted of [card] -> investigatorIds submitted == [field "owner" card] && null (ownedStory absent submitted); _ -> False
    (ready,trace) <- runNotz False True submitted
    (readyReplay,_) <- runNotz False True replayed
    assert ("settings campaign54 reward natural Setup preserves one physical owned card " <> label) $
      length (ownedStory code ready) == 1 && length (heldStory code ready) == 1 && null (ownedStory absent ready)
    assert ("settings campaign54 reward whole snapshot replay " <> label) $ exportSnapshot ready == exportSnapshot readyReplay
    saves <- traverse decodeSave [submitted,ready,readyReplay]
    emit "SETTINGS_CAMPAIGN54_REWARD_FIXTURE " $ object ["label" .= (label :: Text),"code" .= code,"absent" .= absent,"form" .= form
      ,"submittedSave" .= headEx saves,"readySave" .= (saves P.!! 1),"replaySave" .= (saves P.!! 2),"trace" .= trace]
  for_ [("54029","escapedFalse"),("54029","escapedTrue"),("54046","interludeFalse"),("54046","interludeTrue")] $ \(target,label) -> do
    let (_,before,schema,defaultForm,_) = tcuCase target
        edited = case label of
          "escapedFalse" -> change "campaign:05/step/2/4" (FormChoice $ Just "campaign:05/step/2/4/entry/1") defaultForm
          "escapedTrue" -> change "campaign:05/step/2/4" (FormChoice $ Just "campaign:05/step/2/4/entry/0") defaultForm
          "interludeFalse" -> change "campaign:05/step/6/2" (FormBoolean False) $
            change "campaign:05/step/6/0" (FormChoice $ Just "campaign:05/step/6/0/entry/1") defaultForm
          _ -> change "campaign:05/step/7/0" (FormChoice $ Just "campaign:05/step/7/0/entry/1") $
            change "campaign:05/step/6/2" (FormBoolean True) $
            change "campaign:05/step/6/0" (FormChoice $ Just "campaign:05/step/6/0/entry/1") defaultForm
    form <- completeFrom schema edited
    compiled <- right $ Engine.compileForm schema form
    preview <- right $ Engine.previewForm schema form
    let scopedRows = [r | r <- array $ field "rows" preview,String ident <- [field "id" r]
          ,(if target == "54029" then "campaign:05/step/3/" else "campaign:05/step/7/") `T.isPrefixOf` ident]
        active = any ((== String "visible") . field "visibility") scopedRows
        expected = label `elem` ["escapedTrue","interludeTrue"]
    assert ("settings campaign54 conditional prior chapter visibility not unconditional " <> label) $ not (null scopedRows) && active == expected
    (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
    (ready,_) <- runNotz False True submitted
    assert ("settings campaign54 conditions do not auto jump to interlude " <> label) $ actualScenario ready == String ("c" <> target)
    when (label == "interludeTrue") $ assert "settings campaign54 Proceed option passive but hidden mementos key active" $
      any ((== String "TheInvestigatorsKeptsTheirMementosHidden") . field "contents") (array $ field "recorded" $ toJSON compiled)
    saved <- decodeSave ready
    emit "SETTINGS_CAMPAIGN54_CONDITION_FIXTURE " $ object ["label" .= label,"target" .= target,"form" .= form,"preview" .= preview,"compiled" .= compiled,"readySave" .= saved]
  for_ [("omitted",Nothing,[player]),("explicit",Just "54016",[player]),("two-seat",Just "54016",[player,other])] $ \(label,start,players) -> do
    request <- typed $ object $ ["campaign" .= ("54" :: Text),"seed" .= (171019 :: Int),"players" .= players] <> maybe [] (\sid -> ["scenario" .= (sid :: Text)]) start
    (initial,_) <- createSession request >>= right
    (ready,trace) <- runNotz False True initial
    restored <- importSnapshot (exportSnapshot initial) >>= right
    (replayed,_) <- runNotz False True restored
    assert ("settings campaign54 special prologue no fabricated settings or ordinary player decks " <> label) $
      actualScenario ready == String "c54016"
        && not (gameInSetup $ snapshotGame ready)
        && not (any (\entry -> field "tag" (field "question" entry) `elem` map String ["ChooseDeck","PickCampaignSettings","PickScenarioSettings"]) trace)
    assert ("settings campaign54 special prologue whole snapshot replay " <> label) $ exportSnapshot ready == exportSnapshot replayed
    let ids = investigatorIds ready
        actualInvestigators = map snd $ objectPairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame ready
    assert ("settings campaign54 special prologue unique actual investigators for all seats " <> label) $
      length ids == length players && length (nub ids) == length ids && all (`elem` map String ["c05046","c05047","c05048","c05049"]) ids
        && all (\who -> length (filter ((== toJSON who) . field "playerId") actualInvestigators) == 1) players
    saves <- traverse decodeSave [initial,ready,replayed]
    emit "SETTINGS_CAMPAIGN54_PROLOGUE_FIXTURE " $ object ["label" .= (label :: Text),"initialSave" .= headEx saves,"readySave" .= (saves P.!! 1),"replaySave" .= (saves P.!! 2),"trace" .= trace]
  skip54Request <- typed $ object ["campaign" .= ("54" :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
  (skip54Initial,_) <- createSession skip54Request >>= right
  (skip54Ready,skip54Trace) <- runWithPrologue "returnToSkip" False True skip54Initial
  skip54Restored <- importSnapshot (exportSnapshot skip54Initial) >>= right
  (skip54Replay,_) <- runWithPrologue "returnToSkip" False True skip54Restored
  let ordinaryDecks = [entry | entry <- skip54Trace,field "tag" (field "question" entry) == String "ChooseDeck"]
      inheritedSets = field "recordedSets" $ field "beforeCampaignLog" $ headEx ordinaryDecks
  assert "settings campaign54 natural skip crosses genuine continuation to ordinary deck then54017" $
    actualScenario skip54Ready == String "c54017" && length ordinaryDecks == 1
      && any ((== String "ContinueCampaign") . field "tag" . field "question") skip54Trace
  assert "settings campaign54 natural skip keeps actual prologue history records without reasking settings" $
    inheritedSets /= toJSON ([] :: [Value]) && field "recordedSets" (field "log" $ campaignOf skip54Ready) == inheritedSets
      && any (\step -> field "tag" step == String "ScenarioStep" && field "contents" step == String "c54016") (array $ field "completedSteps" $ campaignOf skip54Ready)
  assert "settings campaign54 natural skip continuation entire Game queue RNG replay" $ exportSnapshot skip54Ready == exportSnapshot skip54Replay
  skip54Saves <- traverse decodeSave [skip54Initial,skip54Ready,skip54Replay]
  emit "SETTINGS_CAMPAIGN54_SKIP_FIXTURE " $ object ["initialSave" .= headEx skip54Saves,"readySave" .= (skip54Saves P.!! 1)
    ,"replaySave" .= (skip54Saves P.!! 2),"trace" .= skip54Trace]

  -- These campaigns have no published initial-history editor. A direct late
  -- start is a fresh campaign with its real default bag, NOT a reconstruction
  -- of earlier chapters and NOT standalone setup. The two special53 routes
  -- remain explicitly separate from the standalone manifest.
  let omittedStarts =
        [("52","03","52014","03043",False)] <>
        [("53","04",target,base,False) | (target,base) <-
          [("53016","04043"),("53017","04054"),("53028","04113"),("53038","04161")
          ,("53045","04205a"),("53053","04237"),("53059","04277"),("53061","04314")]] <>
        [("53","04","53048","04205b",True),("53","04","53066","04344",True)]
      scenarioOf s = field "That" $ toJSON $ gameMode $ snapshotGame s
      campaignBag s = array $ field "chaosBag" $ campaignOf s
      suppliesOf s = [(iid,field "supplies" inv) | (iid,inv) <- objectPairs $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame s]
      emptySettingsLog = object ([key .= ([] :: [Value]) | key <- ["recorded","crossedOut","recordedCounts","recordedSets","orderedKeys","options"]] <> ["partners" .= object []])
  omittedCases <- for omittedStarts $ \(cid,definition,target,base,special) -> do
    initial <- createCampaign cid target [player]
    (before,deckTrace) <- runNotz True False initial
    schema <- offered before
    form <- complete schema
    preview <- right $ Engine.previewForm schema form
    compiled <- right $ Engine.compileForm schema form
    baseInitial <- createCampaign definition base [player]
    (baseBefore,_) <- runNotz True False baseInitial
    let (_,foreignSchema,foreignForm) = headEx notzCases
    assert ("settings campaign5253 base omitted form remains explicitly unsupported " <> target) $
      isLeft $ Engine.resolveSchema (snapshotGame baseBefore) player
    let expectedContext = object $
          ["mode" .= ("campaign" :: Text),"campaign" .= cid,"start" .= target
          ,"definitionCampaign" .= definition,"definitionStart" .= base
          ,"aliasPolicy" .= ("reviewed-source-omitted-campaign-starts-v1" :: Text)
          ,"entryPolicy" .= (if special then "fresh-campaign-defaults-no-inherited-history" else "fresh-campaign-input-only" :: Text)]
          <> (if cid == "53" then ["metadataPolicy" .= ("materialize-original-mempty-only-when-null-v1" :: Text),"initializeMissingMetadata" .= True] else [])
          <> ["standaloneSupported" .= False | special]
        beforeBytes = exportSnapshot before
    assert ("settings campaign5253 exact reviewed scope and fresh-entry policy " <> target) $
      field "context" (Engine.schemaView schema) == expectedContext
    assert ("settings campaign5253 empty form cannot invent previous history " <> target) $
      Map.null (valuesByRowId form) && null (array $ field "rows" preview)
        && field "canSubmit" preview == Bool True && toJSON compiled == emptySettingsLog
        && campaignBag initial == campaignBag before
    assert ("settings campaign5253 base definition never substitutes actual target " <> target) $
      field "id" (campaignOf before) == String cid
        && isLeft (Engine.compileForm schema foreignForm) && isLeft (Engine.compileForm foreignSchema form)
    assert ("settings campaign5253 unknown history row rejected " <> target) $
      isLeft $ Engine.compileForm schema $ change "fabricated-prior-supplies" (FormBoolean True) form
    when special do
      standalone <- create target
      assert ("settings campaign5253 special is campaign-only not standalone permission " <> target) $
        isLeft $ Engine.resolveSchema (snapshotGame standalone) player
    (submitted,_) <- applyAnswer before player (snapshotRevision before) (SettingsFormAnswer form) >>= right
    when (cid == "53") $ assert ("settings campaign53 fresh Null materialized exact original empty Metadata not fabricated supplies " <> target) $
      field "meta" (campaignOf before) == Null
        && field "meta" (campaignOf submitted) == object ["supplyPoints" .= object [],"yithians" .= ([] :: [Value]),"expeditionLeader" .= Null,"bonusXp" .= object []]
    restored <- importSnapshot beforeBytes >>= right
    (replayed,_) <- applyAnswer restored player (snapshotRevision restored) (SettingsFormAnswer form) >>= right
    (ready,trace) <- runNotz False True submitted
    (readyReplay,_) <- runNotz False True replayed
    assert ("settings campaign5253 actual typed submission natural Setup entire replay " <> target) $
      exportSnapshot before == beforeBytes && exportSnapshot submitted == exportSnapshot replayed
        && exportSnapshot ready == exportSnapshot readyReplay
    assert ("settings campaign5253 real wrapper identity and ended Setup " <> target) $
      field "id" (campaignOf ready) == String cid && actualScenario ready == String ("c" <> target)
        && not (gameInSetup $ snapshotGame ready)
        && field "scenarioOptionsStandalone" (field "options" $ scenarioOf ready) == Bool False
    assert ("settings campaign5253 no fabricated completed chapters at fresh late start " <> target) $
      all ((== toJSON ([] :: [Value])) . field "completedSteps" . campaignOf) [initial,before,submitted,ready]
    when (cid == "53") $ assert ("settings campaign5253 late fresh start did not fake supply prologue " <> target) $
      all ((== toJSON ([] :: [Value])) . snd) (suppliesOf ready)
        && not (any ((== String "PickSupplies") . field "tag" . field "question") trace)
    when (target == "53066") $ assert "settings campaign5253 fresh TurnBackTime adds exactly one ElderThing to real default campaign bag" $
      sort (campaignBag ready) == sort (String "ElderThing" : campaignBag initial)
    saves <- traverse decodeSave [initial,before,submitted,replayed,ready,readyReplay]
    emit "SETTINGS_CAMPAIGN5253_FRESH_FIXTURE " $ object
      ["campaign" .= cid,"definitionCampaign" .= definition,"target" .= target,"base" .= base,"special" .= special
      ,"schema" .= Engine.schemaView schema,"form" .= form,"preview" .= preview,"compiled" .= compiled
      ,"initialSave" .= headEx saves,"beforeSave" .= (saves P.!! 1),"submittedSave" .= (saves P.!! 2)
      ,"replayedSave" .= (saves P.!! 3),"readySave" .= (saves P.!! 4),"readyReplaySave" .= (saves P.!! 5)
      ,"deckTrace" .= deckTrace,"setupTrace" .= trace]
    pure (cid,target,before,schema,form,ready)
  for_ omittedCases $ \(_,target,_,schema,form,_) -> assert ("settings campaign5253 all11 fresh scopes mutually isolated " <> target) $
    all (\(_,otherTarget,_,otherSchema,otherForm,_) -> target == otherTarget ||
      (isLeft (Engine.compileForm schema otherForm) && isLeft (Engine.compileForm otherSchema form))) omittedCases
  for_ [("52","03043"),("53","04043"),("52","53016"),("53","52014"),("52","53048") ] $ \(cid,target) -> do
    initial <- createCampaign cid target [player]
    (before,_) <- runNotz True False initial
    assert ("settings campaign5253 no generic foreign target fallback " <> cid <> "/" <> target) $
      isLeft $ Engine.resolveSchema (snapshotGame before) player
  normalOmitted <- for [("52",[player]),("53",[player]),("53",[player,other])] $ \(cid,players) -> do
    request <- typed $ object ["campaign" .= (cid :: Text),"seed" .= (171019 :: Int),"players" .= players]
    (initial,_) <- createSession request >>= right
    (ready,trace) <- runNotz False True initial
    restored <- importSnapshot (exportSnapshot initial) >>= right
    (replayed,_) <- runNotz False True restored
    let target = if cid == "52" then "52014" else "53016"
        supplyTrace = [t | t <- trace,field "tag" (field "question" t) == String "PickSupplies"]
        supplyRecipients = nub $ map (field "recipient") supplyTrace
    assert ("settings campaign5253 real normal prologue without invented initial form " <> cid <> "/" <> tshow (length players)) $
      actualScenario ready == String ("c" <> target) && not (gameInSetup $ snapshotGame ready)
        && any ((== String "Read") . field "tag" . field "question") trace
        && not (any (\t -> field "tag" (field "question" t) `elem` map String ["PickCampaignSettings","PickScenarioSettings"]) trace)
    assert ("settings campaign5253 normal whole snapshot and RNG replay " <> cid <> "/" <> tshow (length players)) $
      exportSnapshot ready == exportSnapshot replayed
    when (cid == "53") do
      let points = if length players == 1 then 10 else 7 :: Int
          remaining = field "supplyPoints" $ field "meta" $ campaignOf ready
      assert ("settings campaign53 every actual seat buys one genuine Provisions " <> tshow (length players)) $
        sort supplyRecipients == sort (map toJSON players)
          && length supplyTrace == 2 * length players
          && all ((== toJSON [String "Provisions"]) . snd) (suppliesOf ready)
          && length (suppliesOf ready) == length players
      assert ("settings campaign53 actual per-seat supply points decrease once " <> tshow (length players)) $
        length (objectPairs remaining) == length players && all ((== toJSON (points-1)) . snd) (objectPairs remaining)
    saves <- traverse decodeSave [initial,ready,replayed]
    emit "SETTINGS_CAMPAIGN5253_NORMAL_FIXTURE " $ object
      ["campaign" .= cid,"seats" .= length players,"initialSave" .= headEx saves,"readySave" .= (saves P.!! 1)
      ,"replaySave" .= (saves P.!! 2),"trace" .= trace]
    pure (cid,length players,ready)
  assert "settings campaign5253 three distinct actual normal prologue cases retained" $ length normalOmitted == 3

  for_ ["52021","52028","52034","52040","52048","52054","52059"] $ \target -> do
    request <- typed $ object ["campaign" .= ("52" :: Text),"scenario" .= (target :: Text),"seed" .= (171019 :: Int),"players" .= [player]]
    result <- createSession request
    assert ("settings campaign52 pending complete history editor rejects empty fresh start before deck " <> target) $
      case result of Left reason -> "empty-history creation" `T.isInfixOf` reason; _ -> False

  -- TEST-ONLY scenario jump plus separately controlled resolution. Supplies
  -- come from the genuine normal prologue above. No prior completion, history,
  -- chaos bag or victory condition is fabricated, and no Raw UI route exists.
  let (_,_,natural53) = fromJustNote "actual solo53 normal prologue" $ find (\(cid,seats,_) -> cid == "53" && seats == 1) normalOmitted
      controlledQueue s messages = do
        env <- makeEnv (snapshotGame s) $ ClearUI : messages
        advance env
        fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s) env
      jumpMessages sid = traverse (typed @Message)
        [object ["tag" .= (tag :: Text),"contents" .= object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c" <> sid :: Text)]]
        | tag <- ["SetCampaignStep","CampaignStep"]]
  let (_,_,metadataBefore,_,_,_) = fromJustNote "actual53 initial settings before Null meta" $ find (\(cid,target,_,_,_,_) -> cid == "53" && target == "53016") omittedCases
      metadataCheckpoint value = do
        message <- typed @Message $ object ["tag" .= ("SetCampaignMeta" :: Text),"contents" .= value]
        env <- makeEnv (snapshotGame metadataBefore) [message]
        advance env
        fst <$> finish (snapshotHistoricalBootstrap metadataBefore) (snapshotRevision metadataBefore) env
  preservedMetadata <- metadataCheckpoint $ field "meta" $ campaignOf natural53
  preservedSchema <- offered preservedMetadata
  preservedForm <- complete preservedSchema
  preservedMessages <- right $ Engine.compileSubmission preservedSchema preservedForm
  let (_,_,_,freshMetadataSchema,freshMetadataForm,_) = fromJustNote "actual53 fresh Null schema" $ find (\(cid,target,_,_,_,_) -> cid == "53" && target == "53016") omittedCases
  assert "settings campaign53 metadata presence bound to server-resolved schema not client flag" $
    isLeft (Engine.compileForm preservedSchema freshMetadataForm) && isLeft (Engine.compileForm freshMetadataSchema preservedForm)
  assert "settings campaign53 actual nonNull prologue Metadata never replaced by empty default" $
    not (any (\case SetCampaignMeta {} -> True; _ -> False) preservedMessages)
      && field "initializeMissingMetadata" (field "context" $ Engine.schemaView preservedSchema) == Bool False
  (preservedReady,_) <- applyAnswer preservedMetadata player (snapshotRevision preservedMetadata) (SettingsFormAnswer preservedForm) >>= right
  assert "settings campaign53 nonNull real supply-point Metadata survives actual submission" $
    field "meta" (campaignOf preservedReady) == field "meta" (campaignOf natural53)
  malformedMetadata <- metadataCheckpoint $ object ["notAValidMetadata" .= True]
  assert "settings campaign53 malformed nonNull Metadata rejected not reset" $ isLeft $ Engine.resolveSchema (snapshotGame malformedMetadata) player
  let malformedBytes = exportSnapshot malformedMetadata
  rejectedMetadata <- applyAnswer malformedMetadata player (snapshotRevision malformedMetadata) (SettingsFormAnswer freshMetadataForm)
  assert "settings campaign53 malformed Metadata actual submission rejects and leaves old bytes" $
    isLeft rejectedMetadata && exportSnapshot malformedMetadata == malformedBytes
  metadataSaves <- traverse decodeSave [metadataBefore,preservedMetadata,preservedReady,malformedMetadata]
  emit "SETTINGS_CAMPAIGN53_METADATA_FIXTURE " $ object
    ["controlledMetadataCheckpoint" .= True,"nullBeforeSave" .= headEx metadataSaves,"preservedBeforeSave" .= (metadataSaves P.!! 1)
    ,"preservedAfterSave" .= (metadataSaves P.!! 2),"malformedSave" .= (metadataSaves P.!! 3),"schema" .= Engine.schemaView preservedSchema]
  for_ [("53045","53048",1),("53061","53066",5)] $ \(precursor,target,resolution) -> do
    jump <- jumpMessages precursor
    jumped <- controlledQueue natural53 jump
    (prepared,setupTrace) <- runNotz False True jumped
    assert ("settings campaign53 controlled jump genuine Setup without fictional completion " <> precursor) $
      actualScenario prepared == String ("c" <> precursor)
        && field "completedSteps" (campaignOf prepared) == field "completedSteps" (campaignOf natural53)
        && sort (suppliesOf prepared) == sort (suppliesOf natural53)
        && sort (campaignBag prepared) == sort (campaignBag natural53)
    resolutionMessage <- typed @Message $ object ["tag" .= ("ScenarioResolution" :: Text)
      ,"contents" .= object ["tag" .= ("Resolution" :: Text),"contents" .= (resolution :: Int)]]
    let preparedBytes = exportSnapshot prepared
    afterResolution <- controlledQueue prepared [resolutionMessage]
    (ready,trace) <- runNotz False True afterResolution
    imported <- importSnapshot preparedBytes >>= right
    replayResolution <- controlledQueue imported [resolutionMessage]
    (replayed,_) <- runNotz False True replayResolution
    assert ("settings campaign53 original resolution actually continues into special chapter " <> target) $
      actualScenario ready == String ("c" <> target) && not (gameInSetup $ snapshotGame ready)
        && any ((== String "ContinueCampaign") . field "tag" . field "question") trace
    assert ("settings campaign53 inherited actual per-investigator Provisions not erased by fresh form " <> target) $
      sort (suppliesOf ready) == sort (suppliesOf natural53)
        && not (any (\t -> field "tag" (field "question" t) `elem` map String ["PickCampaignSettings","PickScenarioSettings","PickSupplies"]) trace)
    assert ("settings campaign53 inherited bag only original special addition " <> target) $
      sort (campaignBag ready) == sort ((if target == "53066" then [String "ElderThing"] else []) <> campaignBag prepared)
    assert ("settings campaign53 true resolution marks actual precursor completed " <> target) $
      object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c" <> precursor :: Text)] `elem` array (field "completedSteps" $ campaignOf ready)
    let completedScenarios s = filter ((== String "ScenarioStep") . field "tag") $ array $ field "completedSteps" $ campaignOf s
        precursorStep = object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c" <> precursor :: Text)]
    assert ("settings campaign53 no skipped fictional scenario completion after resolution " <> target) $
      sort (completedScenarios ready) == sort (precursorStep : completedScenarios natural53)
    assert ("settings campaign53 inherited entire Game queue RNG replay and old snapshot immutable " <> target) $
      exportSnapshot ready == exportSnapshot replayed && exportSnapshot prepared == preparedBytes
    when (target == "53066") $ assert "settings campaign53 ShatteredAeons R5 actually records turned back time" $
      any ((== String "TheInvestigatorsTurnedBackTime") . field "contents") (array $ field "recorded" $ field "log" $ campaignOf ready)
    saves <- traverse decodeSave [natural53,jumped,prepared,afterResolution,ready,replayed]
    emit "SETTINGS_CAMPAIGN53_CONTROLLED_CONTINUATION_FIXTURE " $ object
      ["controlledJump" .= True,"controlledResolution" .= True,"naturalFullScenarioClear" .= False
      ,"precursor" .= precursor,"target" .= target,"resolution" .= resolution,"jumpMessages" .= jump,"resolutionMessage" .= resolutionMessage
      ,"naturalPrologueSave" .= headEx saves,"jumpedSave" .= (saves P.!! 1),"preparedSave" .= (saves P.!! 2)
      ,"afterResolutionSave" .= (saves P.!! 3),"readySave" .= (saves P.!! 4),"replaySave" .= (saves P.!! 5)
      ,"setupTrace" .= setupTrace,"continuationTrace" .= trace]

  -- Separate campaign52 inherited-state proof. The Stranger is ACTUALLY
  -- granted by original CurtainCall R1, never created by this test helper.
  -- Seven later chapters branch from one earned-reward checkpoint; the jump
  -- is controlled and never claims the skipped scenarios were played.
  let (_,_,natural52) = fromJustNote "actual normal52 prologue" $ find (\(cid,seats,_) -> cid == "52" && seats == 1) normalOmitted
  curtainResolution <- typed @Message $ object ["tag" .= ("ScenarioResolution" :: Text),"contents" .= object ["tag" .= ("Resolution" :: Text),"contents" .= (1 :: Int)]]
  curtainResolved <- controlledQueue natural52 [curtainResolution]
  (curtainCheckpoint,curtainTrace) <- runToContinuation curtainResolved
  let strangerCode = "c03059"
      earnedStrangers = ownedStory strangerCode curtainCheckpoint
      savedStranger = headEx earnedStrangers
  assert "settings campaign52 actual original CurtainCall resolution grants unique owned Stranger" $
    length earnedStrangers == 1 && field "owner" savedStranger `elem` investigatorIds curtainCheckpoint
  continuation52 <- right $ offeredContinuation $ gameMode $ snapshotGame curtainCheckpoint
  curtainCheckpointSave <- decodeSave curtainCheckpoint
  emit "SETTINGS_CAMPAIGN52_EARNED_CHECKPOINT_FIXTURE " $ object
    ["controlledResolution" .= True,"save" .= curtainCheckpointSave,"continuation" .= continuation52,"trace" .= curtainTrace]
  assert "settings campaign52 actual first Continue not submitted before controlled jump" $
    field "nextStep" (toJSON continuation52) == object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c52021" :: Text)]
      -- Original ForTarget GameTarget EndOfScenario tears down the scenario
      -- and changes These to This BEFORE displaying campaign continuation.
      && actualScenario curtainCheckpoint == Null
      && object ["tag" .= ("ScenarioStep" :: Text),"contents" .= ("c52014" :: Text)] `elem` array (field "completedSteps" $ campaignOf curtainCheckpoint)
      && any ((== ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame curtainCheckpoint)
  for_ ["52021","52028","52034","52040","52048","52054","52059"] $ \target -> do
    jump <- jumpMessages target
    jumped <- controlledQueue curtainCheckpoint jump
    (ready,trace) <- runNotz False True jumped
    restored <- importSnapshot (exportSnapshot jumped) >>= right
    (replayed,_) <- runNotz False True restored
    assert ("settings campaign52 inherited Stranger exact UUID owner not regenerated " <> target) $
      ownedStory strangerCode ready == earnedStrangers
    let oldLog = field "log" $ campaignOf curtainCheckpoint
        newLog = field "log" $ campaignOf ready
        enteredKey = object ["tag" .= ("ThePathToCarcosaKey" :: Text),"contents" .= ("YouEnteredTheCatacombsOnYourOwn" :: Text)]
    assert ("settings campaign52 original Setup changes only exact permitted campaign log facts " <> target) $
      if target == "52048" then
        sort (array $ field "recorded" newLog) == sort (enteredKey : array (field "recorded" oldLog))
          && array (field "orderedKeys" newLog) == array (field "orderedKeys" oldLog) <> [enteredKey]
          && all (\key -> field key newLog == field key oldLog) ["crossedOut","recordedCounts","recordedSets","options","partners"]
      else newLog == oldLog
    assert ("settings campaign52 original inherited late Setup replay and real target " <> target) $
      exportSnapshot ready == exportSnapshot replayed && actualScenario ready == String ("c" <> target)
        && not (gameInSetup $ snapshotGame ready)
    assert ("settings campaign52 controlled jump adds no fictitious completed scenario " <> target) $
      all ((== field "completedSteps" (campaignOf curtainCheckpoint)) . field "completedSteps" . campaignOf) [jumped,ready]
        && not (any (\t -> field "tag" (field "question" t) `elem` map String ["PickCampaignSettings","PickScenarioSettings"]) trace)
    when (target `elem` ["52048","52059"]) do
      let setAsideCards = [field "contents" card | card <- array $ field "setAsideCards" $ scenarioOf ready
            ,field "tag" card == String "PlayerCard",field "cardCode" (field "contents" card) == String strangerCode]
      assert ("settings campaign52 required Stranger consumed from original physical deck into setAside " <> target) $
        null (heldStory strangerCode ready) && setAsideCards == [savedStranger]
    saves <- traverse decodeSave [natural52,curtainCheckpoint,jumped,ready,replayed]
    emit "SETTINGS_CAMPAIGN52_INHERITED_FIXTURE " $ object
      ["controlledResolution" .= True,"controlledJump" .= True,"naturalFullScenarioClear" .= False
      ,"target" .= (target :: Text),"stranger" .= savedStranger,"jumpMessages" .= jump,"resolutionMessage" .= curtainResolution
      ,"naturalPrologueSave" .= headEx saves,"earnedRewardSave" .= (saves P.!! 1),"jumpedSave" .= (saves P.!! 2)
      ,"readySave" .= (saves P.!! 3),"replaySave" .= (saves P.!! 4),"resolutionTrace" .= curtainTrace,"setupTrace" .= trace]

objectPairs :: Value -> [(Text,Value)]
objectPairs (Object o) = [(K.toText key,value) | (key,value) <- KM.toList o]
objectPairs (Array xs) = [("",lastEx pair) | Array items <- toList xs,let pair = toList items,not $ null pair]
objectPairs _ = []
