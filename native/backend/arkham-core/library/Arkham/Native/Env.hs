module Arkham.Native.Env where

import Arkham.Prelude
import Arkham.Classes.GameLogger
import Arkham.Classes.HasGame
import Arkham.Classes.HasQueue
import Arkham.Classes.Entity (toAttrs)
import Arkham.ChaosBag.Base (emptyChaosBag)
import Arkham.Debug
import Arkham.Epic.Types (HasMaybeEpic (..), EpicEnv)
import Arkham.Game (Game (..), HasGameRef (..))
import Arkham.Message (Message (StandaloneSetup), pattern SetChaosTokens)
import Arkham.Phase (Phase)
import Arkham.Scenario.Types (scenarioId, scenarioChaosBag)
import Arkham.Scenario.Scenarios.TheForgottenAge.HeartOfTheElders qualified as Heart
import Arkham.Queue
import Arkham.Random
import Control.Monad.Catch (MonadCatch, MonadMask, MonadThrow)
import Control.Monad.Random

-- No account, database, network or UI callback exists in the rule environment.
data CoreEnv = CoreEnv
  { coreGame :: IORef Game
  , coreQueue :: Queue Message
  , coreGen :: IORef StdGen
  , coreEvents :: IORef [ClientMessage]
  , coreUndoRandomResult :: IORef Bool
  , coreUndoObservedBeginsRev :: IORef [Phase]
  , coreUndoInitialPhase :: Phase
  , coreAdvancing :: IORef Bool
  , coreHeartSetupQueued :: IORef Bool
  , coreEpic :: Maybe EpicEnv
  }

newtype CoreM a = CoreM {unCoreM :: ReaderT CoreEnv IO a}
  deriving newtype
    (Functor, Applicative, Monad, MonadFail, MonadIO, MonadReader CoreEnv,
     MonadUnliftIO, MonadMask, MonadCatch, MonadThrow)

instance HasMaybeEpic CoreEnv where
  getMaybeEpicEnv = coreEpic
instance HasDebugLevel CoreM where
  getDebugLevel = pure 0
instance HasGame CoreM where
  getGame = asks coreGame >>= readIORef
  getCache = GameCache \_ build -> build
instance HasGameRef CoreEnv where
  gameRefL = lens coreGame $ \e v -> e {coreGame = v}
instance HasStdGen CoreEnv where
  genL = lens coreGen $ \e v -> e {coreGen = v}
instance HasQueue Message CoreM where
  messageQueue = do
    env <- ask
    advancing <- readIORef $ coreAdvancing env
    queued <- readIORef $ coreHeartSetupQueued env
    -- Native lifecycle middleware, not a rule observer. StartScenario rebuilds
    -- the scenario without its old bag. Intercept only the original standalone
    -- setup anchor after that rebuild; use the original message/RNG handler.
    -- Import/view/hydration never enable this middleware.
    when (advancing && not queued) do
      game <- readIORef $ coreGame env
      case gameMode game of
        That scenario
          | gameInSetup game
          , scenarioId (toAttrs scenario) == "53048"
          , scenarioChaosBag (toAttrs scenario) == emptyChaosBag -> do
              inserted <- atomicModifyIORef' (queueToRef $ coreQueue env) $ \messages ->
                case messages of
                  StandaloneSetup : rest ->
                    (SetChaosTokens Heart.standaloneChaosTokens : StandaloneSetup : rest, True)
                  -- Also recognize a restored, already scheduled initialization.
                  SetChaosTokens faces : StandaloneSetup : _
                    | faces == Heart.standaloneChaosTokens -> (messages, True)
                  _ -> (messages, False)
              -- runWithEnv asks for the queue again after the token message is
              -- popped but before its effect is applied. Do not schedule twice.
              when inserted $ writeIORef (coreHeartSetupQueued env) True
        _ -> pure ()
    pure $ coreQueue env
instance HasGameLogger CoreM where
  getLogger = do
    ref <- asks coreEvents
    pure $ \event -> atomicModifyIORef' ref $ \events -> (event : events, ())
instance MonadRandom CoreM where
  getRandom = do
    ref <- asks coreGen
    atomicModifyIORef' ref (swap . random)
  getRandomR bounds = do
    ref <- asks coreGen
    atomicModifyIORef' ref (swap . randomR bounds)
  getRandoms = do
    ref <- asks coreGen
    gen <- atomicModifyIORef' ref splitGen
    pure $ randoms gen
  getRandomRs bounds = do
    ref <- asks coreGen
    gen <- atomicModifyIORef' ref splitGen
    pure $ randomRs bounds gen

runCore :: CoreEnv -> CoreM a -> IO a
runCore env = flip runReaderT env . unCoreM
