module SettingsDefinitionSpec (settingsDefinitionSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Native.Settings.Types
import Arkham.Native.Settings.Definition
import Arkham.Native.Settings.Catalog
import Arkham.Native.Settings.Predicate
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Either (isLeft, isRight)
import Data.Text qualified as T
import Data.ByteString.Lazy qualified as BL
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label result = if result then putStrLn $ "PASS " <> label else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\reason -> putStrLn reason >> exitFailure) pure

knownState :: [(Fact,Truth)] -> CrashDraft -> PredicateState
knownState facts crash = either (const $ error "Invalid predicate test state") id $ makePredicateState (map fst facts) facts crash

display :: Show a => a -> Text
display = T.pack . P.show

objectsIn :: Value -> [Value]
objectsIn value = case value of
  Object fields -> value : concatMap objectsIn (KM.elems fields)
  Array values -> concatMap objectsIn $ toList values
  _ -> []

settingsDefinitionSpecs :: IO ()
settingsDefinitionSpecs = do
  catalog <- right catalogResult
  let standalone = concatMap (flattenRows . scenarioRows) $ catalogScenarios catalog
      campaign = concatMap (concatMap chapterAllRows . campaignChapters) $ catalogCampaigns catalog
      rows = standalone <> campaign
      getRow ident = fromJustNote "actual catalog row" $ find ((== ident) . rowId) rows
      getCampaign ident = fromJustNote "actual catalog campaign" $ find ((== ident) . campaignIdentity) $ catalogCampaigns catalog
      parseForm value = parseDefinition @SettingsFormRequest value
      form values = object ["schemaId" .= ("test-schema" :: Text),"valuesByRowId" .= (values :: [Value])]
      row ident value = object ["rowId" .= (ident :: Text),"value" .= (value :: Value)]
      boolean b = object ["kind" .= ("boolean" :: Text),"value" .= b]
      integer n = object ["kind" .= ("integer" :: Text),"value" .= (n :: Value)]
      entry ident checked = object ["id" .= (ident :: Text),"checked" .= checked]
      entries values = object ["kind" .= ("entries" :: Text),"value" .= (values :: [Value])]
      choice value = object ["kind" .= ("choice" :: Text),"value" .= value]
  assert "settings typed catalog exact identity" $ catalogIdentity catalog == "arkham-settings-sha256:27a8e5559a273b0ab09a88081e384bf821488ef23014ed2828d536b4d5b9a920"
  assert "settings actual Haskell parses every 146 standalone and 75 campaign row" $ length standalone == 146 && length campaign == 75
  assert "settings catalog contains 146 scenarios and 16 campaigns" $ length (catalogScenarios catalog) == 146 && length (catalogCampaigns catalog) == 16
  assert "settings preserves all 43 pending aliases without base reuse" $ length (catalogAliases catalog) == 43 && case lookupScenario catalog "50011" of AliasPending a -> aliasBase a == "01104"; _ -> False
  assert "settings unknown identity differs from alias" $ case lookupScenario catalog "999999999" of UnknownDefinition -> True; _ -> False
  assert "settings omitted Gathering definition not converted to declared empty" $ case lookupScenario catalog "01104" of ExactDefinition s -> scenarioPresence s == SourceOmitted && null (scenarioRows s); _ -> False
  assert "settings declared empty BeforeBlackThrone precursor retains presence" $ case lookupScenario catalog "05284" of ExactDefinition s -> scenarioPresence s == Declared && null (scenarioRows s); _ -> False
  assert "settings pending resolution row retained but outside chapter rows" $ let chapters = campaignChapters $ getCampaign "05" in sum (map (length . chapterResolutions) chapters) == 1 && length (concatMap (flattenRows . chapterRows) chapters) + 1 == length (concatMap chapterAllRows chapters)
  for_ rows $ \definition -> do
    value <- right $ first T.pack $ Aeson.parseEither (const $ defaultFormValue definition) ()
    assert ("settings typed default/roundtrip " <> rowId definition) $ maybe True (\v -> parseDefinition @FormValue (toJSON v) == Right v) value
  let campPlaceholders = filter ((== Just "source-placeholder") . rowDefaultPresence) standalone
  assert "settings both invalid Camp source defaults remain unanswered" $ length campPlaceholders == 2 && all (\r -> Aeson.parseEither (const $ defaultFormValue r) () == Right (Just $ FormChoice Nothing)) campPlaceholders
  let numberNoDefault = filter (\r -> rowKind r == ChooseNum && rowDefault r == Just Null) rows
  assert "settings no-default numbers remain unanswered not zero" $ not (null numberNoDefault) && all (\r -> Aeson.parseEither (const $ defaultFormValue r) () == Right Nothing) numberNoDefault
  for_ [CardRecord,MementoRecord,MemoryRecord,GenericRecord] $ \kind -> do
    let value = case kind of
          CardRecord -> String "01121b"
          MementoRecord -> String "MesmerizingFlute"
          MemoryRecord -> String "AMeetingWithThomasDawson"
          GenericRecord -> object ["nested" .= ([1,2] :: [Int])]
        parseRecord crossed = Aeson.parseEither (const $ typedRecord kind crossed value) ()
    a <- right $ first T.pack $ parseRecord False
    b <- right $ first T.pack $ parseRecord True
    let roundtrip = Aeson.parseEither (\v -> (\parsed -> parsed `asTypeOf` a) <$> parseJSON v) $ toJSON a
    assert ("settings Haskell recorded and crossed distinct " <> display kind) $ toJSON a /= toJSON b && isRight roundtrip
  assert "settings invalid actual memento rejected before log construction" $ isLeft $ Aeson.parseEither (const $ typedRecord MementoRecord False $ String "NotAMemento") ()
  assert "settings record kind cannot be replaced by same string in Memory" $ isLeft $ Aeson.parseEither (const $ typedRecord MemoryRecord False $ String "MesmerizingFlute") ()
  let wellFormed = form [row "r1" $ boolean True,row "r2" $ integer $ toJSON (9007199254740993 :: Int),row "r3" $ entries [entry "e1" False],row "r4" $ choice Null]
  request <- right $ parseForm wellFormed
  assert "settings request roundtrip retains Int64 above JS safe range" $ parseForm (toJSON request) == Right request && Map.lookup "r2" (valuesByRowId request) == Just (FormInteger 9007199254740993)
  assert "settings duplicate row IDs rejected before Map" $ isLeft $ parseForm $ form [row "r" $ boolean False,row "r" $ boolean True]
  assert "settings duplicate entry IDs rejected before Map" $ isLeft $ parseForm $ form [row "r" $ entries [entry "e" False,entry "e" True]]
  assert "settings object dictionary not accepted as duplicate-losing row map" $ isLeft $ parseForm $ object ["schemaId" .= ("s" :: Text),"valuesByRowId" .= object ["r" .= boolean True]]
  assert "settings fractional count rejected" $ isLeft $ parseForm $ form [row "r" $ integer $ Number 1.5]
  assert "settings Int64 overflow rejected" $ isLeft $ parseForm $ form [row "r" $ integer $ Number 9223372036854775808]
  assert "settings boolean cannot stand in for integer" $ isLeft $ parseForm $ form [row "r" $ integer $ Bool False]
  assert "settings null integer is not zero" $ isLeft $ parseForm $ form [row "r" $ integer Null]
  assert "settings unknown raw message form rejected" $ isLeft $ parseForm $ form [row "r" $ object ["kind" .= ("Raw" :: Text),"value" .= ("SetCampaignLog" :: Text)]]
  assert "settings identity length bounded" $ isLeft $ parseForm $ form [row (T.replicate 513 "x") $ boolean True]
  assert "settings control character identity rejected" $ isLeft $ parseForm $ form [row "r\n" $ boolean True]
  assert "settings row count bounded before Map" $ isLeft $ parseForm $ form $ replicate 513 $ row "r" $ boolean True
  for_ ["Safe","Resolute","Eliminated","Mia","Victim","CannotTake","TheEntity"] $ \status -> do
    let value = form [row "partner" $ object ["kind" .= ("partner" :: Text),"damage" .= (0 :: Int),"horror" .= (0 :: Int),"status" .= (status :: Text)]]
    assert ("settings only three editor partner states " <> status) $ isRight (parseForm value) == (status `elem` ["Safe","Resolute","Eliminated"])
  let secret = "UNTRUSTED-PAYLOAD-MUST-NOT-BE-ECHOED"
      bad = parseForm $ form [row "r" $ object ["kind" .= (secret :: Text),"value" .= secret]]
  assert "settings unknown value error does not echo payload" $ case bad of Left reason -> not $ secret `T.isInfixOf` reason; _ -> False
  let badStatus = parseForm $ form [row "r" $ object ["kind" .= ("partner" :: Text),"damage" .= (0 :: Int),"horror" .= (0 :: Int),"status" .= secret]]
      badInteger = parseForm $ form [row "r" $ integer $ String secret]
  assert "settings untrusted partner enum error never echoes payload" $ case badStatus of Left reason -> not $ secret `T.isInfixOf` reason; _ -> False
  assert "settings untrusted numeric error never echoes payload" $ case badInteger of Left reason -> not $ secret `T.isInfixOf` reason; _ -> False
  for_ [ProvenFalse,ProvenTrue,Unanswered] $ \truth -> do
    assert ("settings tri not involution " <> display truth) $ negateTruth (negateTruth truth) == truth
    assert ("settings tri and identity " <> display truth) $ allTruth [ProvenTrue,truth] == truth
    assert ("settings tri or identity " <> display truth) $ anyTruth [ProvenFalse,truth] == truth
  assert "settings not unanswered is unanswered not true" $ negateTruth Unanswered == Unanswered
  assert "settings false dominates unknown all" $ allTruth [Unanswered,ProvenFalse] == ProvenFalse
  assert "settings true dominates unknown any" $ anyTruth [Unanswered,ProvenTrue] == ProvenTrue
  let st = knownState [(KeyFact "same" (Just "chapter-one"),ProvenTrue),(OptionFact "pending",Unanswered)] CrashAbsent
  assert "settings scope exact and unscoped OR are distinct" $ evaluateCondition st (HasKey "same" $ Just "chapter-two") == ProvenFalse && evaluateCondition st (HasKey "same" Nothing) == ProvenTrue
  assert "settings unknown parent cannot become a forced negation" $ evaluateCondition st (Not $ HasOption "pending") == Unanswered
  assert "settings counts above 2^53 compare as actual Int64" $ evaluateCondition (knownState [(CountFact "count" $ Just 9007199254740993,ProvenTrue)] CrashAbsent) (CountAtLeast "count" 9007199254740993) == ProvenTrue
  assert "settings unanswered count predicate remains unknown" $ evaluateCondition (knownState [(CountFact "count" Nothing,Unanswered)] CrashAbsent) (CountAtMost "count" 0) == Unanswered
  let duplicateCounts = makePredicateState [CountFact "count" Nothing] [(CountFact "count" $ Just 5,ProvenTrue),(CountFact "count" $ Just 1,ProvenTrue)] CrashAbsent
  assert "settings conflicting effective count values rejected not OR matched" $ isLeft duplicateCounts
  missingState <- right $ makePredicateState (definitionFactDomain Nothing $ getRow "campaign:02/step/0/0") [] CrashAbsent
  assert "settings actual unsubmitted required choice produces Unknown automatically" $ evaluateCondition missingState (Not $ HasKey "TheInvestigatorsRescuedProfessorWarrenRice" Nothing) == Unanswered
  assert "settings future or foreign fact cannot enter declared context" $ isLeft $ makePredicateState [] [(OptionFact "future",ProvenTrue)] CrashAbsent
  for_ [CrashAbsent,CrashUnanswered,CrashChosen "08720",CrashChosen "08714"] $ \draft ->
    assert ("settings explicit crash availability " <> display draft) $ evaluateCondition (knownState [] draft) (NotCrashVictim "08720") == (if draft == CrashChosen "08720" then ProvenFalse else ProvenTrue)
  assert "settings self-reference exception exact row and atom only" $ readsPriorState "campaign:02/step/5/2" (HasKey "TheNecronomiconWasStolen" Nothing) && not (readsPriorState "campaign:02/step/5/1" (HasKey "TheNecronomiconWasStolen" Nothing)) && not (readsPriorState "campaign:02/step/5/2" $ HasOption "TakeTheNecronomicon")
  assert "settings reviewed before-row visibility annotation retained" $ rowVisibilityEvaluation (getRow "campaign:02/step/5/2") == Just "before-row"
  let tcu = getCampaign "05"
      rowFates = [getRow $ "campaign:05/step/0/" <> display n | n <- [1..4 :: Int]]
      keys = ["WasTakenByTheWatcher","WasClaimedBySpecters","DisappearedIntoTheMist","WasPulledIntoTheSpectralRealm"]
      fateCondition i = choiceVisible $ headEx $ rowChoices $ rowFates P.!! i
      state selected = knownState
        ((RecordFact False "MissingPersons" CardRecord (String "05046"),ProvenTrue)
          : [(RecordFact False key CardRecord (String "05046"),if i `elem` selected then ProvenTrue else ProvenFalse) | (i,key) <- zip [0..] keys]) CrashAbsent
  for_ [0..3] $ \i -> do
    assert ("settings actual TCU one selected fate stays valid " <> display i) $ evaluateCondition (state [i]) (fateCondition i) == ProvenTrue
    for_ [j | j <- [0..3],j /= i] $ \j ->
      assert ("settings actual TCU conflicting simultaneous fates both invalid " <> display i <> "/" <> display j) $ evaluateCondition (state [i,j]) (fateCondition i) == ProvenFalse && evaluateCondition (state [i,j]) (fateCondition j) == ProvenFalse
  early <- right $ chaptersForStart (getCampaign "02") "02159"
  late <- right $ chaptersForStart (getCampaign "02") "02274"
  let ids chapters = map chapterIdentity chapters
  assert "settings early Dunwich context excludes future survivor options" $ "campaign:02/step/2" `elem` ids early && "campaign:02/step/6" `notElem` ids early && "campaign:02/step/6" `elem` ids late
  forced <- right $ chaptersForStart (getCampaign "02") "02041"
  assert "settings Dunwich starting Extracurricular includes alternate-first forced chapter" $ ids forced == ["campaign:02/step/1"]
  undimensioned <- right $ chaptersForStart (getCampaign "02") "02236"
  altar <- right $ chaptersForStart (getCampaign "02") "02195"
  assert "settings reviewed 02236 context includes conditional Survivors not current scenario outcomes" $ "campaign:02/step/6" `elem` ids undimensioned && "campaign:02/step/7" `notElem` ids undimensioned
  assert "settings 02274 includes Survivors and Undimensioned prior outcomes" $ all (`elem` ids late) ["campaign:02/step/6","campaign:02/step/7"]
  assert "settings 02195 cannot read future Survivors" $ "campaign:02/step/6" `notElem` ids altar
  let survivors = fromJustNote "actual survivors" $ find ((== "campaign:02/step/6") . chapterIdentity) undimensioned
  assert "settings Survivors inclusion does not remove failed-ritual visibility guard" $ evaluateCondition (knownState [(KeyFact "TheRitualWasCompleted" Nothing,ProvenTrue)] CrashAbsent) (chapterVisible survivors) == ProvenFalse
  let duplicateStart = (getCampaign "02") {campaignIdentity="unreviewed"}
  assert "settings duplicate scenario IDs need exact reviewed campaign binding" $ isLeft $ chaptersForStart duplicateStart "02236"
  assert "settings reviewed original 02236 does not authorize Return-To51041" $ isLeft $ chaptersForStart (getCampaign "02") "51041"
  assert "settings unknown start cannot fall back to all or empty chapters" $ isLeft $ chaptersForStart tcu "unknown"
  let partners = getRow "scenario:08648b/11"
      child = headEx $ rowChildren partners
      effective = fromJustNote "actual nested row" $ lookup (rowId child) [(rowId r,c) | (r,c) <- effectiveRows [] [partners]]
  assert "settings flattened partner retains parent Group restriction" $ evaluateCondition emptyPredicateState effective == ProvenFalse
  assert "settings enabled parent Group permits partner child" $ evaluateCondition (knownState [(OptionFact "IncludePartners",ProvenTrue)] CrashAbsent) effective == ProvenTrue
  pendingGroup <- right $ makePredicateState [OptionFact "IncludePartners",OptionFact "PerformIntro"] [] CrashAbsent
  assert "settings actual unanswered parent Group remains Unknown" $ evaluateCondition pendingGroup effective == Unanswered
  rawCatalog <- right $ first T.pack $ eitherDecodeStrict' @Value catalogBytes
  let change key value (Object fields) = Object $ KM.insert key value fields
      change _ _ value = value
      policyBad = change "evaluationPolicy" (object ["campaign" .= ("future-unsafe-policy" :: Text)]) rawCatalog
      rawRow ident = fromJustNote "normalized fixture row" $ find (\case Object o -> KM.lookup "id" o == Just (String ident); _ -> False) $ objectsIn rawCatalog
      originalRow = rowRawDefinition $ getRow "scenario:01120/0"
  assert "settings unknown provenance policy rejected" $ isLeft $ parseDefinition @SettingsCatalog policyBad
  let rawOriginal = case rawRow "scenario:01120/0" of Object o -> KM.lookup "definition" o; _ -> Nothing
  assert "settings actual raw definition equals exact embedded original" $ Just originalRow == rawOriginal
  let booleanRow = rawRow "scenario:01120/0"
  assert "settings row kind/effect mismatch rejected" $ isLeft $ parseDefinition @SettingRow $ change "kind" (String "Group") booleanRow
  assert "settings boolean default wrong type rejected" $ isLeft $ parseDefinition @SettingRow $ change "default" (String "false") booleanRow
  assert "settings unsolicited forced flag rejected" $ isLeft $ parseDefinition @SettingRow $ change "forced" (Bool True) booleanRow
  assert "settings prior-state annotation cannot be injected into another row" $ isLeft $ parseDefinition @SettingRow $ change "visibilityEvaluation" (String "before-row") booleanRow
  assert "settings row unknown field rejected" $ isLeft $ parseDefinition @SettingRow $ change "unreviewedField" (Bool True) booleanRow
  assert "settings recordable declaration cannot disagree with effect" $ isLeft $ parseDefinition @SettingRow $ change "recordable" (String "RecordableMemory") $ rawRow "scenario:01142/2"
  assert "settings saved extra partner statuses not editor authorization" $ isLeft $ parseDefinition @SettingRow $ change "statuses" (toJSON (["Safe","Resolute","Eliminated","Mia"] :: [Text])) $ rawRow "scenario:08648b/11/0"
  foreignEffect <- right $ parseDefinition @Effect $ object
    ["kind" .= ("key" :: Text),"key" .= ("GhoulPriestIsStillAlive" :: Text),"conditionKey" .= ("GhoulPriestIsStillAlive" :: Text)]
  let actualDunwichRow = getRow "campaign:02/step/4/3"
      wrongScopeRow = actualDunwichRow {rowEffect=foreignEffect}
  assert "settings actual normalized Dunwich key has reviewed typed scope" $ isRight $ Aeson.parseEither (const $ validateRowScope "TheDunwichLegacyKey" actualDunwichRow) ()
  assert "settings parseable foreign-campaign key rejected by row scope" $ isLeft $ Aeson.parseEither (const $ validateRowScope "TheDunwichLegacyKey" wrongScopeRow) ()
  optionEffect <- right $ parseDefinition @Effect $ object
    ["kind" .= ("option-toggle" :: Text),"conditionKey" .= ("TakeZebulonWhateley" :: Text),"wireOption" .= ("TakeZebulonWhately" :: Text)]
  assert "settings metadata string converts through actual tagged CampaignOption parser" $ case optionEffect of
    OptionEffect condition (Just option) -> condition == "TakeZebulonWhateley" && toJSON option == object ["tag" .= ("TakeZebulonWhately" :: Text)]
    _ -> False
  assert "settings catalog option cannot inject parameterized CampaignVariant" $ isLeft $ Aeson.parseEither (const $ parseOptionalWireOption $ Just $ String "CampaignVariant") ()
  assert "settings catalog option cannot supply arbitrary tagged payload" $ isLeft $ Aeson.parseEither (const $ parseOptionalWireOption $ Just $ object ["tag" .= ("CampaignVariant" :: Text),"contents" .= ("unreviewed" :: Text)]) ()
  putStrLn $ "SETTINGS_TYPED_CATALOG " <> decodeUtf8 (BL.toStrict $ encode $ catalogSummary catalog)
