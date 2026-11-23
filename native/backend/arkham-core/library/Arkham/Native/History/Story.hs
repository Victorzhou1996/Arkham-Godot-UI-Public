-- Historical custody is a final-state plan, not a replay of all past rewards.
-- Only the native resolver constructs Context; the wire names offered rewards
-- and current roster slots, never CardId/CardDef/Message/Source or an owner UUID.
module Arkham.Native.History.Story where

import Arkham.Prelude
import Arkham.Campaign.Option
import Arkham.CampaignLog
import Arkham.CampaignLogKey (recordedCardCodes)
import Arkham.Card
import Arkham.Card.CardDef (DeckRestriction (..))
import Arkham.Card.CardType (CardSubType (BasicWeakness))
import Arkham.ClassSymbol (ClassSymbol)
import Arkham.Id
import Arkham.Resolution (Resolution (..))
import Arkham.Message (Message (AddCampaignCardToDeck,HandleOption),ShuffleIn (..))
import Arkham.GameEnv (runWithEnv)
import Arkham.Native.Env (CoreM)
import Arkham.Native.History.Types
import Arkham.Native.History.Profile (keyName)
import Arkham.Asset.Cards.NightOfTheZealot qualified as N
import Arkham.Asset.Cards.TheDunwichLegacy qualified as D
import Arkham.Asset.Cards.ThePathToCarcosa qualified as C
import Arkham.Asset.Cards.TheForgottenAge qualified as F
import Arkham.Asset.Cards.TheCircleUndone qualified as U
import Arkham.Enemy.CardDefs.ThePathToCarcosa.CurtainCall qualified as Stranger
import Arkham.Treachery.CardDefs.TheDunwichLegacy.TheEssexCountyExpress qualified as Essex
import Arkham.Treachery.CardDefs.ThePathToCarcosa.APhantomOfTruth qualified as Phantom
import Arkham.Treachery.CardDefs.TheForgottenAge.Poison qualified as Poison
import Arkham.Treachery.CardDefs.TheForgottenAge.TheCityOfArchives qualified as Archives
import Arkham.PlayerCard (allPlayerCards)
import Arkham.Trait qualified as Trait
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set

data Multiplicity = GlobalOne | PerOwnerOne | SharedFamilyOne Text | PerOwnerFamilyOne Text
  deriving stock (Show,Eq)

data StoryRule = StoryRule
  { storyRewardId :: Text
  , storyDefinition :: CardDef
  , storyMultiplicity :: Multiplicity
  , storyOption :: Maybe CampaignOption
  , storyAllowedOwners :: [InvestigatorId]
  , storyUnavailable :: Maybe Text
  , storyExistingCopyPermitted :: Bool
  }
  deriving stock Show

data Context = Context
  { storyActualCampaign :: Text
  , storyFamily :: Text
  , storyStart :: Text
  , storyLog :: CampaignLog
  , storyActors :: Map Text (InvestigatorId,ClassSymbol)
  , storyCompletedScenarios :: [Text]
  , storyScenarioResolutions :: Map Text Resolution
  , storyExistingCards :: Map InvestigatorId [CardCode]
  , storyRequiresStranger :: Bool
  }
  deriving stock Show

data Plan = Plan
  { storyCardsToCreate :: [(CardDef,InvestigatorId)]
  , storyOriginalOptions :: [CampaignOption]
  , storyChapterOptionsConsumed :: [CampaignOption]
  , storyAnetteOwners :: [InvestigatorId]
  }
  deriving stock Show

rewardId :: CardDef -> Text
rewardId def="story:"<>unCardCode (cdCardCode def)

hasFlag :: Context -> Text -> Bool
hasFlag context name=any ((==name) . keyName) (Set.toList $ campaignLogRecorded $ storyLog context)
  || any ((==name) . keyName) (Map.keys $ campaignLogRecordedCounts $ storyLog context)

recordedCodes :: Context -> Text -> [CardCode]
recordedCodes context name=concat
  [recordedCardCodes entries|(key,entries)<-Map.toList $ campaignLogRecordedSets $ storyLog context,keyName key==name]

completedAny :: Context -> [Text] -> Bool
completedAny context=any (`elem` storyCompletedScenarios context)

resolutionIs :: Context -> [Text] -> Int -> Bool
resolutionIs context scenarios number=any (\sid->Map.lookup sid (storyScenarioResolutions context)==Just (Resolution number)) scenarios

catalog :: Context -> Either Text [StoryRule]
catalog context@Context{..}=do
  let owners=map fst $ Map.elems storyActors
      rule def multiplicity option blocked=StoryRule (rewardId def) def multiplicity option owners blocked False
      unique def=rule def GlobalOne Nothing Nothing
      perOwner def=rule def PerOwnerOne Nothing Nothing
      forbid condition reason=if condition then Just reason else Nothing
      after scenarios reason=forbid (not $ completedAny context scenarios) reason
      sacrificed def=forbid (any (cardCodeExactEq $ cdCardCode def) $ recordedCodes context "SacrificedToYogSothoth") "This ally was sacrificed to Yog-Sothoth"
      takeAlly def option=rule def GlobalOne (Just option) $ sacrificed def
      deadOrNotRescued alive dead=forbid
        (not (hasFlag context alive) || hasFlag context dead || not (resolutionIs context ["05238","54046"] 8))
        "This ally requires the corresponding alive record and Union and Disillusion resolution 8"
      historicalLostSoul=(perOwner Phantom.lostSoul)
        {storyUnavailable=if storyStart `elem` ["03200","52040"]
          then Just "Current PreScenarioSetup will grant Lost Soul; do not pre-install it"
          else after ["03200","52040"] "Lost Soul belongs to the already completed A Phantom of Truth"}
      relic def=(unique def) {storyMultiplicity=SharedFamilyOne "relic-of-ages"}
      erynn=(unique U.erynnMacAoidhDevotedEnchantress)
        {storyUnavailable=forbid (storyActualCampaign/="54" || not (hasFlag context "ErynnJoinedTheInvestigators")
          || not (resolutionIs context ["54034"] 3)) "Erynn requires the Return-To resolution and joined record"}
      tcuAlly def alive dead=(unique def) {storyUnavailable=deadOrNotRescued alive dead}
      tarots=[(unique def) {storyUnavailable=after ["05050","54017"] "Current Witching Hour setup, not inherited history, grants these cards"}
        |def<-[U.aceOfRods1,U.theTowerXVI]]
      fixed=case storyFamily of
        "01"->[(unique N.litaChantler) {storyOption=Just AddLitaChantler,storyUnavailable=forbid
          (hasFlag context "TheInvestigatorsSacrificedLitaChantlerToUmordhoth" || hasFlag context "LitaWasForcedToFindOthersToHelpHerCause")
          "Lita is not in the investigators' custody in this log"}]
        "02"->[takeAlly D.drHenryArmitage TakeArmitage,takeAlly D.professorWarrenRice TakeWarrenRice
          ,takeAlly D.drFrancisMorgan TakeFrancisMorgan,rule D.zebulonWhateley GlobalOne (Just TakeZebulonWhately) Nothing
          ,rule D.earlSawyer GlobalOne (Just TakeEarlSawyer) Nothing
          ,rule D.powderOfIbnGhazi GlobalOne (Just TakePowderOfIbnGhazi) Nothing
          ,rule D.theNecronomiconOlausWormiusTranslation GlobalOne (Just TakeTheNecronomicon)
             $ forbid (hasFlag context "TheNecronomiconWasStolen" || hasFlag context "TheInvestigatorsDestroyedTheNecronomicon") "The Necronomicon is stolen or destroyed"
          ,rule Essex.acrossSpaceAndTime PerOwnerOne (Just AddAcrossSpaceAndTime) Nothing]
        "03"->[unique Stranger.theManInThePallidMask
          ,(unique C.claspOfBlackOnyx) {storyUnavailable=after ["03120","52028"] "Clasp requires completed Echoes of the Past"}
          ,(unique C.theTatteredCloak) {storyUnavailable=after ["03120","52028"] "Cloak requires completed Echoes of the Past"}
          ,historicalLostSoul]
        "04"->[unique F.alejandroVela,unique F.ichtacaTheForgottenGuardian,unique F.expeditionJournal
          ,(unique F.vedaWhitsleySkilledBotanist) {storyUnavailable=forbid (storyActualCampaign/="53") "Veda is a Return-To reward"}
          ,relic F.relicOfAgesADeviceOfSomeSort,relic F.relicOfAgesForestallingTheFuture
          ,relic F.relicOfAgesRepossessThePast,relic F.relicOfAgesUnleashTheTimestream
          ,perOwner Poison.poisoned,perOwner Archives.outOfBodyExperience]
        "05"->[rule U.theBlackBook GlobalOne (Just TakeBlackBook) Nothing,rule U.puzzleBox GlobalOne (Just TakePuzzleBox)
            (forbid (resolutionIs context ["05238","54046"] 8) "Union resolution 8 removes the Puzzle Box")
          ,tcuAlly U.gavriellaMizrah "GavriellaIsAlive" "GavriellaIsDead"
          ,tcuAlly U.jeromeDavids "JeromeIsAlive" "JeromeIsDead"
          ,tcuAlly U.pennyWhite "PennyIsAlive" "PennyIsDead"
          ,tcuAlly U.valentinoRivas "ValentinoIsAlive" "ValentinoIsDead",erynn]<>tarots
        _->[]
      anette=[StoryRule ("anette:"<>unCardCode (cdCardCode def)) def (PerOwnerFamilyOne "anette-spells") Nothing
          eligible (forbid (not (hasFlag context "AnetteTaughtYouTheSpellsOfOld") || not (completedAny context ["interlude:4"]))
            "Anette's historical reward requires her taught-spells record and the already completed interlude four") True
        |storyActualCampaign=="54"
        ,def<-Map.elems allPlayerCards,cdCardSubType def==Just BasicWeakness
        ,any (`member` cdCardTraits def) [Trait.Pact,Trait.Curse,Trait.Omen,Trait.SilverTwilight]
        ,let eligible=[iid|(_, (iid,actorClass))<-Map.toAscList storyActors
                   ,length owners>=2 || MultiplayerOnly `notElem` cdDeckRestrictions def
                   ,not $ any (notForClass actorClass) $ cdDeckRestrictions def]
        ,notNull eligible]
      allRules=fixed<>anette
  unless (notNull fixed && length (nub $ map storyRewardId allRules)==length allRules) $ Left "No finite story policy or duplicate native reward identity"
  pure allRules
 where
  -- Intentionally preserve the original Return-To Anette restriction filter,
  -- including its treatment of non-OnlyClass restrictions. Not a rule repair.
  notForClass actorClass= \case OnlyClass actual->actual/=actorClass;_->True

schema :: Context -> Either Text Value
schema context=do
  rules<-catalog context
  pure $ object ["rewards" .= [object ["id" .= storyRewardId rule,"cardCode" .= cdCardCode (storyDefinition rule)
      ,"name" .= cdName (storyDefinition rule),"multiplicity" .= multiplicity (storyMultiplicity rule)
      ,"option" .= storyOption rule,"unavailable" .= storyUnavailable rule
      ,"owners" .= [slot|(slot,(iid,_))<-Map.toAscList $ storyActors context,iid `elem` storyAllowedOwners rule]]|rule<-rules]
    ,"requiresStranger" .= storyRequiresStranger context
    ,"anetteExperiencePolicy" .= ("historical-four-xp-not-granted-again-target-is-current-unspent-xp"::Text)]
 where
  multiplicity= \case
    GlobalOne->object ["kind" .= ("global-one"::Text)]
    PerOwnerOne->object ["kind" .= ("per-owner-one"::Text)]
    SharedFamilyOne family->object ["kind" .= ("global-family-one"::Text),"family" .= family]
    PerOwnerFamilyOne family->object ["kind" .= ("per-owner-family-one"::Text),"family" .= family]

compile :: Context -> Set CampaignOption -> [HistoryStoryAssignment] -> Either Text Plan
compile context selected assignments=do
  rules<-catalog context
  let byId=Map.fromList [(storyRewardId rule,rule)|rule<-rules]
      owners=storyActors context
      allowedOptions=case storyFamily context of
        "01"->[AddLitaChantler]
        "02"->[Cheated,TakeArmitage,TakeWarrenRice,TakeFrancisMorgan,TakeZebulonWhately,TakeEarlSawyer,TakePowderOfIbnGhazi,TakeTheNecronomicon,AddAcrossSpaceAndTime]
        "05"->[TakeBlackBook,TakePuzzleBox,ProceedToInterlude3]
        _->[]
  unless (all (`elem` allowedOptions) selected) $ Left "Historical chapter option lacks an explicit execution/custody policy"
  resolved<-for assignments \assignment->do
    rule<-maybe (Left "Unknown historical reward") Right $ Map.lookup (historyRewardId assignment) byId
    (owner,_)<-maybe (Left "Unknown historical reward owner slot") Right $ Map.lookup (historyOwnerSlotId assignment) owners
    unless (historyStoryQuantity assignment==1 && owner `elem` storyAllowedOwners rule) $ Left "Historical reward quantity or owner is not offered"
    for_ (storyUnavailable rule) Left
    pure (rule,owner)
  let identifiers=[(storyRewardId rule,owner)|(rule,owner)<-resolved]
  unless (length identifiers==length (nub identifiers)) $ Left "Duplicate historical reward assignment"
  for_ resolved \(rule,owner)->do
    let familyMatches other=case (storyMultiplicity rule,storyMultiplicity other) of
          (SharedFamilyOne a,SharedFamilyOne b)->a==b
          (PerOwnerFamilyOne a,PerOwnerFamilyOne b)->a==b
          _->storyRewardId rule==storyRewardId other
        sameOwnerRequired=case storyMultiplicity rule of PerOwnerOne->True;PerOwnerFamilyOne _->True;_->False
        matches=[()|(other,otherOwner)<-resolved,familyMatches other,not sameOwnerRequired || owner==otherOwner]
        familyCodes=[cdCardCode $ storyDefinition other|other<-rules,familyMatches other]
        existing=[code|(iid,codes)<-Map.toList $ storyExistingCards context,not sameOwnerRequired || iid==owner,code<-codes]
    unless (length matches==1) $ Left "Historical story-card multiplicity/family conflicts"
    unless (storyExistingCopyPermitted rule || not (any (\code->any (cardCodeExactEq code) familyCodes) existing)) $
      Left "Historical reward already exists in a regular/campaign deck; do not install a second copy"
  -- Chapter grants name a reward; explicit assignments choose its owner. Both
  -- sources become ONE plan, never two AddCampaignCardToDeck messages.
  for_ rules \rule->for_ (storyOption rule) \option->when (option `member` selected && isNothing (storyUnavailable rule)) do
    let granted=[owner|(actual,owner)<-resolved,storyRewardId actual==storyRewardId rule]
        currentPowder=option/=TakePowderOfIbnGhazi || storyStart context `elem` ["02236","51041"]
    when currentPowder $ unless (notNull granted) $ Left "Choose a current owner for the selected chapter reward"
  when (storyRequiresStranger context) $ unless
    (length [()|(rule,_)<-resolved,cardCodeExactEq (cdCardCode $ storyDefinition rule) "03059"]==1) $
      Left "This historical start requires exactly one real Man in the Pallid Mask with a current owner"
  let nonCard=[option|option<-[Cheated,ProceedToInterlude3],option `member` selected]
      consumed=[option|option<-toList selected,option `notElem` nonCard]
      anette=[owner|(rule,owner)<-resolved,case storyMultiplicity rule of PerOwnerFamilyOne "anette-spells"->True;_->False]
  pure $ Plan [(storyDefinition rule,owner)|(rule,owner)<-resolved] nonCard consumed anette

materialize :: Plan -> CoreM [Message]
materialize Plan{..}=do
  cards<-for storyCardsToCreate \(def,owner)->do
    card<-runWithEnv $ genCard def
    pure $ AddCampaignCardToDeck owner ShuffleIn card
  pure $ cards<>map HandleOption storyOriginalOptions
