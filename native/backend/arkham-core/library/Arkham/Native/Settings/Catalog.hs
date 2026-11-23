{-# LANGUAGE TemplateHaskell #-}
module Arkham.Native.Settings.Catalog where

import Arkham.Prelude
import Arkham.Native.Settings.Types
import Arkham.Native.Settings.Definition
import Control.Monad.Fail (fail)
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (Parser)
import Data.ByteString qualified as BS
import Data.FileEmbed (embedFile, makeRelativeToProject)
import Data.Text qualified as T

data DefinitionPresence = Declared | SourceOmitted
  deriving stock (Show, Eq)

instance FromJSON DefinitionPresence where
  parseJSON = withText "settings definition presence" \case
    "declared" -> pure Declared
    "source-omitted" -> pure SourceOmitted
    _ -> fail "Unknown definition presence"

data ScenarioDefinition = ScenarioDefinition
  { scenarioIdentity :: Text
  , scenarioCampaign :: Maybe Text
  , scenarioLocale :: Maybe Text
  , scenarioPresence :: DefinitionPresence
  , scenarioRows :: [SettingRow]
  }
  deriving stock Show

instance FromJSON ScenarioDefinition where
  parseJSON = withObject "scenario settings definition" \o -> do
    exactFields ["id","source","campaign","i18n","presence","rows"] o
    ScenarioDefinition <$> o .: "id" <*> o .: "campaign" <*> o .: "i18n" <*> o .: "presence" <*> o .: "rows"

data ResolutionDefinition = ResolutionDefinition
  { resolutionIdentity :: Text
  , resolutionRows :: [SettingRow]
  }
  deriving stock Show

instance FromJSON ResolutionDefinition where
  parseJSON = withObject "pending resolution definition" \o -> do
    exactFields ["id","label","rows","policy"] o
    policy <- o .: "policy" :: Parser Text
    unless (policy == "requires-explicit-resolution-flow") $ fail "Unknown resolution policy"
    ResolutionDefinition <$> o .: "id" <*> o .: "rows"

data ChapterDefinition = ChapterDefinition
  { chapterIdentity :: Text
  , chapterScope :: Text
  , chapterScenario :: Maybe Text
  , chapterVisible :: Condition
  , chapterRows :: [SettingRow]
  , chapterForceScenario :: Maybe Text
  , chapterResolutions :: [ResolutionDefinition]
  }
  deriving stock Show

instance FromJSON ChapterDefinition where
  parseJSON = withObject "campaign settings chapter" \o -> do
    allowedFields ["id","sourceKey","scenarioId","visible","rows","forceScenario","resolutions"] o
    ChapterDefinition <$> o .: "id" <*> o .: "sourceKey" <*> o .: "scenarioId"
      <*> o .: "visible" <*> o .: "rows" <*> o .:? "forceScenario" <*> o .:? "resolutions" .!= []

data CampaignDefinition = CampaignDefinition
  { campaignIdentity :: Text
  , campaignPresence :: DefinitionPresence
  , campaignChapters :: [ChapterDefinition]
  }
  deriving stock Show

instance FromJSON CampaignDefinition where
  parseJSON = withObject "campaign settings definition" \o -> do
    exactFields ["id","source","presence","steps"] o
    CampaignDefinition <$> o .: "id" <*> o .: "presence" <*> o .: "steps"

data PendingAlias = PendingAlias
  { aliasKind :: Text
  , aliasIdentity :: Text
  , aliasBase :: Text
  }
  deriving stock (Show, Eq)

instance FromJSON PendingAlias where
  parseJSON = withObject "pending settings alias" \o -> do
    exactFields ["kind","id","base","source","policy"] o
    kind <- o .: "kind"
    policy <- o .: "policy" :: Parser Text
    unless (kind `elem` ["scenario","campaign"] && policy == "pending-explicit-definition-review") $
      fail "Unreviewed alias policy"
    PendingAlias kind <$> o .: "id" <*> o .: "base"

data SettingsCatalog = SettingsCatalog
  { catalogIdentity :: Text
  , catalogScenarios :: [ScenarioDefinition]
  , catalogCampaigns :: [CampaignDefinition]
  , catalogAliases :: [PendingAlias]
  }
  deriving stock Show

instance FromJSON SettingsCatalog where
  parseJSON = withObject "native settings catalog" \o -> do
    exactFields ["version","scenarios","campaigns","aliases","overrides","coreBindings","evaluationPolicy","catalogId","sources"] o
    version <- o .: "version" :: Parser Int
    unless (version == 2) $ fail "Unsupported settings catalog version"
    policy <- o .: "evaluationPolicy" :: Parser Value
    unless (policy == object
      ["campaign" .= ("ordered-before-row" :: Text),"standalone" .= ("dependency-ordered-draft" :: Text)
      ,"conditionIdentity" .= ("original-condition-key-not-wire-key" :: Text)
      ,"resolutionSections" .= ("pending-explicit-selection-flow" :: Text)]) $
      fail "Unreviewed catalog provenance policy"
    -- The above describes the preserved extraction provenance. Runtime must
    -- use chapter consistency semantics, NOT a blanket row-order evaluator.
    bindings <- o .: "coreBindings" :: Parser [Value]
    unless (bindings ==
      [object ["path" .= ("Arkham/Campaigns/TheDunwichLegacy/Key.hs" :: Text),"sha256" .= ("afe895c9567871c62f20c7e3a7636ab6500c2e2d359b1b2ae8fdddf39dfbf6b6" :: Text)]
      ,object ["path" .= ("Arkham/Campaign/Option.hs" :: Text),"sha256" .= ("af334aa6c63b41e030edf0a5dffdff13f3002f9da600637a0253339349bc7d49" :: Text)]
      ,object ["path" .= ("Arkham/Campaigns/TheCircleUndone/Key.hs" :: Text),"sha256" .= ("55864a132fb310b8191ac6a2ac8209d15a23db1ec70f39d5595b08a33c6dfb52" :: Text)]]) $
      fail "Unreviewed core enum bindings"
    overrides <- o .: "overrides" :: Parser [Object]
    actualOverrides <- for overrides $ \entry -> do
      allowedFields ["row","path","before","after","source","sourceSha256","core","reason"] entry
      source <- entry .: "source" :: Parser Text
      sourceSha <- entry .: "sourceSha256" :: Parser Text
      unless (source == "src/arkham/data/campaigns.json" && sourceSha == "a437ab5a86d3447b9c33dfe9964190874daba28e1f86cd58ef86a13872ba9307") $
        fail "Unreviewed correction source"
      (,,,) <$> entry .: "row" <*> entry .: "path" <*> entry .: "before" <*> entry .: "after"
    unless ((actualOverrides :: [(Text,Text,Text,Text)]) ==
      [("campaign:02/step/5/2","visible.items[0].key","TakeNecronomicon","TakeTheNecronomicon")
      ,("campaign:02/step/4/3","effect.key","WarrenRiceWasKidnapped","ProfessorWarrenRiceWasKidnapped")
      ,("campaign:02/step/6/7","effect.wireOption","TakeZebulonWhateley","TakeZebulonWhately")
      ,("campaign:05/step/7/2","effect.key","TheInvestigatorsKeptTheirMementosHidden","TheInvestigatorsKeptsTheirMementosHidden")]) $
        fail "Unreviewed settings corrections"
    catalog <- SettingsCatalog <$> o .: "catalogId" <*> o .: "scenarios" <*> o .: "campaigns" <*> o .: "aliases"
    let unique xs = length xs == length (nub xs)
        scenarios = catalogScenarios catalog
        campaigns = catalogCampaigns catalog
        aliases = catalogAliases catalog
        allRows = concatMap (flattenRows . scenarioRows) scenarios
          <> concatMap (concatMap chapterAllRows . campaignChapters) campaigns
    unless (unique (map scenarioIdentity scenarios) && unique (map campaignIdentity campaigns)
      && unique [(aliasKind a,aliasIdentity a) | a <- aliases] && unique (map rowId allRows)) $
      fail "Duplicate catalog identity"
    unless (catalogIdentity catalog == "arkham-settings-sha256:27a8e5559a273b0ab09a88081e384bf821488ef23014ed2828d536b4d5b9a920") $
      fail "Unknown reviewed settings catalog identity"
    for_ aliases $ \alias -> unless (case aliasKind alias of
      "scenario" -> aliasIdentity alias `notElem` map scenarioIdentity scenarios && aliasBase alias `elem` map scenarioIdentity scenarios
      "campaign" -> aliasIdentity alias `notElem` map campaignIdentity campaigns && aliasBase alias `elem` map campaignIdentity campaigns
      _ -> False) $ fail "Alias conflicts with exact definition or unknown base"
    for_ scenarios $ \s -> when (scenarioPresence s == SourceOmitted && not (null $ scenarioRows s)) $
      fail "Omitted scenario definition has rows"
    for_ campaigns $ \c -> when (campaignPresence c == SourceOmitted && not (null $ campaignChapters c)) $
      fail "Omitted campaign definition has chapters"
    for_ allRows $ void . defaultFormValue
    for_ scenarios $ \scenario -> unless (null $ scenarioRows scenario) $ do
      let reviewed =
            [("01120","01"),("01142","01"),("05238","05"),("05325","05"),("06119","06")
            ,("07056","07"),("07123","07"),("07163","07"),("07198","07"),("07231","07"),("07274","07"),("07311","07")
            ,("08501b","08"),("08501c","08"),("08549","08"),("08596","08"),("08621","08"),("08648a","08"),("08648b","08")]
      campaign <- maybe (fail "Unreviewed configured scenario scope") pure $ lookup (scenarioIdentity scenario) reviewed
      unless (scenarioCampaign scenario == Just campaign) $ fail "Scenario campaign metadata disagrees with reviewed scope"
      scope <- reviewedScope campaign
      for_ (flattenRows $ scenarioRows scenario) $ validateRowScope scope
    for_ campaigns $ \campaign -> unless (null $ campaignChapters campaign) $ do
      unless (campaignIdentity campaign `elem` ["02","05"]) $ fail "Unreviewed configured campaign scope"
      scope <- reviewedScope $ campaignIdentity campaign
      for_ (concatMap chapterAllRows $ campaignChapters campaign) $ validateRowScope scope
    pure catalog

reviewedScope :: Text -> Parser Text
reviewedScope = \case
  "01" -> pure "NightOfTheZealotKey"
  "02" -> pure "TheDunwichLegacyKey"
  "05" -> pure "TheCircleUndoneKey"
  "06" -> pure "TheDreamEatersKey"
  "07" -> pure "TheInnsmouthConspiracyKey"
  "08" -> pure "EdgeOfTheEarthKey"
  _ -> fail "Unreviewed configured settings scope"

-- A valid naked enum string is NOT by itself proof that it belongs to this
-- row. Bind every actual persisted key/choice target to reviewed source scope.
-- This is an explicit current catalog roster, not an inferred universal rule
-- for side stories/homebrew which may legitimately use foreign/global logs.
validateRowScope :: Text -> SettingRow -> Parser ()
validateRowScope scope row = do
  actualKeys <- case rowEffect row of
    KeyEffect _ key -> pure [key]
    CountEffect _ key -> pure [key]
    RecordEffect _ key _ _ -> pure [key]
    SelectedKeyEffect -> traverse (parseJSON . choiceValue) $ rowChoices row
    SelectedRecordTarget {} -> traverse (parseJSON . choiceValue) $ rowChoices row
    _ -> pure []
  for_ actualKeys $ \key -> case toJSON key of
    Object fields | KM.lookup "tag" fields == Just (String scope) -> pure ()
    _ -> fail "Settings key is valid but outside its reviewed scope"

chapterAllRows :: ChapterDefinition -> [SettingRow]
chapterAllRows chapter = flattenRows (chapterRows chapter)
  <> concatMap (flattenRows . resolutionRows) (chapterResolutions chapter)

-- New native-data is independently identity-bound alongside (not replacing)
-- the original six published data files. No filesystem access at runtime.
catalogBytes :: BS.ByteString
catalogBytes = $(makeRelativeToProject "native-data/settings-catalog.json" >>= embedFile)

catalogResult :: Either Text SettingsCatalog
catalogResult = first T.pack $ eitherDecodeStrict' catalogBytes

data DefinitionLookup a = ExactDefinition a | AliasPending PendingAlias | UnknownDefinition
  deriving stock Show

lookupScenario :: SettingsCatalog -> Text -> DefinitionLookup ScenarioDefinition
lookupScenario catalog ident = case find ((== ident) . scenarioIdentity) $ catalogScenarios catalog of
  Just result -> ExactDefinition result
  Nothing -> maybe UnknownDefinition AliasPending $ find (\a -> aliasKind a == "scenario" && aliasIdentity a == ident) $ catalogAliases catalog

lookupCampaign :: SettingsCatalog -> Text -> DefinitionLookup CampaignDefinition
lookupCampaign catalog ident = case find ((== ident) . campaignIdentity) $ catalogCampaigns catalog of
  Just result -> ExactDefinition result
  Nothing -> maybe UnknownDefinition AliasPending $ find (\a -> aliasKind a == "campaign" && aliasIdentity a == ident) $ catalogAliases catalog

-- Context selection is separate from conditional visibility. Keeping all
-- eligible chapters here must never include a future chapter's producer.
chaptersForStart :: CampaignDefinition -> Text -> Either Text [ChapterDefinition]
chaptersForStart campaign start = do
  let matching = [(position,chapterIdentity chapter) | (position,chapter) <- zip [(0 :: Int)..] $ campaignChapters campaign,chapterScenario chapter == Just start]
  selected <- case matching of
    [(position,_)] -> Right position
    -- Fixed source assigns 02236 to both the Survivors interlude and the
    -- actual Undimensioned and Unseen scenario. The Haskell route has a real
    -- InterludeStep 2 BEFORE ScenarioStep 02236. This exact reviewed binding
    -- includes the interlude as a conditional prior chapter, not a grant.
    [(6,"campaign:02/step/6"),(7,"campaign:02/step/7")]
      | campaignIdentity campaign == "02" && start == "02236" -> Right 7
    [] -> Left "Starting scenario is absent from declared campaign settings"
    _ -> Left "Ambiguous unreviewed campaign starting scenario"
  pure [chapter | (position,chapter) <- zip [0..] $ campaignChapters campaign
    , position < selected || chapterForceScenario chapter == Just start]

catalogSummary :: SettingsCatalog -> Value
catalogSummary catalog = object
  [ "catalogId" .= catalogIdentity catalog
  , "scenarios" .= length (catalogScenarios catalog)
  , "campaigns" .= length (catalogCampaigns catalog)
  , "standaloneRows" .= length (concatMap (flattenRows . scenarioRows) $ catalogScenarios catalog)
  , "campaignRowsIncludingPendingResolutions" .= length (concatMap (concatMap chapterAllRows . campaignChapters) $ catalogCampaigns catalog)
  , "pendingAliases" .= length (catalogAliases catalog)
  ]
