module Arkham.Native.Settings.Definition where

import Arkham.Prelude
import Arkham.Campaign.Option (CampaignOption)
import Arkham.CampaignLog (PartnerStatus (..))
import Arkham.CampaignLogKey
import Arkham.Card.CardCode (CardCode, unCardCode)
import Arkham.Native.Settings.Types
import Control.Monad.Fail (fail)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (Parser, parseEither)
import Data.Map.Strict qualified as Map
import Data.Text qualified as T

data RecordKind = CardRecord | MementoRecord | MemoryRecord | GenericRecord
  deriving stock (Show, Eq, Ord)

instance FromJSON RecordKind where
  parseJSON = withText "settings record type" \case
    "RecordableCardCode" -> pure CardRecord
    "RecordableMemento" -> pure MementoRecord
    "RecordableMemory" -> pure MemoryRecord
    "RecordableGeneric" -> pure GenericRecord
    _ -> fail "Unsupported settings record type"

instance ToJSON RecordKind where
  toJSON = String . \case
    CardRecord -> "RecordableCardCode"
    MementoRecord -> "RecordableMemento"
    MemoryRecord -> "RecordableMemory"
    GenericRecord -> "RecordableGeneric"

-- Condition identities remain the SOURCE form names (with optional scope),
-- not corrected wire enums. A temporary ChooseOption is a real condition fact
-- even when it deliberately has no CampaignOption to persist.
data Condition
  = Always
  | AllOf [Condition]
  | AnyOf [Condition]
  | NoneOf [Condition]
  | Not Condition
  | HasKey Text (Maybe Text)
  | HasOption Text
  | HasRecord Bool Text (Maybe RecordKind) Value
  | CountAtLeast Text Int
  | CountAtMost Text Int
  | NotCrashVictim Text
  deriving stock (Show, Eq)

instance FromJSON Condition where
  parseJSON = withObject "settings condition" \o -> do
    op <- o .: "op" :: Parser Text
    case op of
      "true" -> exactFields ["op"] o >> pure Always
      "all" -> exactFields ["op","items"] o >> AllOf <$> o .: "items"
      "any" -> exactFields ["op","items"] o >> AnyOf <$> o .: "items"
      "none" -> exactFields ["op","items"] o >> NoneOf <$> o .: "items"
      "not" -> exactFields ["op","item"] o >> Not <$> o .: "item"
      "key" -> allowedFields ["op","key","scope"] o >> HasKey <$> o .: "key" <*> o .:? "scope"
      "option" -> do
        allowedFields ["op","key","scope"] o
        scope <- o .:? "scope" :: Parser (Maybe Text)
        unless (isNothing scope) $ fail "Option scope not implemented"
        HasOption <$> o .: "key"
      "recorded" -> record False o
      "crossed" -> record True o
      "count" -> do
        exactFields ["op","key","comparison","value"] o
        comparison <- o .: "comparison" :: Parser Text
        ctor <- case comparison of
          "gte" -> pure CountAtLeast
          "lte" -> pure CountAtMost
          _ -> fail "Unknown count predicate"
        ctor <$> o .: "key" <*> o .: "value"
      "notCrashVictim" -> exactFields ["op","value"] o >> NotCrashVictim <$> o .: "value"
      _ -> fail "Unknown settings predicate"
   where
    record crossed o = do
      allowedFields ["op","key","value","recordable"] o
      HasRecord crossed <$> o .: "key" <*> o .:? "recordable" <*> o .: "value"

data SettingKind
  = Group | ToggleKey | ToggleOption | PickKey | ToggleCrossedOut
  | ToggleRecords | ChooseRecord | ChooseNum | SetPartnerKilled
  | SetPartnerDetails | ChooseKey | SetKey | ForceKey | Option
  | ChooseOption | Record | CrossOut | SetRecordable | ChooseRecordable
  | ChooseRecordables | ForceRecorded
  deriving stock (Show, Eq, Ord, Generic)
  deriving anyclass (FromJSON, ToJSON)

data Effect
  = NoEffect
  | KeyEffect Text CampaignLogKey
  | SelectedKeyEffect
  | OptionEffect Text (Maybe CampaignOption)
  | LocalChoiceEffect
  | CountEffect Text CampaignLogKey
  | RecordEffect Text CampaignLogKey RecordKind (Maybe Value)
  | SelectedRecordTarget RecordKind Value
  | CrashEffect
  | PartnerEffect CardCode
  deriving stock Show

instance FromJSON Effect where
  parseJSON = withObject "settings effect" \o -> do
    kind <- o .: "kind" :: Parser Text
    case kind of
      "none" -> exactFields ["kind"] o >> pure NoEffect
      "key" -> exactFields ["kind","key","conditionKey"] o >> KeyEffect <$> o .: "conditionKey" <*> o .: "key"
      "selected-key" -> exactFields ["kind"] o >> pure SelectedKeyEffect
      "option-toggle" -> do
        exactFields ["kind","conditionKey","wireOption"] o
        OptionEffect <$> o .: "conditionKey" <*> parseOptionalWireOption (KM.lookup "wireOption" o)
      "local-choice" -> exactFields ["kind"] o >> pure LocalChoiceEffect
      "count" -> exactFields ["kind","key","conditionKey"] o >> CountEffect <$> o .: "conditionKey" <*> o .: "key"
      "record-set" -> do
        allowedFields ["kind","key","conditionKey","recordable","value"] o
        RecordEffect <$> o .: "conditionKey" <*> o .: "key" <*> o .: "recordable" <*> pure (KM.lookup "value" o)
      "selected-record-target" -> exactFields ["kind","recordable","value"] o >> SelectedRecordTarget <$> o .: "recordable" <*> o .: "value"
      "plane-crash-victim" -> exactFields ["kind"] o >> pure CrashEffect
      "partner" -> exactFields ["kind","code"] o >> PartnerEffect <$> o .: "code"
      _ -> fail "Unknown settings effect"

-- CampaignOption has a parameterized CampaignVariant constructor, so its
-- real Aeson representation is a tagged object even for nullary options.
-- The audited metadata stores finite nullary names, not arbitrary options.
parseOptionalWireOption :: Maybe Value -> Parser (Maybe CampaignOption)
parseOptionalWireOption Nothing = pure Nothing
parseOptionalWireOption (Just Null) = pure Nothing
parseOptionalWireOption (Just value) = Just <$> withText "finite settings option" (\name -> do
  void $ boundedText name
  parseJSON $ object ["tag" .= name]) value

data SettingChoice = SettingChoice
  { choiceId :: Text
  , choiceLabel :: Text
  , choiceValue :: Value
  , choiceVisible :: Condition
  , choiceForced :: Maybe Condition
  , choiceDefault :: Maybe Bool
  , choiceConditionKey :: Maybe Text
  , choiceWireOption :: Maybe CampaignOption
  }
  deriving stock Show

instance FromJSON SettingChoice where
  parseJSON = withObject "settings choice definition" \o -> do
    allowedFields ["id","label","value","visible","forced","default","conditionKey","wireOption"] o
    SettingChoice <$> o .: "id" <*> o .: "label" <*> o .: "value" <*> o .: "visible"
      <*> o .:? "forced" <*> o .:? "default" <*> o .:? "conditionKey" <*> parseOptionalWireOption (KM.lookup "wireOption" o)

data SettingRow = SettingRow
  { rowId :: Text
  , rowKind :: SettingKind
  , rowFormKey :: Text
  , rowLabel :: Text
  , rowVisible :: Condition
  , rowEffect :: Effect
  , rowChoices :: [SettingChoice]
  , rowChildren :: [SettingRow]
  , rowDefault :: Maybe Value
  , rowDefaultPresence :: Maybe Text
  , rowMinimum :: Maybe Int
  , rowMaximum :: Maybe Int
  , rowMaxDamage :: Maybe Int
  , rowMaxHorror :: Maybe Int
  , rowRequired :: Bool
  , rowVisibilityEvaluation :: Maybe Text
  , rowRawDefinition :: Value
  }
  deriving stock Show

instance FromJSON SettingRow where
  parseJSON = withObject "settings row definition" \o -> do
    allowedFields
      ["id","kind","formKey","label","visible","definition","effect","children","recordable"
      ,"minimum","maximum","defaultPresence","default","items","required","sourcePlaceholder"
      ,"partner","maximumDamage","maximumHorror","statuses","forced","visibilityEvaluation"] o
    row <- SettingRow <$> o .: "id" <*> o .: "kind" <*> o .: "formKey" <*> o .: "label"
      <*> o .: "visible" <*> o .: "effect" <*> o .:? "items" .!= [] <*> o .:? "children" .!= []
      <*> pure (KM.lookup "default" o) <*> o .:? "defaultPresence" <*> o .:? "minimum" <*> o .:? "maximum"
      <*> o .:? "maximumDamage" <*> o .:? "maximumHorror" <*> o .:? "required" .!= False
      <*> o .:? "visibilityEvaluation" <*> o .: "definition"
    let expectedVisibility = if rowId row == "campaign:02/step/5/2" then Just "before-row" else Nothing
    unless (rowVisibilityEvaluation row == expectedVisibility) $ fail "Unreviewed row visibility evaluation"
    declaredRecord <- o .:? "recordable"
    let expectedRecord = case rowEffect row of
          RecordEffect _ _ kind _ -> Just kind
          SelectedRecordTarget kind _ -> Just kind
          _ -> Nothing
        expectedForced = if rowKind row `elem` [ForceKey,ForceRecorded] then Just (Bool True) else Nothing
        expectedRequired = case rowKind row of
          ChooseRecord -> Just $ Bool False
          kind | kind `elem` [PickKey,ChooseKey,ChooseOption,ChooseRecordable,SetPartnerKilled] -> Just $ Bool True
          _ -> Nothing
    unless (declaredRecord == expectedRecord && KM.lookup "forced" o == expectedForced
      && KM.lookup "required" o == expectedRequired) $ fail "Redundant settings declarations disagree"
    case rowEffect row of
      PartnerEffect code -> do
        partner <- o .: "partner"
        statuses <- o .: "statuses" :: Parser [Text]
        unless (partner == unCardCode code && statuses == ["Safe","Resolute","Eliminated"]) $
          fail "Partner identity/editor states disagree"
      _ -> when (any (`KM.member` o) ["partner","statuses","maximumDamage","maximumHorror"]) $
        fail "Unexpected partner declaration"
    withObject "original row definition" (\raw -> do
      originalKind <- raw .: "type"
      originalKey <- raw .: "key"
      unless (originalKind == rowKind row && originalKey == rowFormKey row) $
        fail "Normalized row identity differs from original definition") $ rowRawDefinition row
    validateRowDefinition row
    pure row

allowedFields :: [K.Key] -> Object -> Parser ()
allowedFields expected o = unless (all (`elem` expected) $ KM.keys o) $ fail "Unknown settings definition field"

-- Parse every enumerated/fixed value through the ACTUAL Haskell record type,
-- before any future log construction. Never use the legacy unsafe error path.
typedRecord :: RecordKind -> Bool -> Value -> Parser SomeRecorded
typedRecord kind crossed value = parseJSON $ object
  ["recordType" .= kind,"recordVal" .= object ["tag" .= (if crossed then "CrossedOut" else "Recorded" :: Text),"contents" .= value]]

validateRowDefinition :: SettingRow -> Parser ()
validateRowDefinition row = do
  _ <- boundedText $ rowId row
  let choices = rowChoices row
  unless (length (nub $ map choiceId choices) == length choices) $ fail "Duplicate definition choice ID"
  unless (length (nub $ map choiceValue choices) == length choices) $ fail "Duplicate definition choice value"
  for_ choices $ \c -> void $ boundedText $ choiceId c
  case (rowKind row,rowEffect row) of
    (Group,NoEffect) -> unless (null choices && isNothing (rowDefault row)) $ fail "Group cannot have an answer"
    (ToggleKey,KeyEffect {}) -> booleanDefault
    (SetKey,KeyEffect {}) -> booleanDefault
    (ForceKey,KeyEffect {}) -> pure ()
    (ToggleOption,OptionEffect {}) -> booleanDefault
    (Option,OptionEffect {}) -> booleanDefault
    (PickKey,SelectedKeyEffect) -> keyChoices
    (ChooseKey,SelectedKeyEffect) -> keyChoices
    (ChooseOption,LocalChoiceEffect) -> for_ choices $ \c ->
      unless (isJust $ choiceConditionKey c) $ fail "Local option lacks condition identity"
    (ChooseNum,CountEffect {}) -> do
      case (rowMinimum row,rowMaximum row) of
        (Just lo,Just hi) | lo > hi -> fail "Inverted settings number bounds"
        _ -> pure ()
      for_ (rowDefault row) $ \case
        Null -> pure ()
        v -> do
          n <- parseJSON v
          unless (withinBounds row n) $ fail "Number default outside bounds"
    (ChooseRecord,SelectedRecordTarget kind value) -> do
      void $ typedRecord kind False value
      for_ choices $ \c -> void (parseJSON (choiceValue c) :: Parser CampaignLogKey)
    (SetPartnerKilled,CrashEffect) -> for_ choices $ \c -> void (parseJSON (choiceValue c) :: Parser CardCode)
    (SetPartnerDetails,PartnerEffect _) -> do
      when (isNothing (rowMaxDamage row) || isNothing (rowMaxHorror row)) $ fail "Partner bounds required"
      void $ defaultFormValue row
    (kind,RecordEffect _ _ recordKind fixed) | kind `elem`
      [ToggleCrossedOut,ToggleRecords,Record,CrossOut,SetRecordable,ChooseRecordable,ChooseRecordables,ForceRecorded] -> do
        for_ fixed $ void . typedRecord recordKind False
        for_ choices $ void . typedRecord recordKind False . choiceValue
        when (kind `elem` [SetRecordable,ForceRecorded] && isNothing fixed) $ fail "Missing fixed record value"
    _ -> fail "Settings kind/effect mismatch"
  when (rowKind row /= Group && not (null $ rowChildren row)) $ fail "Only Group has children"
 where
  booleanDefault = do
    value <- maybe (fail "Missing boolean default") pure $ rowDefault row
    void (parseJSON value :: Parser Bool)
  keyChoices = for_ (rowChoices row) $ \c -> void (parseJSON (choiceValue c) :: Parser CampaignLogKey)

withinBounds :: SettingRow -> Int -> Bool
withinBounds row n = maybe True (<= n) (rowMinimum row) && maybe True (>= n) (rowMaximum row)

defaultFormValue :: SettingRow -> Parser (Maybe FormValue)
defaultFormValue row = case rowKind row of
  Group -> pure Nothing
  ForceKey -> pure Nothing
  ForceRecorded -> pure Nothing
  ChooseNum -> case rowDefault row of
    Nothing -> pure Nothing
    Just Null -> pure Nothing
    Just value -> Just . FormInteger <$> parseJSON value
  kind | kind `elem` [ToggleKey,ToggleOption,SetKey,Option,SetRecordable] ->
    traverse (fmap FormBoolean . parseJSON) $ rowDefault row
  SetPartnerDetails -> case rowDefault row of
    Just value -> withObject "partner default" (\o -> do
      exactFields ["damage","horror","status"] o
      damage <- o .: "damage"
      horror <- o .: "horror"
      status <- o .: "status"
      unless (damage >= 0 && horror >= 0 && maybe False (damage <=) (rowMaxDamage row)
        && maybe False (horror <=) (rowMaxHorror row) && status `elem` [Safe,Resolute,Eliminated]) $
        fail "Partner default outside editable bounds"
      pure $ Just $ FormPartner damage horror status) value
    _ -> fail "Partner default required"
  kind | kind `elem` [ToggleCrossedOut,ToggleRecords,Record,CrossOut,ChooseRecordables] -> do
    values <- maybe (fail "Record defaults required") parseJSON (rowDefault row) :: Parser [Bool]
    unless (length values == length (rowChoices row)) $ fail "Record defaults do not match entries"
    pure $ Just $ FormEntries $ Map.fromList $ zip (map choiceId $ rowChoices row) values
  _ -> case rowDefault row of
    Just value | value /= Null -> case find ((== value) . choiceValue) $ rowChoices row of
      Just choice -> pure $ Just $ FormChoice $ Just $ choiceId choice
      Nothing -> fail "Choice default not offered"
    _ -> pure $ Just $ FormChoice Nothing

flattenRows :: [SettingRow] -> [SettingRow]
flattenRows = concatMap $ \row -> row : flattenRows (rowChildren row)

-- Explicitly carry parent Group visibility into descendants. Flattening only
-- the child row's own predicate would grant hidden partner rows permission.
effectiveRows :: [Condition] -> [SettingRow] -> [(SettingRow,Condition)]
effectiveRows parents = concatMap $ \row ->
  let conditions = parents <> [rowVisible row]
   in (row,AllOf conditions) : effectiveRows conditions (rowChildren row)

parseDefinition :: FromJSON a => Value -> Either Text a
parseDefinition = first T.pack . parseEither parseJSON
