{-# LANGUAGE AllowAmbiguousTypes #-}
-- A typed field catalog, NOT an unrestricted CampaignLog JSON editor. The
-- five domains have been checked against their fixed production consumers.
-- Return-To aliases are explicit; absence of an old chapter form grants no
-- empty-history permission or fallback to another campaign.
module Arkham.Native.History.Profile where

import Arkham.Prelude
import Arkham.CampaignLogKey qualified as CL
import Arkham.Campaigns.NightOfTheZealot.Key qualified as N
import Arkham.Campaigns.TheDunwichLegacy.Key qualified as D
import Arkham.Campaigns.ThePathToCarcosa.Key qualified as C
import Arkham.Campaigns.TheForgottenAge.Key qualified as F
import Arkham.Campaigns.TheCircleUndone.Key qualified as U
import Arkham.Campaigns.TheCircleUndone.Memento (Memento (..))
import Arkham.Native.Settings.Catalog
import Arkham.Native.Settings.Definition
import Data.Aeson.Types (parseEither)
import Data.Data (Data,dataTypeOf,dataTypeConstrs,fromConstrM)
import Data.Map.Strict qualified as Map
import Data.Text qualified as T

data HistoryStorage = FlagStorage | CountStorage | SetStorage
  deriving stock (Show,Eq,Ord)

data HistoryRecordDomain
  = FixedRecords RecordKind [Value]
  | HistoricalInvestigatorCodes
  | TfaVengeanceCodes
  | NotzCultistCodes Bool
  deriving stock (Show,Eq)

data HistoryField = HistoryField
  { historyFieldKey :: CL.CampaignLogKey
  , historyFieldStorage :: [HistoryStorage]
  , historyFieldDomain :: Maybe HistoryRecordDomain
  , historyFieldEvidence :: [Text]
  }
  deriving stock Show

data HistoricalProfile = HistoricalProfile
  { historyActualCampaign :: Text
  , historyDefinitionCampaign :: Text
  , historyProfileFields :: [HistoryField]
  }
  deriving stock Show

historyFamily :: Text -> Either Text Text
historyFamily actual=maybe (Left "No typed history profile for this campaign") Right $
  lookup actual [("01","01"),("02","02"),("05","05"),("50","01"),("51","02"),("52","03"),("53","04"),("54","05")]

-- Only enums whose fixed source has nullary constructors are used. Reject a
-- future parameterized constructor instead of evaluating an invented payload.
nullaryValues :: forall a. Data a => Either Text [a]
nullaryValues=do
  let constructors=dataTypeConstrs $ dataTypeOf (undefined::a)
      values=mapMaybe (fromConstrM Nothing) constructors
  unless (length values==length constructors) $ Left "History enum acquired an unsupported parameter"
  pure values

familyKeys :: Text -> Either Text [CL.CampaignLogKey]
familyKeys= \case
  "01"->map CL.NightOfTheZealotKey <$> nullaryValues @N.NightOfTheZealotKey
  "02"->map CL.TheDunwichLegacyKey <$> nullaryValues @D.TheDunwichLegacyKey
  "03"->map CL.ThePathToCarcosaKey <$> nullaryValues @C.ThePathToCarcosaKey
  "04"->map CL.TheForgottenAgeKey <$> nullaryValues @F.TheForgottenAgeKey
  "05"->map CL.TheCircleUndoneKey <$> nullaryValues @U.TheCircleUndoneKey
  _->Left "Unknown fixed history family"

keyName :: CL.CampaignLogKey -> Text
keyName key=case key of
  CL.NightOfTheZealotKey value->simple value
  CL.TheDunwichLegacyKey value->simple value
  CL.ThePathToCarcosaKey value->simple value
  CL.TheForgottenAgeKey value->simple value
  CL.TheCircleUndoneKey value->simple value
  _->T.pack $ show key
 where
  simple :: (Show a,ToJSON a) => a -> Text
  simple value=case toJSON value of String name->name;_->T.pack $ show value

historyFieldId :: HistoryField -> Text
historyFieldId field="log:"<>keyName (historyFieldKey field)

countNames :: Text -> [Text]
countNames= \case
  "02"->["BroodEscapedIntoTheWild"]
  "03"->["Doubt","Conviction","ChasingTheStranger"]
  "04"->["YigsFury","TheHarbingerIsStillAlive","PathsAreKnownToYou"]
  "05"->["PiecesOfEvidenceWereLeftBehind","HereticsWereUnleashedUntoArkham","ThePathWindsBeforeYou"]
  _->[]

setNames :: Text -> [Text]
setNames= \case
  "01"->["CultistsWeInterrogated","CultistsWhoGotAway"]
  "02"->["SacrificedToYogSothoth"]
  "03"->["VIPsInterviewed","VIPsSlain","ReadActII","Possessed"]
  "04"->["TheJungleWatches"]
  "05"->["MissingPersons","WasTakenByTheWatcher","WasClaimedBySpecters","DisappearedIntoTheMist","WasPulledIntoTheSpectralRealm","MementosDiscovered"]
  _->[]

keyInFamily :: Text -> CL.CampaignLogKey -> Bool
keyInFamily family key=case key of
  CL.NightOfTheZealotKey {}->family=="01"
  CL.TheDunwichLegacyKey {}->family=="02"
  CL.ThePathToCarcosaKey {}->family=="03"
  CL.TheForgottenAgeKey {}->family=="04"
  CL.TheCircleUndoneKey {}->family=="05"
  _->False

-- Existing reviewed forms provide exact fixed record domains; their defaults,
-- forced facts and standalone initialization are NOT executed by this catalog.
evidenceFields :: Text -> Either Text [HistoryField]
evidenceFields family=do
  catalog<-catalogResult
  let chapters=[row|c<-catalogCampaigns catalog,campaignIdentity c==family,ch<-campaignChapters c,row<-chapterRows ch]
      standalone=[row|s<-catalogScenarios catalog,scenarioCampaign s==Just family,row<-scenarioRows s]
      flatten rows=concatMap (\r->r:flatten (rowChildren r)) rows
      make row key storage domain=do
        unless (keyInFamily family key) $ Left "Chapter field escaped its declared history family"
        pure $ HistoryField key [storage] domain [rowId row]
  fmap concat $ for (flatten $ chapters<>standalone) \row->case rowEffect row of
    KeyEffect _ key->pure <$> make row key FlagStorage Nothing
    CountEffect _ key->pure <$> make row key CountStorage Nothing
    SelectedKeyEffect->for (rowChoices row) \choice->do
      key<-first (const "Invalid exact chapter history key") $ parseEither parseJSON $ choiceValue choice
      make row key FlagStorage Nothing
    RecordEffect _ key kind fixed->do
      let values=maybe (map choiceValue $ rowChoices row) pure fixed
      unless (notNull values) $ Left "Chapter record domain is not finite"
      pure <$> make row key SetStorage (Just $ FixedRecords kind values)
    SelectedRecordTarget kind value->for (rowChoices row) \choice->do
      key<-first (const "Invalid selected chapter history key") $ parseEither parseJSON $ choiceValue choice
      make row key SetStorage (Just $ FixedRecords kind [value])
    _->pure []

mergeFields :: [HistoryField] -> Either Text [HistoryField]
mergeFields fields=Map.elems <$> foldM add Map.empty fields
 where
  add current field=case Map.lookup (historyFieldKey field) current of
    Nothing->pure $ Map.insert (historyFieldKey field) field current
    Just previous->do
      domain<-case (historyFieldDomain previous,historyFieldDomain field) of
        (Nothing,other)->pure other
        (other,Nothing)->pure other
        (Just (FixedRecords kind a),Just (FixedRecords kind' b)) | kind==kind'->pure $ Just $ FixedRecords kind $ nub $ a<>b
        (Just a,Just b) | a==b->pure $ Just a
        _->Left "Conflicting historical record value domains"
      pure $ Map.insert (historyFieldKey field) previous
        {historyFieldStorage=nub $ historyFieldStorage previous<>historyFieldStorage field
        ,historyFieldDomain=domain,historyFieldEvidence=nub $ historyFieldEvidence previous<>historyFieldEvidence field} current

profileFor :: Text -> Either Text HistoricalProfile
profileFor actual=do
  family<-historyFamily actual
  keys<-familyKeys family
  oldEvidence<-evidenceFields family
  -- These domains are established by the actual source consumers, not by an
  -- English name heuristic. Harbinger is intentionally BOTH flag and count.
  let flags=[HistoryField key [FlagStorage] Nothing ["fixed-production-key-storage:"<>family]
            |key<-keys,keyName key `notElem` setNames family
            ,keyName key `notElem` countNames family || keyName key=="TheHarbingerIsStillAlive"]
      counts=[HistoryField key [CountStorage] Nothing ["fixed-production-count:"<>family]|key<-keys,keyName key `elem` countNames family]
      field key domain=HistoryField key [SetStorage] (Just domain) ["fixed-production-record-domain:"<>family]
      finite codes=FixedRecords CardRecord $ map (String . ("c"<>)) codes
      exact keyName' domain=[field key domain|key<-keys,keyName key==keyName']
      carcosa=exact "VIPsInterviewed" (finite ["03076a","03077","03078","03079","03080"])
        <>exact "VIPsSlain" (finite ["03065","03066","03067","03068","03069"])
        <>concatMap (`exact` HistoricalInvestigatorCodes) ["ReadActII","Possessed"]
      tfa=exact "TheJungleWatches" TfaVengeanceCodes
      notz=concatMap (\key->exact key $ NotzCultistCodes $ actual=="50") ["CultistsWeInterrogated","CultistsWhoGotAway"]
      prologue=finite ["05046","05047","05048","05049"]
      tcu=concatMap (`exact` prologue) ["MissingPersons","WasTakenByTheWatcher","WasClaimedBySpecters","DisappearedIntoTheMist","WasPulledIntoTheSpectralRealm"]
  let mementos=[MesmerizingFlute,StrangeIncantation,RitualComponents,ScrapOfTornShadow,Gilman'sJournal,Keziah'sFormulae,WornCrucifix,WispOfSpectralMist,CornHuskDoll,BloodyTreeCarvings]
      tcuMementos=exact "MementosDiscovered" $ FixedRecords MementoRecord $ map toJSON mementos
      supplements=case family of "01"->notz;"03"->carcosa;"04"->tfa;"05"->tcu<>tcuMementos;_->[]
      specialized=map historyFieldKey supplements
      withEvidence=[field {historyFieldEvidence=historyFieldEvidence field<>concat
          [historyFieldEvidence previous|previous<-oldEvidence,historyFieldKey previous==historyFieldKey field]}|field<-supplements]
      existing=[row|row<-oldEvidence,historyFieldKey row `notElem` specialized]
  fields<-mergeFields $ flags<>counts<>existing<>withEvidence
  for_ keys \key->unless (any ((==key) . historyFieldKey) fields) $ Left $ "Historical key lacks a typed field domain: "<>actual<>"/"<>keyName key
  let common=[HistoryField key [SetStorage] (Just HistoricalInvestigatorCodes) ["shared-campaign-investigator-record"]
             |key<-[CL.KilledInvestigators,CL.DrivenInsaneInvestigators]]
  pure $ HistoricalProfile actual family $ fields<>common
