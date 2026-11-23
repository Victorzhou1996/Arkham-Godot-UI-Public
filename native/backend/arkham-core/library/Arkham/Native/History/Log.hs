{-# LANGUAGE GADTs #-}
-- Pure compilation of the seven real CampaignLog fields. This module does not
-- run a Message, generate a card, consume RNG, mutate a game, or open storage.
module Arkham.Native.History.Log where

import Arkham.Prelude
import Arkham.CampaignLog
import Arkham.CampaignLogKey
import Arkham.Campaigns.TheCircleUndone.Key qualified as U
import Arkham.Campaign.Option (CampaignOption)
import Arkham.Card.CardCode
import Arkham.Card.CardDef (CardDef (..))
import Arkham.Card.CardType (CardType (EnemyType))
import Arkham.EncounterCard (allEncounterCards)
import Arkham.EncounterSet qualified as ES
import Arkham.Trait qualified as Trait
import Arkham.PlayerCard (allPlayerCards)
import Arkham.Native.History.Types
import Arkham.Native.History.Profile
import Arkham.Native.Settings.Definition (RecordKind (..),typedRecord)
import Crypto.Hash.SHA256 qualified as SHA
import Data.ByteString.Base16 qualified as B16
import Data.ByteString.Lazy qualified as BL
import Data.Aeson.Types (parseEither)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text qualified as T

data ResolvedHistoryField = ResolvedHistoryField
  { resolvedHistoryField :: HistoryField
  , resolvedHistoryEntries :: [(Text,SomeRecorded)]
  }
  deriving stock Show

data HistoryPartnerPolicy = HistoryPartnerPolicy
  { historyPartnerCode :: CardCode
  , historyPartnerMaximumDamage :: Int
  , historyPartnerMaximumHorror :: Int
  , historyPartnerAllowedStatuses :: [PartnerStatus]
  }
  deriving stock Show

data HistoryLogPolicy = HistoryLogPolicy
  { resolvedHistoryFields :: Map Text ResolvedHistoryField
  , resolvedHistoryOptions :: Map Text CampaignOption
  , resolvedHistoryPartners :: Map Text HistoryPartnerPolicy
  }
  deriving stock Show

resolveHistoryFields :: HistoricalProfile -> [CardCode] -> Either Text (Map Text ResolvedHistoryField)
resolveHistoryFields profile investigatorCodes=do
  fields<-for (historyProfileFields profile) \field->do
    values<-case historyFieldDomain field of
      Nothing->pure []
      Just (FixedRecords kind fixed)->traverse (makeRecord kind) fixed
      Just HistoricalInvestigatorCodes->traverse (makeRecord CardRecord . toJSON) investigatorCodes
      Just TfaVengeanceCodes->traverse (makeRecord CardRecord . toJSON . cdCardCode) vengeance
      Just (NotzCultistCodes includeReturn)->traverse (makeRecord CardRecord . toJSON . cdCardCode) $ cultists includeReturn
    let entries=[(recordEntryId record,record)|record<-values]
    unless (length (nub $ map fst entries)==length entries) $ Left "Duplicate native history value identity"
    pure (historyFieldId field,ResolvedHistoryField field entries)
  unless (length (nub $ map fst fields)==length fields) $ Left "Duplicate native history field identity"
  pure $ Map.fromList fields
 where
  makeRecord kind value=first (const "Invalid fixed typed history record") $ parseEither (const $ typedRecord kind False value) ()
  vengeance=[def|def<-Map.elems $ allEncounterCards<>allPlayerCards,isJust $ cdVengeancePoints def
                 ,let code=unCardCode $ cdCardCode def,any (`T.isPrefixOf` code) ["04","53"]]
  -- MidnightMasks records actual unique Cultists from its victory display,
  -- remaining play area and dedicated deck. Return-To adds three pool cards
  -- and the alternative agenda enemy; the old standalone form lists only six.
  cultists includeReturn=[def|def<-Map.elems allEncounterCards,cdCardType def==EnemyType,cdUnique def,Trait.Cultist `member` cdCardTraits def
      ,cdEncounterSet def `elem` map Just ([ES.CultOfUmordhoth,ES.TheMidnightMasks]
        <>[value|includeReturn,value<-[ES.ReturnCultOfUmordhoth,ES.ReturnToTheMidnightMasks]])]

recordEntryId :: SomeRecorded -> Text
recordEntryId record="record:"<>decodeUtf8 (B16.encode $ SHA.hash $ BL.toStrict $ encode record)

historyLogSchema :: HistoryLogPolicy -> Value
historyLogSchema HistoryLogPolicy{..}=object
  ["fields" .= [object ["id" .= ident,"key" .= keyName (historyFieldKey field)
      ,"storage" .= map storageName (historyFieldStorage field)
      ,"minimumCount" .= (0::Int),"maximumCount" .= (maxBound::Int)
      ,"recordsAreOrderedList" .= True
      ,"entries" .= [object ["id" .= entryId,"record" .= record]|(entryId,record)<-resolvedHistoryEntries resolved]
      ,"evidence" .= historyFieldEvidence field]
    |(ident,resolved)<-Map.toAscList resolvedHistoryFields,let field=resolvedHistoryField resolved]
  ,"options" .= [object ["id" .= ident,"option" .= option]|(ident,option)<-Map.toAscList resolvedHistoryOptions]
  ,"partners" .= [object ["id" .= ident,"cardCode" .= historyPartnerCode policy
      ,"minimumDamage" .= (0::Int),"minimumHorror" .= (0::Int)
      ,"maximumDamage" .= min 99 (historyPartnerMaximumDamage policy),"maximumHorror" .= min 99 (historyPartnerMaximumHorror policy)
      ,"statuses" .= historyPartnerAllowedStatuses policy]|(ident,policy)<-Map.toAscList resolvedHistoryPartners]
  ]
 where
  storageName= \case FlagStorage->"flags"::Text;CountStorage->"count";SetStorage->"records"

compileHistoricalLog :: HistoryLogPolicy -> CampaignLog -> CampaignLog -> HistoryLogDraft -> Either Text CampaignLog
compileHistoricalLog policy base forced draft=do
  flagged<-foldM flags base $ Map.toAscList $ historyFlags draft
  counted<-foldM counts flagged $ Map.toAscList $ historyCounts draft
  recordedSets<-foldM records counted $ Map.toAscList $ historySets draft
  ordered<-traverse (fmap (historyFieldKey . resolvedHistoryField) . knownField) $ historyOrderedKeys draft
  withOptions<-foldM options (recordedSets {campaignLogOrderedKeys=ordered}) $ Map.toAscList $ historyOptions draft
  compiled<-foldM partners withOptions $ Map.toAscList $ historyPartners draft
  checkForcedLog forced compiled
  checkHistoryOrder base forced compiled
  checkTcuFates policy compiled
  pure compiled
 where
  knownField ident=maybe (Left "Unknown historical log field") Right $ Map.lookup ident $ resolvedHistoryFields policy
  offered ident storage=do
    field<-knownField ident
    unless (storage `elem` historyFieldStorage (resolvedHistoryField field)) $ Left "Historical log field does not permit this storage type"
    pure field
  flags current (ident,values)=do
    field<-offered ident FlagStorage
    let key=historyFieldKey $ resolvedHistoryField field
        set membership collection=if membership then Set.insert key collection else Set.delete key collection
    pure current {campaignLogRecorded=set (historyRecorded values) $ campaignLogRecorded current
      ,campaignLogCrossedOut=set (historyCrossedOut values) $ campaignLogCrossedOut current}
  counts current (ident,value)=do
    field<-offered ident CountStorage
    unless (value>=0) $ Left "Historical counts must be nonnegative core integers"
    pure current {campaignLogRecordedCounts=Map.insert (historyFieldKey $ resolvedHistoryField field) value $ campaignLogRecordedCounts current}
  records current (ident,values)=do
    field<-offered ident SetStorage
    entries<-for values \value->do
      actual<-maybe (Left "Unknown historical recorded value") Right $ lookup (historyEntryId value) $ resolvedHistoryEntries field
      pure $ changeRecord value actual
    -- Preserve duplicates, original list order, crossed state AND circling.
    pure current {campaignLogRecordedSets=Map.insert (historyFieldKey $ resolvedHistoryField field) entries $ campaignLogRecordedSets current}
  options current (ident,enabled)=do
    actual<-maybe (Left "Unknown historical campaign option") Right $ Map.lookup ident $ resolvedHistoryOptions policy
    pure current {campaignLogOptions=(if enabled then Set.insert actual else Set.delete actual) $ campaignLogOptions current}
  partners current (ident,value)=do
    actual<-maybe (Left "Unknown historical partner") Right $ Map.lookup ident $ resolvedHistoryPartners policy
    unless (historyPartnerDamage value>=0 && historyPartnerDamage value<=min 99 (historyPartnerMaximumDamage actual)
      && historyPartnerHorror value>=0 && historyPartnerHorror value<=min 99 (historyPartnerMaximumHorror actual)
      && historyPartnerStatus value `elem` historyPartnerAllowedStatuses actual) $ Left "Historical partner values exceed this campaign's policy"
    pure current {campaignLogPartners=Map.insert (historyPartnerCode actual)
      (CampaignLogPartner (historyPartnerDamage value) (historyPartnerHorror value) $ historyPartnerStatus value) $ campaignLogPartners current}

changeRecord :: HistoryRecord -> SomeRecorded -> SomeRecorded
changeRecord value (SomeRecorded kind current)=SomeRecorded kind $ applyCircle $ state $ payload current
 where
  payload :: Recorded a -> a
  payload (Recorded actual)=actual
  payload (CrossedOut actual)=actual
  payload (Circled actual)=payload actual
  state :: a -> Recorded a
  state actual=case historyRecordState value of HistoricalRecorded->Recorded actual;HistoricalCrossedOut->CrossedOut actual
  applyCircle :: Recorded a -> Recorded a
  applyCircle actual=if historyRecordCircled value then Circled actual else actual

-- The wizard starts with a lossless editable projection of the actual chapter
-- result. Unknown stored facts fail visibly: never silently discard them to
-- manufacture an apparently complete history form. Empty optional fields are
-- omitted, while an explicitly stored zero/empty record list is retained.
draftFromLog :: HistoryLogPolicy -> CampaignLog -> Either Text HistoryLogDraft
draftFromLog policy logValue=do
  let fields=resolvedHistoryFields policy
      byKey=Map.fromList [(historyFieldKey $ resolvedHistoryField field,ident)|(ident,field)<-Map.toList fields]
      fieldId key=maybe (Left "Stored chapter key lacks a historical field") Right $ Map.lookup key byKey
      flagKeys=Set.toList $ campaignLogRecorded logValue<>campaignLogCrossedOut logValue
  flags<-for flagKeys \key->do
    ident<-fieldId key
    pure (ident,HistoryFlags (key `member` campaignLogRecorded logValue) (key `member` campaignLogCrossedOut logValue))
  counts<-for (Map.toList $ campaignLogRecordedCounts logValue) \(key,value)->(,value) <$> fieldId key
  sets<-for (Map.toList $ campaignLogRecordedSets logValue) \(key,records)->do
    ident<-fieldId key
    field<-maybe (Left "Missing resolved chapter field") Right $ Map.lookup ident fields
    values<-for records \record->do
      entry<-maybe (Left "Stored chapter record lacks a historical value") (Right . fst) $
        find (\(_,offered)->withoutCircle offered==withoutCircle record
          || withoutCircle offered==withoutCircle (asRecorded record)) $ resolvedHistoryEntries field
      pure $ HistoryRecord entry (state record) (circled record)
    pure (ident,values)
  ordered<-traverse fieldId $ campaignLogOrderedKeys logValue
  options<-for (Set.toList $ campaignLogOptions logValue) \option->do
    ident<-maybe (Left "Stored chapter option lacks a finite policy") (Right . fst) $
      find ((==option) . snd) $ Map.toList $ resolvedHistoryOptions policy
    pure (ident,True)
  partners<-for (Map.toList $ campaignLogPartners logValue) \(code,value)->do
    ident<-maybe (Left "Stored chapter partner lacks a finite policy") (Right . fst) $
      find (cardCodeExactEq code . historyPartnerCode . snd) $ Map.toList $ resolvedHistoryPartners policy
    pure (ident,HistoryPartner (campaignLogPartnerDamage value) (campaignLogPartnerHorror value) $ campaignLogPartnerStatus value)
  let draft=HistoryLogDraft (Map.fromList flags) (Map.fromList counts) (Map.fromList sets) ordered (Map.fromList options) (Map.fromList partners)
  roundtrip<-compileHistoricalLog policy logValue mempty draft
  unless (toJSON roundtrip==toJSON logValue) $ Left "Historical chapter projection was not lossless"
  pure draft
 where
  state (SomeRecorded _ value)=go value
   where
    go :: Recorded a -> HistoryRecordState
    go (Circled inner)=go inner
    go (Recorded _)=HistoricalRecorded
    go (CrossedOut _)=HistoricalCrossedOut
  circled (SomeRecorded _ value)=case value of Circled _->True;_->False
  asRecorded (SomeRecorded kind value)=SomeRecorded kind $ go value
   where
    go :: Recorded a -> Recorded a
    go (Circled inner)=go inner
    go (CrossedOut actual)=Recorded actual
    go (Recorded actual)=Recorded actual

eraseCircle :: Recorded a -> Recorded a
eraseCircle (Circled value)=eraseCircle value
eraseCircle value=value

withoutCircle :: SomeRecorded -> Value
withoutCircle (SomeRecorded kind value)=toJSON $ SomeRecorded kind $ eraseCircle value

checkForcedLog :: CampaignLog -> CampaignLog -> Either Text ()
checkForcedLog forced actual=do
  let require condition=unless condition $ Left "Historical edits conflict with a forced chapter result"
  require $ campaignLogRecorded forced `Set.isSubsetOf` campaignLogRecorded actual
  require $ campaignLogCrossedOut forced `Set.isSubsetOf` campaignLogCrossedOut actual
  for_ (Map.toList $ campaignLogRecordedCounts forced) \(key,value)->require $ Map.lookup key (campaignLogRecordedCounts actual)==Just value
  for_ (Map.toList $ campaignLogRecordedSets forced) \(key,values)->do
    let present=Map.findWithDefault [] key $ campaignLogRecordedSets actual
    -- Ordered, consuming matching preserves physical-card multiplicity. A
    -- compulsory circle cannot be erased, while a player may circle an
    -- otherwise identical un-circled compulsory entry.
    require $ subsequenceBy forcedRecordMatches values present
  require $ campaignLogOptions forced `Set.isSubsetOf` campaignLogOptions actual
  for_ (Map.toList $ campaignLogPartners forced) \(key,value)->require $ Map.lookup key (campaignLogPartners actual)==Just value

forcedRecordMatches :: SomeRecorded -> SomeRecorded -> Bool
forcedRecordMatches required actual=withoutCircle required==withoutCircle actual
  && (not (circled required) || circled actual)
 where
  circled :: SomeRecorded -> Bool
  circled (SomeRecorded _ (Circled _))=True
  circled _=False

subsequenceBy :: (a -> b -> Bool) -> [a] -> [b] -> Bool
subsequenceBy _ [] _=True
subsequenceBy _ _ []=False
subsequenceBy matches wanted@(required:rest) (actual:remaining)
  | matches required actual=subsequenceBy matches rest remaining
  | otherwise=subsequenceBy matches wanted remaining

-- The wire order is the final complete order. The source chapter's existing
-- sequence and forced sequence must survive as subsequences. New display keys
-- must actually exist in one of the supported log stores; no phantom history.
checkHistoryOrder :: CampaignLog -> CampaignLog -> CampaignLog -> Either Text ()
checkHistoryOrder base forced actual=do
  let order=campaignLogOrderedKeys actual
      required=campaignLogOrderedKeys base
      exists key=key `member` campaignLogRecorded actual || key `member` campaignLogCrossedOut actual
        || Map.member key (campaignLogRecordedCounts actual) || Map.member key (campaignLogRecordedSets actual)
  unless (length order==length (nub order)
    && subsequenceBy (==) required order
    && subsequenceBy (==) (campaignLogOrderedKeys forced) order
    && all (\key->key `elem` required || exists key) order) $
      Left "Historical display order removes/reorders chapter keys or introduces an absent record"

-- This named constraint is TCU-specific, not a global rule that would discard
-- valid duplicate vengeance cards. MissingPersons always retains all four
-- initial identities; original play crosses out the unselected people. An
-- active fate therefore needs its matching NOT-crossed missing-person entry.
checkTcuFates :: HistoryLogPolicy -> CampaignLog -> Either Text ()
checkTcuFates policy logValue=for_ (Map.lookup "log:MissingPersons" $ resolvedHistoryFields policy) \field->do
  let active key=[withoutCircle record|record<-Map.findWithDefault [] (TheCircleUndoneKey key)
                    (campaignLogRecordedSets logValue),isActive record]
      missing=active U.MissingPersons
      groups=map active [U.WasTakenByTheWatcher,U.WasClaimedBySpecters,U.DisappearedIntoTheMist,U.WasPulledIntoTheSpectralRealm]
      values=concat groups
      canonical record=toJSON $ changeRecord (HistoryRecord "" HistoricalRecorded False) record
      expected=map (canonical . snd) $ resolvedHistoryEntries field
      present=map canonical $ Map.findWithDefault [] (TheCircleUndoneKey U.MissingPersons) $ campaignLogRecordedSets logValue
  unless (length present==length expected && all (\entry->length (filter (==entry) present)==1) expected) $
    Left "TCU MissingPersons must preserve all four identities exactly once, recording or crossing out each"
  unless (length values==length (nub values) && all (`elem` missing) values) $
    Left "TCU history assigns conflicting fates or a fate to an absent/crossed-out missing person"
 where
  isActive (SomeRecorded _ value)=go value
  go :: Recorded a -> Bool
  go (Recorded _)=True
  go (CrossedOut _)=False
  go (Circled value)=go value
