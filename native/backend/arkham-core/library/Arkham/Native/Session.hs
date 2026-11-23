module Arkham.Native.Session where

import Arkham.Prelude
import Arkham.Classes.GameLogger (ClientMessage (ClientText))
import Arkham.Card.CustomCard
  ( CustomCard, CustomCardRegistry (..), customCardRegistry, customCardDefCache, signatureOwners )
import Arkham.Card.CardCode (CardCode)
import Arkham.Card (CardId)
import Arkham.Classes.HasQueue
import Arkham.Difficulty
import Arkham.Epic.Types (EpicEnv (..), SharedKey (AwaitingOrganizer), actProgressStages, sharedCounter)
import Arkham.Campaign.Option (CampaignOption (..))
import Arkham.Game
import Arkham.Game.Base (Transient (..))
import Arkham.Game.Diff qualified as GameDiff
import Arkham.Game.Json ()
import Arkham.Game.Settings (UndoMode (..), AsIfRuling, defaultAsIfRulingForCampaign, settingsUndoMode, settingsAsIfRuling, settingsAchievementsEnabled, settingsUltimatumsAndBoons)
import Arkham.UltimatumsAndBoons.Types (UltimatumOrBoon, allUltimatumsAndBoons)
import Arkham.Id
import Arkham.Log.Narrator (emptyNarrator, flushNarrator)
import Arkham.Message
import Arkham.Phase (Phase (..))
import Arkham.Native.Answer
import Arkham.Native.Env
import Arkham.Native.Network.View qualified as NetworkView
import Arkham.Native.Epic.Shared qualified as EpicShared
import Arkham.Classes.Entity (toAttrs)
import Arkham.Scenario.Types qualified as Scenario
import Arkham.Native.Events (eventJSON)
import Arkham.Native.Validation
import Arkham.Native.Validation qualified as Validation
import Arkham.Native.Settings qualified as Settings
import Arkham.Native.Settings.Engine qualified as SettingsEngine
import Arkham.Native.Settings.Types (SettingsFormRequest)
import Arkham.Native.History.Types
import Arkham.Native.History.Engine qualified as History
import Arkham.Native.Triggers qualified as Triggers
import Arkham.Native.Regions qualified as Regions
import Arkham.Native.Debug qualified as Debug
import Arkham.Native.Debug.Actions qualified as DebugActions
import Arkham.Native.Debug.Workbench qualified as DebugWorkbench
import Arkham.Native.Author qualified as Author
import Arkham.Native.SourceIdentity qualified as SourceIdentity
import Arkham.Queue
import Arkham.Metrics (withMetric)
import Arkham.Metrics qualified as Metrics
import Control.Exception qualified as E
import Control.Concurrent.MVar qualified as MV
import Control.Monad.Fail (fail)
import Control.Monad.Random (mkStdGen)
import Data.ByteString.Lazy qualified as BL
import Data.Map.Strict qualified as Map
import Data.Aeson.KeyMap qualified as KM
import Data.Set qualified as Set
import Data.Text qualified as T
import Data.Text.IO qualified as TIO
import Data.Text.Encoding qualified as TE
import Data.UUID qualified as UUID
import System.IO.Unsafe (unsafePerformIO)
import System.Timeout qualified as Timeout
import System.Environment (lookupEnv)

-- Opt-in diagnostics. No timings enter rule state, saves, events or responses.
{-# NOINLINE coreTimings #-}
coreTimings :: Bool
coreTimings = unsafePerformIO $ (== Just "1") <$> lookupEnv "ARKHAM_CORE_TIMINGS"

profileNativeCall :: Value -> IO a -> IO a
profileNativeCall request action
  | not coreTimings = action
  | otherwise = do
      ref <- Metrics.isMetricsEnabled >>= maybe Metrics.enableMetrics pure
      Metrics.resetMetrics
      let method = case request of Object o -> KM.lookup "method" o; _ -> Nothing
      withMetric "ffi/dispatch" action `E.finally` do
        entries <- readIORef ref
        let spans = [object ["name" .= name,"count" .= Metrics.meCount e
                    ,"totalMs" .= (fromIntegral (Metrics.meTotalNs e) / 1000000 :: Double)
                    ,"maxMs" .= (fromIntegral (Metrics.meMaxNs e) / 1000000 :: Double)]
                    | (name,e) <- Map.toList entries]
        TIO.hPutStrLn stderr $ "CORE_TIMING " <> TE.decodeUtf8 (BL.toStrict $ encode $ object ["method" .= method,"spans" .= spans])

-- Generated from a canonical manifest of source/data/config inputs. This is
-- conservative save compatibility, not a signature or complete build identity.
coreSourceId :: Text
coreSourceId = SourceIdentity.coreSourceId

-- Explicitly reviewed format-2 baseline only. All Game/Message decoders and
-- historical bootstrap validation still run below; unknown identities fail.
-- Original files are not rewritten. Export always carries this build's ID.
compatibleImportSourceIds :: [Text]
compatibleImportSourceIds =
  [ "arkham-core-sha256:aff2a4063ac68bfb45fe0a4677679c0fb9bb313c3f620df340358fa7640bc5ae"
  , "arkham-core-sha256:f221e26bb963b5d0df72aea7374e13a44ac6e168fec4eb5882ec18b4c6380218"
  , "arkham-core-sha256:d6e10a4373412d6beabcb6e984cee995e22c7fe241e56a6420fb317b1e5ec1fe"
  , "arkham-core-sha256:9f5699e6a8297a71e8e09cf3839ab174a460cd13e04e3df125b909e6f2489c1a"
  , "arkham-core-sha256:e98793b07a293bea0febf803fd642eb066fe946aa9ebf4d3ee9eb812db9022a0"
  , "arkham-core-sha256:cc9503c7e95277b2eb0f82c4ec7cc5edc06fb1340d73c8e37b62c16eff5fb76a"
  , "arkham-core-sha256:318be7e4726ee3af9ebcf659831a077366fba4e702aec21428e590a738e5dac9"
  -- Same rule/save schema; diagnostics and equivalent computation only.
  , "arkham-core-sha256:ba13b8bdc066c7795b77cc5a13e55ae398bf46a74eff1f5bb13ba258d088dce9"
  -- classic-fast-1.0.5, the released Windows core before the QQ fixes #8 #11 #19 #20 (2026-11-23): same save schema,
  -- those fixes add no fields, so its saves load as they are
  , "arkham-core-sha256:5bb054d7030a498821bfb2a1b120b8550be87cfa8f7374459c5f161608c79ba8"
  -- b08f62c4: the same fixes before the Ancient Vault "cannot be canceled" entries; never released, QA saves only
  , "arkham-core-sha256:b08f62c4ebc024cbf87e140a33547e457e9bf90611869bf169a3eb5e06026427" ]

-- Answer-only side-channel. This type is returned beside the candidate in the
-- FFI envelope and is deliberately absent from Game/Snapshot/Save JSON.
data UndoMetadata = UndoMetadata
  { schemaVersion :: Int
  , metadataComplete :: Bool
  , hasRandomResult :: Bool
  , intermissionBoundary :: Bool
  , phaseEdges :: [(Text, Text)]
  }
  deriving stock (Eq, Show)

instance ToJSON UndoMetadata where
  toJSON UndoMetadata {..} = object
    [ "schemaVersion" .= schemaVersion
    , "metadataComplete" .= metadataComplete
    , "hasRandomResult" .= hasRandomResult
    , "intermissionBoundary" .= intermissionBoundary
    , "phaseEdges" .= map (\(from, to) -> object ["from" .= from, "to" .= to]) phaseEdges
    ]

finishUndoMetadata :: Bool -> Phase -> Bool -> [Phase] -> UndoMetadata
finishUndoMetadata isIntermission initialPhase sawRandom begins = UndoMetadata
  { schemaVersion = 1
  , metadataComplete = True
  , hasRandomResult = sawRandom
  , intermissionBoundary = isIntermission
  , phaseEdges = edgesFrom initialPhase begins
  }
 where
  edgesFrom _ [] = []
  edgesFrom from (to : rest) = (phaseName from, phaseName to) : edgesFrom to rest
  phaseName = T.pack . show

-- Shared.hs random-result classes, plus the V2 investigator-deck draw result
-- as the same class as its legacy DrewCards draw.
isRandomOutcomeMessage :: Message -> Bool
isRandomOutcomeMessage message = case message of
  DrewCards {} -> True
  InvestigatorDrewPlayerCardFrom {} -> True
  InvestigatorDrewEncounterCard {} -> True
  InvestigatorDrewEncounterCardFrom {} -> True
  InvestigatorDrawEnemy {} -> True
  DrawChaosToken {} -> True
  RevealChaosToken {} -> True
  SilentRevealChaosToken {} -> True
  RequestedChaosTokens _ _ (_ : _) -> True
  _ -> case messageType message of
    Just DrawChaosTokenMessage -> True
    Just RevealChaosTokenMessage -> True
    Just DrawEncounterCardMessage -> True
    Just DrawEnemyMessage -> True
    _ -> False

settingsView :: Game -> PlayerId -> Value
settingsView = Settings.settingsView

-- Pure read-only projection, bound to the SAME revision, known player and
-- actual current pending question as submission. No CoreEnv or queue advance.
settingsPreview :: Snapshot -> PlayerId -> Word64 -> SettingsFormRequest -> Either Text Value
settingsPreview snapshot player expected draft = do
  unless (expected == snapshotRevision snapshot) $ Left "Stale revision"
  when (historyPending snapshot) $ Left "Use the bound historical chapter preview during historical bootstrap"
  schema <- SettingsEngine.resolveSchema (snapshotGame snapshot) player
  SettingsEngine.previewForm schema draft

settingsFormView :: Game -> PlayerId -> Value
settingsFormView game player = case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
  Just PickScenarioSettings -> project
  Just PickCampaignSettings -> project
  _ -> Null
 where
  project = case SettingsEngine.resolveSchema game player of
    Right schema -> SettingsEngine.schemaView schema
    Left reason -> object ["schemaVersion" .= (2 :: Int),"supported" .= False,"reason" .= reason]

specificView :: Game -> PlayerId -> Value
specificView = Validation.interactionView

deckView :: Game -> PlayerId -> Value
deckView = localDeckView

campaignView :: Game -> PlayerId -> Value
campaignView = campaignManagementView

triggerView :: Game -> PlayerId -> IO Value
triggerView game player = do
  context <- readTriggerContext game player
  let value = Triggers.triggerView context game player
  _ <- E.evaluate $ BL.length $ encode value
  pure value

readTriggerContext :: Game -> PlayerId -> IO Triggers.TriggerContext
readTriggerContext game player = do
  env <- makeEnv game []
  let capture = do
        current <- readIORef $ coreGame env
        queue <- readIORef $ queueToRef $ coreQueue env
        gen <- readIORef $ coreGen env
        events <- readIORef $ coreEvents env
        pure $ encode (current,queue,show gen,map eventJSON events)
  before <- capture
  context <- runCore env $ Triggers.buildTriggerContext game player
  _ <- E.evaluate $ length $ show $ Triggers.triggerView context game player
  after <- capture
  unless (before == after) $ fail "Read-only trigger context changed native state"
  pure context

-- Region permissions may depend on effective modifiers and engagement. Run
-- those queries in a disposable CoreEnv and prove that even private runtime
-- state, pending messages, events and RNG were not consumed by inspection.
regionsView :: Game -> PlayerId -> IO Value
regionsView game player = do
  env <- makeEnv game []
  let capture = do
        current <- readIORef $ coreGame env
        queue <- readIORef $ queueToRef $ coreQueue env
        gen <- readIORef $ coreGen env
        events <- readIORef $ coreEvents env
        pure $ encode (current,queue,show gen,map eventJSON events)
  before <- capture
  value <- runCore env $ Regions.regionsView game player
  _ <- E.evaluate $ BL.length $ encode value
  after <- capture
  unless (before == after) $ fail "Read-only region projection changed native state"
  pure value

networkSkillTestView :: Game -> PlayerId -> IO Value
networkSkillTestView game player = do
  unless (player `elem` gamePlayers game) $ fail "Unknown network seat"
  env <- makeEnv game []
  let capture = do
        current <- readIORef $ coreGame env
        queue <- readIORef $ queueToRef $ coreQueue env
        gen <- readIORef $ coreGen env
        events <- readIORef $ coreEvents env
        pure $ encode (current,queue,show gen,map eventJSON events)
  before <- capture
  value <- runCore env NetworkView.skillTestView
  _ <- E.evaluate $ BL.length $ encode value
  after <- capture
  unless (before == after) $ fail "Read-only network skill test changed native state"
  pure value

-- Opaque context type for the exposed native API. Ordinary callers pass
-- Nothing; Epic creates one original per-group context from its event snapshot.
type NativeEpicContext = EpicEnv

-- One disposable rule-query environment, including the real Epic shared state
-- when present. Reads must not advance the group, consume RNG, queue deltas or
-- change shared counters. Call under the existing custom-registry boundary.
networkExtrasView :: Maybe NativeEpicContext -> Game -> PlayerId -> Bool -> IO ([CardId],Value)
networkExtrasView epic game player includeRegions = do
  unless (player `elem` gamePlayers game) $ fail "Unknown network recipient"
  env <- makeEnvWithEpic epic game []
  let capture = do
        current <- readIORef $ coreGame env
        queue <- readIORef $ queueToRef $ coreQueue env
        gen <- readIORef $ coreGen env
        events <- readIORef $ coreEvents env
        shared <- traverse (\ctx -> (,) <$> readIORef (epicEnvSharedRef ctx) <*> readIORef (epicEnvDeltaRef ctx)) epic
        let bytes=encode (current,queue,show gen,map eventJSON events,shared)
        _ <- E.evaluate $ BL.length bytes
        pure bytes
  before <- capture
  (refs,cards,skill,trigger,zones) <- runCore env do
    (refs,cards) <- NetworkView.choiceCardsView player
    skill <- NetworkView.skillTestView
    context <- Triggers.buildTriggerContext game player
    let trigger=Triggers.triggerView context game player
    zones <- if includeRegions then Regions.regionsView game player else pure Null
    pure (refs,cards,skill,trigger,zones)
  let value=object ["choiceCards" .= cards,"skillTest" .= skill,"triggerSchema" .= trigger,"zoneSchema" .= zones]
  _ <- E.evaluate $ BL.length $ encode (refs,value)
  after <- capture
  unless (before == after) $ fail "Read-only network inspection changed game or Epic state"
  pure (refs,value)

{-# NOINLINE coreGate #-}
coreGate :: MV.MVar ()
coreGate = unsafePerformIO $ MV.newMVar ()

-- Existing custom-card lookups have a process-global registry. Serialize *all*
-- native operations and restore that registry even on rejection/exception/timeout.
-- Upstream now caches derived defs by registry generation. Replacing a session's
-- card map must rebuild its signature index and advance that generation, including
-- on restoration; restoring an old counter could reuse another game's cache.
replaceCustomCards :: Map CardCode CustomCard -> IO ()
replaceCustomCards cards = do
  changed <- atomicModifyIORef' customCardRegistry \previous ->
    if registryCards previous == cards then (previous, False)
    else (CustomCardRegistry cards (signatureOwners cards) (registryGeneration previous + 1), True)
  when changed $ atomicModifyIORef' customCardDefCache (const (mempty, ()))

boundary :: Map CardCode CustomCard -> IO a -> IO (Either Text a)
boundary cards action = MV.withMVar coreGate \_ -> do
  previous <- readIORef customCardRegistry
  outcome <- E.try @SomeException $ E.bracket_
    (replaceCustomCards cards)
    (replaceCustomCards $ registryCards previous)
    (Timeout.timeout 30000000 action)
  pure $ case outcome of
    Left exception -> Left $ "Rule execution failed: " <> T.pack (E.displayException exception)
    Right Nothing -> Left "Rule execution exceeded cooperative time limit"
    Right (Just result) -> Right result

data CreateRequest = CreateRequestData
  { createCampaign :: Maybe CampaignId
  , createScenario :: Maybe ScenarioId
  , createSeed :: Int
  , createPlayers :: [PlayerId]
  , createDifficulty :: Difficulty
  , createTarot :: Bool
  , createLabyrinthMiniCampaign :: Bool
  , createUndoMode :: UndoMode
  , createAsIfRuling :: Maybe AsIfRuling
  , createAchievementsEnabled :: Bool
  , createUltimatumsAndBoons :: Set UltimatumOrBoon
  , createOptions :: [CampaignOption]
  }

-- Keep the established positional constructor compatible for existing native
-- callers; JSON callers may opt into one of the original five modes.
pattern CreateRequest
  :: Maybe CampaignId -> Maybe ScenarioId -> Int -> [PlayerId] -> Difficulty -> Bool -> Bool -> CreateRequest
pattern CreateRequest campaign scenario seed players difficulty tarot mini <-
  CreateRequestData campaign scenario seed players difficulty tarot mini _ _ _ _ _
  where
    CreateRequest campaign scenario seed players difficulty tarot mini =
      CreateRequestData campaign scenario seed players difficulty tarot mini FullUndo Nothing True mempty []

instance FromJSON CreateRequest where
  parseJSON = withObject "CreateRequest" \o -> do
    campaign <- o .:? "campaign"
    scenario <- o .:? "scenario"
    seed <- o .: "seed"
    players <- o .: "players"
    difficulty <- o .:? "difficulty" .!= Standard
    tarot <- o .:? "tarot" .!= False
    mini <- o .:? "labyrinthMiniCampaign" .!= False
    undoMode <- o .:? "undoMode" .!= FullUndo
    ruling <- o .:? "asIfRuling"
    achievements <- o .:? "achievementsEnabled" .!= True
    variants <- o .:? "ultimatumsAndBoons" .!= mempty
    unless (all (`elem` allUltimatumsAndBoons) variants) $ fail "Unknown ultimatum or boon"
    options <- o .:? "options" .!= []
    when (length options > 64) $ fail "Too many campaign options"
    pure $ CreateRequestData campaign scenario seed players difficulty tarot mini undoMode ruling achievements variants options

data Snapshot = SnapshotState
  { snapshotRevision :: Word64
  , snapshotGame :: Game
  , snapshotQueue :: [Message]
  , snapshotHistoricalBootstrap :: Maybe HistoricalBootstrap
  }

-- Ordinary fixture construction remains concise. Production transitions use
-- SnapshotState and the mandatory bootstrap argument to finish, so a pending
-- history intention cannot disappear on a deck/trauma/preference answer.
pattern Snapshot :: Word64 -> Game -> [Message] -> Snapshot
pattern Snapshot revision game queue <- SnapshotState revision game queue _
 where Snapshot revision game queue=SnapshotState revision game queue Nothing
{-# COMPLETE Snapshot #-}

historyPending :: Snapshot -> Bool
historyPending snapshot=maybe False ((==HistoryPending) . bootstrapPhase) $ snapshotHistoricalBootstrap snapshot

historicalSchema :: Snapshot -> PlayerId -> Value
historicalSchema snapshot player=case snapshotHistoricalBootstrap snapshot of
  Just bootstrap | bootstrapPhase bootstrap==HistoryPending->case
    History.resolveHistory (snapshotGame snapshot) (snapshotQueue snapshot) (snapshotRevision snapshot) bootstrap player of
      Right resolved->History.historyView resolved
      Left reason->object ["schemaVersion" .= (1::Int),"supported" .= False,"pending" .= True,"reason" .= reason]
  _->Null

historicalResolved :: Snapshot -> PlayerId -> Word64 -> Either Text History.ResolvedHistory
historicalResolved snapshot player expected=do
  unless (expected==snapshotRevision snapshot) $ Left "Stale revision"
  bootstrap<-maybe (Left "This game was not created as a historical start") Right $ snapshotHistoricalBootstrap snapshot
  History.resolveHistory (snapshotGame snapshot) (snapshotQueue snapshot) expected bootstrap player

historicalChapterPreview :: Snapshot -> PlayerId -> Word64 -> SettingsFormRequest -> Either Text Value
historicalChapterPreview snapshot player expected form=do
  resolved<-historicalResolved snapshot player expected
  History.chapterPreview resolved form

historicalPrepare :: Snapshot -> PlayerId -> Word64 -> SettingsFormRequest -> Either Text Value
historicalPrepare snapshot player expected form=do
  resolved<-historicalResolved snapshot player expected
  History.prepareDraft resolved form

historicalPreview :: Snapshot -> PlayerId -> Word64 -> HistoricalDraft -> Either Text Value
historicalPreview snapshot player expected draft=do
  resolved<-historicalResolved snapshot player expected
  History.planView <$> History.compileHistory resolved draft

snapshotSettingsFormView :: Snapshot -> PlayerId -> Value
snapshotSettingsFormView snapshot player
  | historyPending snapshot=Null
  | otherwise=settingsFormView (snapshotGame snapshot) player

instance ToJSON Snapshot where
  toJSON SnapshotState {..} = object
    [ "snapshotFormat" .= (2 :: Int), "coreSourceId" .= coreSourceId
    , "revision" .= snapshotRevision, "game" .= snapshotGame, "queue" .= snapshotQueue
    , "historicalBootstrap" .= snapshotHistoricalBootstrap ]

instance FromJSON Snapshot where
  parseJSON = withObject "Snapshot" \o -> do
    format <- o .: "snapshotFormat"
    unless (format == (2 :: Int)) $ fail "Unsupported snapshot format"
    source <- o .: "coreSourceId"
    unless (source == coreSourceId || source `elem` compatibleImportSourceIds) $
      fail "Core source mismatch; migration required"
    snapshot<-SnapshotState <$> o .: "revision" <*> o .: "game" <*> o .: "queue" <*> o .: "historicalBootstrap"
    for_ (snapshotHistoricalBootstrap snapshot) \bootstrap->
      either (fail . T.unpack) pure $ History.validateBootstrap bootstrap $ snapshotGame snapshot
    pure snapshot

-- New refs preserve the previous Game/queue. Global custom-card registry
-- isolation requires the separately tracked native single-session guard.
makeEnv :: Game -> [Message] -> IO CoreEnv
makeEnv = makeEnvWithEpic Nothing

-- Explicit per-candidate context. No global event, network service or ambient
-- thread-local state is used, and ordinary sessions retain Nothing.
makeEnvWithEpic :: Maybe EpicEnv -> Game -> [Message] -> IO CoreEnv
makeEnvWithEpic epic game queue = do
  hydrated <- preloadModifiers $ restoreActionStart game
  CoreEnv <$> newIORef hydrated <*> newQueue queue
    <*> newIORef (mkStdGen $ gameSeed game) <*> newIORef [] <*> newIORef False <*> newIORef []
    <*> pure (gamePhase hydrated)
    <*> newIORef False <*> newIORef False <*> pure epic

finish :: Maybe HistoricalBootstrap -> Word64 -> CoreEnv -> IO (Snapshot, [ClientMessage])
finish bootstrap revision env = do
  game <- readIORef $ coreGame env
  queue <- readIORef $ queueToRef $ coreQueue env
  events <- reverse <$> readIORef (coreEvents env)
  let snapshot = SnapshotState revision (synchronizeActionDiff game) queue bootstrap
  -- The DLL must never return a lazy computation that can fail in its caller.
  _ <- withMetric "finish/snapshotEncodeAndForce" $ E.evaluate $ BL.length $ encode snapshot
  _ <- withMetric "finish/eventsEncodeAndForce" $ E.evaluate $ BL.length $ encode $ map eventJSON events
  pure (snapshot, events)

undoMetadataForAnswer :: Bool -> CoreEnv -> IO UndoMetadata
undoMetadataForAnswer isIntermission env = do
  sawRandom <- readIORef $ coreUndoRandomResult env
  begins <- reverse <$> readIORef (coreUndoObservedBeginsRev env)
  pure $ finishUndoMetadata isIntermission (coreUndoInitialPhase env) sawRandom begins

observeUndoMessage :: CoreEnv -> Message -> IO ()
observeUndoMessage env message = do
  observeUndoLeaf env message
  -- runMessages' Simultaneously branch executes these exact direct children
  -- itself rather than popping them through RunObservers. Observe only that
  -- execution layer; do not infer unexecuted nested wrappers or queues.
  case message of
    Simultaneously messages -> traverse_ (observeUndoLeaf env) messages
    _ -> pure ()

observeUndoLeaf :: CoreEnv -> Message -> IO ()
observeUndoLeaf env message = do
  when (isRandomOutcomeMessage message) $ writeIORef (coreUndoRandomResult env) True
  case message of
    Begin phase -> modifyIORef' (coreUndoObservedBeginsRev env) (phase :)
    _ -> pure ()

finishWithUndoMetadata :: Bool -> Maybe HistoricalBootstrap -> Word64 -> CoreEnv -> IO (Snapshot, [ClientMessage], UndoMetadata)
finishWithUndoMetadata isIntermission bootstrap revision env = do
  (snapshot, events) <- finish bootstrap revision env
  metadata <- undoMetadataForAnswer isIntermission env
  pure (snapshot, events, metadata)

-- Transient action-start caches are intentionally absent from JSON. Recover
-- them from the original raw checkpoint BEFORE modifier hydration, using the
-- same patch order as the rule runner. Never invent a start for an empty diff.
restoreActionStart :: Game -> Game
restoreActionStart game
  | gameInAction game
  , Nothing <- getTransient $ gameActionSnapshot game
  , notNull $ gameActionDiff game =
      let start = (foldl' GameDiff.unsafePatch game $ gameActionDiff game) {gameActionDiff = []}
       in game {gameActionSnapshot = Transient $ Just start}
  | otherwise = game

-- The final paused Ask updates question/seed AFTER the last rule-runner diff.
-- Persist a patch from that finalized pause, not from its preceding message.
synchronizeActionDiff :: Game -> Game
synchronizeActionDiff game
  | gameInAction game
  , Just start <- getTransient $ gameActionSnapshot game =
      game {gameActionDiff = [GameDiff.diff game start]}
  | otherwise = game

advance :: CoreEnv -> IO ()
advance env = do
  withMetric "advance/rules" $ advanceRules env
  void $ withMetric "advance/automaticTriggers" $ settleAutomaticTriggers env

-- Run the original queue once. Automatic decisions are part of the same
-- candidate environment/transaction, never simulated by a frontend timer.
advanceRules :: CoreEnv -> IO ()
advanceRules env = do
  narrator <- newIORef emptyNarrator
  E.bracket_
    (writeIORef (coreAdvancing env) True)
    (writeIORef (coreAdvancing env) False)
    $ runCore env do
      runMessages "NATIVE-LOCAL" (RunObservers (Just $ observeUndoMessage env) $ Just narrator)
      flushNarrator narrator

-- This function also accepts an already parked question (preference changes
-- and explicit resume). In that case DO NOT first run its continuation tail:
-- only an eligible, actual SkipTriggersButton may consume that pause.
settleAutomaticTriggers :: CoreEnv -> IO Int
settleAutomaticTriggers env = go 0 Set.empty
 where
  capture = do
    game <- readIORef $ coreGame env
    queue <- readIORef $ queueToRef $ coreQueue env
    gen <- readIORef $ coreGen env
    events <- readIORef $ coreEvents env
    pure $ encode (game,queue,show gen,map eventJSON events)
  go count visited = do
    game <- readIORef $ coreGame env
    let candidates = filter (Triggers.mayAutomaticallySkip game) (gamePlayers game)
    if null candidates then pure count else inspect count visited game candidates
  inspect count visited game candidates = do
    before <- withMetric "automatic/captureBefore" $ capture >>= \bytes -> E.evaluate (BL.length bytes) >> pure bytes
    eligible <- withMetric "automatic/contexts" $ fmap catMaybes $ for candidates \player ->
      if Map.member player $ gameQuestion game then do
        context <- runCore env $ Triggers.buildTriggerContext game player
        pure $ (player,) <$> Triggers.automaticSkip context game player
      else pure Nothing
    _ <- E.evaluate $ BL.length $ encode eligible
    after <- withMetric "automatic/captureAfter" $ capture >>= \bytes -> E.evaluate (BL.length bytes) >> pure bytes
    withMetric "automatic/equality" $ unless (before == after) $ fail "Automatic trigger inspection changed rule state"
    case listToMaybe eligible of
      Nothing -> pure count
      Just (player,index) -> do
        unless (count < 256) $ fail "Automatic trigger step limit exceeded; candidate discarded"
        queue <- readIORef $ queueToRef $ coreQueue env
        gen <- readIORef $ coreGen env
        -- Detect exact repeats after ignoring the question counter. This is
        -- not a proof against all cycles (RNG/diffs can change); the hard step
        -- bound above and the enclosing timeout remain authoritative.
        let stable = encode (game {gameScenarioSteps=0},queue,show gen)
        when (Set.member stable visited) $ fail "Repeated automatic trigger state; candidate discarded"
        reply <- handleAnswerPure game player $ AutoSkipTriggersAnswer index
        case reply of
          Unhandled reason -> fail $ T.unpack reason
          Handled messages -> do
            let active=gameActivePlayerId game
                bracketed=if active==player then messages else SetActivePlayer player:messages<>[SetActivePlayer active]
            writeIORef (queueToRef $ coreQueue env) $ ClearUI:bracketed<>queue
            modifyIORef' (coreEvents env) $ (ClientText "根据已保存偏好自动跳过可选触发窗口":)
            advanceRules env
            go (count+1) $ Set.insert stable visited

-- Explicit local debug channel, never routed through a player's normal Answer.
-- The host additionally requires an in-process opt-in which resets on restart.
-- Direct edits execute no rules and preserve the exact pending Ask and tail.
-- Only a named confirmed scenario finish deliberately discards that old tail.
debugView :: Game -> PlayerId -> Value
debugView = Debug.debugView

applyDebug :: Snapshot -> PlayerId -> Word64 -> Bool -> Value -> IO (Either Text (Snapshot,[ClientMessage]))
applyDebug SnapshotState {..} player expected enabled command = fmap join $ boundary (gameCustomCards snapshotGame) $ do
  let prepared = do
        unless enabled $ Left "Local debug mode is disabled"
        when (maybe False ((==HistoryPending) . bootstrapPhase) snapshotHistoricalBootstrap) $
          Left "Complete or cancel the historical new-game setup before debug mutations"
        unless (expected == snapshotRevision && snapshotRevision < maxBound) $ Left "Stale or exhausted debug revision"
        unless (not $ Map.null $ gameQuestion snapshotGame) $ Left "Debug edits require a paused local question"
        Debug.prepareDebug snapshotGame player command
  case prepared of
    Left reason -> pure $ Left reason
    Right (Debug.DirectEdit game) -> do
      -- Hydrate derived modifiers only: do not execute a message or consume RNG.
      let originalStart = gameActionSnapshot $ restoreActionStart snapshotGame
      env <- makeEnv (game {gameActionSnapshot = originalStart}) snapshotQueue
      Right <$> finish snapshotHistoricalBootstrap (snapshotRevision+1) env
    Right (Debug.FinishScenario messages) -> do
      env <- makeEnv snapshotGame messages
      advance env
      Right <$> finish snapshotHistoricalBootstrap (snapshotRevision+1) env
    Right (Debug.WorkbenchPlan plan) -> do
      -- Creation uses original GameT CardGen on this candidate's real RNG and
      -- registry. Never reset that RNG by constructing another environment.
      env <- makeEnv snapshotGame []
      preparedMessages <- runCore env $ DebugWorkbench.materialize plan
      candidate <- readIORef $ coreGame env
      reply <- handleAnswerPure candidate player $ Raw $ Run preparedMessages
      case reply of
        Unhandled reason -> pure $ Left reason
        Handled messages -> do
          let routed = if DebugActions.regeneratedPause candidate
                then filter (\case AskMap {} -> False; _ -> True) messages
                else messages
              active = gameActivePlayerId candidate
              bracketed = if active==player then routed else SetActivePlayer player:routed<>[SetActivePlayer active]
          writeIORef (queueToRef $ coreQueue env) $ ClearUI:bracketed<>snapshotQueue
          advance env
          Right <$> finish snapshotHistoricalBootstrap (snapshotRevision+1) env
    Right (Debug.OriginalDebugMessage message) -> do
      -- Only prepareDebug can construct this message. Reuse original debug
      -- routing; never accept Raw through public Answer validation. Pass/Fail
      -- keep the original continuation but do not re-publish an obsolete Ask.
      reply <- handleAnswerPure snapshotGame player $ Raw message
      case reply of
        Unhandled reason -> pure $ Left reason
        Handled messages -> do
          let routed=if DebugActions.regeneratedPause snapshotGame
                then filter (\case AskMap {} -> False; _ -> True) messages
                else messages
              active=gameActivePlayerId snapshotGame
              bracketed=if active==player then routed else SetActivePlayer player:routed<>[SetActivePlayer active]
          env <- makeEnv snapshotGame $ ClearUI:bracketed<>snapshotQueue
          advance env
          Right <$> finish snapshotHistoricalBootstrap (snapshotRevision+1) env

createSession :: CreateRequest -> IO (Either Text (Snapshot, [ClientMessage]))
createSession=createSessionWith Nothing

-- Trusted event coordinator entry, not a JSON Message/option endpoint. The
-- original scenario consumes exactly these original Epic/ELSE metadata keys.
createEpicGroup :: EpicEnv -> [PlayerId] -> Int -> Difficulty -> Bool -> Bool -> IO (Either Text (Snapshot,[ClientMessage]))
createEpicGroup epic players seed difficulty tarot blobElse = fmap join $ boundary mempty do
  if null players || length players>4 || length (nub players)/=length players
    || any ((==UUID.nil) . unPlayerId) players || epicEnvId epic==UUID.nil
    then pure $ Left "Expected one to four distinct nonnil Epic players and event"
    else do
      let game=(if blobElse then setInitialScenarioMeta "variant" ("else"::Text) else id)
            $ setInitialScenarioMeta "blobThatAteEverythingElse" blobElse
            $ setInitialScenarioMeta "epicMultiplayer" True
            $ newScenario "85001" seed (length players) difficulty tarot
      env<-makeEnvWithEpic (Just epic) game []
      runCore env $ traverse_ addPlayer players
      advance env
      Right <$> finish Nothing 0 env

requiresEpicContext :: Game -> Bool
requiresEpicContext game=case gameMode game of
  That scenario->case Scenario.scenarioMeta $ toAttrs scenario of
    Object meta->KM.lookup "epicMultiplayer" meta==Just (Bool True)
    _->False
  _->False

createHistoricalSession :: CreateRequest -> IO (Either Text (Snapshot,[ClientMessage]))
createHistoricalSession request
  | createLabyrinthMiniCampaign request = pure $ Left "Labyrinth mini-campaign cannot use historical creation"
  | otherwise = case (createCampaign request,createScenario request) of
      (Just cid,Just sid)->case History.validateCreation cid sid of
        Left reason->pure $ Left reason
        Right bootstrap->createSessionWith (Just bootstrap) request
      _->pure $ Left "Historical creation requires an explicit campaign and starting scenario"

createSessionWith :: Maybe HistoricalBootstrap -> CreateRequest -> IO (Either Text (Snapshot,[ClientMessage]))
createSessionWith bootstrap request = fmap join $ boundary mempty $ resolve request
 where
  resolve CreateRequestData {..}
    | createLabyrinthMiniCampaign
    , isJust bootstrap || createCampaign /= Nothing || createScenario /= Just "70001" =
        pure $ Left "Labyrinth mini-campaign requires standalone 70001 without historical creation"
    | null createPlayers || length createPlayers > 4 = pure $ Left "Expected one to four players"
    | length (nub createPlayers) /= length createPlayers = pure $ Left "Duplicate player"
    | any ((== UUID.nil) . unPlayerId) createPlayers = pure $ Left "Nil player is not valid"
    | Nothing <- bootstrap,Just reason <- SettingsEngine.freshCampaignStartError createCampaign createScenario = pure $ Left reason
    | otherwise = case (createCampaign, createScenario) of
      (Nothing, Nothing) -> pure $ Left "Campaign or scenario required"
      _ -> do
        let count = length createPlayers
            game0 = case createCampaign of
              Just cid -> newCampaign cid createScenario createSeed count createDifficulty createTarot
              Nothing -> newScenario (fromJustNote "checked scenario" createScenario) createSeed count createDifficulty createTarot
            game = game0 {gameSettings = (gameSettings game0)
              { settingsUndoMode = createUndoMode
              , settingsAsIfRuling = fromMaybe (defaultAsIfRulingForCampaign $ unCampaignId <$> createCampaign) createAsIfRuling
              , settingsAchievementsEnabled = createAchievementsEnabled
              , settingsUltimatumsAndBoons = createUltimatumsAndBoons
              }}
        env <- makeEnv game []
        runCore env do
          traverse_ addPlayer createPlayers
          when createLabyrinthMiniCampaign $ push $ HandleOption PlayAsMiniCampaign
          traverse_ (push . HandleOption) createOptions
        advance env
        candidate<-readIORef $ coreGame env
        case traverse_ (`History.validateBootstrap` candidate) bootstrap of
          Left reason->pure $ Left reason
          Right ()->Right <$> finish bootstrap 0 env

-- This is a trusted-host entry point. Never accept arbitrary raw rule messages from UI.
applyAnswer :: Snapshot -> PlayerId -> Word64 -> Answer -> IO (Either Text (Snapshot, [ClientMessage]))
applyAnswer=applyAnswerWithCards mempty

applyAnswerWithMetadata :: Snapshot -> PlayerId -> Word64 -> Answer -> IO (Either Text (Snapshot, [ClientMessage], UndoMetadata))
applyAnswerWithMetadata = applyAnswerUsing Nothing mempty

-- The ordinary resolver stays authoritative. Only this trusted package envelope
-- can supply definitions before it resolves an original deck/overlay answer.
applyAuthorAnswer :: Snapshot -> PlayerId -> Word64 -> Bool -> Text -> Text -> Answer -> IO (Either Text (Snapshot,[ClientMessage]))
applyAuthorAnswer snapshot player expected enabled raw digest answer=case do
  unless enabled $ Left "Explicit trusted author confirmation is required"
  unless (expected==snapshotRevision snapshot&&player `elem` gamePlayers (snapshotGame snapshot)) $ Left "Stale revision or unknown author player"
  case answer of
    DeckListAnswer {}->Right ()
    UpgradeDeckAnswer {}->Right ()
    ReplaceInvestigatorAnswer {}->Right ()
    ApplyOverlayAnswer {}->Right ()
    _->Left "Author package answer must use an original deck, upgrade, replacement or overlay operation"
  package<-Author.validatePackage raw digest
  Author.mergePackage (gameCustomCards $ snapshotGame snapshot) package of
    Left reason->pure $ Left reason
    Right merged->applyAnswerWithCards merged snapshot player expected answer

applyAuthorInstall :: Snapshot -> PlayerId -> Word64 -> Bool -> Text -> Text -> IO (Either Text (Snapshot,[ClientMessage]))
applyAuthorInstall snapshot@SnapshotState{..} player expected enabled raw digest=fmap join $ boundary (gameCustomCards snapshotGame) $ do
  let prepared=do
        unless enabled $ Left "Explicit trusted author/debug mode is required"
        unless (expected==snapshotRevision&&snapshotRevision<maxBound) $ Left "Stale author revision"
        when (historyPending snapshot) $ Left "Finish historical setup before installing author content"
        DebugActions.stablePlayerWindow snapshotGame player
        package<-Author.validatePackage raw digest
        merged<-Author.mergePackage (gameCustomCards snapshotGame) package
        pure (package,merged)
  case prepared of
    Left reason->pure $ Left reason
    Right (package,merged)->do
      let originalStart=gameActionSnapshot $ restoreActionStart snapshotGame
          candidate=snapshotGame{gameCustomCards=merged,gameActionSnapshot=originalStart}
      replaceCustomCards merged
      reply<-handleAnswerPure candidate player $ Raw $ Run $ map DebugRegisterCustomCard $ Author.packageCards package
      case reply of
        Unhandled reason->pure $ Left reason
        Handled messages->do
          let routed=if DebugActions.regeneratedPause candidate then filter (\case AskMap {}->False;_->True) messages else messages
              active=gameActivePlayerId candidate
              bracketed=if active==player then routed else SetActivePlayer player:routed<>[SetActivePlayer active]
          env<-makeEnv candidate $ ClearUI:bracketed<>snapshotQueue
          advance env
          Right <$> finish snapshotHistoricalBootstrap (snapshotRevision+1) env

applyAnswerWithCards :: Map CardCode CustomCard -> Snapshot -> PlayerId -> Word64 -> Answer -> IO (Either Text (Snapshot,[ClientMessage]))
applyAnswerWithCards cards snapshot player expected answer =
  fmap withoutUndoMetadata $ applyAnswerUsing Nothing cards snapshot player expected answer

applyAnswerInEpic :: EpicEnv -> Snapshot -> PlayerId -> Word64 -> Answer -> IO (Either Text (Snapshot,[ClientMessage]))
applyAnswerInEpic epic snapshot player expected answer =
  fmap withoutUndoMetadata $ applyAnswerUsing (Just epic) mempty snapshot player expected answer

withoutUndoMetadata :: Either Text (Snapshot, [ClientMessage], UndoMetadata) -> Either Text (Snapshot, [ClientMessage])
withoutUndoMetadata = \case
  Left reason -> Left reason
  Right (snapshot, events, _) -> Right (snapshot, events)

applyAnswerUsing :: Maybe EpicEnv -> Map CardCode CustomCard -> Snapshot -> PlayerId -> Word64 -> Answer -> IO (Either Text (Snapshot,[ClientMessage],UndoMetadata))
applyAnswerUsing epic authorCards snapshot player expected answer = fmap join $ boundary mempty $ resolve snapshot
 where
  answerEnv originalGame game queue = do
    env <- withMetric "answer/makeEnv" $ makeEnvWithEpic epic game queue
    pure $ env {coreUndoInitialPhase = gamePhase originalGame}
  resolve SnapshotState {snapshotGame=originalGame,..} = do
    let snapshotGame=if Map.null authorCards then originalGame else originalGame
          {gameCustomCards=authorCards,gameActionSnapshot=gameActionSnapshot $ restoreActionStart originalGame}
    replaceCustomCards $ gameCustomCards snapshotGame
    let checks = do
          unless (isJust epic || not (requiresEpicContext snapshotGame)) $ Left "Epic member games require their event coordinator"
          unless (expected == snapshotRevision) $ Left "Stale revision"
          unless (snapshotRevision < maxBound) $ Left "Revision exhausted"
          unless (player `elem` gamePlayers snapshotGame) $ Left "Unknown player"
          case answer of
            SetTriggerModeAnswer {} -> pure ()
            _ -> unless (Map.member player $ gameQuestion snapshotGame) $ Left "Player is not being asked"
          unless (maybe True (== player) $ answerPlayer answer) $ Left "Answer player mismatch"
          when (historyPending snapshot) $ case (unwrapQuestion <$> Map.lookup player (gameQuestion snapshotGame),answer) of
            (Just PickCampaignSettings,HistoricalSettingsAnswer {})->pure ()
            (Just PickCampaignSettings,SetTriggerModeAnswer {})->pure ()
            (Just PickCampaignSettings,_)->Left "Historical initial settings cannot be bypassed by an ordinary or legacy answer"
            _->pure ()
    context <- case checks of
      Left _ -> pure Nothing
      Right () -> case answer of
        SetTriggerModeAnswer {} -> Just <$> readTriggerContext snapshotGame player
        AutoSkipTriggersAnswer {} -> Just <$> readTriggerContext snapshotGame player
        ResumeAutomaticTriggersAnswer {} -> Just <$> readTriggerContext snapshotGame player
        _ -> pure Nothing
    -- Only this guarded branch can prepare a form log. It is not a JSON
    -- constructor, Raw Message, legacy downgrade or broad validation bypass.
    prepared <- case checks of
      Left reason -> pure $ Left reason
      Right () -> case answer of
        SettingsFormAnswer form -> pure $ do
          schema <- SettingsEngine.resolveSchema snapshotGame player
          Just <$> SettingsEngine.compileSubmission schema form
        _ -> pure $ Right Nothing
    validated <- case checks of
      Left reason -> pure $ Left reason
      Right () -> case answer of
        SetTriggerModeAnswer change ->
          pure $ Triggers.validatePreference (fromJustNote "computed trigger context" context) snapshotGame player change
        AutoSkipTriggersAnswer {} ->
          pure $ Left "Frontend skip indexes are retired; use native automatic resume"
        ResumeAutomaticTriggersAnswer {} -> pure $
          unless (isJust $ Triggers.automaticSkip (fromJustNote "computed trigger context" context) snapshotGame player) $
            Left "No saved preference permits resuming this player's trigger window"
        SettingsFormAnswer {} -> pure $ void prepared
        HistoricalSettingsAnswer draft -> pure $ void $ do
          resolved<-historicalResolved snapshot player expected
          History.compileHistory resolved draft
        _ -> pure $ validateAnswer snapshotGame player answer
    case validated of
      Left reason -> pure $ Left reason
      Right () | SetTriggerModeAnswer change <- answer -> pure do
        -- Apply only to the original snapshot, not a hydrated/author-overlay
        -- environment. A preference must not alter action diffs, RNG, pending
        -- questions or the queue, and never settles automatic game choices.
        game <- Triggers.applyPreference (fromJustNote "computed trigger context" context) originalGame player change
        let snapshot = SnapshotState (snapshotRevision+1) game snapshotQueue snapshotHistoricalBootstrap
            metadata = UndoMetadata
              { schemaVersion = 1
              , metadataComplete = True
              , hasRandomResult = False
              , intermissionBoundary = False
              , phaseEdges = []
              }
        pure (snapshot,[],metadata)
      Right () | ResumeAutomaticTriggersAnswer {} <- answer -> do
        env <- answerEnv originalGame snapshotGame snapshotQueue
        void $ settleAutomaticTriggers env
        Right <$> finishWithUndoMetadata (isIntermissionAnswer answer) snapshotHistoricalBootstrap (snapshotRevision+1) env
      Right () | HistoricalSettingsAnswer draft <- answer -> case do
        resolved<-historicalResolved snapshot player expected
        History.compileHistory resolved draft of
          Left reason->pure $ Left reason
          Right plan->do
            -- One candidate owns all CardIds/RNG/registry and the original
            -- continuation. A failed materialization never changes the caller's
            -- old snapshot/handle. Bootstrap is marked only in the final result.
            env<-answerEnv originalGame (History.planGame plan) []
            messages<-runCore env $ History.materialize plan
            alreadyPushed<-readIORef $ queueToRef $ coreQueue env
            let active=gameActivePlayerId snapshotGame
                install=alreadyPushed<>messages
                bracketed=if active==player then install else SetActivePlayer player:install<>[SetActivePlayer active]
                installed=fmap (\b->b {bootstrapPhase=HistoryInstalled}) snapshotHistoricalBootstrap
            writeIORef (queueToRef $ coreQueue env) $ ClearUI:bracketed<>snapshotQueue
            advance env
            Right <$> finishWithUndoMetadata (isIntermissionAnswer answer) installed (snapshotRevision+1) env
      Right () -> do
        reply <- case prepared of
          Right (Just messages) -> pure $ Handled messages
          _ -> handleAnswerPure snapshotGame player answer
        case reply of
          Unhandled reason -> pure $ Left reason
          Handled messages -> do
            let active = gameActivePlayerId snapshotGame
                bracketed = if active == player then messages else SetActivePlayer player : messages <> [SetActivePlayer active]
            (sync,blocked)<-case epic of
              Nothing->pure ([],False)
              Just context->do
                shared<-readIORef $ epicEnvSharedRef context
                let gate=any (\stage->sharedCounter (AwaitingOrganizer stage) shared>0) $ actProgressStages shared
                    continues=any (\case NextAdvanceActStep {}->True;_->False) messages
                pure (EpicShared.epicSyncMessages (epicEnvGroup context) shared,gate&&continues)
            if blocked then pure $ Left "Epic act advance is waiting for organizer allocation"
            else do
              env <- answerEnv originalGame snapshotGame (ClearUI : sync <> bracketed <> snapshotQueue)
              advance env
              Right <$> finishWithUndoMetadata (isIntermissionAnswer answer) snapshotHistoricalBootstrap (snapshotRevision + 1) env

isIntermissionAnswer :: Answer -> Bool
isIntermissionAnswer = \case
  CampaignStepAnswerFor _ True _ -> True
  _ -> False

exportSnapshot :: Snapshot -> BL.ByteString
exportSnapshot = encode

importSnapshot :: BL.ByteString -> IO (Either Text Snapshot)
importSnapshot bytes = fmap join $ boundary mempty do
  case eitherDecode bytes of
    Left reason -> pure $ Left $ T.pack reason
    Right snapshot -> do
      _ <- E.evaluate $ BL.length $ exportSnapshot snapshot
      pure $ Right snapshot
