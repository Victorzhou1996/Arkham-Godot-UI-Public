module Arkham.Native.Debug where

import Arkham.Prelude
import Arkham.Game (Game (..))
import Arkham.Entities (Entities (..))
import Arkham.Game.State (GameState (IsActive))
import Arkham.Id
import Arkham.Message
import Arkham.Source
import Arkham.Target
import Arkham.Token qualified as Token
import Arkham.Resolution (Resolution (..))
import Arkham.Classes.Entity (toAttrs,overAttrs)
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Asset.Types (AssetAttrs (..))
import Arkham.Enemy.Types (EnemyAttrs (..))
import Arkham.Location.Types (LocationAttrs (..))
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Key (Key)
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Control.Monad.Fail (fail)
import Arkham.Native.Debug.Actions qualified as Actions
import Arkham.Native.Debug.Workbench qualified as Workbench
import Arkham.Native.Debug.Scenario qualified as Scenario

-- A local debug API, deliberately separate from Answer and its recipient
-- semantics. Never accepts a Message, arbitrary step, queue or JSON expression.
data DebugCommand
  = SetExperience InvestigatorId Int
  | AdjustTokens Target Token.Token Int
  | ResolveGathering Int
  deriving stock Show

exactFields :: Object -> [Key] -> Aeson.Parser ()
exactFields o keys = unless (sort (KM.keys o) == sort keys) $ fail "Unknown or missing debug field"

canonical :: (FromJSON a,ToJSON a) => Value -> Aeson.Parser a
canonical raw = do
  result <- parseJSON raw
  unless (toJSON result == raw) $ fail "Debug target must be canonical"
  pure result

instance FromJSON DebugCommand where
  parseJSON = withObject "finite local debug command" \o -> do
    operation <- o .: "operation" :: Aeson.Parser Text
    case operation of
      "setExperience" -> do
        exactFields o ["operation","investigatorId","amount"]
        SetExperience <$> (canonical =<< o .: "investigatorId") <*> o .: "amount"
      "adjustTokens" -> do
        exactFields o ["operation","target","token","amount"]
        target <- canonical =<< o .: "target"
        -- All existing finite Token constructors are offered by the original
        -- debug picker (including Ammo/Secret/Charge). No arbitrary token tag.
        token <- canonical =<< o .: "token"
        AdjustTokens target token <$> o .: "amount"
      "resolveGathering" -> do
        exactFields o ["operation","resolution"]
        ResolveGathering <$> o .: "resolution"
      _ -> fail "Unsupported debug operation"

data PreparedDebug = DirectEdit Game | FinishScenario [Message] | OriginalDebugMessage Message | WorkbenchPlan Workbench.Plan

-- This is deliberately not PlaceTokens: direct editing must not dispatch
-- windows/defeat/other effects and then resurrect an invalid parked question.
-- Rule-effect debug actions are a separate, not-yet-ported command family.
prepareDebug :: Game -> PlayerId -> Value -> Either Text PreparedDebug
prepareDebug game player raw = do
  unless (player `elem` gamePlayers game) $ Left "Unknown local debug player"
  command <- case Aeson.parseEither parseJSON raw of
    Right parsed -> Right $ Just parsed
    Left _ -> Right Nothing
  case command of
    Nothing -> case raw of
      Object o | Just (String name) <- KM.lookup "operation" o, name `elem` Scenario.operationNames ->
        OriginalDebugMessage <$> Scenario.prepare game player raw
      Object o | Just (String name) <- KM.lookup "operation" o, name `elem` Workbench.operationNames ->
        WorkbenchPlan <$> Workbench.prepare game player raw
      _ -> OriginalDebugMessage <$> Actions.prepareAction game player raw
    Just (SetExperience iid amount) -> do
      unless (Map.member iid (gameEntities game).investigators) $ Left "Unknown debug investigator"
      unless (amount >= 0 && amount <= 999) $ Left "Debug experience must be between0and999"
      let entities=gameEntities game
      pure $ DirectEdit game {gameEntities=entities {entitiesInvestigators=Map.adjust (overAttrs $ \a -> a {investigatorXp=amount}) iid entities.investigators}}
    Just (AdjustTokens target token amount) -> do
      unless (amount /= 0 && amount >= -99 && amount <= 99) $ Left "Debug token change must be nonzero and bounded"
      -- This finite manual correction does not run a message, pay a cost,
      -- resume a test or consume RNG. Keep the parked question and continuation
      -- intact, just as SetExperience does; a live enclosing action is not a ban.
      let entities=gameEntities game
          revised tokens = do
            let value=toInteger (Token.countTokens token tokens)+toInteger amount
            unless (value >= 0 && value <= toInteger (maxBound :: Int)) $ Left "Debug token result is outside the valid integer range"
            pure $ if value == 0 then Map.delete token tokens else Map.insert token (fromInteger value) tokens
          missing=Left "Debug target is not a present editable entity"
      updated <- case target of
        InvestigatorTarget iid -> do
          actor <- maybe missing Right $ Map.lookup iid entities.investigators
          tokens <- revised $ investigatorTokens $ toAttrs actor
          pure entities {entitiesInvestigators=Map.insert iid (overAttrs (\a -> a {investigatorTokens=tokens}) actor) entities.investigators}
        AssetTarget aid -> do
          actor <- maybe missing Right $ Map.lookup aid entities.assets
          tokens <- revised $ assetTokens $ toAttrs actor
          pure entities {entitiesAssets=Map.insert aid (overAttrs (\a -> a {assetTokens=tokens}) actor) entities.assets}
        EnemyTarget eid -> do
          actor <- maybe missing Right $ Map.lookup eid entities.enemies
          tokens <- revised $ enemyTokens $ toAttrs actor
          pure entities {entitiesEnemies=Map.insert eid (overAttrs (\a -> a {enemyTokens=tokens}) actor) entities.enemies}
        LocationTarget lid -> do
          actor <- maybe missing Right $ Map.lookup lid $ entitiesLocations entities
          tokens <- revised $ locationTokens $ toAttrs actor
          pure entities {entitiesLocations=Map.insert lid (overAttrs (\a -> a {locationTokens=tokens}) actor) (entitiesLocations entities)}
        _ -> missing
      pure $ DirectEdit game {gameEntities=updated}
    Just (ResolveGathering resolution) -> do
      unless (resolution `elem` [1,2,3]) $ Left "Unknown Gathering resolution"
      stableGathering game player
      -- Deliberate debug finish, not a natural victory. Pending play choices
      -- are abandoned only for this explicitly confirmed operation.
      pure $ FinishScenario [ClearUI,ScenarioResolution $ Resolution resolution]

stableGathering :: Game -> PlayerId -> Either Text ()
stableGathering game player = do
  Actions.stablePlayerWindow game player
  let current=case gameMode game of These _ s -> Just s.id;That s -> Just s.id;_ -> Nothing
  unless (current == Just "01104" && gameGameState game == IsActive && not (gameInSetup game)
    && isNothing (gameSkillTest game) && Map.null (gameActiveCost game) && Map.null (gameSimultaneousAsks game)
    && not (gameInAction game) && isNothing (gameResolvingCard game) && null (gameActiveAbilities game)) $
    Left "Resolution shortcut requires the Gathering's stable player window, with no pending cost, test, action or simultaneous barrier"

debugView :: Game -> PlayerId -> Value
debugView game player = object
  [ "schemaVersion" .= (1 :: Int),"scope" .= ("trusted-local-explicit-debug-not-normal-answer" :: Text)
  , "operations" .= (["setExperience","adjustTokens"] <> ["resolveGathering" | canResolve] :: [Text])
  , "investigators" .= [object ["id" .= iid,"experience" .= investigatorXp (toAttrs actor)] | (iid,actor) <- Map.toList (gameEntities game).investigators]
  , "targets" .= (map (toJSON . InvestigatorTarget) (Map.keys (gameEntities game).investigators)
      <> map (toJSON . AssetTarget) (Map.keys (gameEntities game).assets)
      <> map (toJSON . EnemyTarget) (Map.keys (gameEntities game).enemies)
      <> map (toJSON . LocationTarget) (Map.keys $ entitiesLocations $ gameEntities game))
  , "tokens" .= ([Token.Resource,Token.Clue,Token.Damage,Token.Horror,Token.Doom])
  , "numericPolicy" .= ("direct-state-no-effects-or-defeat-v1" :: Text)
  , "actions" .= Actions.actionView game player
  , "workbench" .= Workbench.view game player
  , "scenario" .= Scenario.view game player
  , "resolutionChoices" .= (if canResolve then [1,2,3] else [] :: [Int])
  ]
 where
  canResolve=case stableGathering game player of Right () -> True;Left _ -> False
