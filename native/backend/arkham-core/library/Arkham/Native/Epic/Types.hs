-- Unreleased native integration candidate; not a published save format yet.
-- Thin history replaces the earlier unpublished embedded-beforeSnapshot draft.
-- The FFI must first reject duplicate JSON properties and enforce its complete
-- 64 MiB byte limit. Aeson Value has already lost duplicate-property evidence.
-- Structural validation here is NOT authorization of a rule command.
module Arkham.Native.Epic.Types where

import Arkham.Prelude
import Arkham.Card.CardCode (CardCode, unCardCode)
import Arkham.Classes.Entity (toAttrs)
import Arkham.Difficulty
import Arkham.Epic.Types (SharedEventState (..), SharedDelta (..))
import Arkham.Game (Game (..))
import Arkham.Game.Json ()
import Arkham.Id (PlayerId, unPlayerId)
import Arkham.Message (Message)
import Arkham.Native.Answer (Answer (Raw))
import Arkham.Native.Settings.Types (exactFields)
import Arkham.Native.StrictJSON (validateJsonProperties)
import Arkham.Native.SourceIdentity qualified as Identity
import Arkham.Scenario.Types (scenarioId, scenarioMeta)
import Data.Aeson.KeyMap qualified as KM
import Arkham.Target (Target)
import Control.Monad.Fail (fail)
import Data.Aeson.Types (Parser)
import Data.Bits (finiteBitSize)
import Data.Set qualified as Set
import Data.Text qualified as T
import Data.UUID (UUID)
import Data.UUID qualified as UUID

-- Separate snapshot wire avoids a Session -> Epic -> Session import cycle.
-- Conversion to/from Session.Snapshot is mechanical at the integration seam.
-- Historical bootstrap is explicitly null: Epic is not campaign-history setup.
data GroupSnapshot = GroupSnapshot
  { groupCoreRevision :: Word64, groupGame :: Game, groupQueue :: [Message] }

data EventDefinition = EventDefinition
  { eventDifficulty :: Difficulty, eventTarot :: Bool, eventBlobElse :: Bool
  , eventTimeLimitMinutes :: Int }

data ActionHistory = ActionHistory
  { actionId :: UUID, actionEventRevision :: Word64
  , beforeSequence :: Word64, afterSequence :: Word64
  , beforeEventRevision :: Word64, beforeSnapshotSha256 :: Text
  , beforeCoreRevision :: Word64, beforeMembers :: [PlayerId]
  , effectiveDeltas :: [SharedDelta] }

data EventGroup = EventGroup
  { groupGameId :: UUID, groupOrdinal :: Int, groupName :: Text
  , groupSeatCount :: Int, groupMembers :: [PlayerId], groupSequence :: Word64
  , groupSnapshot :: GroupSnapshot, groupUndoFloor :: Word64
  , groupHistory :: [ActionHistory] }

data EventSnapshot = EventSnapshot
  { eventId :: UUID, eventRevision :: Word64, eventDefinition :: EventDefinition
  , eventShared :: SharedEventState, eventGroups :: [EventGroup] }

data CreateGroup = CreateGroup
  { createGameId :: UUID, createOrdinal :: Int, createName :: Text
  , createPlayers :: [PlayerId], createSeed :: Int }

data EpicCreate = EpicCreate
  { createEventId :: UUID, createDefinition :: EventDefinition, createGroups :: [CreateGroup] }

-- Store original answer JSON for lossless forward encoding. It is parsed as the
-- existing Answer immediately, but still MUST pass Native.Validation at apply.
-- No arbitrary Message command exists.
data EpicCommand
  = GroupAnswer UUID Word64 PlayerId Value
  | GroupReady UUID Word64
  | ResolveAdvance Int [(Int,Int)]
  | TimeUp
  | Replicate UUID CardCode Target
  | SwapMainStreet UUID UUID
  | CorrectCountermeasures Int
  | UndoGroup UUID Word64 UUID

data EpicApply = EpicApply
  { applySnapshot :: EventSnapshot, expectedRevision :: Word64
  , operationId :: UUID, nowUnixSeconds :: Int64, applyCommand :: EpicCommand
  , undoBase :: Maybe UndoBase }

-- snapshotText is exactly decodeUtf8 (Aeson.encode groupSnapshot), produced by
-- the future DLL epicExtractUndoBase, not managed JSON reserialization.
-- Engine must hash its UTF8 bytes, compare BOTH the supplied digest and current
-- history digest, then check core revision/members/floor before executing Undo.
data UndoBase = UndoBase {undoSnapshotText :: Text, undoSha256 :: Text}

data EpicSelection = OrganizerSelection | GroupSelection UUID PlayerId
data EpicView = EpicView EventSnapshot EpicSelection

require :: Bool -> Text -> Parser ()
require ok reason = unless ok $ fail $ T.unpack reason

-- SQLite INTEGER and the managed host use signed Int64 even though native
-- revisions are nonnegative Word64. Enforce the common wire domain.
sqliteRevision :: Word64 -> Parser ()
sqliteRevision n = require (toInteger n <= toInteger (maxBound::Int64)) "Revision exceeds SQLite Int64"

sha256Shape :: Text -> Parser ()
sha256Shape t = require (T.length t == 64 && T.all (\c -> c >= '0' && c <= '9' || c >= 'a' && c <= 'f') t)
  "Expected lowercase SHA256"

nonNil :: UUID -> Parser ()
nonNil u = require (u /= UUID.nil) "Nil UUID"

distinct :: Ord a => [a] -> Bool
distinct xs = Set.size (Set.fromList xs) == length xs

membersValid :: [PlayerId] -> Parser ()
membersValid ps = do
  require (not (null ps) && length ps <= 4 && distinct ps) "Expected 1-4 distinct players"
  traverse_ (nonNil . unPlayerId) ps

nameValid :: Text -> Parser ()
nameValid t = require (not $ T.null $ T.strip t) "Empty group name"

-- The original ready barrier uses an Int bitmask. This is its representation
-- limit, not a scenario-specific limit on how many groups may participate.
groupCountValid :: Int -> Parser ()
groupCountValid n = require (n > 0 && n < finiteBitSize (0::Int)-1) "Group count exceeds ready-mask representation"

source :: Object -> Parser ()
source o = do
  ident <- o .: "coreSourceId"
  require (ident == Identity.coreSourceId) "Core source mismatch; migration required"

instance ToJSON GroupSnapshot where
  toJSON GroupSnapshot{..} = object
    ["snapshotFormat" .= (2::Int),"coreSourceId" .= Identity.coreSourceId
    ,"revision" .= groupCoreRevision,"game" .= groupGame,"queue" .= groupQueue
    ,"historicalBootstrap" .= Null]
instance FromJSON GroupSnapshot where
  parseJSON = withObject "Epic group snapshot" \o -> do
    exactFields ["snapshotFormat","coreSourceId","revision","game","queue","historicalBootstrap"] o
    version <- o .: "snapshotFormat"
    require (version == (2::Int)) "Unsupported group snapshot format"
    source o
    bootstrap <- o .: "historicalBootstrap"
    require (bootstrap == Null) "Epic cannot contain historical bootstrap"
    s <- GroupSnapshot <$> o .: "revision" <*> o .: "game" <*> o .: "queue"
    sqliteRevision $ groupCoreRevision s
    membersValid $ gamePlayers $ groupGame s
    require (gamePlayerCount (groupGame s) == length (gamePlayers $ groupGame s)) "Snapshot seat count mismatch"
    case gameMode $ groupGame s of
      That scenario -> do
        require (scenarioId (toAttrs scenario) == "85001") "Epic requires standalone 85001"
        case scenarioMeta (toAttrs scenario) of
          Object meta -> require (KM.lookup "epicMultiplayer" meta == Just (Bool True))
            "Snapshot is not an Epic member"
          _ -> fail "Epic member metadata missing"
      _ -> fail "Epic requires standalone mode"
    pure s

instance ToJSON EventDefinition where
  toJSON EventDefinition{..} = object
    ["scenarioId" .= ("85001"::Text),"difficulty" .= eventDifficulty,"tarot" .= eventTarot
    ,"blobElse" .= eventBlobElse,"timeLimitMinutes" .= eventTimeLimitMinutes]
instance FromJSON EventDefinition where
  parseJSON = withObject "Epic definition" \o -> do
    exactFields ["scenarioId","difficulty","tarot","blobElse","timeLimitMinutes"] o
    sid <- o .: "scenarioId"
    require (sid == ("85001"::Text)) "Unsupported Epic scenario"
    d <- EventDefinition <$> o .: "difficulty" <*> o .: "tarot" <*> o .: "blobElse" <*> o .: "timeLimitMinutes"
    require (eventTimeLimitMinutes d >= 0 && toInteger (eventTimeLimitMinutes d)*60 <= toInteger (maxBound::Int))
      "Invalid time limit"
    pure d

instance ToJSON ActionHistory where
  toJSON ActionHistory{..} = object
    ["actionId" .= actionId,"eventRevision" .= actionEventRevision
    ,"beforeSequence" .= beforeSequence,"afterSequence" .= afterSequence
    ,"beforeEventRevision" .= beforeEventRevision,"beforeSnapshotSha256" .= beforeSnapshotSha256
    ,"beforeCoreRevision" .= beforeCoreRevision,"beforeMembers" .= beforeMembers
    ,"effectiveDeltas" .= effectiveDeltas]
instance FromJSON ActionHistory where
  parseJSON = withObject "Epic action history" \o -> do
    exactFields ["actionId","eventRevision","beforeSequence","afterSequence","beforeEventRevision"
      ,"beforeSnapshotSha256","beforeCoreRevision","beforeMembers","effectiveDeltas"] o
    h <- ActionHistory <$> o .: "actionId" <*> o .: "eventRevision" <*> o .: "beforeSequence"
      <*> o .: "afterSequence" <*> o .: "beforeEventRevision" <*> o .: "beforeSnapshotSha256"
      <*> o .: "beforeCoreRevision" <*> o .: "beforeMembers" <*> o .: "effectiveDeltas"
    nonNil $ actionId h
    traverse_ sqliteRevision [actionEventRevision h,beforeSequence h,afterSequence h,beforeEventRevision h,beforeCoreRevision h]
    sha256Shape $ beforeSnapshotSha256 h
    membersValid $ beforeMembers h
    require (beforeEventRevision h < actionEventRevision h) "Undo base is not before its action"
    require (beforeSequence h < afterSequence h) "Invalid history sequence"
    require (distinct $ map sharedDeltaId $ effectiveDeltas h) "Duplicate delta IDs"
    traverse_ (\d -> require (not $ T.null $ sharedDeltaId d) "Empty delta ID") $ effectiveDeltas h
    pure h

instance ToJSON EventGroup where
  toJSON EventGroup{..} = object
    ["gameId" .= groupGameId,"ordinal" .= groupOrdinal,"name" .= groupName
    ,"seatCount" .= groupSeatCount,"members" .= groupMembers,"sequence" .= groupSequence
    ,"snapshot" .= groupSnapshot,"undoFloor" .= groupUndoFloor,"history" .= groupHistory]
instance FromJSON EventGroup where
  parseJSON = withObject "Epic group" \o -> do
    exactFields ["gameId","ordinal","name","seatCount","members","sequence","snapshot","undoFloor","history"] o
    g <- EventGroup <$> o .: "gameId" <*> o .: "ordinal" <*> o .: "name"
      <*> o .: "seatCount" <*> o .: "members" <*> o .: "sequence"
      <*> o .: "snapshot" <*> o .: "undoFloor" <*> o .: "history"
    nonNil $ groupGameId g
    traverse_ sqliteRevision [groupSequence g,groupUndoFloor g]
    nameValid $ groupName g
    membersValid $ groupMembers g
    require (groupOrdinal g >= 0 && groupSeatCount g == length (groupMembers g)) "Invalid group seats or ordinal"
    require (groupMembers g == gamePlayers (groupGame $ groupSnapshot g)) "Group member order differs from game"
    require (groupUndoFloor g <= groupSequence g) "Undo floor exceeds sequence"
    let hs = groupHistory g
    require (distinct $ map actionId hs) "Duplicate action IDs"
    require (and $ zipWith (\a b -> afterSequence a <= beforeSequence b) hs (drop 1 hs)) "Unordered undo history"
    traverse_ (\h -> require
      (groupUndoFloor g <= beforeSequence h && afterSequence h <= groupSequence g)
      "History crosses undo floor/current sequence") hs
    pure g

instance ToJSON EventSnapshot where
  toJSON EventSnapshot{..} = object
    ["eventFormat" .= (1::Int),"coreSourceId" .= Identity.coreSourceId,"eventId" .= eventId
    ,"revision" .= eventRevision,"definition" .= eventDefinition,"shared" .= eventShared,"groups" .= eventGroups]
instance FromJSON EventSnapshot where
  parseJSON = withObject "Epic snapshot" \o -> do
    exactFields ["eventFormat","coreSourceId","eventId","revision","definition","shared","groups"] o
    version <- o .: "eventFormat"
    require (version == (1::Int)) "Unsupported event format"
    source o
    e <- EventSnapshot <$> o .: "eventId" <*> o .: "revision" <*> o .: "definition" <*> o .: "shared" <*> o .: "groups"
    nonNil $ eventId e
    sqliteRevision $ eventRevision e
    let gs = eventGroups e
        hs = concatMap groupHistory gs
        ds = concatMap effectiveDeltas hs
    groupCountValid $ length gs
    require (map groupOrdinal gs == [0..length gs-1] && distinct (map groupGameId gs)) "Invalid event groups"
    require (all ((/=eventId e) . groupGameId) gs) "Group game ID equals event ID"
    require (distinct $ concatMap groupMembers gs) "Player belongs to multiple groups"
    require (sharedTotalInvestigators (eventShared e) == sum (map groupSeatCount gs)) "Shared total differs from seats"
    require (sharedVersion (eventShared e) >= 1) "Invalid shared revision"
    require (distinct $ map actionId hs) "Duplicate event action IDs"
    require (distinct $ map sharedDeltaId ds) "Duplicate event delta IDs"
    traverse_ (\h -> require (actionEventRevision h <= eventRevision e) "Future action history") hs
    require (all (\d -> sharedDeltaId d `Set.member` sharedAppliedDeltas (eventShared e)) ds)
      "Undo history references unapplied delta"
    pure e

instance ToJSON CreateGroup where
  toJSON CreateGroup{..} = object
    ["gameId" .= createGameId,"ordinal" .= createOrdinal,"name" .= createName,"players" .= createPlayers,"seed" .= createSeed]
instance FromJSON CreateGroup where
  parseJSON = withObject "Epic create group" \o -> do
    exactFields ["gameId","ordinal","name","players","seed"] o
    g <- CreateGroup <$> o .: "gameId" <*> o .: "ordinal" <*> o .: "name" <*> o .: "players" <*> o .: "seed"
    nonNil $ createGameId g
    nameValid $ createName g
    membersValid $ createPlayers g
    require (createOrdinal g >= 0) "Negative ordinal"
    pure g
instance ToJSON EpicCreate where
  toJSON EpicCreate{..} = object ["eventId" .= createEventId,"definition" .= createDefinition,"groups" .= createGroups]
instance FromJSON EpicCreate where
  parseJSON = withObject "Epic create" \o -> do
    exactFields ["eventId","definition","groups"] o
    c <- EpicCreate <$> o .: "eventId" <*> o .: "definition" <*> o .: "groups"
    nonNil $ createEventId c
    let gs = createGroups c
    groupCountValid $ length gs
    require (map createOrdinal gs == [0..length gs-1]) "Invalid group ordinals"
    require (distinct (map createGameId gs) && distinct (concatMap createPlayers gs)) "Duplicate group/player"
    require (all ((/=createEventId c) . createGameId) gs) "Group game ID equals event ID"
    pure c

instance ToJSON EpicCommand where
  toJSON c = object $ case c of
    GroupAnswer gid seqn pid a -> ["tag" .= ("GroupAnswer"::Text),"gameId" .= gid,"expectedSequence" .= seqn,"playerId" .= pid,"answer" .= a]
    GroupReady gid seqn -> ["tag" .= ("GroupReady"::Text),"gameId" .= gid,"expectedSequence" .= seqn]
    ResolveAdvance stage xs -> ["tag" .= ("ResolveAdvance"::Text),"stage" .= stage
      ,"allocation" .= [object ["ordinal" .= ordinal,"spend" .= spend] | (ordinal,spend) <- xs]]
    TimeUp -> ["tag" .= ("TimeUp"::Text)]
    Replicate gid code target -> ["tag" .= ("Replicate"::Text),"gameId" .= gid,"cardCode" .= code,"target" .= target]
    SwapMainStreet a b -> ["tag" .= ("SwapMainStreet"::Text),"firstGameId" .= a,"secondGameId" .= b]
    CorrectCountermeasures n -> ["tag" .= ("CorrectCountermeasures"::Text),"value" .= n]
    UndoGroup gid seqn aid -> ["tag" .= ("UndoGroup"::Text),"gameId" .= gid,"expectedSequence" .= seqn,"targetActionId" .= aid]
instance FromJSON EpicCommand where
  parseJSON = withObject "Epic command" \o -> do
    tag <- o .: "tag" :: Parser Text
    let fields xs = exactFields ("tag":xs) o
        gid key = do u <- o .: key; nonNil u; pure u
    case tag of
      "GroupAnswer" -> do
        fields ["gameId","expectedSequence","playerId","answer"]
        pid <- o .: "playerId"; nonNil $ unPlayerId pid
        a <- o .: "answer"
        parsed <- parseJSON a :: Parser Answer
        case parsed of Raw {} -> fail "Raw Epic answers are forbidden"; _ -> pure ()
        seqn <- o .: "expectedSequence"; sqliteRevision seqn
        GroupAnswer <$> gid "gameId" <*> pure seqn <*> pure pid <*> pure a
      "GroupReady" -> do
        fields ["gameId","expectedSequence"]
        seqn <- o .: "expectedSequence"; sqliteRevision seqn
        GroupReady <$> gid "gameId" <*> pure seqn
      "ResolveAdvance" -> do
        fields ["stage","allocation"]
        stage <- o .: "stage"
        require (stage > 0) "Invalid act stage"
        raw <- o .: "allocation"
        xs <- traverse (withObject "Allocation" \a -> do
          exactFields ["ordinal","spend"] a
          ordinal <- a .: "ordinal"; spend <- a .: "spend"
          require (ordinal >= 0 && spend >= 0) "Negative allocation"
          pure (ordinal,spend)) raw
        require (distinct $ map fst xs) "Duplicate allocation ordinal"
        pure $ ResolveAdvance stage xs
      "TimeUp" -> fields [] >> pure TimeUp
      "Replicate" -> do
        fields ["gameId","cardCode","target"]
        code <- o .: "cardCode"
        require (unCardCode code `elem` ["89010" <> T.singleton suffix | suffix <- ['a'..'i']])
          "Only original Replicating Aberrations are allowed"
        Replicate <$> gid "gameId" <*> pure code <*> o .: "target"
      "SwapMainStreet" -> do
        fields ["firstGameId","secondGameId"]
        a <- gid "firstGameId"; b <- gid "secondGameId"
        require (a /= b) "Swap requires distinct groups"; pure $ SwapMainStreet a b
      "CorrectCountermeasures" -> do
        fields ["value"]; n <- o .: "value"
        require (n >= 0) "Negative countermeasures"; pure $ CorrectCountermeasures n
      "UndoGroup" -> do
        fields ["gameId","expectedSequence","targetActionId"]
        seqn <- o .: "expectedSequence"; sqliteRevision seqn
        UndoGroup <$> gid "gameId" <*> pure seqn <*> gid "targetActionId"
      _ -> fail "Unsupported Epic command"

instance ToJSON UndoBase where
  toJSON UndoBase{..} = object ["snapshotText" .= undoSnapshotText,"sha256" .= undoSha256]
instance FromJSON UndoBase where
  parseJSON = withObject "Epic undo base" \o -> do
    exactFields ["snapshotText","sha256"] o
    b <- UndoBase <$> o .: "snapshotText" <*> o .: "sha256"
    sha256Shape $ undoSha256 b
    let bytes = encodeUtf8 $ undoSnapshotText b
    either (const $ fail "Invalid undo snapshot JSON") pure $ validateJsonProperties bytes
    -- Source/format/mode and structural checks are the same as ordinary group
    -- snapshots. This is not cryptographic verification or Undo authorization.
    case eitherDecodeStrict' @GroupSnapshot bytes of
      Left _ -> fail "Invalid undo group snapshot"
      Right _ -> pure b

instance ToJSON EpicApply where
  toJSON EpicApply{..} = object ["snapshot" .= applySnapshot,"expectedRevision" .= expectedRevision
    ,"operationId" .= operationId,"nowUnixSeconds" .= nowUnixSeconds,"command" .= applyCommand,"undoBase" .= undoBase]
instance FromJSON EpicApply where
  parseJSON = withObject "Epic apply" \o -> do
    -- Trusted-local envelope: the controller supplies wall time once when it
    -- constructs the idempotent request. UI answers cannot supply/override it.
    -- GroupReady and TimeUp both consume this same host time. Retrying a saved
    -- request must retain it, not obtain a new time and change its payload hash.
    exactFields ["snapshot","expectedRevision","operationId","nowUnixSeconds","command","undoBase"] o
    a <- EpicApply <$> o .: "snapshot" <*> o .: "expectedRevision" <*> o .: "operationId"
      <*> o .: "nowUnixSeconds" <*> o .: "command" <*> o .: "undoBase"
    nonNil $ operationId a
    sqliteRevision $ expectedRevision a
    require (nowUnixSeconds a >= 0) "Negative host time"
    require (expectedRevision a == eventRevision (applySnapshot a)) "Stale event revision"
    case (applyCommand a,undoBase a) of
      (UndoGroup {},Just _) -> pure ()
      (UndoGroup {},Nothing) -> fail "UndoGroup requires undoBase"
      (_,Nothing) -> pure ()
      (_,Just _) -> fail "Only UndoGroup accepts undoBase"
    let gs = eventGroups $ applySnapshot a
        group gid = case filter ((==gid) . groupGameId) gs of
          [g] -> pure g
          _ -> fail "Unknown event group"
        current gid seqn = do
          g <- group gid
          require (seqn == groupSequence g) "Stale group sequence"
          pure g
    case applyCommand a of
      GroupAnswer gid seqn pid _ -> do
        g <- current gid seqn
        require (pid `elem` groupMembers g) "Player is not in this group"
      GroupReady gid seqn -> void $ current gid seqn
      UndoGroup gid seqn aid -> do
        g <- current gid seqn
        require (any ((==aid) . actionId) $ groupHistory g) "Unknown retained undo action"
      Replicate gid _ _ -> do
        void $ group gid
        require (eventBlobElse $ eventDefinition $ applySnapshot a) "Replicate requires ELSE variant"
      SwapMainStreet x y -> void (group x) >> void (group y)
      ResolveAdvance _ xs -> require (all (\(ordinal,_) -> ordinal < length gs) xs) "Unknown allocation group"
      _ -> pure ()
    pure a
instance ToJSON EpicSelection where
  toJSON OrganizerSelection = object ["role" .= ("Organizer"::Text)]
  toJSON (GroupSelection gid pid) = object ["role" .= ("Group"::Text),"gameId" .= gid,"playerId" .= pid]
instance FromJSON EpicSelection where
  parseJSON = withObject "Epic selection" \o -> do
    role <- o .: "role" :: Parser Text
    case role of
      "Organizer" -> exactFields ["role"] o >> pure OrganizerSelection
      "Group" -> do
        exactFields ["role","gameId","playerId"] o
        gid <- o .: "gameId"; pid <- o .: "playerId"
        nonNil gid; nonNil $ unPlayerId pid
        pure $ GroupSelection gid pid
      _ -> fail "Unsupported Epic role"
instance ToJSON EpicView where
  toJSON (EpicView s selection) = object ["snapshot" .= s,"selection" .= selection]
instance FromJSON EpicView where
  parseJSON = withObject "Epic view" \o -> do
    exactFields ["snapshot","selection"] o
    s <- o .: "snapshot"; selection <- o .: "selection"
    case selection of
      OrganizerSelection -> pure ()
      GroupSelection gid pid -> require
        (any (\g -> groupGameId g == gid && pid `elem` groupMembers g) $ eventGroups s)
        "Selection is not a current group member"
    pure $ EpicView s selection
