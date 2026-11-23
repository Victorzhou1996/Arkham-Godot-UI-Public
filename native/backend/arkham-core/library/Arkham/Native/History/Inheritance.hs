-- Finite, pure compilation of state which is NOT stored in CampaignLog.
-- The policy is built by the trusted history resolver from the actual game;
-- callers supply only offered identities, never Modifier/CampaignStep/metadata.
module Arkham.Native.History.Inheritance where

import Arkham.Prelude
import Arkham.CampaignStep (CampaignStep (..))
import Arkham.Campaigns.TheForgottenAge.Meta qualified as TFA
import Arkham.Campaigns.TheForgottenAge.Supply (Supply)
import Arkham.ChaosToken.Types (ChaosTokenFace)
import Arkham.Id
import Arkham.Modifier (ModifierType)
import Arkham.Resolution (Resolution)
import Arkham.Tarot
import Arkham.Native.History.Types
import Arkham.Native.Settings.Types (FormValue (..))
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text qualified as T

data HistoryStepPolicy = HistoryStepPolicy
  { historyNativeStep :: CampaignStep
  , historyResolutions :: Map Text Resolution
  , historyRequiredPreviousSteps :: [Text]
  }
  deriving stock Show

data InheritancePolicy = InheritancePolicy
  { inheritanceFamily :: Text
  , inheritanceCurrentActors :: Map Text InvestigatorId
  , inheritanceHistoricalActors :: Map Text InvestigatorId
  , inheritanceLeaderActors :: Map Text InvestigatorId
  , inheritanceStepChoices :: Map Text HistoryStepPolicy
  , inheritanceFaceChoices :: Map Text ChaosTokenFace
  , inheritanceDestinyScopes :: Map Text Text
  , inheritanceDestinyGroups :: [[Text]]
  , inheritanceFixedDestiny :: Map Text TarotCardArcana
  , inheritanceDestinyRequired :: Bool
  , inheritanceEffectChoices :: Map Text ModifierType
  }
  deriving stock Show

data InvestigatorHistory = InvestigatorHistory
  { targetExperience :: Int
  , targetPhysicalTrauma :: Int
  , targetMentalTrauma :: Int
  , targetSupplies :: [Supply]
  }
  deriving stock (Show,Eq)

data CompiledInheritance = CompiledInheritance
  { inheritedInvestigators :: Map InvestigatorId InvestigatorHistory
  , inheritedMetadata :: Maybe TFA.Metadata
  , inheritedBag :: Maybe [ChaosTokenFace]
  -- Native Campaign.completeStep prepends: this is already NEWEST FIRST.
  , inheritedSteps :: [CampaignStep]
  , inheritedResolutions :: Map ScenarioId Resolution
  , inheritedDestiny :: Map Text TarotCard
  , inheritedEffects :: [ModifierType]
  , inheritedRetired :: Set InvestigatorId
  }
  deriving stock Show

enumId :: Show a => a -> Text
enumId=T.pack . show

-- These are protocol/product limits, not assertions about original rule maxima.
-- Project them into the same schema which binds the submitted draft.
historyInputLimits :: Value
historyInputLimits=object
  ["namedRows" .= (512::Int),"recordOccurrencesPerField" .= (512::Int)
  ,"investigators" .= (4::Int),"experience" .= (999::Int),"trauma" .= (99::Int)
  ,"suppliesPerKind" .= (99::Int),"supplyOccurrencesPerInvestigator" .= (512::Int)
  ,"bagPerFace" .= (30::Int),"bagTotal" .= (128::Int)
  ,"storyAssignments" .= (128::Int),"storyQuantity" .= (10::Int)]

inheritanceSchema :: InheritancePolicy -> Value
inheritanceSchema InheritancePolicy{..}=object
  ["limits" .= historyInputLimits
  ,"experienceSemantics" .= ("absolute-current-unspent-xp-not-lifetime-earned"::Text)
  ,"actors" .= [object ["id" .= slot,"investigator" .= iid]|(slot,iid)<-Map.toAscList inheritanceCurrentActors]
  ,"retiredIdentities" .= [object ["id" .= slot,"investigator" .= iid]|(slot,iid)<-Map.toAscList inheritanceHistoricalActors]
  ,"supplies" .= [object ["id" .= enumId supply,"supply" .= supply]|supply<-[minBound..maxBound::Supply],inheritanceFamily=="04"]
  ,"metadata" .= metadataRows
  ,"bagFaces" .= [object ["id" .= ident,"face" .= face]|(ident,face)<-Map.toAscList inheritanceFaceChoices]
  ,"steps" .= [object ["id" .= ident,"step" .= historyNativeStep step
      ,"resolutions" .= [object ["id" .= rid,"resolution" .= resolution]|(rid,resolution)<-Map.toAscList $ historyResolutions step]
      ,"requiredPrevious" .= historyRequiredPreviousSteps step]|(ident,step)<-Map.toAscList inheritanceStepChoices]
  ,"completedStepsInputOrder" .= ("chronological-oldest-first"::Text)
  ,"completedStepsStorageOrder" .= ("newest-first"::Text)
  ,"destinyScopes" .= [object (["id" .= ident,"scope" .= scope]
      <>["fixedArcana" .= card|Just card<-[Map.lookup ident inheritanceFixedDestiny]])
      |(ident,scope)<-Map.toAscList inheritanceDestinyScopes]
  ,"tarot" .= [object ["id" .= enumId card,"arcana" .= card]|card<-[minBound..maxBound::TarotCardArcana]]
  ,"destinyRequired" .= inheritanceDestinyRequired,"destinyMustFillAllScopesWhenPresent" .= True
  ,"destinyUniqueArcanaGroups" .= inheritanceDestinyGroups
  ,"permanentEffects" .= [object ["id" .= ident,"effect" .= effect]|(ident,effect)<-Map.toAscList inheritanceEffectChoices]]
 where
  row slot historical field kind=object $ ["id" .= ("tfa:"<>field<>":"<>slot),"kind" .= (kind::Text)
      ,"optional" .= historical] <> ["requiresRetiredIdentity" .= slot|historical]
      <> (if kind=="integer" then ["minimum" .= (0::Int),"maximum" .= (999::Int)] else [])
  metadataRows=if inheritanceFamily/="04" then [] else
    [row slot historical field kind
      |(slots,historical)<-[(Map.keys inheritanceCurrentActors,False),(Map.keys inheritanceHistoricalActors,True)]
      ,slot<-slots,(field,kind)<-[("supply-points","integer"),("yithian","boolean"),("bonus-xp","integer")]]
      <>[object ["id" .= ("tfa:expedition-leader"::Text),"kind" .= ("choice"::Text)
          ,"choices" .= Map.keys inheritanceLeaderActors,"nullable" .= True]]

compileInheritance :: InheritancePolicy -> HistoricalDraft -> Either Text CompiledInheritance
compileInheritance policy@InheritancePolicy{..} draft=do
  unless (Map.keys (historyInvestigators draft)==Map.keys inheritanceCurrentActors) $
    Left "Historical target totals must name exactly the actual current roster"
  actors<-for (Map.toAscList $ historyInvestigators draft) \(slot,values)->do
    iid<-known "current investigator slot" slot inheritanceCurrentActors
    unless (historyExperience values>=0 && historyExperience values<=999
      && historyPhysicalTrauma values>=0 && historyPhysicalTrauma values<=99
      && historyMentalTrauma values>=0 && historyMentalTrauma values<=99) $
        Left "Historical target totals exceed product input limits"
    supplies<-compileSupplies inheritanceFamily $ historySupplies values
    pure (iid,InvestigatorHistory (historyExperience values) (historyPhysicalTrauma values) (historyMentalTrauma values) supplies)
  unless (length (nub $ map fst actors)==length actors) $ Left "Historical roster contains duplicate native identities"
  retired<-traverse (\ident->known "retired investigator identity" ident inheritanceHistoricalActors) $ historyRetiredIdentities draft
  unless (length retired==length (nub retired) && all (`notElem` Map.elems inheritanceCurrentActors) retired) $
    Left "Historical retired identities duplicate a current or retired investigator"
  metadata<-compileMetadata policy (Set.fromList $ historyRetiredIdentities draft) $ historyMetadata draft
  bag<-for (historyBag draft) \counts->do
    faces<-for (Map.toAscList counts) \(ident,count)->do
      face<-known "chaos face" ident inheritanceFaceChoices
      unless (count>=0 && count<=30) $ Left "Historical chaos face count exceeds product input limit"
      pure $ replicate count face
    let tokens=concat faces
    unless (notNull tokens && length tokens<=128) $ Left "Historical chaos bag must contain one to 128 tokens"
    pure tokens
  (_,chronological,resolutions)<-foldM compileStep ([],[],Map.empty) $ historySteps draft
  let requestedScopes=Map.keys $ historyDestiny draft
      expectedScopes=Map.keys inheritanceDestinyScopes
      grouped=concat inheritanceDestinyGroups
  unless (sort grouped==expectedScopes && length grouped==length (nub grouped)
    && all (`elem` expectedScopes) (Map.keys inheritanceFixedDestiny)
    && (not inheritanceDestinyRequired || notNull expectedScopes)) $
    Left "Invalid trusted destiny grouping policy"
  unless ((null requestedScopes && not inheritanceDestinyRequired) || requestedScopes==expectedScopes) $
    Left "Historical destiny must be absent when optional or fill its entire scope set"
  destiny<-for (Map.toAscList $ historyDestiny draft) \(ident,value)->do
    scope<-known "destiny scope" ident inheritanceDestinyScopes
    arcana<-known "tarot arcana" (historyTarotId value) $ Map.fromList [(enumId card,card)|card<-[minBound..maxBound]]
    for_ (Map.lookup ident inheritanceFixedDestiny) \fixed->unless (arcana==fixed) $
      Left "Historical destiny may rotate, but cannot reassign this campaign's original fixed arcana"
    facing<-case historyTarotFacing value of "upright"->Right Upright;"reversed"->Right Reversed;_->Left "Unknown tarot facing"
    pure (scope,TarotCard facing arcana)
  unless (length (nub $ map fst destiny)==length destiny) $ Left "Historical destiny scopes alias one another"
  for_ inheritanceDestinyGroups \group->do
    let arcana=[historyTarotId tarot|ident<-group,Just tarot<-[Map.lookup ident $ historyDestiny draft]]
    unless (length arcana==length (nub arcana)) $ Left "Historical destiny repeats an arcana in the same deck"
  effects<-traverse (\ident->known "permanent effect" ident inheritanceEffectChoices) $ historyPermanentEffects draft
  unless (length effects==length (nub $ historyPermanentEffects draft)) $ Left "Duplicate historical permanent effect"
  pure $ CompiledInheritance (Map.fromList actors) metadata bag (reverse chronological) resolutions (Map.fromList destiny) effects (Set.fromList retired)
 where
  compileStep (seen,previous,resolutions) (ident,resolutionId)=do
    unless (ident `notElem` seen) $ Left "Duplicate historical completed step"
    step<-known "completed step" ident inheritanceStepChoices
    unless (all (`elem` seen) $ historyRequiredPreviousSteps step) $ Left "Historical completed-step dependency/order is not satisfied"
    resolution<-traverse (\rid->known "scenario resolution" rid $ historyResolutions step) resolutionId
    let native=historyNativeStep step
        scenario=case native of
          ScenarioStep sid->Just sid
          ScenarioStepWithOptions sid _->Just sid
          StandaloneScenarioStep sid _->Just sid
          StandaloneScenarioStepWithOptions sid _ _->Just sid
          _->Nothing
    unless (isNothing resolution || isJust scenario) $ Left "A non-scenario completed step cannot carry a resolution"
    unless (toJSON native `notElem` map toJSON previous) $ Left "Two historical step identities alias the same native completed step"
    -- A scenario may occur at different explicit step variants. The later
    -- chronological occurrence wins, as original Runner's Map.insert does.
    let nextResolutions=case (scenario,resolution) of
          (Just sid,Just value)->Map.insert sid value resolutions
          _->resolutions
    pure (seen<>[ident],previous<>[native],nextResolutions)

compileSupplies :: Text -> Map Text Int -> Either Text [Supply]
compileSupplies family counts=do
  unless (family=="04" || Map.null counts) $ Left "Supplies are not offered for this campaign"
  items<-for (Map.toAscList counts) \(ident,count)->do
    supply<-known "TFA supply" ident $ Map.fromList [(enumId value,value)|value<-[minBound..maxBound]]
    unless (count>=0 && count<=99) $ Left "Historical supply count exceeds product input limit"
    pure $ replicate count supply
  let expanded=concat items
  unless (length expanded<=512) $ Left "Historical supply list exceeds product input limit"
  pure expanded

compileMetadata :: InheritancePolicy -> Set Text -> Map Text FormValue -> Either Text (Maybe TFA.Metadata)
compileMetadata InheritancePolicy{..} retiredSlots values
  | inheritanceFamily/="04"=do
      unless (Map.null values) $ Left "No historical metadata adapter for this campaign"
      pure Nothing
  | otherwise=do
      let current=Map.toAscList inheritanceCurrentActors
          historical=[(slot,iid)|(slot,iid)<-Map.toAscList inheritanceHistoricalActors,slot `member` retiredSlots]
          actors=current<>historical
          fields=["supply-points","yithian","bonus-xp"]
          ids entries=["tfa:"<>field<>":"<>slot|(slot,_)<-entries,field<-fields]<>["tfa:expedition-leader"]
          required=ids current
          allowed=ids actors
          supplied=Map.keys values
          present field=[(slot,iid)|(slot,iid)<-actors,Map.member ("tfa:"<>field<>":"<>slot) values]
      unless (all (`elem` supplied) required && all (`elem` allowed) supplied) $
        Left "TFA metadata omits a current field or names an unselected historical identity"
      points<-for (present "supply-points") \(slot,iid)->(iid,) <$> integer ("tfa:supply-points:"<>slot)
      bonuses<-for (present "bonus-xp") \(slot,iid)->(iid,) <$> integer ("tfa:bonus-xp:"<>slot)
      yithians<-fmap catMaybes $ for (present "yithian") \(slot,iid)->lookupValue ("tfa:yithian:"<>slot) >>= \case
        FormBoolean enabled->pure $ if enabled then Just iid else Nothing
        _->Left "TFA Yithian history requires a boolean"
      leader<-lookupValue "tfa:expedition-leader" >>= \case
        FormChoice ident->traverse (\slot->do
          unless (Map.member slot inheritanceCurrentActors || slot `member` retiredSlots) $ Left "Historical expedition leader is not selected"
          known "TFA expedition leader eligible at this start" slot inheritanceLeaderActors) ident
        _->Left "TFA expedition leader requires an offered investigator choice"
      pure $ Just $ TFA.Metadata (Map.fromList points) (Set.fromList yithians) leader (Map.fromList bonuses)
 where
  lookupValue ident=known "TFA metadata field" ident values
  integer ident=lookupValue ident >>= \case
    FormInteger n | n>=0 && n<=999->pure n
    _->Left "TFA historical metadata requires a nonnegative integer within product input limits"

known :: Text -> Text -> Map Text a -> Either Text a
known label ident=maybe (Left $ "Unknown historical "<>label) Right . Map.lookup ident
