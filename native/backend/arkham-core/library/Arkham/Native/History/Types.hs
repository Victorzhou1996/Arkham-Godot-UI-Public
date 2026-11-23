-- Finite history editor wire types. None of these identifiers is a raw log
-- key, CardId, Message, CampaignStep, Modifier, or arbitrary metadata object.
-- Every identifier is resolved against a native, context-bound profile.
module Arkham.Native.History.Types where

import Arkham.Prelude
import Arkham.CampaignLog (PartnerStatus (..))
import Arkham.Id (CampaignId,ScenarioId)
import Arkham.Native.Settings.Types (FormValue,SettingsFormRequest,exactFields,boundedText,uniqueMap)
import Control.Monad.Fail (fail)
import Data.Aeson.Types (Parser)
import Data.Foldable qualified as F
import Data.Map.Strict qualified as Map

data HistoryPhase = HistoryPending | HistoryInstalled
  deriving stock (Show,Eq,Generic)
  deriving anyclass (ToJSON,FromJSON)

-- Pending is only an intention to visit the original initial settings pause.
-- Deck/initial trauma questions remain ordinary, durable human interactions.
-- Installed is provenance, not permission to reinstall on a future Ask.
data HistoricalBootstrap = HistoricalBootstrap
  { bootstrapCampaign :: CampaignId
  , bootstrapStart :: ScenarioId
  , bootstrapPhase :: HistoryPhase
  }
  deriving stock (Show,Eq)

instance ToJSON HistoricalBootstrap where
  toJSON HistoricalBootstrap{..}=object
    ["version" .= (1::Int),"campaign" .= bootstrapCampaign,"start" .= bootstrapStart,"phase" .= bootstrapPhase]
instance FromJSON HistoricalBootstrap where
  parseJSON=withObject "historical bootstrap" \o->do
    exactFields ["version","campaign","start","phase"] o
    version<-o .: "version"
    unless (version==(1::Int)) $ fail "Unsupported historical bootstrap"
    HistoricalBootstrap <$> o .: "campaign" <*> o .: "start" <*> o .: "phase"

data HistoryFlags = HistoryFlags {historyRecorded :: Bool,historyCrossedOut :: Bool}
  deriving stock (Show,Eq)
instance FromJSON HistoryFlags where
  parseJSON=withObject "historical flags" \o->exactFields ["recorded","crossedOut"] o >>
    (HistoryFlags <$> o .: "recorded" <*> o .: "crossedOut")
instance ToJSON HistoryFlags where
  toJSON HistoryFlags{..}=object ["recorded" .= historyRecorded,"crossedOut" .= historyCrossedOut]

data HistoryRecordState = HistoricalRecorded | HistoricalCrossedOut
  deriving stock (Show,Eq)
instance FromJSON HistoryRecordState where
  parseJSON=withText "historical record state" \case
    "recorded"->pure HistoricalRecorded
    "crossedOut"->pure HistoricalCrossedOut
    _->fail "Unknown historical record state"
instance ToJSON HistoryRecordState where
  toJSON=String . \case HistoricalRecorded->"recorded";HistoricalCrossedOut->"crossedOut"

data HistoryRecord = HistoryRecord
  { historyEntryId :: Text
  , historyRecordState :: HistoryRecordState
  , historyRecordCircled :: Bool
  }
  deriving stock (Show,Eq)
instance FromJSON HistoryRecord where
  parseJSON=withObject "historical record" \o->do
    exactFields ["entryId","state","circled"] o
    HistoryRecord <$> (boundedText=<<o .: "entryId") <*> o .: "state" <*> o .: "circled"
instance ToJSON HistoryRecord where
  toJSON HistoryRecord{..}=object ["entryId" .= historyEntryId,"state" .= historyRecordState,"circled" .= historyRecordCircled]

data HistoryPartner = HistoryPartner
  { historyPartnerDamage :: Int
  , historyPartnerHorror :: Int
  , historyPartnerStatus :: PartnerStatus
  }
  deriving stock (Show,Eq)
instance FromJSON HistoryPartner where
  parseJSON=withObject "historical partner" \o->do
    exactFields ["damage","horror","status"] o
    damage<-boundedNonnegative 99=<<o .: "damage"
    horror<-boundedNonnegative 99=<<o .: "horror"
    status<-o .: "status"
    -- This is deliberately distinct from ordinary chapter FormPartner, whose
    -- narrower status permissions must not be silently widened.
    unless (status `elem` [Eliminated,Resolute,Mia,Safe,Victim,CannotTake,TheEntity]) $ fail "Unknown partner history status"
    pure $ HistoryPartner damage horror status
instance ToJSON HistoryPartner where
  toJSON HistoryPartner{..}=object ["damage" .= historyPartnerDamage,"horror" .= historyPartnerHorror,"status" .= historyPartnerStatus]

data HistoryLogDraft = HistoryLogDraft
  { historyFlags :: Map Text HistoryFlags
  , historyCounts :: Map Text Int
  , historySets :: Map Text [HistoryRecord]
  , historyOrderedKeys :: [Text]
  , historyOptions :: Map Text Bool
  , historyPartners :: Map Text HistoryPartner
  }
  deriving stock (Show,Eq)
instance FromJSON HistoryLogDraft where
  parseJSON=withObject "historical log draft" \o->do
    exactFields ["flags","counts","sets","orderedKeys","options","partners"] o
    flags<-namedValues=<<o .: "flags"
    counts<-namedValues=<<o .: "counts"
    sets<-namedValues=<<o .: "sets"
    for_ (Map.elems sets) \records->do
      boundedLength 512 records
      -- Original recordedSets are ordered lists. Vengeance history can contain
      -- several physical cards with the same code; do not silently deduplicate
      -- or reject those rows. Each occurrence retains its own record state.
      pure ()
    ordered<-identities=<<o .: "orderedKeys"
    options<-namedValues=<<o .: "options"
    partners<-namedValues=<<o .: "partners"
    pure $ HistoryLogDraft flags counts sets ordered options partners
instance ToJSON HistoryLogDraft where
  toJSON HistoryLogDraft{..}=object
    ["flags" .= namedJSON historyFlags,"counts" .= namedJSON historyCounts,"sets" .= namedJSON historySets
    ,"orderedKeys" .= historyOrderedKeys,"options" .= namedJSON historyOptions,"partners" .= namedJSON historyPartners]

data HistoryInvestigator = HistoryInvestigator
  { historyExperience :: Int
  , historyPhysicalTrauma :: Int
  , historyMentalTrauma :: Int
  , historySupplies :: Map Text Int
  }
  deriving stock (Show,Eq)
instance FromJSON HistoryInvestigator where
  parseJSON=withObject "historical investigator target totals" \o->do
    exactFields ["experience","physicalTrauma","mentalTrauma","supplies"] o
    experience<-boundedNonnegative 999=<<o .: "experience"
    physical<-boundedNonnegative 99=<<o .: "physicalTrauma"
    mental<-boundedNonnegative 99=<<o .: "mentalTrauma"
    supplies<-namedValues=<<o .: "supplies"
    traverse_ (boundedNonnegative 99) supplies
    pure $ HistoryInvestigator experience physical mental supplies
instance ToJSON HistoryInvestigator where
  toJSON HistoryInvestigator{..}=object
    ["experience" .= historyExperience,"physicalTrauma" .= historyPhysicalTrauma
    ,"mentalTrauma" .= historyMentalTrauma,"supplies" .= namedJSON historySupplies]

data HistoryStoryAssignment = HistoryStoryAssignment
  { historyRewardId :: Text
  , historyOwnerSlotId :: Text
  , historyStoryQuantity :: Int
  }
  deriving stock (Show,Eq)
instance FromJSON HistoryStoryAssignment where
  parseJSON=withObject "historical story ownership" \o->do
    exactFields ["rewardId","ownerSlotId","quantity"] o
    reward<-boundedText=<<o .: "rewardId"
    owner<-boundedText=<<o .: "ownerSlotId"
    quantity<-o .: "quantity"
    unless (quantity>0 && quantity<=10) $ fail "Invalid historical story quantity"
    pure $ HistoryStoryAssignment reward owner quantity
instance ToJSON HistoryStoryAssignment where
  toJSON HistoryStoryAssignment{..}=object ["rewardId" .= historyRewardId,"ownerSlotId" .= historyOwnerSlotId,"quantity" .= historyStoryQuantity]

data HistoryTarot = HistoryTarot {historyTarotId :: Text,historyTarotFacing :: Text}
  deriving stock (Show,Eq)
instance FromJSON HistoryTarot where
  parseJSON=withObject "historical tarot" \o->do
    exactFields ["cardId","facing"] o
    ident<-boundedText=<<o .: "cardId"
    facing<-o .: "facing"
    unless (facing `elem` ["upright","reversed"]) $ fail "Invalid historical tarot facing"
    pure $ HistoryTarot ident facing
instance ToJSON HistoryTarot where
  toJSON HistoryTarot{..}=object ["cardId" .= historyTarotId,"facing" .= historyTarotFacing]

data HistoricalDraft = HistoricalDraft
  { historySchemaId :: Text
  , historyChapterForm :: SettingsFormRequest
  , historyLog :: HistoryLogDraft
  , historyInvestigators :: Map Text HistoryInvestigator
  , historyStoryAssignments :: [HistoryStoryAssignment]
  , historyMetadata :: Map Text FormValue
  , historyBag :: Maybe (Map Text Int)
  , historySteps :: [(Text,Maybe Text)]
  , historyDestiny :: Map Text HistoryTarot
  , historyPermanentEffects :: [Text]
  , historyRetiredIdentities :: [Text]
  , historyLodgeSetupBeneficiaries :: [Text]
  }
  deriving stock (Show,Eq)
instance FromJSON HistoricalDraft where
  parseJSON=withObject "historical start draft" \o->do
    exactFields ["schemaId","chapterForm","log","investigators","storyAssignments","metadata","bag","steps","destiny","permanentEffects","retiredIdentities","lodgeSetupBeneficiaries"] o
    ident<-boundedText=<<o .: "schemaId"
    chapter<-o .: "chapterForm"
    logDraft<-o .: "log"
    investigators<-namedValues=<<o .: "investigators"
    unless (Map.size investigators>=1 && Map.size investigators<=4) $ fail "Expected one to four historical investigator slots"
    assignments<-o .: "storyAssignments"
    boundedLength 128 assignments
    -- Non-unique Poisoned may be assigned to several investigators. Global
    -- story-family uniqueness belongs to the resolved native reward policy.
    void $ uniqueMap [((historyRewardId r,historyOwnerSlotId r),())|r<-assignments]
    metadata<-namedValues=<<o .: "metadata"
    bagRaw<-o .: "bag"
    bag<-traverse namedValues bagRaw
    for_ bag $ traverse_ $ boundedNonnegative 30
    stepsRaw<-namedRows=<<o .: "steps"
    void $ uniqueMap stepsRaw
    steps<-traverse (\(key,value)->(key,) <$> traverse boundedText value) stepsRaw
    destiny<-namedValues=<<o .: "destiny"
    effects<-identities=<<o .: "permanentEffects"
    retired<-identities=<<o .: "retiredIdentities"
    beneficiaries<-identities=<<o .: "lodgeSetupBeneficiaries"
    boundedLength 4 beneficiaries
    pure $ HistoricalDraft ident chapter logDraft investigators assignments metadata bag steps destiny effects retired beneficiaries
instance ToJSON HistoricalDraft where
  toJSON HistoricalDraft{..}=object
    ["schemaId" .= historySchemaId,"chapterForm" .= historyChapterForm,"log" .= historyLog
    ,"investigators" .= namedJSON historyInvestigators,"storyAssignments" .= historyStoryAssignments
    ,"metadata" .= namedJSON historyMetadata,"bag" .= fmap namedJSON historyBag
    ,"steps" .= [object ["id" .= key,"value" .= value]|(key,value)<-historySteps]
    ,"destiny" .= namedJSON historyDestiny,"permanentEffects" .= historyPermanentEffects,"retiredIdentities" .= historyRetiredIdentities
    ,"lodgeSetupBeneficiaries" .= historyLodgeSetupBeneficiaries]

namedValues :: FromJSON a => Value -> Parser (Map Text a)
namedValues raw=namedRows raw >>= uniqueMap

namedRows :: FromJSON a => Value -> Parser [(Text,a)]
namedRows=withArray "bounded named historical values" \raw->do
  boundedLength 512 raw
  for (toList raw) $ withObject "named historical value" \o->do
    exactFields ["id","value"] o
    ident<-boundedText=<<o .: "id"
    value<-o .: "value"
    pure (ident,value)

namedJSON :: ToJSON a => Map Text a -> Value
namedJSON values=toJSON [object ["id" .= k,"value" .= v]|(k,v)<-Map.toAscList values]

identities :: Value -> Parser [Text]
identities=withArray "bounded unique historical identities" \raw->do
  boundedLength 512 raw
  values<-traverse (parseJSON >=> boundedText) $ toList raw
  void $ uniqueMap [(v,())|v<-values]
  pure values

boundedLength :: Foldable f => Int -> f a -> Parser ()
boundedLength limit values=when (F.length values>limit) $ fail "Historical list exceeds its bound"

boundedNonnegative :: Int -> Int -> Parser Int
boundedNonnegative maximum value=do
  unless (value>=0 && value<=maximum) $ fail "Historical numeric value out of range"
  pure value
