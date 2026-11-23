-- Context-bound settings interpreter. No Answer/Session import and no raw log
-- or Message supplied by the caller. A complete form is compiled exactly once.
module Arkham.Native.Settings.Engine
  ( ResolvedSchema, resolveSchema, resolveHistoricalSchema, schemaIdentity, schemaView
  , previewForm, compileForm, compileFormWithForced, compileSubmission, freshCampaignStartError
  ) where

import Arkham.Prelude
import Arkham.CampaignLog
import Arkham.Campaign.Option (CampaignOption (..))
import Arkham.CampaignLogKey
import Arkham.CampaignStep qualified as CS
import Arkham.Campaigns.EdgeOfTheEarth.Key (EdgeOfTheEarthKey (WasKilledInThePlaneCrash))
import Arkham.Campaigns.EdgeOfTheEarth.Partner (toResolute)
import Arkham.Campaigns.TheForgottenAge.Meta qualified as TFA
import Arkham.Card.CardCode (CardCode, unCardCode)
import Arkham.Classes.Entity (toAttrs)
import Arkham.ChaosBag.Base (emptyChaosBag)
import Arkham.Scenario.Types (scenarioChaosBag)
import Arkham.Scenario.Scenarios.TheForgottenAge.HeartOfTheElders qualified as Heart
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Message (Message (SetCampaignLog,SetCampaignMeta,HandleOption))
import Arkham.Question
import Arkham.Native.Settings.Types
import Arkham.Native.Settings.Definition
import Arkham.Native.Settings.Catalog
import Arkham.Native.Settings.Predicate
import Arkham.Native.SourceIdentity qualified as Source
import Crypto.Hash.SHA256 qualified as SHA
import Data.ByteString.Base16 qualified as B16
import Data.ByteString.Lazy qualified as BL
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (parseEither)
import Data.Map.Strict qualified as Map

data Section = Section (Maybe Text) Condition [(SettingRow,Condition)]
data ResolvedSchema = ResolvedSchema
  { schemaIdentity :: Text
  , schemaContext :: Value
  , schemaLocale :: Maybe Text
  , schemaStandalone :: Bool
  , schemaSections :: [Section]
  }

-- Bind the actual UI numeric policy, not just raw metadata min=null. These
-- two immutable source hashes are recorded in the schema compatibility ID.
policyIdentity :: Value
policyIdentity = object
  [ "interpreter" .= ("grounded-chapter-settings-v1" :: Text)
  , "numericMinimumWhenOmitted" .= (0 :: Int)
  , "scenarioNumericSource" .= ("6862cd4c8352a807c2cb3cd48c1d638424a37a47b127bbb16e16b2ba0f8fe23c" :: Text)
  , "campaignNumericSource" .= ("7b82a10296604f9c102635f982f052326d697e1e032bf088b4efe653e7be4129" :: Text)
  , "campaignCompletionSource" .= ("a589f5ef366f23da780c655dd5f152d5ad3a124a958538b3031a1d057e291a33" :: Text)
  , "optionalRecordChoice" .= ("campaign:05/step/1/3" :: Text)
  , "exactOmittedStandalonePolicy" .= ("original-empty-settings-that-only-v1" :: Text)
  , "heartPart2StandalonePolicy" .= ("53048-empty-fresh-that-standalone-setup-bag-v2" :: Text)
  , "heartPart2StandaloneTokens" .= Heart.standaloneChaosTokens
  , "originalScenarioSettingsSource" .= ("40981abd75d4c476936b1e1f2038debf974effe31bfb997f9af06cc7cd818513" :: Text)
  , "reviewedStandaloneAliases" .= reviewedStandaloneAliases
  , "reviewedEmptyStandaloneAliases" .= reviewedEmptyStandaloneAliases
  , "reviewedNativeStandaloneSetups" .=
      [object ["target" .= target,"base" .= base,"definitionPresence" .= presenceName presence,"family" .= family]
      | (target,(base,presence,family)) <- reviewedNativeSetupAliases]
  , "reviewedCampaignAliases" .= reviewedCampaignAliases
  , "campaignAliasPolicy" .= ("reviewed-return-to-dunwich-starts-v1" :: Text)
  , "reviewedEmptyCampaignStarts" .= reviewedEmptyCampaignStarts
  , "reviewedTcuCampaignStarts" .= reviewedTcuCampaignStarts
  , "reviewedOmittedCampaignStarts" .= reviewedOmittedCampaignStarts
  , "reviewedFreshSpecialCampaign53Starts" .= reviewedFreshSpecialCampaign53Starts
  , "pendingInheritedCampaign52Starts" .= pendingInheritedCampaign52Starts
  , "fresh53MetadataPolicy" .= ("materialize-original-mempty-only-when-null-v1" :: Text)
  , "fresh53OriginalEmptyMetadata" .= (mempty :: TFA.Metadata)
  , "oneShotSubmissionPolicy" .= ("compiled-campaign-options-only-in-explicit-list-order-v1" :: Text)
  , "oneShotCampaignOptions" .= oneShotCampaignOptions
  ]

-- Explicitly reviewed wrappers delegate every non-Setup message to these
-- definitions. They remain distinct scenario/schema identities; no fallback
-- for other pending aliases is authorized. Exact omitted definitions use the
-- separate original empty-settings policy below, never alias inheritance.
reviewedStandaloneAliases :: [(Text,Text)]
reviewedStandaloneAliases = [("50025","01120"),("50032","01142"),("54046","05238"),("54056","05325")]

-- These individual Setup/StandaloneSetup paths have been reviewed as needing
-- no prior-log input in a standalone game. SourceOmitted alone NEVER grants
-- authority. In particular 51012's completed-51015 branch is not empty in an
-- actual campaign, so these entries must not resolve in a These context.
reviewedEmptyStandaloneAliases :: [(Text,Text)]
reviewedEmptyStandaloneAliases = [("50011","01104"),("51015","02062"),("51012","02041")]

-- Each exact wrapper delegates StandaloneSetup to the reviewed original
-- initializer. An empty FRONT form neither invents history nor replaces the
-- native choices/bag/log initialization which happen later. Presence is part
-- of this manifest: 05284 is Declared-empty, unlike the other 28 omissions.
-- 53048/53066 are deliberately not authorized by mere absence of form rows.
presenceName :: DefinitionPresence -> Text
presenceName Declared = "declared"
presenceName SourceOmitted = "source-omitted"

reviewedNativeSetupAliases :: [(Text,(Text,DefinitionPresence,Text))]
reviewedNativeSetupAliases =
  [("51020",("02118",SourceOmitted,"fixed-bag")),("51025",("02159",SourceOmitted,"fixed-bag"))
  ,("51032",("02195",SourceOmitted,"fixed-bag")),("51041",("02236",SourceOmitted,"fixed-log"))
  ,("51047",("02274",SourceOmitted,"fixed-log")),("51053",("02311",SourceOmitted,"fixed-bag"))
  ,("52014",("03043",SourceOmitted,"fixed-bag")),("52021",("03061",SourceOmitted,"random-bag"))
  ,("52028",("03120",SourceOmitted,"random-bag")),("52034",("03159",SourceOmitted,"random-bag"))
  ,("52040",("03200",SourceOmitted,"conviction-doubt")),("52048",("03240",SourceOmitted,"fixed-log"))
  ,("52054",("03274",SourceOmitted,"random-bag")),("52059",("03316",SourceOmitted,"conviction-doubt-neither"))
  ,("53016",("04043",SourceOmitted,"fixed-bag")),("53017",("04054",SourceOmitted,"fixed-log"))
  ,("53028",("04113",SourceOmitted,"relic-custody")),("53038",("04161",SourceOmitted,"fixed-bag"))
  ,("53045",("04205a",SourceOmitted,"paths")),("53053",("04237",SourceOmitted,"fixed-bag"))
  ,("53059",("04277",SourceOmitted,"fury")),("53061",("04314",SourceOmitted,"faction"))
  ,("54016",("05043",SourceOmitted,"prologue")),("54017",("05050",SourceOmitted,"fixed-bag"))
  ,("54024",("05065",SourceOmitted,"fixed-log")),("54029",("05120",SourceOmitted,"fixed-bag"))
  ,("54034",("05161",SourceOmitted,"fixed-bag")),("54042",("05197",SourceOmitted,"membership"))
  ,("54049",("05284",Declared,"finale-enemy"))]

reviewedCampaignAliases :: [(Text,Text)]
reviewedCampaignAliases =
  [("51012","02041"),("51015","02062"),("51020","02118"),("51025","02159")
  ,("51032","02195"),("51041","02236"),("51047","02274"),("51053","02311")]

-- Campaign50 delegates to NOTZ rules but retains its own step IDs. Absence of
-- metadata is not generic authority: only these reviewed exact starts may use
-- the currently source-omitted, chapterless01 form. No history is synthesized.
reviewedEmptyCampaignStarts :: [(Text,Text)]
reviewedEmptyCampaignStarts = [("50011","01104"),("50025","01120"),("50032","01142")]

-- 54016 is a special prologue creation route, NOT an offered campaign form.
-- The original05 chapter definitions remain05 for conditional interludes.
reviewedTcuCampaignStarts :: [(Text,Text)]
reviewedTcuCampaignStarts =
  [("54017","05050"),("54024","05065"),("54029","05120"),("54034","05161")
  ,("54042","05197"),("54046","05238"),("54049","05284"),("54056","05325")]

-- These exact aliases have no front-end historical form in the fixed source.
-- Empty INPUT means a new campaign at its supplied start, not reconstructed
-- prior play, supplies, story cards, XP or completed scenarios.
reviewedOmittedCampaignStarts :: [(Text,(Text,[(Text,Text)]))]
reviewedOmittedCampaignStarts =
  [("52",("03",[("52014","03043")]))
  ,("53",("04",[("53016","04043"),("53017","04054"),("53028","04113"),("53038","04161")
    ,("53045","04205a"),("53053","04237"),("53059","04277"),("53061","04314")]))]

-- Separately scoped fresh CAMPAIGN53 diagnostics/advanced entries. Their bag
-- comes from the real campaign constructor; this grants NO standalone setup
-- defaults and does not pretend to have inherited Part1 or an unlocked ending.
reviewedFreshSpecialCampaign53Starts :: [(Text,Text)]
reviewedFreshSpecialCampaign53Starts = [("53048","04205b"),("53066","04344")]

-- Temporary product gate for fresh EMPTY-history creation, not a rule that
-- prohibits these scenarios. A complete mid-campaign history/custody editor
-- remains pending. Normal continuation/import must never use this gate.
-- PallidMask/DimCarcosa require the unique Stranger actually earned in Curtain
-- Call. Do not silently synthesize that card or run StandaloneSetup in These.
pendingInheritedCampaign52Starts :: [Text]
pendingInheritedCampaign52Starts = ["52021","52028","52034","52040","52048","52054","52059"]

freshCampaignStartError :: Maybe CampaignId -> Maybe ScenarioId -> Maybe Text
freshCampaignStartError (Just (CampaignId "52")) (Just sid)
  | unCardCode (unScenarioId sid) `elem` pendingInheritedCampaign52Starts =
      Just "Campaign52 intermediate starts do not yet support empty-history creation; choose a standalone adventure or continue an existing campaign save"
freshCampaignStartError _ _ = Nothing

-- Execution order is explicit and identity-bound. Only options from THIS
-- compiled submission are dispatched, never the merged existing campaign log.
-- Passive/unknown options are not silently filtered into arbitrary handlers.
oneShotCampaignOptions :: [(Text,[CampaignOption])]
oneShotCampaignOptions = [("02",dunwich),("51",dunwich),("05",tcu),("54",tcu)]
 where
  tcu = [TakeBlackBook,TakePuzzleBox,ProceedToInterlude3]
  dunwich = [Cheated,TakeArmitage,TakeWarrenRice,TakeFrancisMorgan,TakeZebulonWhately
    ,TakeEarlSawyer,TakePowderOfIbnGhazi,TakeTheNecronomicon,AddAcrossSpaceAndTime]

unwrap :: Question Message -> Question Message
unwrap = \case
  QuestionLabel _ _ q -> unwrap q
  PayCostQuestion _ q -> unwrap q
  QuestionWithSource _ _ q -> unwrap q
  q -> q

resolveSchema :: Game -> PlayerId -> Either Text ResolvedSchema
resolveSchema=resolveSchemaWithHistory False

-- Only the historical bootstrap resolver may use this projection. Its schema
-- identity is distinct even for starts which already offer an ordinary form;
-- accepting it is not permission to bypass the ordinary fresh-start gate.
resolveHistoricalSchema :: Game -> PlayerId -> Either Text ResolvedSchema
resolveHistoricalSchema=resolveSchemaWithHistory True

reviewedHistoricalCarcosaStarts :: [(Text,Text)]
reviewedHistoricalCarcosaStarts=
  [("52014","03043"),("52021","03061"),("52028","03120"),("52034","03159")
  ,("52040","03200"),("52048","03240"),("52054","03274"),("52059","03316")]

resolveSchemaWithHistory :: Bool -> Game -> PlayerId -> Either Text ResolvedSchema
resolveSchemaWithHistory historical game player = do
  unless (player `elem` gamePlayers game) $ Left "Unknown settings player"
  catalog <- catalogResult
  case unwrap <$> Map.lookup player (gameQuestion game) of
    Just PickScenarioSettings -> case gameMode game of
      That s -> standalone catalog $ unCardCode $ unScenarioId s.id
      These _ s -> standalone catalog $ unCardCode $ unScenarioId s.id
      _ -> Left "No scenario settings context"
    Just PickCampaignSettings -> case gameMode game of
      This c -> campaign catalog (toJSON c.id) c.step
      These c _ -> campaign catalog (toJSON c.id) c.step
      _ -> Left "No campaign settings context"
    _ -> Left "No settings form is offered to this player"
 where
  build catalog standaloneMode context locale sections =
    let digest = decodeUtf8 $ B16.encode $ SHA.hash $ BL.toStrict $ encode
          (Source.coreSourceId,catalogIdentity catalog,policyIdentity,context,historical)
     in Right $ ResolvedSchema ("arkham-settings-form:" <> digest) context locale standaloneMode sections
  standalone catalog ident = case lookupScenario catalog ident of
    ExactDefinition s | scenarioPresence s == Declared || ident == "01104" ->
      build catalog True (object ["mode" .= ("standalone" :: Text),"scenario" .= ident]) (scenarioLocale s)
        [Section Nothing Always [pair] | pair <- effectiveRows [] $ scenarioRows s]
    -- Original ScenarioSettings.vue resolves an omitted settings property to
    -- [] and submits that array. Preserve that protocol, not a per-scenario
    -- rule audit. It authorizes no arbitrary log/history or registry fallback:
    -- the exact source-bound metadata entry must exist and contain no rows.
    ExactDefinition s | scenarioPresence s == SourceOmitted && null (scenarioRows s) ->
      case gameMode game of
        That _ ->
          build catalog True (object ["mode" .= ("standalone" :: Text),"scenario" .= ident
            ,"definitionPresence" .= presenceName SourceOmitted
            ,"settingsPolicy" .= ("original-empty-settings-that-only-v1" :: Text)]) (scenarioLocale s) []
        _ -> Left "Omitted scenario settings require an exact standalone context"
    AliasPending alias | ident == "53048" && aliasBase alias == "04205b" ->
      case (gameMode game,lookupScenario catalog "04205b") of
        (That s,ExactDefinition definition)
          | scenarioPresence definition == SourceOmitted && null (scenarioRows definition)
          , gameInSetup game
          , scenarioChaosBag (toAttrs s) == emptyChaosBag ->
            build catalog True (object
              ["mode" .= ("standalone" :: Text),"scenario" .= ident
              ,"definitionScenario" .= ("04205b" :: Text)
              ,"initializationPolicy" .= ("53048-empty-fresh-that-standalone-setup-bag-v2" :: Text)])
              (scenarioLocale definition) []
        _ -> Left "Heart Part2 initialization requires fresh standalone settings and an entirely empty bag"
    AliasPending alias | lookup ident reviewedStandaloneAliases == Just (aliasBase alias) ->
      case lookupScenario catalog (aliasBase alias) of
        ExactDefinition s | scenarioPresence s == Declared && not (null $ scenarioRows s) ->
          build catalog True (object ["mode" .= ("standalone" :: Text),"scenario" .= ident
            ,"definitionScenario" .= aliasBase alias,"aliasPolicy" .= ("reviewed-wrapper-nonsetup-delegation-v1" :: Text)])
            (scenarioLocale s) [Section Nothing Always [pair] | pair <- effectiveRows [] $ scenarioRows s]
        _ -> Left "Reviewed scenario alias has no exact nonempty definition"
    AliasPending alias | lookup ident reviewedEmptyStandaloneAliases == Just (aliasBase alias) ->
      case (gameMode game,lookupScenario catalog $ aliasBase alias) of
        (That _,ExactDefinition s) | scenarioPresence s == SourceOmitted && null (scenarioRows s) ->
          build catalog True (object ["mode" .= ("standalone" :: Text),"scenario" .= ident
            ,"definitionScenario" .= aliasBase alias,"aliasPolicy" .= ("reviewed-empty-standalone-v1" :: Text)])
            (scenarioLocale s) []
        _ -> Left "Reviewed empty standalone alias requires exact standalone context and empty source definition"
    AliasPending alias | Just (base,presence,family) <- lookup ident reviewedNativeSetupAliases
                       , base == aliasBase alias ->
      case (gameMode game,lookupScenario catalog base) of
        (That _,ExactDefinition s) | scenarioPresence s == presence && null (scenarioRows s) ->
          build catalog True (object ["mode" .= ("standalone" :: Text),"scenario" .= ident
            ,"definitionScenario" .= base,"definitionPresence" .= presenceName presence,"nativeSetupFamily" .= family
            ,"aliasPolicy" .= ("reviewed-native-standalone-initialization-v1" :: Text)]) (scenarioLocale s) []
        _ -> Left "Reviewed native setup requires exact standalone mode, presence and empty front form"
    _ -> Left "Scenario settings definition is not reviewed"
  campaign catalog cid step = do
    ident <- parseDefinition @Text cid
    start <- case step of
      CS.ScenarioStep sid -> Right $ unCardCode $ unScenarioId sid
      _ -> Left "Campaign settings starting step is not reviewed"
    case lookupCampaign catalog ident of
      ExactDefinition c | campaignPresence c == Declared -> do
        chapters <- chaptersForStart c start
        build catalog False (object ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start]) Nothing
          [Section (Just $ chapterScope ch) (chapterVisible ch) (effectiveRows [] $ chapterRows ch) | ch <- chapters]
      ExactDefinition _ | ident == "01" && start `elem` ["01104","01120","01142"] ->
        build catalog False (object ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start]) Nothing []
      AliasPending alias | ident == "50" && aliasBase alias == "01" -> do
        definitionStart <- maybe (Left "Return-To NOTZ starting step is not reviewed") Right $ lookup start reviewedEmptyCampaignStarts
        case lookupCampaign catalog "01" of
          ExactDefinition c | campaignPresence c == SourceOmitted && null (campaignChapters c) ->
            build catalog False (object ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start
              ,"definitionCampaign" .= ("01" :: Text),"definitionStart" .= definitionStart
              ,"aliasPolicy" .= ("reviewed-empty-return-to-notz-starts-v1" :: Text)]) Nothing []
          _ -> Left "Reviewed Return-To NOTZ requires exact omitted empty base definition"
      AliasPending alias | ident == "51" && aliasBase alias == "02" -> do
        definitionStart <- maybe (Left "Return-To campaign starting step is not reviewed") Right $ lookup start reviewedCampaignAliases
        case lookupCampaign catalog "02" of
          ExactDefinition c | campaignPresence c == Declared -> do
            -- Keep the definition identity02 for the duplicate02236 interlude
            -- selection rule. Actual51/start/completedSteps are NEVER rewritten.
            chapters <- chaptersForStart c definitionStart
            build catalog False (object ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start
              ,"definitionCampaign" .= ("02" :: Text),"definitionStart" .= definitionStart
              ,"aliasPolicy" .= ("reviewed-return-to-dunwich-starts-v1" :: Text)]) Nothing
              [Section (Just $ chapterScope ch) (chapterVisible ch) (effectiveRows [] $ chapterRows ch) | ch <- chapters]
          _ -> Left "Reviewed campaign alias has no exact declared definition"
      AliasPending alias | ident == "54" && aliasBase alias == "05" -> do
        definitionStart <- maybe (Left "Return-To TCU starting step is not reviewed") Right $ lookup start reviewedTcuCampaignStarts
        case lookupCampaign catalog "05" of
          ExactDefinition c | campaignPresence c == Declared -> do
            chapters <- chaptersForStart c definitionStart
            build catalog False (object ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start
              ,"definitionCampaign" .= ("05" :: Text),"definitionStart" .= definitionStart
              ,"aliasPolicy" .= ("reviewed-return-to-tcu-starts-v1" :: Text)]) Nothing
              [Section (Just $ chapterScope ch) (chapterVisible ch) (effectiveRows [] $ chapterRows ch) | ch <- chapters]
          _ -> Left "Reviewed TCU alias has no exact declared definition"
      AliasPending alias | Just (base,starts) <- lookup ident reviewedOmittedCampaignStarts
                         , aliasBase alias == base -> do
        let special = if ident == "53" then lookup start reviewedFreshSpecialCampaign53Starts else Nothing
        definitionStart <- maybe (Left "Omitted campaign starting step is not reviewed") Right $
          lookup start starts <|> special <|> (if historical && ident=="52" then lookup start reviewedHistoricalCarcosaStarts else Nothing)
        let actualMeta = case gameMode game of This c -> c.meta; These c _ -> c.meta; _ -> Null
            missingMeta = ident == "53" && actualMeta == Null
        when (ident == "53" && not missingMeta) $
          void $ parseDefinition @TFA.Metadata actualMeta
        case lookupCampaign catalog base of
          ExactDefinition c | campaignPresence c == SourceOmitted && null (campaignChapters c) ->
            build catalog False (object $
              ["mode" .= ("campaign" :: Text),"campaign" .= ident,"start" .= start
              ,"definitionCampaign" .= base,"definitionStart" .= definitionStart
              ,"aliasPolicy" .= ("reviewed-source-omitted-campaign-starts-v1" :: Text)
              ,"entryPolicy" .= (if isJust special then "fresh-campaign-defaults-no-inherited-history" else "fresh-campaign-input-only" :: Text)]
              <> (if ident == "53" then ["metadataPolicy" .= ("materialize-original-mempty-only-when-null-v1" :: Text),"initializeMissingMetadata" .= missingMeta] else [])
              <> ["standaloneSupported" .= False | isJust special]) Nothing []
          _ -> Left "Reviewed omitted campaign requires exact chapterless source definition"
      _ -> Left "Campaign settings definition is not reviewed"

schemaRows :: ResolvedSchema -> [SettingRow]
schemaRows schema = [r | Section _ _ rows <- schemaSections schema,(r,_) <- rows]

schemaView :: ResolvedSchema -> Value
schemaView schema = object
  [ "schemaVersion" .= (2 :: Int),"schemaId" .= schemaIdentity schema
  , "context" .= schemaContext schema,"i18n" .= schemaLocale schema
  , "answerTag" .= ("SettingsFormAnswer" :: Text),"supported" .= True
  , "policy" .= policyIdentity
  ]

editable :: SettingRow -> Bool
editable row = rowKind row `notElem` [Group,ForceKey,ForceRecorded]

-- Preserve the original radio's legal initial "no selection". Raw reviewed
-- r6 metadata is provenance, not authority to add a mandatory reward choice.
requiredValue :: SettingRow -> Bool
requiredValue row = rowRequired row && not
  (rowId row == "campaign:05/step/1/3" && rowKind row == ChooseRecordable)

-- Validate even inactive raw values. ID and enum errors are deliberately
-- constant; errors never echo an arbitrary client payload or unknown ID.
checkShape :: SettingRow -> FormValue -> Either Text ()
checkShape row value = unless valid $ Left "Settings value does not match its offered definition"
 where
  valid = case (rowKind row,value) of
    (kind,FormBoolean _) -> kind `elem` [ToggleKey,ToggleOption,SetKey,Option,SetRecordable]
    (ChooseNum,FormInteger n) -> n >= fromMaybe 0 (rowMinimum row) && maybe True (n <=) (rowMaximum row)
    (kind,FormChoice selected) | kind `elem` [PickKey,ChooseKey,ChooseOption,ChooseRecord,ChooseRecordable,SetPartnerKilled] ->
      maybe True (`elem` map choiceId (rowChoices row)) selected
    (kind,FormEntries entries) | kind `elem` [ToggleCrossedOut,ToggleRecords,Record,CrossOut,ChooseRecordables] ->
      all (`elem` map choiceId (rowChoices row)) $ Map.keys entries
    (SetPartnerDetails,FormPartner d h status) -> d >= 0 && h >= 0
      && maybe False (d <=) (rowMaxDamage row) && maybe False (h <=) (rowMaxHorror row)
      && status `elem` [Safe,Resolute,Eliminated]
    _ -> False

validateRequest :: ResolvedSchema -> SettingsFormRequest -> Either Text ()
validateRequest schema request = do
  unless (formSchemaId request == schemaIdentity schema) $ Left "Stale or foreign settings schema"
  when (Map.size (valuesByRowId request) > 512) $ Left "Too many settings rows"
  for_ (Map.toList $ valuesByRowId request) $ \(ident,value) -> case find ((== ident) . rowId) $ schemaRows schema of
    Just row | editable row -> checkShape row value
    _ -> Left "Unknown or non-editable settings row"

type Facts = [(Fact,Truth)]
data RowResult = RowResult
  { resultRow :: SettingRow
  , resultGate :: Truth
  , resultEntries :: [(SettingChoice,Truth)]
  , resultValue :: Maybe FormValue
  , resultForced :: Bool
  , resultFacts :: Facts
  , resultCrash :: CrashDraft
  , resultErrors :: [Text]
  }

truthBool :: Bool -> Truth
truthBool True = ProvenTrue
truthBool False = ProvenFalse

-- Multiple Boolean/record contributors OR, but counts need a single effective
-- producer before entering PredicateState. Even equal competing counts fail.
stateFrom :: Facts -> CrashDraft -> Either Text PredicateState
stateFrom facts crash = do
  let countKeys = nub [key | (CountFact key _,_) <- facts]
      other = [(f,t) | (f,t) <- facts,case f of CountFact {} -> False; _ -> True]
  counts <- for countKeys $ \key -> do
    let matches = [(f,t) | (f@(CountFact actual _),t) <- facts,actual == key]
        active = filter ((/= ProvenFalse) . snd) matches
    case active of
      [] -> Right (CountFact key Nothing,ProvenFalse)
      [item] -> Right item
      _ -> Left "Conflicting settings count producers"
  makePredicateState (map fst facts) (other <> counts) crash

mergeCrash :: [CrashDraft] -> Either Text CrashDraft
mergeCrash drafts = case [v | CrashChosen v <- drafts] of
  [] -> Right $ if CrashUnanswered `elem` drafts then CrashUnanswered else CrashAbsent
  [v] | CrashUnanswered `notElem` drafts -> Right $ CrashChosen v
  _ -> Left "Conflicting settings crash victims"

-- Prior-state semantics are atom-specific, not an excuse to drop all current
-- chapter facts. Earlier unresolved rows stay in the prior fact domain.
evalFor :: SettingRow -> PredicateState -> PredicateState -> Condition -> Truth
evalFor row current before = go
 where
  go condition | readsPriorState (rowId row) condition = evaluateCondition before condition
  go (AllOf children) = allTruth $ map go children
  go (AnyOf children) = anyTruth $ map go children
  go (NoneOf children) = negateTruth $ anyTruth $ map go children
  go (Not child) = negateTruth $ go child
  go condition = evaluateCondition current condition

choiceOf :: SettingRow -> Maybe FormValue -> Maybe SettingChoice
choiceOf row (Just (FormChoice (Just selected))) = find ((== selected) . choiceId) $ rowChoices row
choiceOf _ _ = Nothing

-- Force choice IDs come only from the definition. No first-choice fallback;
-- unknown forced alternatives keep the row unanswered, not user-authorized.
effectiveValue :: SettingRow -> (Condition -> Truth) -> Maybe FormValue -> (Maybe FormValue,Bool,[Text])
effectiveValue row evaluate supplied =
  let forced = [(c,evaluate cond) | c <- rowChoices row,Just cond <- [choiceForced c]]
      trueChoices = [c | (c,ProvenTrue) <- forced]
   in case trueChoices of
        [c] | all ((/= Unanswered) . snd) forced ->
          let value = FormChoice $ Just $ choiceId c
           in (Just value,True,["Submitted choice conflicts with forced choice" | maybe False (/= value) supplied])
        [] | any ((== Unanswered) . snd) forced -> (Nothing,True,[])
        [] -> (supplied,False,[])
        _ -> (Nothing,True,["Conflicting forced settings choices"])

baseFacts :: Maybe Text -> SettingRow -> Maybe FormValue -> Map Text FormValue -> (Condition -> Truth) -> Facts
baseFacts scope row value values evaluateConditionHere = [(fact,base fact) | fact <- definitionFactDomain scope row]
 where
  selected = choiceOf row value
  pick matches = case value of
    Nothing -> Unanswered
    Just (FormChoice Nothing) | requiredValue row -> Unanswered
    _ -> truthBool $ maybe False matches selected
  flag = case value of Just (FormBoolean b) -> truthBool b; _ -> Unanswered
  base = \case
    KeyFact key _ -> case rowEffect row of
      KeyEffect {} | rowKind row == ForceKey -> ProvenTrue
      KeyEffect {} -> flag
      _ -> pick $ (== String key) . choiceValue
    OptionFact key -> case rowEffect row of
      OptionEffect {} -> flag
      _ -> pick $ (== Just key) . choiceConditionKey
    CountFact {} -> case value of Just (FormInteger _) -> ProvenTrue; _ -> Unanswered
    RecordFact crossed key _ actual -> case rowEffect row of
      SelectedRecordTarget {} -> if crossed then ProvenFalse else pick $ (== String key) . choiceValue
      RecordEffect _ _ _ fixed -> case fixed of
        Just _ | rowKind row == ForceRecorded -> truthBool $ not crossed
        Just _ -> if crossed then ProvenFalse else flag
        Nothing | rowKind row == ChooseRecordable -> if crossed then ProvenFalse else pick $ (== actual) . choiceValue
        Nothing -> case find ((== actual) . choiceValue) $ rowChoices row of
          Nothing -> ProvenFalse
          Just choice -> case value of
            Just (FormEntries entries) -> case Map.lookup (choiceId choice) entries of
              Just b | rowKind row `elem` [CrossOut,ToggleCrossedOut] -> truthBool $ crossed == b
              Just b -> truthBool $ b && not crossed
              Nothing | evaluateConditionHere (HasRecord False "MissingPersons" (Just CardRecord) actual) == ProvenTrue
                && tcuExcluded row choice values -> ProvenFalse
              Nothing -> Unanswered
            _ -> Unanswered
      _ -> ProvenFalse

factEntry :: SettingRow -> Fact -> Maybe SettingChoice
factEntry row = \case
  KeyFact key _ | rowKind row `elem` [PickKey,ChooseKey] -> byValue $ String key
  OptionFact key | rowKind row == ChooseOption -> find ((== Just key) . choiceConditionKey) $ rowChoices row
  RecordFact _ key _ value -> case rowEffect row of
    SelectedRecordTarget {} -> byValue $ String key
    RecordEffect _ _ _ Nothing -> byValue value
    _ -> Nothing
  _ -> Nothing
 where
  byValue value = find ((== value) . choiceValue) $ rowChoices row

evaluateRow :: Maybe Text -> Truth -> PredicateState -> PredicateState -> Map Text FormValue -> (SettingRow,Condition) -> RowResult
evaluateRow scope sectionGate current before values (row,condition) =
  let evaluate = evalFor row current before
      gate = allTruth [sectionGate,evaluate condition]
      entries = [(c,allTruth [gate,evaluate $ choiceVisible c]) | c <- rowChoices row]
      (value,forced,errors) = effectiveValue row evaluate $ Map.lookup (rowId row) values
      entryGate fact = maybe gate (\choice -> fromMaybe Unanswered $ lookup (choiceId choice) [(choiceId c,t) | (c,t) <- entries]) $ factEntry row fact
      facts = [ (case (f,value) of (CountFact key _,Just (FormInteger n)) -> CountFact key (Just n); _ -> f
                ,allTruth [entryGate f,truth]) | (f,truth) <- baseFacts scope row value values evaluate]
      crash = case rowEffect row of
        CrashEffect | gate == ProvenFalse -> CrashAbsent
        CrashEffect | gate == ProvenTrue -> case choiceOf row value of
          Just choice | String code <- choiceValue choice -> CrashChosen code
          _ -> CrashUnanswered
        CrashEffect -> CrashUnanswered
        _ -> CrashAbsent
   in RowResult row gate entries value forced facts crash errors

-- The only reviewed simultaneous exclusion group. Do not turn arbitrary
-- circular editable predicates into a self-authorizing candidate fixed point.
exclusionErrors :: [(SettingRow,Condition)] -> Map Text FormValue -> [Text]
exclusionErrors rows values =
  ["Conflicting Circle Undone missing-person fates" | any ((> (1 :: Int)) . length) groups]
 where
  ids = ["campaign:05/step/0/1","campaign:05/step/0/2","campaign:05/step/0/3","campaign:05/step/0/4"]
  selected = [choiceValue c | (r,_) <- rows,rowId r `elem` ids
    ,Just (FormEntries entries) <- [Map.lookup (rowId r) values],c <- rowChoices r,Map.lookup (choiceId c) entries == Just True]
  groups = [[x | x <- selected,x == v] | v <- nub selected]

-- Reviewed TCU constraints, not global "missing means unchecked". A submitted
-- unique fate rules out the OTHER fate producers for that physical person.
-- This permits final canonical omission of those inactive entries without
-- losing the negative facts needed to prove the selected fate. Missing entries
-- for unrelated people remain Unknown and must still be answered when visible.
tcuExcluded :: SettingRow -> SettingChoice -> Map Text FormValue -> Bool
tcuExcluded row choice values = rowId row `elem` fateRows && any selectedOther fateRows
 where
  fateRows = ["campaign:05/step/0/1","campaign:05/step/0/2","campaign:05/step/0/3","campaign:05/step/0/4"]
  reviewed = [(String "05046","0"),(String "05047","1"),(String "05048","2"),(String "05049","3")]
  selectedOther ident = ident /= rowId row && case (lookup (choiceValue choice) reviewed,Map.lookup ident values) of
    (Just suffix,Just (FormEntries entries)) -> Map.lookup (ident <> "/entry/" <> suffix) entries == Just True
    _ -> False

solveSection :: Facts -> CrashDraft -> Map Text FormValue -> Section -> Either Text [RowResult]
solveSection prior priorCrash values (Section scope visible rows) = do
  priorState <- stateFrom prior priorCrash
  let gate = evaluateCondition priorState visible
      initial = [RowResult r Unanswered [] Nothing False
        [(f,Unanswered) | f <- definitionFactDomain scope r] CrashAbsent [] | (r,_) <- rows]
      limit = 2 + sum [length (definitionFactDomain scope r) + length (rowChoices r) + 1 | (r,_) <- rows]
      loop remaining old = do
        crash <- mergeCrash $ priorCrash : map resultCrash old
        current <- stateFrom (prior <> concatMap resultFacts old) crash
        next <- for (zip [(0 :: Int)..] rows) $ \(index,pair) -> do
          earlierCrash <- mergeCrash $ priorCrash : map resultCrash (take index old)
          before <- stateFrom (prior <> concatMap resultFacts (take index old)) earlierCrash
          pure $ evaluateRow scope gate current before values pair
        let previous = concatMap resultFacts old
            updated = concatMap resultFacts next
            changes a b = a /= Unanswered && a /= b
            reversals = or (zipWith (\(_,a) (_,b) -> changes a b) previous updated)
              || or (zipWith (\a b -> changes (resultGate a) (resultGate b)) old next)
              || or (concat $ zipWith (\a b -> zipWith (\(_,x) (_,y) -> changes x y) (resultEntries a) (resultEntries b)) old next)
            stable = previous == updated && map resultCrash old == map resultCrash next
              && map resultValue old == map resultValue next
        when reversals $ Left "Unreviewed non-monotone settings dependency"
        if stable then pure next else if remaining <= (0 :: Int)
          then Left "Settings dependency did not resolve" else loop (remaining-1) next
  results <- loop limit initial
  pure $ case exclusionErrors rows values of
    [] -> results
    errors -> [r {resultErrors = resultErrors r <> errors} | r <- results]

runForm :: Bool -> ResolvedSchema -> SettingsFormRequest -> Either Text [RowResult]
runForm defaults schema request = do
  validateRequest schema request
  initial <- if defaults then do
    pairs <- fmap catMaybes $ for (schemaRows schema) $ \row -> do
      value <- first (const "Invalid settings definition default") $ parseEither (const $ defaultFormValue row) ()
      pure $ (rowId row,) <$> value
    -- Canonical pruning removes inactive entries, not their reviewed defaults.
    -- Re-entering a condition must restore only those defined defaults during
    -- preview. Explicit true/false wins; final compilation never fills gaps.
    let mergeDraft (FormEntries supplied) (FormEntries defined) = FormEntries $ Map.union supplied defined
        mergeDraft supplied _ = supplied
    pure $ Map.unionWith mergeDraft (valuesByRowId request) $ Map.fromList pairs
    else pure $ valuesByRowId request
  let go _ _ [] = Right []
      go prior crash (section:rest) = do
        results <- solveSection prior crash initial section
        nextCrash <- mergeCrash $ crash : map resultCrash results
        following <- go (prior <> concatMap resultFacts results) nextCrash rest
        pure $ results <> following
  go [] CrashAbsent $ schemaSections schema

activeEntries :: RowResult -> [Text]
activeEntries result = [choiceId c | (c,ProvenTrue) <- resultEntries result]

missingErrors :: RowResult -> [Text]
missingErrors result
  | resultGate result == Unanswered = ["Unresolved settings condition"]
  | resultGate result == ProvenFalse = []
  | not $ editable row = resultErrors result
  | otherwise = resultErrors result <> case resultValue result of
      Nothing -> ["Missing settings value"]
      Just (FormChoice Nothing) | requiredValue row -> ["Missing required settings choice"]
      Just (FormChoice (Just selected)) | selected `notElem` activeEntries result -> ["Selected settings entry is not available"]
      Just (FormEntries entries) -> ["Missing visible settings entries" | any (`Map.notMember` entries) $ activeEntries result]
        <> ["Unresolved settings entry condition" | any ((== Unanswered) . snd) $ resultEntries result]
      _ -> []
 where
  row = resultRow result

staleErrors :: SettingsFormRequest -> RowResult -> [Text]
staleErrors request result = case Map.lookup (rowId $ resultRow result) $ valuesByRowId request of
  Just _ | resultGate result == ProvenFalse -> ["Inactive settings row must be omitted"]
  Just (FormEntries entries) | any (`notElem` activeEntries result) $ Map.keys entries -> ["Inactive settings entries must be omitted"]
  _ -> []

canonicalForm :: ResolvedSchema -> [RowResult] -> SettingsFormRequest
canonicalForm schema results = SettingsFormRequest (schemaIdentity schema) $ Map.fromList
  [(rowId row,prune result value) | result <- results,let row = resultRow result,editable row,resultGate result == ProvenTrue
    ,Just value <- [resultValue result]]
 where
  prune result (FormEntries entries) = FormEntries $ Map.filterWithKey (\k _ -> k `elem` activeEntries result) entries
  prune _ value = value

previewForm :: ResolvedSchema -> SettingsFormRequest -> Either Text Value
previewForm schema request = do
  results <- runForm True schema request
  let canonical = canonicalForm schema results
      errors = nub $ concatMap missingErrors results
  -- The preview's canSubmit is obtained from the EXACT final compiler, never
  -- merely from UI visibility. It returns no partial CampaignLog on failure.
  let valid = null errors && case compileForm schema canonical of Right _ -> True; Left _ -> False
  pure $ object
    ["schema" .= schemaView schema,"schemaId" .= schemaIdentity schema,"canSubmit" .= valid
    ,"submission" .= canonical,"errors" .= errors
    ,"staleRows" .= [rowId $ resultRow r | r <- results,not $ null $ staleErrors request r]
    ,"rows" .= map (rowView request) results]

rowView :: SettingsFormRequest -> RowResult -> Value
rowView request result = object
  ["id" .= rowId row,"kind" .= rowKind row,"label" .= rowLabel row,"formKey" .= rowFormKey row
  ,"visibility" .= truthText (resultGate result),"value" .= resultValue result
  ,"valueSource" .= (if not (editable row) || resultForced result then "forced" else
      if Map.member (rowId row) $ valuesByRowId request then "draft" else if isJust $ resultValue result then "default" else "unanswered" :: Text)
  ,"minimum" .= (if rowKind row == ChooseNum then Just $ fromMaybe 0 $ rowMinimum row else Nothing)
  ,"maximum" .= rowMaximum row,"maximumDamage" .= rowMaxDamage row,"maximumHorror" .= rowMaxHorror row
  ,"required" .= requiredValue row,"errors" .= missingErrors result
  ,"choices" .= [object ["id" .= choiceId c,"label" .= choiceLabel c,"visibility" .= truthText gate] | (c,gate) <- resultEntries result]]
 where
  row = resultRow result
  truthText = \case ProvenTrue -> "visible" :: Text; ProvenFalse -> "inactive"; Unanswered -> "pending"

compileForm :: ResolvedSchema -> SettingsFormRequest -> Either Text CampaignLog
compileForm schema request = fst <$> compileFormWithForced schema request

-- Historical editing needs the compulsory chapter facts separately from the
-- player's editable choices. Never mark the entire compiled chapter as forced.
-- Reuse the SAME resolved predicates/results; no defaults are re-evaluated on
-- the caller's amended log and no Message is executed by this pure projection.
compileFormWithForced :: ResolvedSchema -> SettingsFormRequest -> Either Text (CampaignLog,CampaignLog)
compileFormWithForced schema request = do
  results <- runForm False schema request
  unless (null $ concatMap (\r -> missingErrors r <> staleErrors request r) results) $
    Left "Settings form is incomplete, inconsistent or contains inactive values"
  let active=filter ((== ProvenTrue) . resultGate) results
      mandatory result=not (editable $ resultRow result) || resultForced result
  compiled<-foldM (applyResult $ schemaStandalone schema) mkCampaignLog active
  forced<-foldM (applyResult $ schemaStandalone schema) mkCampaignLog $ filter mandatory active
  pure (compiled,forced)

-- A campaign form's executable options previously remained inert in its log.
-- Standalone StartScenario already dispatches them and must NOT do so twice.
-- Preview/compileForm/import/reopen never execute these messages. Only the
-- trusted Session submission path calls this once after recipient/revision checks.
compileSubmission :: ResolvedSchema -> SettingsFormRequest -> Either Text [Message]
compileSubmission schema request = do
  compiled <- compileForm schema request
  if schemaStandalone schema then do
    -- Bag initialization belongs to the native StandaloneSetup lifecycle hook:
    -- an early bag here would be discarded by the original StartScenario.
    pure [SetCampaignLog compiled]
   else do
    ident <- case schemaContext schema of
      Object context -> maybe (Left "Campaign submission has no bound identity") parseDefinition $ KM.lookup "campaign" context
      _ -> Left "Campaign submission has no bound context"
    let allowed = fromMaybe [] $ lookup ident oneShotCampaignOptions
        selected = campaignLogOptions compiled
    unless (all (`elem` allowed) $ toList selected) $ Left "Campaign option has no reviewed execution policy"
    let initializeMissingMetadata = case schemaContext schema of
          Object context -> ident == "53"
            && KM.lookup "metadataPolicy" context == Just (String "materialize-original-mempty-only-when-null-v1")
            && KM.lookup "initializeMissingMetadata" context == Just (Bool True)
          _ -> False
    -- ResolvedSchema is constructed ONLY from the actual server Game and a
    -- real pending settings question. The client cannot supply this flag.
    pure $ [SetCampaignMeta $ toJSON (mempty :: TFA.Metadata) | initializeMissingMetadata]
      <> (SetCampaignLog compiled : [HandleOption option | option <- allowed,option `member` selected])

applyResult :: Bool -> CampaignLog -> RowResult -> Either Text CampaignLog
applyResult standalone logValue result = case rowEffect row of
  NoEffect -> pure logValue
  KeyEffect _ key -> case rowKind row of
    ForceKey -> pure $ setKeyOrdered key
    _ -> case resultValue result of
      Just (FormBoolean True) -> pure $ setKeyOrdered key
      Just (FormBoolean False) -> pure $ if standalone then deleteCampaignLogKey key logValue else logValue
      _ -> Left "Missing validated key setting"
  SelectedKeyEffect -> selected (\c -> do key <- parseDefinition $ choiceValue c; pure $ setKeyOrdered key)
  OptionEffect _ wire -> case resultValue result of
    Just (FormBoolean True) -> pure $ maybe logValue (\option -> setCampaignLogOption option logValue) wire
    _ -> pure logValue
  LocalChoiceEffect -> selected $ \c -> pure $ maybe logValue (\option -> setCampaignLogOption option logValue) $ choiceWireOption c
  CountEffect _ key -> case resultValue result of
    Just (FormInteger n) | Map.notMember key $ campaignLogRecordedCounts logValue -> pure $ setCampaignLogRecordedCount key n logValue
    _ -> Left "Conflicting or missing validated count"
  RecordEffect _ key kind fixed -> do
    let candidates = [(value,crossed) | (RecordFact crossed _ actualKind value,ProvenTrue) <- resultFacts result,actualKind == kind]
    -- fixed is retained in the typed definition; actual authorized facts, not
    -- the unfiltered draft, determine the set in declaration order.
        order = maybe (map choiceValue $ rowChoices row) pure fixed
    records <- traverse (\(value,crossed) -> first (const "Invalid typed record") $ parseEither (const $ typedRecord kind crossed value) ())
      [(value,crossed) | value <- order,(actual,crossed) <- candidates,value == actual]
    addRecords key records logValue
  SelectedRecordTarget kind value -> selected $ \c -> do
    key <- parseDefinition $ choiceValue c
    record <- first (const "Invalid selected record") $ parseEither (const $ typedRecord kind False value) ()
    addRecords key [record] logValue
  CrashEffect -> selected $ \c -> do
    code <- parseDefinition @CardCode $ choiceValue c
    record <- first (const "Invalid crash victim record") $ parseEither (const $ typedRecord CardRecord False $ toJSON code) ()
    withRecord <- addRecords (EdgeOfTheEarthKey WasKilledInThePlaneCrash) [record] logValue
    addPartner code 0 0 Eliminated withRecord
  PartnerEffect code -> case resultValue result of
    Just (FormPartner damage horror status) -> addPartner (if status == Resolute then toResolute code else code) damage horror status logValue
    _ -> Left "Missing validated partner setting"
 where
  row = resultRow result
  selected action = case choiceOf row $ resultValue result of Just c -> action c; Nothing -> pure logValue
  setKeyOrdered key = (setCampaignLogKey key logValue)
    {campaignLogOrderedKeys = if standalone || key `elem` campaignLogOrderedKeys logValue
      then campaignLogOrderedKeys logValue else campaignLogOrderedKeys logValue <> [key]}

-- SomeRecorded's ordinary Eq can normalize CardCode faces. Compare the exact
-- serialized record type and value instead. Keep source order, dedupe exact
-- same-state values, reject contradictory crossed/recorded states.
recordIdentity :: SomeRecorded -> Value
recordIdentity record = case toJSON record of
  Object o -> case KM.lookup "recordVal" o of
    Just (Object value) -> object ["type" .= KM.lookup "recordType" o,"value" .= KM.lookup "contents" value]
    _ -> Null
  _ -> Null

addRecords :: CampaignLogKey -> [SomeRecorded] -> CampaignLog -> Either Text CampaignLog
addRecords key incoming logValue = do
  merged <- foldM add (Map.findWithDefault [] key $ campaignLogRecordedSets logValue) incoming
  pure $ setCampaignLogRecorded key merged logValue
 where
  add previous record = case find ((== recordIdentity record) . recordIdentity) previous of
    Nothing -> Right $ previous <> [record]
    Just existing | toJSON existing == toJSON record -> Right previous
    _ -> Left "Conflicting recorded and crossed-out settings entries"

addPartner :: CardCode -> Int -> Int -> PartnerStatus -> CampaignLog -> Either Text CampaignLog
addPartner code damage horror status logValue = do
  -- Partners in the definition use base codes. Resolute aliases must not
  -- create a second role for the same expedition member.
  let sameRole existing = existing == code || toResolute existing == toResolute code
  unless (not $ any sameRole $ Map.keys $ campaignLogPartners logValue) $
    Left "Conflicting partner settings roles"
  pure $ setCampaignLogPartnerStatus damage horror status code logValue
