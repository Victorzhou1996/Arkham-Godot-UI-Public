-- In-process orchestration of the original Oct15 Epic seams. No HTTP/SQL.
-- Persistence/receipts atomically commit the entire returned event candidate.
module Arkham.Native.Epic.Engine where

import Arkham.Prelude hiding (error, ensure)
import Arkham.Native.Epic.Types
import Arkham.Native.Epic.Shared
import Arkham.Native.Session qualified as Core
import Arkham.Native.Answer (Answer)
import Arkham.Native.Events (eventJSON)
import Arkham.Epic.Types hiding (groupOrdinal)
import Arkham.Game (Game (..))
import Arkham.Game.State (GameState (..))
import Arkham.Phase (Phase (InvestigationPhase))
import Arkham.Message (Message (..))
import Arkham.Id
import Arkham.Card.CardCode (CardCode (..))
import Arkham.Card (toCardCode)
import Arkham.Card.CustomCard (CustomCard)
import Arkham.Classes.Entity
import Arkham.Classes.GameLogger (ClientMessage)
import Arkham.Entities
import Arkham.Scenario.Types (getMetaKeyDefault)
import Arkham.Agenda.Types (AgendaAttrs (agendaSequence))
import Arkham.Agenda.Sequence qualified as Agenda
import Arkham.Agenda.CardDefs.TheBlobThatAteEverything qualified as Agendas
import Arkham.Asset.Types (Asset, AssetAttrs (assetController,assetOwner,assetPlacement))
import Arkham.Investigator.Types (Investigator, InvestigatorAttrs (investigatorPlacement,investigatorPlayerId))
import Arkham.Treachery.Types (TreacheryAttrs (treacheryPlacement))
import Arkham.Event.Types (EventAttrs (eventController))
import Arkham.Effect.Types (EffectAttrs (effectTarget))
import Arkham.Location.Types (LocationAttrs (locationId,locationCardCode))
import Arkham.Placement
import Arkham.Source (Source (GameSource))
import Arkham.Target (Target (InvestigatorTarget,LocationTarget,EnemyTarget))
import Arkham.Native.StrictJSON (validateJsonProperties)
import Control.Monad.Trans.Except
import Control.Exception qualified as E
import Crypto.Hash.SHA256 qualified as SHA
import Data.ByteString.Base16 qualified as B16
import Data.ByteString.Lazy qualified as BL
import Data.Aeson.Types (parseEither)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Bits
import Data.UUID qualified as UUID
import Data.UUID (UUID)
import Data.Text qualified as T
import Relude (error)

type EventM = ExceptT Text IO

ensure :: Bool -> Text -> EventM ()
ensure b reason = unless b $ throwE reason

liftResult :: IO (Either Text a) -> EventM a
liftResult action = ExceptT action

parsed :: FromJSON a => Value -> Either Text a
parsed = first T.pack . parseEither parseJSON

toCore :: GroupSnapshot -> Core.Snapshot
toCore GroupSnapshot{..} = Core.Snapshot groupCoreRevision groupGame groupQueue

fromCore :: Core.Snapshot -> GroupSnapshot
fromCore s = GroupSnapshot (Core.snapshotRevision s) (Core.snapshotGame s) (Core.snapshotQueue s)

selectedGroup :: EventSnapshot -> UUID -> Either Text EventGroup
selectedGroup event gid = case filter ((==gid) . groupGameId) $ eventGroups event of
  [g] -> Right g
  _ -> Left "Unknown Epic group"

putGroup :: EventGroup -> EventSnapshot -> EventSnapshot
putGroup g e = e {eventGroups=map (\old -> if groupGameId old==groupGameId g then g else old) $ eventGroups e}

increment :: Word64 -> EventM Word64
increment n = do
  ensure (toInteger n < toInteger (maxBound::Int64)) "Epic revision exhausted"
  pure $ n+1

-- FFI must use this registry while forcing the final event response too.
-- Conflicting custom definitions cannot safely share one serialized event.
eventCustomCards :: EventSnapshot -> Either Text (Map CardCode CustomCard)
eventCustomCards event = foldM merge mempty $ map (gameCustomCards . groupGame . groupSnapshot) $ eventGroups event
 where
  merge acc cards = foldM add acc $ Map.toList cards
  add acc (k,v) = case Map.lookup k acc of
    Just old | toJSON old /= toJSON v -> Left "Epic groups contain conflicting custom definitions"
    _ -> Right $ Map.insert k v acc

digest :: BL.ByteString -> Text
digest = decodeUtf8 . B16.encode . SHA.hash . BL.toStrict

snapshotDigest :: GroupSnapshot -> EventM Text
snapshotDigest s = liftResult $ Core.boundary (gameCustomCards $ groupGame s) $ do
  let sha=digest $ encode s
  _ <- E.evaluate $ T.length sha
  pure sha

-- Takes the historical event selected by beforeEventRevision, NOT the current
-- event. FFI must encode this under eventCustomCards. The controller transports
-- snapshotText verbatim and never hashes a managed reserialization.
extractUndoBase :: EventSnapshot -> UUID -> Either Text Value
extractUndoBase event gid = do
  g <- selectedGroup event gid
  let bytes=encode $ groupSnapshot g
  pure $ object ["snapshotText" .= decodeUtf8 (BL.toStrict bytes),"sha256" .= digest bytes]

-- Run under the event registry at the FFI boundary. This does not advance any
-- queue. The same checks are used for import validation and actual Undo.
verifiedUndoSnapshot :: EventSnapshot -> UUID -> UUID -> UndoBase -> Either Text GroupSnapshot
verifiedUndoSnapshot event gid aid base = do
  g <- selectedGroup event gid
  h <- maybe (Left "Unknown retained undo action") Right $
    find ((==aid) . actionId) $ groupHistory g
  unless (beforeSequence h >= groupUndoFloor g) $ Left "Undo crosses a shared-event checkpoint"
  let bytes=encodeUtf8 $ undoSnapshotText base
  validateJsonProperties bytes
  unless (digest (BL.fromStrict bytes)==undoSha256 base && undoSha256 base==beforeSnapshotSha256 h) $
    Left "Undo base SHA mismatch"
  old <- first (const "Invalid undo snapshot") $ eitherDecodeStrict' @GroupSnapshot bytes
  unless (groupCoreRevision old==beforeCoreRevision h && gamePlayers (groupGame old)==beforeMembers h) $
    Left "Undo base revision or members mismatch"
  unless (beforeMembers h==groupMembers g) $ Left "Undo would cross group membership change"
  pure old

verifyUndoBase :: EventSnapshot -> UUID -> UUID -> UndoBase -> Either Text ()
verifyUndoBase event gid aid base = void $ verifiedUndoSnapshot event gid aid base

runEvent :: EventM (EventSnapshot,[Value]) -> IO (Either Text (EventSnapshot,[Value]))
runEvent body = do
  result <- E.try @E.SomeException $ runExceptT do
    pair@(e,logs) <- body
    registry <- either throwE pure $ eventCustomCards e
    liftResult $ Core.boundary registry do
      _ <- E.evaluate $ BL.length $ encode (e,logs)
      pure pair
  pure $ case result of
    Left _ -> Left "Epic operation failed; no event candidate committed"
    Right r -> r

newContext :: EventSnapshot -> EventGroup -> EventM EpicEnv
newContext e g = liftIO $ EpicEnv (eventId e) (GroupOrdinal $ groupOrdinal g)
  <$> newIORef (eventShared e) <*> newIORef []

logEnvelope :: UUID -> [ClientMessage] -> Value
logEnvelope gid logs = object ["gameId" .= gid,"events" .= map eventJSON logs]

-- Original Games.Shared.runMessagesInGroupCore: fresh server-message queue,
-- no EpicEnv (no feedback), produced continuation BEFORE old continuation.
serverRun :: (Game -> Bool) -> [Message] -> EventGroup -> EventM (EventGroup,[Value])
serverRun predicate messages g
  | gameGameState old /= IsActive || not (predicate old) = pure (g,[])
  | otherwise = do
      seqn <- increment $ groupSequence g
      coreRev <- increment $ groupCoreRevision $ groupSnapshot g
      (s,logs) <- liftResult $ Core.boundary (gameCustomCards old) do
        env <- Core.makeEnvWithEpic Nothing old messages
        Core.advanceRules env
        (result,events) <- Core.finish Nothing coreRev env
        let combined=result {Core.snapshotQueue=Core.snapshotQueue result <> groupQueue (groupSnapshot g)}
        _ <- E.evaluate $ BL.length $ encode combined
        _ <- E.evaluate $ BL.length $ encode $ logEnvelope (groupGameId g) events
        pure (combined,events)
      pure (g {groupSequence=seqn,groupSnapshot=fromCore s},[logEnvelope (groupGameId g) logs])
 where old=groupGame $ groupSnapshot g

syncAll :: EventSnapshot -> EventM (EventSnapshot,[Value])
syncAll e = do
  results <- for (eventGroups e) $ \g ->
    serverRun (const True) (epicSyncMessages (GroupOrdinal $ groupOrdinal g) $ eventShared e) g
  pure (e {eventGroups=map fst results},concatMap snd results)

floorGroup :: EventGroup -> EventGroup
floorGroup g = g {groupUndoFloor=groupSequence g,groupHistory=[]}

actFingerprint :: Game -> [ActId]
actFingerprint = sort . Map.keys . entitiesActs . gameEntities

createEvent :: EpicCreate -> IO (Either Text (EventSnapshot,[Value]))
createEvent request = runEvent do
  -- Validate direct Haskell callers as well as JSON callers.
  c <- either throwE pure $ parsed @EpicCreate $ toJSON request
  let def=createDefinition c
      total=sum $ map (length . createPlayers) $ createGroups c
      -- Single persisted event seed, deterministic across retries. Original
      -- uses one random seed in [0,999999]; derive it from the caller's UUID.
      seed=foldl' (\n ch -> (n*33+fromEnum ch) `mod` 1000000) 0 $ UUID.toString $ createEventId c
      shared=setSharedCounter TimeLimitMinutes (eventTimeLimitMinutes def)
        $ setSharedCounter BlobStorySeed seed
        $ setSharedCounter Countermeasures ((total+1) `div` 2)
        $ setSharedCounter (SharedEnemyHealth $ CardCode "85037") (15*total)
        $ setSharedCounter (SharedActProgress 1) 0 $ emptySharedEventState total
      emptyEvent=EventSnapshot (createEventId c) 0 def shared []
  results <- for (createGroups c) $ \cg -> do
    ctx <- liftIO $ EpicEnv (createEventId c) (GroupOrdinal $ createOrdinal cg)
      <$> newIORef shared <*> newIORef []
    (s,logs) <- liftResult $ Core.createEpicGroup ctx (createPlayers cg) (createSeed cg)
      (eventDifficulty def) (eventTarot def) (eventBlobElse def)
    deltas <- liftIO $ readIORef $ epicEnvDeltaRef ctx
    ensure (null deltas) "Epic creation unexpectedly emitted shared deltas before setup answers"
    let g=EventGroup (createGameId cg) (createOrdinal cg) (createName cg)
          (length $ createPlayers cg) (createPlayers cg) 0 (fromCore s) 0 []
    pure (g,logEnvelope (createGameId cg) logs)
  pure (emptyEvent {eventGroups=map fst results},map snd results)

applyEvent :: EpicApply -> IO (Either Text (EventSnapshot,[Value]))
applyEvent request = runEvent do
  -- Input decoded at FFI; repeat structure validation under the union registry
  -- to protect direct native callers without nesting rule-operation boundaries.
  registry <- either throwE pure $ eventCustomCards $ applySnapshot request
  a <- liftResult $ Core.boundary registry $
    either (E.throwIO . userError . T.unpack) pure $ parsed @EpicApply $ toJSON request
  let e=applySnapshot a
      shared=eventShared e
      get gid=either throwE pure $ selectedGroup e gid
      originalRevision=eventRevision e
  nextRev <- increment originalRevision
  ensure (sharedVersion shared < maxBound) "Shared revision exhausted"
  ensure (all ((/=operationId a) . actionId) $ concatMap groupHistory $ eventGroups e)
    "Operation ID already belongs to a retained action; use the host receipt"
  (candidate,logs) <- case applyCommand a of
    GroupAnswer gid _ player raw -> do
      g <- get gid
      let old=groupGame $ groupSnapshot g
      ensure (gameInSetup old || gamePhase old /= InvestigationPhase
        || sharedCounter TimerStartedAt shared > 0) "All groups must be ready before investigation play"
      response <- either throwE pure $ parsed @Answer raw
      -- Session's single original Handled branch enforces the organizer
      -- NextAdvanceActStep gate. Do not call the answer resolver twice here.
      ctx <- newContext e g
      (s,events) <- liftResult $ Core.applyAnswerInEpic ctx (toCore $ groupSnapshot g) player
        (groupCoreRevision $ groupSnapshot g) response
      deltas <- liftIO $ readIORef $ epicEnvDeltaRef ctx
      let (updated,effective)=applyBoundedDeltas shared deltas
          armed=armActAdvanceGates updated
          shared'=bumpSharedRevision shared armed
      sha <- snapshotDigest $ groupSnapshot g
      seqn <- increment $ groupSequence g
      let h=ActionHistory (operationId a) nextRev (groupSequence g) seqn originalRevision sha
            (groupCoreRevision $ groupSnapshot g) (groupMembers g) effective
          nextGroup=g {groupSequence=seqn,groupSnapshot=fromCore s
            ,groupMembers=gamePlayers $ Core.snapshotGame s,groupHistory=groupHistory g<>[h]}
          changedAct=actFingerprint old /= actFingerprint (Core.snapshotGame s)
          g'=if changedAct then floorGroup nextGroup else nextGroup
      (synced,syncLogs) <- syncAll $ (putGroup g' e) {eventShared=shared'}
      pure (synced,logEnvelope gid events:syncLogs)
    GroupReady gid _ -> do
      g <- get gid
      let game=groupGame $ groupSnapshot g
      ensure (gameGameState game==IsActive && not (gameInSetup game)
        && gamePhase game==InvestigationPhase) "Group is not ready for the initial investigation"
      let mask=sharedCounter GroupsReadyMask shared .|. (1 `shiftL` groupOrdinal g)
          full=(1 `shiftL` length (eventGroups e))-1
          ready=setSharedCounter GroupsReadyMask mask shared
          started=if mask==full && sharedCounter TimerStartedAt shared==0
            then setSharedCounter TimerStartedAt (fromIntegral $ nowUnixSeconds a) ready else ready
      (synced,events) <- syncAll $ e {eventShared=bumpSharedRevision shared started}
      -- Ready is a shared barrier, not an independent reversible group answer.
      -- Undo must not restore pre-setup while its ready bit/timer stays released.
      pure (synced {eventGroups=map (\x -> if groupGameId x==gid then floorGroup x else x)
        $ eventGroups synced},events)
    ResolveAdvance stage allocations -> do
      ensure (sharedCounter (AwaitingOrganizer stage) shared==1) "No organizer allocation is pending"
      let threshold=2*sharedTotalInvestigators shared
      ensure (sum (map (toInteger . snd) allocations)==toInteger threshold) "Allocation must equal the shared threshold"
      for_ allocations $ \(ord,spend) ->
        ensure (spend <= sharedCounter (ActContribution stage $ GroupOrdinal ord) shared) "Allocation exceeds group contribution"
      let spent=foldl' (\st g -> setSharedCounter (ActSpend stage $ GroupOrdinal $ groupOrdinal g)
            (fromMaybe 0 $ lookup (groupOrdinal g) allocations) st) shared $ eventGroups e
          settled=setSharedCounter (AwaitingOrganizer stage) 0
            $ updateSharedCounter (+1) (ActAdvanceGen stage)
            $ setSharedCounter (SharedActProgress stage) 0 spent
      (synced,events) <- syncAll $ e {eventShared=bumpSharedRevision shared settled}
      pure (synced {eventGroups=map floorGroup $ eventGroups synced},events)
    TimeUp -> do
      let start=sharedCounter TimerStartedAt shared
          minutes=sharedCounter TimeLimitMinutes shared
      ensure (minutes>0 && start>0 && toInteger (nowUnixSeconds a)>=toInteger start+60*toInteger minutes)
        "Event time limit has not elapsed"
      results <- for (eventGroups e) $ serverRun
        (not . any ((>=3) . Agenda.agendaSequenceStep . attr agendaSequence) . Map.elems . entitiesAgendas . gameEntities)
        [AdvanceToAgenda 1 Agendas.theAnomalyConsumes Agenda.B GameSource]
      -- These organizer messages cannot be reconstructed by shared counters
      -- alone. Never let an older group snapshot erase their durable effect.
      let groups=zipWith (\old (new,_) -> if groupSequence new/=groupSequence old then floorGroup new else new)
            (eventGroups e) results
      pure (e {eventGroups=groups},concatMap snd results)
    Replicate gid code target -> do
      g <- get gid
      let game=groupGame $ groupSnapshot g
          entities=gameEntities game
          validTarget=case target of
            LocationTarget lid -> Map.member lid $ entitiesLocations entities
            InvestigatorTarget iid -> Map.member iid $ entitiesInvestigators entities
            EnemyTarget eid -> Map.member eid $ entitiesEnemies entities
            _ -> False
      ensure (validTarget && gameGameState game==IsActive) "Replicate target is not an active group entity"
      (g',events) <- serverRun (const True)
        [ScenarioSpecific "blobRequestAberration" (toJSON (code,target))] g
      pure (putGroup (floorGroup g') e,events)
    SwapMainStreet first second -> swapGroups e first second
    CorrectCountermeasures value -> do
      let d=SharedDelta (UUID.toText $ operationId a) Countermeasures
            (value-sharedCounter Countermeasures shared)
          (changed,_)=applyBoundedDeltas shared [d]
      syncAll $ e {eventShared=bumpSharedRevision shared $ armActAdvanceGates changed}
    UndoGroup gid _ aid -> do
      g <- get gid
      let (keep,removed)=break ((==aid) . actionId) $ groupHistory g
      h <- maybe (throwE "Unknown undo action") pure $ headMay removed
      ensure (beforeSequence h >= groupUndoFloor g) "Undo crosses a shared-event checkpoint"
      base <- maybe (throwE "Undo base required") pure $ undoBase a
      old <- liftResult $ Core.boundary registry $
        either (E.throwIO . userError . T.unpack) pure $ verifiedUndoSnapshot e gid aid base
      let reversed=foldl' (flip revertDelta) shared $ concatMap (reverse . effectiveDeltas) $ reverse removed
      seqn <- increment $ groupSequence g
      rev <- increment $ groupCoreRevision $ groupSnapshot g
      let restored=g {groupSequence=seqn,groupSnapshot=old {groupCoreRevision=rev},groupHistory=keep}
      syncAll $ (putGroup restored e) {eventShared=bumpSharedRevision shared reversed}
  pure (candidate {eventRevision=nextRev},logs)

-- No hidden hands or raw group snapshots in organizer output.
organizerView :: EventSnapshot -> Value
organizerView e = object
  ["eventId" .= eventId e,"revision" .= eventRevision e,"definition" .= eventDefinition e
  ,"shared" .= eventShared e
  ,"groups" .= [object ["gameId" .= groupGameId g,"ordinal" .= groupOrdinal g,"name" .= groupName g
      ,"members" .= groupMembers g,"sequence" .= groupSequence g,"undoFloor" .= groupUndoFloor g
      ,"state" .= gameGameState (groupGame $ groupSnapshot g)
      ,"phase" .= gamePhase (groupGame $ groupSnapshot g)
      ,"ready" .= testBit (sharedCounter GroupsReadyMask $ eventShared e) (groupOrdinal g)
      ,"mainStreetReady" .= sharedCounter (MainStreetReady $ GroupOrdinal $ groupOrdinal g) (eventShared e)
      ,"replicateTargets" .= replicateTargets g] | g <- eventGroups e]]
 where
  replicateTargets g
    | not (eventBlobElse $ eventDefinition e) = []
    | otherwise =
        let entities=gameEntities $ groupGame $ groupSnapshot g
            entry target card kind=object ["target" .= target,"cardCode" .= card,"kind" .= (kind::Text)]
         in [entry (LocationTarget lid) (toCardCode l) "location" | (lid,l)<-Map.toList $ entitiesLocations entities]
            <> [entry (InvestigatorTarget iid) (toCardCode i) "investigator" | (iid,i)<-Map.toList $ entitiesInvestigators entities]
            <> [entry (EnemyTarget eid) (toCardCode x) "enemy" | (eid,x)<-Map.toList $ entitiesEnemies entities]

-- Event-scoped authorization follows the player's latest unique membership,
-- not a stale client-supplied group. Public summary has no raw shared map,
-- private group roster/history, replication targets or undo floors.
networkPlayerGroup :: EventSnapshot -> PlayerId -> Either Text EventGroup
networkPlayerGroup e player = case filter (elem player . groupMembers) $ eventGroups e of
  [g] | player `elem` gamePlayers (groupGame $ groupSnapshot g) -> Right g
  _ -> Left "Unknown or ambiguous Epic player membership"

networkSharedSummary :: EventSnapshot -> Value
networkSharedSummary e = object
  ["schemaVersion" .= (1::Int),"totalGroups" .= length (eventGroups e)
  ,"totalInvestigators" .= sharedTotalInvestigators shared
  ,"countermeasures" .= sharedCounter Countermeasures shared
  ,"timeLimitMinutes" .= eventTimeLimitMinutes (eventDefinition e)
  ,"timerStartedAt" .= sharedCounter TimerStartedAt shared
  ,"groupsReady" .= length [g | g <- eventGroups e,testBit ready (groupOrdinal g)]
  ,"awaitingOrganizer" .= any (\stage -> sharedCounter (AwaitingOrganizer stage) shared > 0) (actProgressStages shared)]
 where
  shared=eventShared e
  ready=sharedCounter GroupsReadyMask shared

swapGroups :: EventSnapshot -> UUID -> UUID -> EventM (EventSnapshot,[Value])
swapGroups e first second = do
  ensure (eventBlobElse $ eventDefinition e) "Main Street requires ELSE variant"
  a <- either throwE pure $ selectedGroup e first
  b <- either throwE pure $ selectedGroup e second
  let ga=groupGame $ groupSnapshot a
      gb=groupGame $ groupSnapshot b
      scenario g=case gameMode g of That s -> Just s; _ -> Nothing
      ready g=scenario g >>= (\s -> getMetaKeyDefault "mainStreetReady" Nothing $ toAttrs s)
      street g=attr locationId <$> find ((==CardCode "89006") . attr locationCardCode)
        (Map.elems $ entitiesLocations $ gameEntities g)
  ia <- maybe (throwE "First group has not activated Main Street") pure $ ready ga
  ib <- maybe (throwE "Second group has not activated Main Street") pure $ ready gb
  da <- maybe (throwE "First Main Street location missing") pure $ street ga
  db <- maybe (throwE "Second Main Street location missing") pure $ street gb
  ensure (ia /= ib && Map.notMember ia (entitiesInvestigators $ gameEntities gb)
    && Map.notMember ib (entitiesInvestigators $ gameEntities ga)) "Swap would overwrite an investigator"
  ensure (gameGameState ga==IsActive && gameGameState gb==IsActive) "Swap requires two active groups"
  ensure (sharedCounter (MainStreetReady $ GroupOrdinal $ groupOrdinal a) (eventShared e)>0
    && sharedCounter (MainStreetReady $ GroupOrdinal $ groupOrdinal b) (eventShared e)>0)
    "Both groups must have current Main Street readiness"
  let (ga',gb',_,_)=swapInvestigatorState ia db ga ib da gb
  registry <- either throwE pure $ eventCustomCards e
  sa <- increment $ groupSequence a
  sb <- increment $ groupSequence b
  ra <- increment $ groupCoreRevision $ groupSnapshot a
  rb <- increment $ groupCoreRevision $ groupSnapshot b
  let a'=a {groupSequence=sa,groupSnapshot=(groupSnapshot a) {groupGame=ga' {gameCustomCards=registry},groupCoreRevision=ra},groupMembers=gamePlayers ga'}
      b'=b {groupSequence=sb,groupSnapshot=(groupSnapshot b) {groupGame=gb' {gameCustomCards=registry},groupCoreRevision=rb},groupMembers=gamePlayers gb'}
      shared=eventShared e
      cleared=updateSharedCounter (subtract 1) (MainStreetReady $ GroupOrdinal $ groupOrdinal a)
        $ updateSharedCounter (subtract 1) (MainStreetReady $ GroupOrdinal $ groupOrdinal b) shared
  (synced,logs) <- syncAll $ (putGroup b' $ putGroup a' e) {eventShared=bumpSharedRevision shared cleared}
  pure (synced {eventGroups=map (\g -> if groupGameId g `elem` [first,second] then floorGroup g else g) $ eventGroups synced},logs)

-- Mechanical extraction: fixed Api/Handler/Arkham/Games/Shared.hs:1343-1506.
swapInvestigatorState
  :: InvestigatorId
  -> LocationId
  -> Game
  -> InvestigatorId
  -> LocationId
  -> Game
  -> (Game, Game, PlayerId, PlayerId)
swapInvestigatorState firstIid firstDestination firstGame secondIid secondDestination secondGame
  | attr investigatorPlacement firstInvestigator /= AtLocation secondDestination =
      error "First ready investigator is no longer at Main Street"
  | attr investigatorPlacement secondInvestigator /= AtLocation firstDestination =
      error "Second ready investigator is no longer at Main Street"
  | otherwise =
      ( install secondIid secondMoved secondOwned secondPid (remove firstIid firstPid firstGame)
      , install firstIid firstMoved firstOwned firstPid (remove secondIid secondPid secondGame)
      , firstPid
      , secondPid
      )
 where
  firstInvestigator =
    fromMaybe (error "First Main Street investigator is not in its game")
      $ Map.lookup firstIid (entitiesInvestigators $ gameEntities firstGame)
  secondInvestigator =
    fromMaybe (error "Second Main Street investigator is not in its game")
      $ Map.lookup secondIid (entitiesInvestigators $ gameEntities secondGame)
  firstPid = attr investigatorPlayerId firstInvestigator
  secondPid = attr investigatorPlayerId secondInvestigator
  firstMoved = overAttrs (\a -> a {investigatorPlacement = AtLocation secondDestination}) firstInvestigator
  secondMoved = overAttrs (\a -> a {investigatorPlacement = AtLocation firstDestination}) secondInvestigator
  firstOwned = ownedEntities firstIid (gameEntities firstGame)
  secondOwned = ownedEntities secondIid (gameEntities secondGame)

  remove iid pid game =
    game
      { gameEntities = removeOwned iid (gameEntities game)
      , gamePlayers = filter (/= pid) (gamePlayers game)
      , gamePlayerOrder = filter (/= iid) (gamePlayerOrder game)
      , gameInHandEntities = Map.delete iid (gameInHandEntities game)
      , gameInDiscardEntities = Map.delete iid (gameInDiscardEntities game)
      , gamePhaseHistory = Map.delete iid (gamePhaseHistory game)
      , gameTurnHistory = Map.delete iid (gameTurnHistory game)
      , gameRoundHistory = Map.delete iid (gameRoundHistory game)
      , gameQuestion = Map.delete pid (gameQuestion game)
      , gameModifiers = Map.delete (InvestigatorTarget iid) (gameModifiers game)
      , gameCardUses = Map.map (filter (/= iid)) (gameCardUses game)
      }

  install iid investigator owned pid game =
    game
      { gameEntities = addOwned investigator owned (gameEntities game)
      , gamePlayers = gamePlayers game <> [pid]
      , gamePlayerOrder = gamePlayerOrder game <> [iid]
      , gameInHandEntities =
          copyMapEntry
            iid
            (if iid == firstIid then gameInHandEntities firstGame else gameInHandEntities secondGame)
            (gameInHandEntities game)
      , gameInDiscardEntities =
          copyMapEntry
            iid
            (if iid == firstIid then gameInDiscardEntities firstGame else gameInDiscardEntities secondGame)
            (gameInDiscardEntities game)
      , gamePhaseHistory =
          copyMapEntry
            iid
            (if iid == firstIid then gamePhaseHistory firstGame else gamePhaseHistory secondGame)
            (gamePhaseHistory game)
      , gameTurnHistory =
          copyMapEntry
            iid
            (if iid == firstIid then gameTurnHistory firstGame else gameTurnHistory secondGame)
            (gameTurnHistory game)
      , gameRoundHistory =
          copyMapEntry
            iid
            (if iid == firstIid then gameRoundHistory firstGame else gameRoundHistory secondGame)
            (gameRoundHistory game)
      , gameQuestion =
          copyMapEntry
            pid
            (if pid == firstPid then gameQuestion firstGame else gameQuestion secondGame)
            (gameQuestion game)
      , gameModifiers =
          copyMapEntry
            (InvestigatorTarget iid)
            (if iid == firstIid then gameModifiers firstGame else gameModifiers secondGame)
            (gameModifiers game)
      , gameCardUses =
          transferCardUses
            iid
            (if iid == firstIid then gameCardUses firstGame else gameCardUses secondGame)
            (gameCardUses game)
      , gameActiveInvestigatorId = replaceId firstIid secondIid iid (gameActiveInvestigatorId game)
      , gameTurnPlayerInvestigatorId =
          replaceId firstIid secondIid iid <$> gameTurnPlayerInvestigatorId game
      , gameLeadInvestigatorId = replaceId firstIid secondIid iid (gameLeadInvestigatorId game)
      , gameActivePlayerId =
          if gameActivePlayerId game `elem` [firstPid, secondPid] then pid else gameActivePlayerId game
      }

  replaceId removedA removedB inserted current
    | current == removedA || current == removedB = inserted
    | otherwise = current

transferCardUses
  :: InvestigatorId
  -> Map CardCode [InvestigatorId]
  -> Map CardCode [InvestigatorId]
  -> Map CardCode [InvestigatorId]
transferCardUses iid source destination =
  Map.unionWith
    (<>)
    (Map.map (const [iid]) $ Map.filter (elem iid) source)
    (Map.map (filter (/= iid)) destination)

copyMapEntry :: Ord key => key -> Map key value -> Map key value -> Map key value
copyMapEntry key source destination = maybe destination (\value -> Map.insert key value destination) (Map.lookup key source)

ownedEntities :: InvestigatorId -> Entities -> Entities
ownedEntities iid entities =
  mempty
    { entitiesAssets = Map.filter (assetBelongsTo iid) (entitiesAssets entities)
    , entitiesTreacheries =
        Map.filter
          ((`elem` [InThreatArea iid, AttachedToInvestigator iid]) . attr treacheryPlacement)
          (entitiesTreacheries entities)
    , entitiesEvents = Map.filter ((== iid) . attr eventController) (entitiesEvents entities)
    , entitiesEffects =
        Map.filter ((== InvestigatorTarget iid) . attr effectTarget) (entitiesEffects entities)
    }

removeOwned :: InvestigatorId -> Entities -> Entities
removeOwned iid entities =
  entities
    { entitiesInvestigators = Map.delete iid (entitiesInvestigators entities)
    , entitiesAssets = Map.filter (not . assetBelongsTo iid) (entitiesAssets entities)
    , entitiesTreacheries =
        Map.filter
          (not . (`elem` [InThreatArea iid, AttachedToInvestigator iid]) . attr treacheryPlacement)
          (entitiesTreacheries entities)
    , entitiesEvents = Map.filter ((/= iid) . attr eventController) (entitiesEvents entities)
    , entitiesEffects =
        Map.filter ((/= InvestigatorTarget iid) . attr effectTarget) (entitiesEffects entities)
    }

addOwned :: Investigator -> Entities -> Entities -> Entities
addOwned investigator owned entities =
  entities
    { entitiesInvestigators = Map.insert investigator.id investigator (entitiesInvestigators entities)
    , entitiesAssets = entitiesAssets owned <> entitiesAssets entities
    , entitiesTreacheries = entitiesTreacheries owned <> entitiesTreacheries entities
    , entitiesEvents = entitiesEvents owned <> entitiesEvents entities
    , entitiesEffects = entitiesEffects owned <> entitiesEffects entities
    }

assetBelongsTo :: InvestigatorId -> Asset -> Bool
assetBelongsTo iid asset =
  attr assetController asset
    == Just iid
    || attr assetOwner asset
    == Just iid
    || attr assetPlacement asset
    `elem` [InPlayArea iid, InThreatArea iid, StillInHand iid, AttachedToInvestigator iid]

