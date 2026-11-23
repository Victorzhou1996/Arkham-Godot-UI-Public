module Arkham.Native.Triggers where

import Arkham.Prelude
import Arkham.Ability.Types (Ability (..))
import Arkham.Ability.Type (AbilityType (..))
import Arkham.Asset.Types (AssetAttrs (..))
import Arkham.Card
import Arkham.Card.CardCode (cardCodeExactEq)
import Arkham.Card.Settings
import Arkham.Cost (Payment (NoPayment), CostStatus (UnpaidCost), ActionStatus (NeedsAction))
import Arkham.Classes.Entity
import Arkham.Classes.HasGame (HasGame)
import Arkham.Classes.HasAbilities (getAbilities)
import Arkham.Entities
import Arkham.Event.Types (EventAttrs (..))
import Arkham.Game (Game (..))
import Arkham.Helpers.Ability (isForcedAbilityType)
import Arkham.Helpers.Playable (getCardPlayability, playabilityFast)
import Arkham.Id
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Placement (isInPlayPlacement)
import Arkham.Source (Source (InvestigatorSource))
import Arkham.Target
import Data.Map.Strict qualified as Map
import Data.List (nubBy)

type TriggerIdentity = (InvestigatorId,CardCode,Int)

-- Computed once in a private CoreEnv, using the same dynamic playability
-- calculation as the offered card. Never reconstructed by the client.
newtype TriggerContext = TriggerContext [Maybe TriggerIdentity]

buildTriggerContext :: HasGame m => Game -> PlayerId -> m TriggerContext
buildTriggerContext game player = TriggerContext <$> traverse (optionalIdentity game player) (fromMaybe [] $ windowChoices game player)

-- Unconditionally optional types. Configuration can additionally include a
-- conditional reaction, but execution must evaluate its forced criterion.
optionalTriggerType :: AbilityType -> Bool
optionalTriggerType = \case
  Objective inner -> optionalTriggerType inner
  DelayedAbility inner -> optionalTriggerType inner
  FastAbility' {} -> True
  ReactionAbility {} -> True
  ConstantReaction {} -> True
  _ -> False

configurableTriggerType :: AbilityType -> Bool
configurableTriggerType = \case
  Objective inner -> configurableTriggerType inner
  DelayedAbility inner -> configurableTriggerType inner
  ForcedWhen _ inner -> configurableTriggerType inner
  other -> optionalTriggerType other

ownedInvestigator :: Game -> PlayerId -> InvestigatorId -> Maybe InvestigatorAttrs
ownedInvestigator game player iid = listToMaybe
  [ attrs
  | (key,investigator) <- Map.toList (gameEntities game).investigators
  , toJSON key == toJSON iid
  , let attrs = toAttrs investigator
  , investigatorPlayerId attrs == player
  ]

windowChoices :: Game -> PlayerId -> Maybe [UI Message]
windowChoices game player = unwrapQuestion <$> Map.lookup player (gameQuestion game) >>= \case
  PlayerWindowChooseOne choices -> Just choices
  WindowChooseOne choices -> Just choices
  _ -> Nothing

optionalIdentity :: HasGame m => Game -> PlayerId -> UI Message -> m (Maybe TriggerIdentity)
optionalIdentity game player = \case
  AbilityLabel iid ability _ _ _ | configurableTriggerType $ abilityType ability ->
    case ownedInvestigator game player iid of
      Nothing -> pure Nothing
      Just _ -> do
        -- This read-only context is rebuilt for the exact current pause. A
        -- saved preference never makes a currently forced reaction skippable.
        forced <- isForcedAbilityType iid (abilitySource ability) (abilityType ability)
        pure do
          guard $ not forced
          guard $ abilityIndex ability >= 0
          pure (iid,abilityCardCode ability,abilityIndex ability)
  TargetLabel (CardIdTarget cardId) messages -> case messages of
    [InitiatePlayCardWithWindows iid card Nothing NoPayment windows _]
      | cardId == toCardId card -> fastIdentity iid card windows
    [InitiatePlayCardWithWindows iid card Nothing NoPayment windows usesAction, PlayerWindow next _ isAdditional False]
      | cardId == toCardId card && toJSON next == toJSON iid && usesAction == not isAdditional -> fastIdentity iid card windows
    _ -> pure Nothing
  _ -> pure Nothing
 where
  fastIdentity iid card windows = case ownedInvestigator game player iid of
    Nothing -> pure Nothing
    Just _ -> do
      result <- getCardPlayability iid (InvestigatorSource iid) (UnpaidCost NeedsAction) windows card
      pure $ guard (playabilityFast result) $> (iid,toCardCode card,playCardTriggerModeIndex)

sameIdentity :: TriggerIdentity -> TriggerIdentity -> Bool
sameIdentity (i,c,n) (i',c',n') = toJSON i == toJSON i' && cardCodeExactEq c c' && n == n'

savedIdentities :: Game -> PlayerId -> [TriggerIdentity]
savedIdentities game player =
  [ (iid,code,index)
  | (iid,investigator) <- Map.toList (gameEntities game).investigators
  , let attrs = toAttrs investigator
  , investigatorPlayerId attrs == player
  , (code,settings) <- Map.toList $ perCardSettings $ investigatorSettings attrs
  , index <- Map.keys $ cardAbilityModes settings
  , index >= playCardTriggerModeIndex
  ]

-- Printed fast hand cards can be configured before their play window opens.
-- Eligibility here never inserts a play choice or bypasses getCardPlayability.
editableHandIdentities :: Game -> PlayerId -> [TriggerIdentity]
editableHandIdentities game player =
  [ (iid,toCardCode card,playCardTriggerModeIndex)
  | (iid,investigator) <- Map.toList (gameEntities game).investigators
  , let attrs = toAttrs investigator
  , investigatorPlayerId attrs == player
  , card <- investigatorHand attrs
  , cdCardType (toCardDef card) `elem` [AssetType,EventType]
  , isFastCard card
  ]

-- Preferences remain editable for controlled, in-play assets while exhausted,
-- including conditional reactions. The offered context above, not this list,
-- decides whether automaticSkip may actually advance the current window.
editableAssetIdentities :: Game -> PlayerId -> [TriggerIdentity]
editableAssetIdentities game player =
  [ (iid,abilityCardCode ability,abilityIndex ability)
  | asset <- Map.elems (gameEntities game).assets
  , let attrs = toAttrs asset
  , isInPlayPlacement $ assetPlacement attrs
  , not $ assetFlipped attrs
  , Just iid <- [assetController attrs <|> assetOwner attrs]
  , Just _ <- [ownedInvestigator game player iid]
  , ability <- getAbilities asset
  , abilityIndex ability >= 0
  , cardCodeExactEq (abilityCardCode ability) (assetCardCode attrs)
  , configurableTriggerType $ abilityType ability
  ]

editableEventIdentities :: Game -> PlayerId -> [TriggerIdentity]
editableEventIdentities game player =
  [ (iid,abilityCardCode ability,abilityIndex ability)
  | event <- Map.elems $ entitiesEvents $ gameEntities game
  , let attrs = toAttrs event
  , isInPlayPlacement $ eventPlacement attrs
  , let iid = eventController attrs
  , Just _ <- [ownedInvestigator game player iid]
  , ability <- getAbilities event
  , abilityIndex ability >= 0
  , cardCodeExactEq (abilityCardCode ability) (eventCardCode attrs)
  , configurableTriggerType $ abilityType ability
  ]

triggerIdentities :: TriggerContext -> Game -> PlayerId -> [TriggerIdentity]
triggerIdentities (TriggerContext offered) game player = nubBy sameIdentity $
  catMaybes offered
    <> editableHandIdentities game player
    <> editableAssetIdentities game player
    <> editableEventIdentities game player
    <> savedIdentities game player

modeFor :: InvestigatorAttrs -> CardCode -> Int -> AbilityTriggerMode
modeFor attrs code index = fromMaybe AbilityAlwaysAsk do
  (_,settings) <- find (cardCodeExactEq code . fst) $ Map.toList $ perCardSettings $ investigatorSettings attrs
  Map.lookup index $ cardAbilityModes settings

validatePreference :: TriggerContext -> Game -> PlayerId -> TriggerModePreference -> Either Text ()
validatePreference context game player TriggerModePreference {..} = do
  attrs <- maybe (Left "Trigger preference belongs to another seat or unknown investigator") Right $ ownedInvestigator game player triggerInvestigator
  unless (any (sameIdentity (triggerInvestigator,triggerCardCode,triggerAbilityIndex)) $ triggerIdentities context game player) $ Left "Trigger source or index is not offered or previously recorded"
  when (modeFor attrs triggerCardCode triggerAbilityIndex == triggerMode) $ Left "Trigger preference is already selected"

applyPreference :: TriggerContext -> Game -> PlayerId -> TriggerModePreference -> Either Text Game
applyPreference context game player change@TriggerModePreference {..} = do
  validatePreference context game player change
  let update investigator = overAttrs (\attrs ->
        let settings = investigatorSettings attrs
            existing = fromMaybe defaultPerCardSettings $ Map.lookup triggerCardCode $ perCardSettings settings
            next = existing {cardAbilityModes = Map.insert triggerAbilityIndex triggerMode $ cardAbilityModes existing}
         in attrs {investigatorSettings = settings {perCardSettings = Map.insert triggerCardCode next $ perCardSettings settings}}) investigator
      entities = gameEntities game
  pure game {gameEntities = entities {entitiesInvestigators = Map.adjust update triggerInvestigator $ entitiesInvestigators entities}}

automaticSkip :: TriggerContext -> Game -> PlayerId -> Maybe Int
automaticSkip (TriggerContext offered) game player = do
  choices <- windowChoices game player
  (index,iid) <- case [(index,iid) | (index,SkipTriggersButton iid) <- zip [0..] choices] of
    [one] -> Just one
    _ -> Nothing
  _ <- ownedInvestigator game player iid
  guard $ length offered == length choices
  let rest = filter (\(choice,_) -> case choice of SkipTriggersButton {} -> False; Info {} -> False; InvalidLabel {} -> False; _ -> True) $ zip choices offered
  guard $ notNull rest
  for_ rest \(_,identity) -> do
    (who,code,abilityIndex) <- identity
    guard $ toJSON who == toJSON iid
    attrs <- ownedInvestigator game player who
    guard $ case modeFor attrs code abilityIndex of
      AbilityAlwaysAsk -> False
      AbilityAutoSkip -> True
      AbilityOwnerOnly -> gameActiveInvestigatorId game /= who
  pure index

-- Necessary conditions only, never authority to skip a window. If there is
-- no unique Skip button or no saved mode that can skip for its owner, the
-- full context cannot possibly produce Just. Avoid querying playability and
-- encoding the complete before/after state in those cases. Real candidates
-- still go through automaticSkip, including its forced/ownership checks.
mayAutomaticallySkip :: Game -> PlayerId -> Bool
mayAutomaticallySkip game player = isJust do
  choices <- windowChoices game player
  iid <- case [iid | SkipTriggersButton iid <- choices] of
    [one] -> Just one
    _ -> Nothing
  attrs <- ownedInvestigator game player iid
  let canSkip AbilityAutoSkip = True
      canSkip AbilityOwnerOnly = gameActiveInvestigatorId game /= iid
      canSkip AbilityAlwaysAsk = False
  guard $ any canSkip [mode | settings <- Map.elems $ perCardSettings $ investigatorSettings attrs, mode <- Map.elems $ cardAbilityModes settings]
  pure ()

triggerView :: TriggerContext -> Game -> PlayerId -> Value
triggerView context game player = object
  [ "schemaVersion" .= (2 :: Int)
  , "modes" .= [AbilityAlwaysAsk,AbilityOwnerOnly,AbilityAutoSkip]
  , "entries" .=
      [ object ["investigatorId" .= iid,"cardCode" .= code,"abilityIndex" .= index,"mode" .= modeFor attrs code index]
      | (iid,code,index) <- triggerIdentities context game player
      , Just attrs <- [ownedInvestigator game player iid]
      ]
  , "automaticSkipIndex" .= automaticSkip context game player
  , "policy" .= ("preserve-choices-explicit-skip-only" :: Text)
  , "executionOwner" .= ("native-rules" :: Text)
  , "maximumAutomaticSteps" .= (256 :: Int)
  , "canResumeAutomaticTriggers" .= isJust (automaticSkip context game player)
  ]
