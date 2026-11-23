-- Trusted, pure preparation of one historical initial-settings transaction.
-- No Session import, random generation, hidden continuation replay or generic
-- JSON patch lives in the schema/preview path. Materialization is separate.
module Arkham.Native.History.Engine
  ( ResolvedHistory, HistoricalPlan, resolveHistory, historyIdentity, historyView
  , chapterPreview, prepareDraft, compileHistory, planView, planGame, materialize
  , validateCreation, validateBootstrap
  ) where

import Arkham.Prelude
import Arkham.Campaign.Types (Campaign,CampaignAttrs (..))
import Arkham.Campaign.Option (CampaignOption (..))
import Arkham.CampaignStep (CampaignStep (..))
import Arkham.CampaignLog
import Arkham.Card
import Arkham.ChaosToken.Types (allChaosTokenFaces)
import Arkham.ClassSymbol (ClassSymbol)
import Arkham.Classes.Entity (toAttrs,toId,overAttrs)
import Arkham.Entities (Entities (..))
import Arkham.Game (Game (..))
import Arkham.GameEnv (runWithEnv)
import Arkham.Helpers (Deck (..))
import Arkham.Helpers.Modifiers (scenarioSetupModifier)
import Arkham.Id
import Arkham.Investigator (allInvestigators)
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Message (Message (CampaignStep),pattern SetCampaignChaosBag)
import Arkham.Modifier (ModifierType (StartingResources,StartingHand))
import Arkham.Source (Source (CampaignSource))
import Arkham.Question
import Arkham.Resolution (Resolution)
import Arkham.Native.Env (CoreM)
import Arkham.Native.History.Types
import Arkham.Native.History.Profile
import Arkham.Native.History.Log
import Arkham.Native.History.Inheritance
import Arkham.Native.History.Manifest qualified as Manifest
import Arkham.Native.History.Story qualified as Story
import Arkham.Native.Settings.Engine qualified as Chapter
import Arkham.Native.Settings.Types (SettingsFormRequest,FormValue (..))
import Arkham.Native.SourceIdentity qualified as Source
import Crypto.Hash.SHA256 qualified as SHA
import Data.ByteString.Base16 qualified as B16
import Data.ByteString.Lazy qualified as BL
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set

data ResolvedHistory = ResolvedHistory
  { historyIdentity :: Text
  , boundGame :: Game
  , boundBootstrap :: HistoricalBootstrap
  , boundActors :: Map Text (InvestigatorId,ClassSymbol)
  , boundRetired :: Map Text InvestigatorId
  , boundChapter :: Chapter.ResolvedSchema
  , boundLogPolicy :: HistoryLogPolicy
  , boundInheritance :: InheritancePolicy
  }

data HistoricalPlan = HistoricalPlan
  { planGame :: Game
  , planStory :: Story.Plan
  , planInheritance :: CompiledInheritance
  , planLodgeSetupBeneficiaries :: [InvestigatorId]
  }

digest :: ToJSON a => a -> Text
digest=decodeUtf8 . B16.encode . SHA.hash . BL.toStrict . encode

validateCreation :: CampaignId -> ScenarioId -> Either Text HistoricalBootstrap
validateCreation cid sid=do
  void $ Manifest.choicesForStart (unCampaignId cid) (unCardCode $ unScenarioId sid)
  unless (sid `notElem` ["05043","54016"]) $
    Left "The special prologue uses its original fresh-game route, not historical initial settings"
  pure $ HistoricalBootstrap cid sid HistoryPending

validateBootstrap :: HistoricalBootstrap -> Game -> Either Text ()
validateBootstrap bootstrap game=do
  campaign<-case gameMode game of
    This c->Right c
    These c _ | bootstrapPhase bootstrap==HistoryInstalled->Right c
    _->Left "Historical bootstrap is not bound to a campaign-only initial setup"
  unless ((toAttrs campaign).campaignId==bootstrapCampaign bootstrap) $ Left "Historical bootstrap campaign mismatch"
  when (bootstrapPhase bootstrap==HistoryPending) do
    unless ((toAttrs campaign).campaignStep==ScenarioStep (bootstrapStart bootstrap)
      && null ((toAttrs campaign).campaignCompletedSteps)
      && all null (Map.elems $ campaignStoryCards $ toAttrs campaign)) $
        Left "Pending historical bootstrap has passed or changed its initial setup boundary"
    void $ validateCreation (bootstrapCampaign bootstrap) (bootstrapStart bootstrap)

campaignOnly :: Game -> Either Text Campaign
campaignOnly game=case gameMode game of This campaign->Right campaign;_->Left "Historical installation requires a campaign before scenario setup"

unwrap :: Question Message -> Question Message
unwrap= \case
  QuestionLabel _ _ q->unwrap q
  PayCostQuestion _ q->unwrap q
  QuestionWithSource _ _ q->unwrap q
  q->q

resolveHistory :: Game -> [Message] -> Word64 -> HistoricalBootstrap -> PlayerId -> Either Text ResolvedHistory
resolveHistory game queue revision bootstrap player=do
  validateBootstrap bootstrap game
  unless (bootstrapPhase bootstrap==HistoryPending) $ Left "Historical state was already installed"
  unless (player `elem` gamePlayers game && fmap unwrap (Map.lookup player $ gameQuestion game)==Just PickCampaignSettings) $
    Left "This player is not at the original historical initial-settings question"
  unless (Map.null $ gameSimultaneousAsks game) $ Left "Finish every seat's deck setup before installing historical state"
  campaign<-campaignOnly game
  let attrs=toAttrs campaign
      originalTail=any (\case CampaignStep step->step==ScenarioStep (bootstrapStart bootstrap);_->False) queue
  unless originalTail $ Left "The original initial CampaignStep continuation is missing"
  chapter<-Chapter.resolveHistoricalSchema game player
  let actual=unCampaignId $ bootstrapCampaign bootstrap
      start=unCardCode $ unScenarioId $ bootstrapStart bootstrap
  family<-historyFamily actual
  profile<-profileFor actual
  actors<-for (zip [1::Int ..] $ gamePlayers game) \(seat,pid)->do
    actor<-case filter ((==pid) . investigatorPlayerId . toAttrs) $ Map.elems $ entitiesInvestigators $ gameEntities game of
      [value]->Right value
      _->Left "Historical roster must contain one actual investigator per current seat"
    let iid=toId actor
    unless (Map.member iid $ campaignDecks attrs) $ Left "A current historical investigator has not completed deck setup"
    pure ("seat:"<>tshow seat,(iid,investigatorClass $ toAttrs actor))
  let actorMap=Map.fromList actors
      current=map (fst . snd) actors
      retired=Map.fromList [("retired:"<>unCardCode code,InvestigatorId code)|code<-Map.keys allInvestigators,InvestigatorId code `notElem` current]
  unless (notNull actors && length actors<=4 && length (nub current)==length current) $ Left "Invalid historical roster"
  fields<-resolveHistoryFields profile $ map unInvestigatorId $ current<>Map.elems retired
  steps<-Manifest.choicesForStart actual start
  let options=case family of
        "01"->[AddLitaChantler]
        "02"->[Cheated,TakeArmitage,TakeWarrenRice,TakeFrancisMorgan,TakeZebulonWhately,TakeEarlSawyer,TakePowderOfIbnGhazi,TakeTheNecronomicon,AddAcrossSpaceAndTime]
        "05"->[TakeBlackBook,TakePuzzleBox,ProceedToInterlude3]
        _->[]
      logPolicy=HistoryLogPolicy fields (Map.fromList [("option:"<>digest option,option)|option<-options]) mempty
      fixed=Manifest.fixedDestiny actual
      scopes=Map.mapWithKey (\ident _->ident) fixed
      leaderActors=Map.map fst actorMap<>(if start `elem` ["53016","53017"] then mempty else retired)
      inheritance=InheritancePolicy family (Map.map fst actorMap) retired leaderActors steps
        (Map.fromList [("face:"<>tshow face,face)|face<-allChaosTokenFaces]) scopes
        [Map.keys scopes|not $ Map.null scopes] fixed False mempty
      identity="arkham-historical-start:"<>digest (Source.coreSourceId,revision,game,queue,bootstrap,player)
  pure $ ResolvedHistory identity game bootstrap actorMap retired chapter logPolicy inheritance

historyView :: ResolvedHistory -> Value
historyView resolved=object
  ["schemaVersion" .= (1::Int),"schemaId" .= historyIdentity resolved,"supported" .= True
  ,"answerTag" .= ("HistoricalSettingsAnswer"::Text),"chapterForm" .= Chapter.schemaView (boundChapter resolved)
  ,"log" .= historyLogSchema (boundLogPolicy resolved),"inheritance" .= inheritanceSchema (boundInheritance resolved)
  ,"outcomeLabels" .= Map.fromList [(ident,Manifest.outcomeLabel ident)
      |step<-Map.elems $ inheritanceStepChoices $ boundInheritance resolved,ident<-Map.keys $ historyResolutions step]
  ,"lodgeSetup" .= object
      ["supported" .= (bootstrapCampaign (boundBootstrap resolved)=="54")
      ,"requiresCompletedStep" .= ("interlude:4"::Text)
      ,"requiresRecordedFlag" .= ("log:TheInvestigatorsAssumedControlOfTheSilverTwilightLodge"::Text)
      ,"maximumBeneficiaries" .= (4::Int),"defaultBeneficiaries" .= ([]::[Text])
      ,"targetsAreInvestigatorIdentitiesNotFutureSeatOccupants" .= True]
  ,"historyMode" .= ("advanced-final-state-editor-not-a-replay-of-past-rule-outcomes"::Text)
  ,"prepareRequired" .= True,"chapterChoicesMustBeCompleteBeforePrepare" .= True]

chapterPreview :: ResolvedHistory -> SettingsFormRequest -> Either Text Value
chapterPreview resolved=Chapter.previewForm $ boundChapter resolved

-- Chapter edits are explicitly re-prepared. They never silently rewrite a
-- user's final historical log, nor run card-generation or option handlers.
chapterLogs :: ResolvedHistory -> SettingsFormRequest -> Either Text (CampaignLog,CampaignLog)
chapterLogs resolved form=do
  (compiled,forced)<-Chapter.compileFormWithForced (boundChapter resolved) form
  campaign<-campaignOnly $ boundGame resolved
  let initial=campaignLog $ toAttrs campaign
      merged=compiled<>initial
  pure (merged {campaignLogOrderedKeys=nub $ campaignLogOrderedKeys initial<>campaignLogOrderedKeys compiled},forced)

prepareDraft :: ResolvedHistory -> SettingsFormRequest -> Either Text Value
prepareDraft resolved form=do
  (base,_)<-chapterLogs resolved form
  logDraft<-draftFromLog (boundLogPolicy resolved) base
  let target (iid,_)=
        let actor=toAttrs $ fromJustNote "validated history actor" $
              Map.lookup iid $ entitiesInvestigators $ gameEntities $ boundGame resolved
         in HistoryInvestigator (investigatorXp actor) (investigatorPhysicalTrauma actor) (investigatorMentalTrauma actor)
              (Map.fromListWith (+) [(enumId supply,1)|supply<-investigatorSupplies actor])
      targets=Map.map target $ boundActors resolved
      metadata=if inheritanceFamily (boundInheritance resolved)/="04" then mempty else Map.fromList $
        [("tfa:"<>field<>":"<>slot,value)|slot<-Map.keys targets,(field,value)<-
          [("supply-points",FormInteger 0),("yithian",FormBoolean False),("bonus-xp",FormInteger 0)]]
        <>[("tfa:expedition-leader",FormChoice Nothing)]
      draft=HistoricalDraft (historyIdentity resolved) form logDraft targets [] metadata Nothing [] mempty [] [] []
  context<-storyContext resolved base [] mempty
  rewards<-Story.schema context
  pure $ object ["draft" .= draft,"story" .= rewards,"chapterLog" .= base
    ,"targetsAreCurrentUnspentExperience" .= True,"preparedDraftStillRequiresHistoricalChoices" .= True]

storyContext :: ResolvedHistory -> CampaignLog -> [CampaignStep] -> Map ScenarioId Resolution -> Either Text Story.Context
storyContext resolved logValue steps resolutions=do
  campaign<-campaignOnly $ boundGame resolved
  let attrs=toAttrs campaign
      actual=unCampaignId $ bootstrapCampaign $ boundBootstrap resolved
      start=unCardCode $ unScenarioId $ bootstrapStart $ boundBootstrap resolved
      completed=concatMap (\case
        ScenarioStep sid->[unCardCode $ unScenarioId sid]
        InterludeStep n _->["interlude:"<>tshow n]
        InterludeStepPart n _ _->["interlude:"<>tshow n]
        _->[]) steps
      existing=Map.unionWith (<>) (Map.map (map toCardCode . unDeck) $ campaignDecks attrs) (Map.map (map toCardCode) $ campaignStoryCards attrs)
  family<-historyFamily actual
  pure $ Story.Context actual family start logValue (boundActors resolved) completed
    (Map.mapKeys (unCardCode . unScenarioId) resolutions) existing
    (actual=="52" && start/="52014")

compileHistory :: ResolvedHistory -> HistoricalDraft -> Either Text HistoricalPlan
compileHistory resolved draft=do
  unless (historySchemaId draft==historyIdentity resolved) $ Left "Stale or foreign historical schema"
  let game=boundGame resolved
      actual=unCampaignId $ bootstrapCampaign $ boundBootstrap resolved
      selectedRetired=mapMaybe (`Map.lookup` boundRetired resolved) $ historyRetiredIdentities draft
  profile<-profileFor actual
  fields<-resolveHistoryFields profile $ map unInvestigatorId $ map fst (Map.elems $ boundActors resolved)<>selectedRetired
  let policy=(boundLogPolicy resolved) {resolvedHistoryFields=fields}
  (base,forced)<-chapterLogs resolved $ historyChapterForm draft
  logValue<-compileHistoricalLog policy base forced $ historyLog draft
  inherited<-compileInheritance (boundInheritance resolved) draft
  Manifest.validateHistoricalRoute actual (unCardCode $ unScenarioId $ bootstrapStart $ boundBootstrap resolved) $ historySteps draft
  Manifest.validateCompletedResolutions actual $ historySteps draft
  context<-storyContext resolved logValue (inheritedSteps inherited) (inheritedResolutions inherited)
  story<-Story.compile context (campaignLogOptions logValue) $ historyStoryAssignments draft
  campaign<-campaignOnly game
  let modified=overAttrs (\attrs->attrs
        {campaignLog=logValue,campaignCompletedSteps=inheritedSteps inherited,campaignResolutions=inheritedResolutions inherited
        ,campaignMeta=maybe (campaignMeta attrs) toJSON $ inheritedMetadata inherited
        ,campaignDestiny=inheritedDestiny inherited,campaignReplacedInvestigators=inheritedRetired inherited
        ,campaignModifiersForAll=inheritedEffects inherited}) campaign
      update actor=case Map.lookup (toId actor) $ inheritedInvestigators inherited of
        Nothing->actor
        Just target->overAttrs (\attrs->attrs {investigatorXp=targetExperience target
          ,investigatorPhysicalTrauma=targetPhysicalTrauma target,investigatorMentalTrauma=targetMentalTrauma target
          ,investigatorSupplies=targetSupplies target}) actor
      entities=gameEntities game
      edited=game {gameMode=This modified,gameEntities=entities {entitiesInvestigators=Map.map update $ entitiesInvestigators entities}}
      lodge=actual=="54" && "interlude:4" `elem` Story.storyCompletedScenarios context
        && Story.hasFlag context "TheInvestigatorsAssumedControlOfTheSilverTwilightLodge"
      requested=historyLodgeSetupBeneficiaries draft
      eligible=Map.map fst (boundActors resolved)<>Map.restrictKeys (boundRetired resolved) (Set.fromList $ historyRetiredIdentities draft)
  unless (null requested || lodge) $ Left "Historical Lodge beneficiaries require Return campaign, completed Interlude 4 and the assumed-control record"
  unless (length requested<=4 && length requested==length (nub requested)) $ Left "Choose at most four distinct historical Lodge beneficiaries"
  beneficiaries<-for requested \ident->maybe (Left "Unknown or unselected historical Lodge beneficiary") Right $ Map.lookup ident eligible
  unless (length beneficiaries==length (nub beneficiaries)) $ Left "Historical Lodge beneficiaries must identify distinct investigators"
  pure $ HistoricalPlan edited story inherited beneficiaries

planView :: HistoricalPlan -> Value
planView plan=object
  ["campaignLog" .= (case gameMode (planGame plan) of This c->campaignLog $ toAttrs c;_->mempty)
  ,"storyCards" .= [object ["cardCode" .= cdCardCode def,"owner" .= owner]|(def,owner)<-Story.storyCardsToCreate $ planStory plan]
  ,"originalOptions" .= Story.storyOriginalOptions (planStory plan)
  ,"consumedCardOptions" .= Story.storyChapterOptionsConsumed (planStory plan)
  ,"bagIsFinalOverride" .= isJust (inheritedBag $ planInheritance plan)
  ,"anetteHistoricalExperienceNotGrantedAgain" .= True,"lodgeFutureFinaleBeneficiaries" .= planLodgeSetupBeneficiaries plan]

-- Caller creates one candidate CoreEnv from planGame with an EMPTY queue, then
-- retains messages pushed here before appending the original continuation.
-- Explicit bag counts are FINAL: original option effects run once, followed by
-- that user-selected override. Preview makes the order visible.
materialize :: HistoricalPlan -> CoreM [Message]
materialize plan=do
  bonuses<-fmap concat $ for (planLodgeSetupBeneficiaries plan) \iid->sequence
      [runWithEnv $ scenarioSetupModifier "54056" CampaignSource iid (StartingResources 2)
      ,runWithEnv $ scenarioSetupModifier "54056" CampaignSource iid (StartingHand 2)]
  cardsAndOptions<-Story.materialize $ planStory plan
  pure $ bonuses<>cardsAndOptions<>[SetCampaignChaosBag bag|Just bag<-[inheritedBag $ planInheritance plan]]
