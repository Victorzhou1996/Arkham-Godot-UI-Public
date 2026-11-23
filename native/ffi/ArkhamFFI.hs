{-# LANGUAGE ForeignFunctionInterface, OverloadedStrings, TypeApplications, BlockArguments #-}
module ArkhamFFI where

import Prelude
import Arkham.Native.Session qualified as Core
import Arkham.Native.Answer (Answer, unwrapQuestion)
import Arkham.Native.Network.View qualified as Network
import Arkham.Native.Validation qualified as Validation
import Arkham.Question (Question (ContinueCampaign))
import Arkham.Native.Author qualified as Author
import Arkham.Native.DeckMetadata qualified as DeckMetadata
import Arkham.Native.DeckBuild.Validate qualified as DeckBuild
import Arkham.Native.DeckBuild.Catalog qualified as BuildCatalog
import Arkham.Native.DeckBuild.UpgradeQuote qualified as UpgradeQuote
import Arkham.Native.Epic.Types qualified as Epic
import Arkham.Native.Epic.Engine qualified as EpicEngine
import Arkham.Native.StrictJSON (validateJsonProperties)
import Arkham.Native.Events (eventJSON, eventJSONForPlayer)
import Arkham.Game (PublicGame(..), gamePlayers, gameCustomCards, gameQuestion, gameMode)
import Arkham.Id (PlayerId, unPlayerId)
import Control.Concurrent.MVar qualified as MV
import Control.Exception qualified as E
import Control.DeepSeq (force)
import Control.Monad (unless, when)
import Control.Monad.Trans.Except
import Control.Monad.IO.Class (liftIO)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (parseEither, Parser, Pair)
import Data.ByteString qualified as BS
import Data.ByteString.Lazy qualified as BL
import Data.Map.Strict qualified as Map
import Data.Maybe (catMaybes)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding qualified as TE
import Data.UUID (UUID)
import Data.UUID qualified as UUID
import Data.Word
import Foreign.C.Types
import Foreign.Ptr
import Foreign.Storable
import Foreign.Marshal.Utils (copyBytes)
import System.IO.Unsafe (unsafePerformIO)
import System.IO (hPutStrLn, stderr)

data Entry = Entry UUID Core.Snapshot [Value]
data Store = Store { nextId :: Word64, sessions :: Map.Map Word64 Entry, results :: Map.Map Word64 BS.ByteString }
{-# NOINLINE store #-}
store :: MV.MVar Store
store = unsafePerformIO $ MV.newMVar (Store 1 Map.empty Map.empty)

maxRequest, maxResult, maxResultBytes :: Int
maxRequest = 64 * 1024 * 1024
maxResult = 64 * 1024 * 1024
maxResultBytes = 128 * 1024 * 1024

ok :: [Pair] -> Value
ok fields = object ("ok" .= True : fields)
reject :: Text -> Value
reject reason = object ["ok" .= False, "error" .= object ["code" .= ("rejected" :: Text), "message" .= reason]]

parse :: Parser a -> ExceptT Text IO a
parse parser = either (throwE . T.pack) pure (parseEither (const parser) ())
field :: FromJSON a => Object -> Text -> ExceptT Text IO a
field o name = parse (o .: K.fromText name)
checked :: IO (Either Text a) -> ExceptT Text IO a
checked action = liftIO action >>= either throwE pure
fresh :: Store -> ExceptT Text IO (Store, Word64)
fresh s = if nextId s == maxBound then throwE "Handle space exhausted" else pure (s { nextId = nextId s + 1 }, nextId s)
entryFor :: Store -> Object -> ExceptT Text IO (Word64, Entry)
entryFor s o = do
  token <- field o "session"
  entry <- maybe (throwE "Unknown or released session") pure (Map.lookup token (sessions s))
  pure (token,entry)
install :: Store -> UUID -> Core.Snapshot -> [Value] -> ExceptT Text IO (Store,Value)
install s gid snapshot events = do
  when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
  (s', token) <- fresh s
  pure (s' { sessions = Map.insert token (Entry gid snapshot events) (sessions s') }, ok ["session" .= token, "revision" .= Core.snapshotRevision snapshot])

installWithUndoMetadata :: Store -> UUID -> Core.Snapshot -> [Value] -> Core.UndoMetadata -> ExceptT Text IO (Store,Value)
installWithUndoMetadata s gid snapshot events undoMetadata = do
  (s', response) <- install s gid snapshot events
  let response' = case response of
        Object fields -> Object $ KM.insert "undoMetadata" (toJSON undoMetadata) fields
        _ -> response
  pure (s', response')

snapshotBytes :: Core.Snapshot -> ExceptT Text IO BL.ByteString
snapshotBytes snapshot = checked $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $ do
  let bytes = Core.exportSnapshot snapshot
  _ <- E.evaluate (BL.length bytes)
  pure bytes
decodeValue :: BL.ByteString -> ExceptT Text IO Value
decodeValue = either (throwE . T.pack) pure . eitherDecode

-- This projection is explicitly for the trusted local hotseat host. It is not
-- a network privacy boundary; LAN requires a separate private-card projection.
localView :: UUID -> Core.Snapshot -> PlayerId -> ExceptT Text IO Value
localView gid snapshot player = do
  unless (player `elem` gamePlayers (Core.snapshotGame snapshot)) $ throwE "Unknown view recipient"
  -- Keep the JSON tree instead of encoding it to bytes and immediately parsing
  -- it again. Fully force it while the custom-card registry is still installed;
  -- the FFI response encoder remains the only required byte serialization.
  value <- checked $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $
    E.evaluate $ force $ toJSON (PublicGame (UUID.toText gid) "Native local game" [] (Core.snapshotGame snapshot))
  pure $ case value of
    Object o ->
      let key = K.fromText (UUID.toText $ unPlayerId player)
          ownQuestion = case KM.lookup "question" o of
            Just (Object questions) -> maybe KM.empty (KM.singleton key) (KM.lookup key questions)
            _ -> KM.empty
      in Object $ KM.insert "question" (Object ownQuestion) o
    _ -> value

projectLocalResponse :: UUID -> Core.Snapshot -> PlayerId -> [Value] -> ExceptT Text IO Value
projectLocalResponse gid snapshot player events = do
  view <- localView gid snapshot player
  projected <- checked $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $ runExceptT do
    triggers <- liftIO $ Core.triggerView (Core.snapshotGame snapshot) player
    regions <- liftIO $ Core.regionsView (Core.snapshotGame snapshot) player
    delivered <- either throwE (pure . catMaybes) $ traverse (eventJSONForPlayer player) events
    let response=ok ["viewVersion" .= (1::Int),"revision" .= Core.snapshotRevision snapshot,
          "recipient" .= player,"scope" .= ("trusted-local-hotseat"::Text),"game" .= view,
          "settingsSchema" .= Core.settingsView (Core.snapshotGame snapshot) player,
          "settingsFormSchema" .= Core.snapshotSettingsFormView snapshot player,
          "historicalSchema" .= Core.historicalSchema snapshot player,
          "interactionSchema" .= Core.specificView (Core.snapshotGame snapshot) player,
          "deckSchema" .= Core.deckView (Core.snapshotGame snapshot) player,
          "authorCards" .= gameCustomCards (Core.snapshotGame snapshot),
          "campaignManagementSchema" .= Core.campaignView (Core.snapshotGame snapshot) player,
          "debugSchema" .= Core.debugView (Core.snapshotGame snapshot) player,
          "triggerSchema" .= triggers,"zoneSchema" .= regions,"events" .= delivered]
        encoded=encode response
    _ <- liftIO $ E.evaluate $ BL.length encoded
    pure encoded
  either throwE decodeValue projected

-- Optional finite sections of the existing local view. The absent-sections
-- branch below remains the original encoder/decoder path, byte for byte.
localSections :: UUID -> Core.Snapshot -> PlayerId -> [Value] -> [Text] -> ExceptT Text IO Value
localSections gid snapshot player events names = do
  let game = Core.snapshotGame snapshot
      allowed = ["game","events","triggerSchema","zoneSchema","deckSchema","interactionSchema",
        "settingsSchema","settingsFormSchema","campaignManagementSchema","debugSchema","historicalSchema","authorCards"]
      wanted name = name `elem` names
  unless (all (`elem` allowed) names) $ throwE "Unknown local view section"
  unless (player `elem` gamePlayers game) $ throwE "Unknown view recipient"
  -- localView has its own registry boundary; do not nest that boundary.
  view <- if wanted "game" then Just <$> localView gid snapshot player else pure Nothing
  result <- checked $ Core.boundary (gameCustomCards game) $ runExceptT do
    triggers <- if wanted "triggerSchema" then Just <$> liftIO (Core.triggerView game player) else pure Nothing
    regions <- if wanted "zoneSchema" then Just <$> liftIO (Core.regionsView game player) else pure Nothing
    delivered <- if wanted "events"
      then Just . toJSON . catMaybes <$> either throwE pure (traverse (eventJSONForPlayer player) events)
      else pure Nothing
    let select name value = if wanted name then Just (K.fromText name .= value) else Nothing
        fields = ["viewVersion" .= (1::Int),"revision" .= Core.snapshotRevision snapshot,
          "recipient" .= player,"scope" .= ("trusted-local-hotseat"::Text)]
        chosen = catMaybes
          [ ("game" .=) <$> view, ("events" .=) <$> delivered,
            ("triggerSchema" .=) <$> triggers, ("zoneSchema" .=) <$> regions,
            select "deckSchema" (Core.deckView game player),
            select "interactionSchema" (Core.specificView game player),
            select "settingsSchema" (Core.settingsView game player),
            select "settingsFormSchema" (Core.snapshotSettingsFormView snapshot player),
            select "campaignManagementSchema" (Core.campaignView game player),
            select "debugSchema" (Core.debugView game player),
            select "historicalSchema" (Core.historicalSchema snapshot player),
            select "authorCards" (toJSON $ gameCustomCards game) ]
    -- No registry-dependent thunk is allowed to escape this boundary.
    liftIO $ E.evaluate $ force $ ok (fields <> chosen)
  either throwE pure result

eventValue :: Epic.EventSnapshot -> Value -> ExceptT Text IO Value
eventValue event value=do
  registry<-either throwE pure $ EpicEngine.eventCustomCards event
  bytes<-checked $ Core.boundary registry do
    let encoded=encode value
    _<-E.evaluate $ BL.length encoded
    pure encoded
  decodeValue bytes

groupCoreSnapshot :: Epic.GroupSnapshot -> Core.Snapshot
groupCoreSnapshot group=Core.SnapshotState (Epic.groupCoreRevision group) (Epic.groupGame group) (Epic.groupQueue group) Nothing

-- This boundary never calls localView/projectLocalResponse. Every attached
-- schema is a named finite native adapter, not a raw Game/Question/Message.
networkResponse :: Core.Snapshot -> PlayerId -> ExceptT Text IO Value
networkResponse = networkResponseWithContext Nothing

networkResponseWithContext :: Maybe Core.NativeEpicContext -> Core.Snapshot -> PlayerId -> ExceptT Text IO Value
networkResponseWithContext epic snapshot player = do
  result <- checked $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $ runExceptT do
    let game=Core.snapshotGame snapshot
    (refs,extras) <- liftIO $ Core.networkExtrasView epic game player False
    projected <- either throwE pure $ Network.networkViewWithCards refs game player
    let continuation=case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
          Just ContinueCampaign -> either (const Null) toJSON $ Validation.offeredContinuation $ gameMode game
          _ -> Null
        fields=KM.fromList
          [ ("revision",toJSON $ Core.snapshotRevision snapshot),("coreSourceId",toJSON Core.coreSourceId)
          , ("settingsFormSchema",Core.snapshotSettingsFormView snapshot player)
          , ("deckSchema",Core.deckView game player),("continuationSchema",continuation)
          , ("campaignManagementSchema",Core.campaignView game player) ]
        additions=case extras of Object o -> KM.delete "zoneSchema" o; _ -> KM.empty
        value=case projected of Object o -> Object $ KM.union fields $ KM.union additions o; _ -> Null
        bytes=encode value
    _ <- liftIO $ E.evaluate $ BL.length bytes
    pure bytes
  either throwE decodeValue result

epicSeatResponse :: Bool -> Epic.EventSnapshot -> PlayerId -> ExceptT Text IO (Value,Value)
epicSeatResponse includeRegions event player = do
  _ <- either throwE pure $ EpicEngine.eventCustomCards event
  group <- either throwE pure $ EpicEngine.networkPlayerGroup event player
  context <- EpicEngine.newContext event group
  let snapshot=groupCoreSnapshot $ Epic.groupSnapshot group
      game=Core.snapshotGame snapshot
  projected <- networkResponseWithContext (Just context) snapshot player
  zones <- if not includeRegions then pure Null else do
    bytes <- checked $ Core.boundary (gameCustomCards game) do
      (_,extras) <- Core.networkExtrasView (Just context) game player True
      let value=case extras of Object fields -> KM.lookup "zoneSchema" fields; _ -> Nothing
          encoded=encode value
      _ <- E.evaluate $ BL.length encoded
      pure encoded
    decodeValue bytes
  let seat=object ["gameId" .= Epic.groupGameId group,"sequence" .= Epic.groupSequence group
        ,"coreRevision" .= Core.snapshotRevision snapshot,"sourceId" .= Core.coreSourceId,"view" .= projected]
      nativeView=object ["eventId" .= Epic.eventId event,"eventRevision" .= Epic.eventRevision event
        ,"groupId" .= Epic.groupGameId group,"groupSequence" .= Epic.groupSequence group
        ,"coreRevision" .= Core.snapshotRevision snapshot,"sourceId" .= Core.coreSourceId,"playerId" .= player
        ,"seat" .= seat,"shared" .= EpicEngine.networkSharedSummary event]
      regions=if includeRegions then object
        ["viewVersion" .= (1::Int),"revision" .= Core.snapshotRevision snapshot
        ,"recipient" .= player,"zoneSchema" .= zones] else Null
  pure (nativeView,regions)

dispatch :: Store -> Value -> ExceptT Text IO (Store,Value)
dispatch s request = do
  o <- parse $ withObject "native request" pure request
  version <- field o "apiVersion" :: ExceptT Text IO Int
  unless (version == 1) $ throwE "Unsupported API version"
  method <- field o "method" :: ExceptT Text IO Text
  case method of
    "info" -> pure (s,ok
      [ "abiVersion" .= (1::Int), "viewVersion" .= (1::Int), "snapshotVersion" .= (2::Int), "networkViewVersion" .= (1::Int)
      , "coreSourceId" .= Core.coreSourceId, "releaseReady" .= False
      , "scope" .= ("trusted-local-hotseat" :: Text)
      , "liveSessions" .= Map.size (sessions s), "liveResults" .= Map.size (results s) ])
    "create" -> do
      gid <- field o "gameId"
      when (gid == UUID.nil) $ throwE "Nil game ID is invalid"
      when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
      args <- field o "request"
      (snapshot,events) <- checked $ Core.createSession args
      install s gid snapshot (map eventJSON events)
    "epicCreate" -> do
      requestValue<-field o "request"
      (event,events)<-checked $ EpicEngine.createEvent requestValue
      result<-eventValue event $ ok ["snapshot" .= event,"summary" .= EpicEngine.organizerView event,"events" .= events]
      pure (s,result)
    "networkView" -> do
      (_,Entry _ snapshot _) <- entryFor s o
      player <- field o "player"
      projected <- networkResponse snapshot player
      pure (s,ok ["networkView" .= projected])
    "epicNetworkView" -> do
      event <- field o "snapshot"
      player <- field o "player"
      (nativeView,_) <- epicSeatResponse False event player
      pure (s,ok ["nativeView" .= nativeView])
    "epicNetworkRegions" -> do
      event <- field o "snapshot"
      player <- field o "player"
      (nativeView,zones) <- epicSeatResponse True event player
      -- Still a trusted finite local region schema. The host must apply the
      -- existing own/public-parent LanRegionProjection before network delivery.
      pure (s,ok ["nativeView" .= nativeView,"regions" .= zones])
    -- Trusted-host finite regions only, deliberately separate from localView.
    -- The LAN host must still filter zone owners against its network view;
    -- this existing local region schema is not itself the network boundary.
    "regionView" -> do
      (_,Entry _ snapshot _) <- entryFor s o
      player <- field o "player"
      let game=Core.snapshotGame snapshot
      unless (player `elem` gamePlayers game) $ throwE "Unknown region recipient"
      bytes <- checked $ Core.boundary (gameCustomCards game) $ do
        regions <- Core.regionsView game player
        let encoded=encode $ ok ["viewVersion" .= (1::Int),"revision" .= Core.snapshotRevision snapshot
              ,"recipient" .= player,"zoneSchema" .= regions]
        _ <- E.evaluate $ BL.length encoded
        pure encoded
      response <- decodeValue bytes
      pure (s,response)
    "epicApply" -> do
      requestValue<-field o "request"
      (event,events)<-checked $ EpicEngine.applyEvent requestValue
      result<-eventValue event $ ok ["snapshot" .= event,"summary" .= EpicEngine.organizerView event,"events" .= events]
      pure (s,result)
    "epicValidate" -> do
      event<-field o "snapshot"
      result<-eventValue event $ ok ["summary" .= EpicEngine.organizerView event,"coreSourceId" .= Core.coreSourceId]
      pure (s,result)
    "epicExtractUndoBase" -> do
      event<-field o "snapshot"
      gid<-field o "gameId"
      base<-either throwE pure $ EpicEngine.extractUndoBase event gid
      result<-eventValue event $ ok ["undoBase" .= base]
      pure (s,result)
    "epicValidateUndoBase" -> do
      event<-field o "snapshot"
      gid<-field o "gameId"
      action<-field o "actionId"
      base<-field o "undoBase"
      either throwE pure $ EpicEngine.verifyUndoBase event gid action base
      result<-eventValue event $ ok ["validated" .= True]
      pure (s,result)
    "epicView" -> do
      Epic.EpicView event selection<-field o "request"
      summary<-eventValue event $ EpicEngine.organizerView event
      response<-case selection of
        Epic.OrganizerSelection->pure Null
        Epic.GroupSelection gid player->do
          group<-either throwE pure $ EpicEngine.selectedGroup event gid
          unless (player `elem` Epic.groupMembers group) $ throwE "Unknown Epic group recipient"
          projectLocalResponse gid (groupCoreSnapshot $ Epic.groupSnapshot group) player []
      pure (s,ok ["summary" .= summary,"response" .= response])
    "epicSettingsPreview" -> do
      event<-field o "snapshot"
      gid<-field o "gameId"
      player<-field o "player"
      draft<-field o "draft"
      group<-either throwE pure $ EpicEngine.selectedGroup event gid
      unless (player `elem` Epic.groupMembers group) $ throwE "Unknown Epic group recipient"
      let snapshot=groupCoreSnapshot $ Epic.groupSnapshot group
      preview<-either throwE pure $ Core.settingsPreview snapshot player (Core.snapshotRevision snapshot) draft
      result<-eventValue event $ ok ["preview" .= preview]
      pure (s,result)
    "epicFilterEvents" -> do
      event<-field o "snapshot"
      gid<-field o "gameId"
      player<-field o "player"
      events<-field o "events"
      group<-either throwE pure $ EpicEngine.selectedGroup event gid
      unless (player `elem` Epic.groupMembers group) $ throwE "Unknown Epic group recipient"
      delivered<-either throwE (pure . catMaybes) $ traverse (eventJSONForPlayer player) events
      result<-eventValue event $ ok ["events" .= delivered]
      pure (s,result)
    "authorSchema" -> do
      let schema=Author.authorSchema
      _ <- liftIO $ E.evaluate $ BL.length $ encode schema
      pure (s,ok ["coreSourceId" .= Core.coreSourceId,"schema" .= schema])
    buildMethod | buildMethod `elem` ["deckBuildSchema","deckBuildValidate"] -> do
      args <- field o "request"
      result <- either throwE pure $ if buildMethod=="deckBuildSchema"
        then DeckBuild.deckBuildSchema args else DeckBuild.deckBuildValidate args
      _ <- liftIO $ E.evaluate $ BL.length $ encode result
      pure (s,ok ["coreSourceId" .= Core.coreSourceId,"result" .= result])
    "deckUpgradeQuote" -> do
      args <- field o "request"
      catalog <- either throwE pure BuildCatalog.catalogResult
      result <- either throwE pure $ UpgradeQuote.quoteUpgrade catalog args
      _ <- liftIO $ E.evaluate $ BL.length $ encode result
      pure (s,ok ["coreSourceId" .= Core.coreSourceId,"result" .= result])
    "deckMetadataValidate" -> do
      args <- field o "request"
      checkedResult <- liftIO $ Core.boundary mempty $ do
        let answer = DeckMetadata.deckMetadataValidate args
        _ <- E.evaluate $ BL.length $ encode answer
        pure answer
      result <- either throwE (either throwE pure) checkedResult
      pure (s,ok ["coreSourceId" .= Core.coreSourceId,"result" .= result])
    "authorPreview" -> do
      raw <- field o "packageText"
      digest <- field o "packageSha256"
      package <- either throwE pure $ Author.validatePackage raw digest
      let preview=Author.packageView package
      _ <- liftIO $ E.evaluate $ BL.length $ encode preview
      pure (s,ok ["coreSourceId" .= Core.coreSourceId,"preview" .= preview])
    authorMethod | authorMethod `elem` ["authorAnswer","authorInstall"] -> do
      (_,Entry gid old _) <- entryFor s o
      when (Map.size (sessions s)>=16) $ throwE "Session limit reached"
      player <- field o "player"
      revision <- field o "revision"
      enabled <- field o "authorEnabled"
      raw <- field o "packageText"
      digest <- field o "packageSha256"
      (candidate,events) <- if authorMethod=="authorInstall"
        then checked $ Core.applyAuthorInstall old player revision enabled raw digest
        else do
          answer<-field o "answer"
          checked $ Core.applyAuthorAnswer old player revision enabled raw digest answer
      install s gid candidate (map eventJSON events)
    "createHistory" -> do
      gid <- field o "gameId"
      when (gid == UUID.nil) $ throwE "Nil game ID is invalid"
      when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
      args <- field o "request"
      (snapshot,events) <- checked $ Core.createHistoricalSession args
      install s gid snapshot (map eventJSON events)
    "answer" -> do
      (_,Entry gid old _) <- entryFor s o
      when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
      player <- field o "player"
      revision <- field o "revision"
      answer <- field o "answer" :: ExceptT Text IO Answer
      (candidate,events,undoMetadata) <- checked $ Core.applyAnswerWithMetadata old player revision answer
      installWithUndoMetadata s gid candidate (map eventJSON events) undoMetadata
    "debug" -> do
      (_,Entry gid old _) <- entryFor s o
      when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
      player <- field o "player"
      revision <- field o "revision"
      enabled <- field o "debugEnabled"
      command <- field o "command"
      (candidate,events) <- checked $ Core.applyDebug old player revision enabled command
      install s gid candidate (map eventJSON events)
    methodName | methodName `elem` ["settingsPreview","historyChapterPreview","historyPrepare"] -> do
      (_,Entry _ snapshot _) <- entryFor s o
      player <- field o "player"
      revision <- field o "revision"
      draft <- field o "draft"
      preview <- checked $ fmap (either Left id) $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $ do
        let operation=case methodName of
              "historyChapterPreview"->Core.historicalChapterPreview
              "historyPrepare"->Core.historicalPrepare
              _->Core.settingsPreview
        case operation snapshot player revision draft of
          Left reason -> pure $ Left reason
          Right value -> do
            _ <- E.evaluate $ BL.length $ encode value
            pure $ Right value
      -- Same Store and session basis: no install/fresh candidate, event append,
      -- revision, RNG or SQLite operation. Only the ordinary result buffer is new.
      pure (s,ok ["revision" .= Core.snapshotRevision snapshot,"recipient" .= player,"preview" .= preview])
    "historyPreview" -> do
      (_,Entry _ snapshot _) <- entryFor s o
      player <- field o "player"
      revision <- field o "revision"
      draft <- field o "draft"
      preview <- checked $ Core.boundary (gameCustomCards $ Core.snapshotGame snapshot) $ do
        value<-either (ioError . userError . T.unpack) pure $ Core.historicalPreview snapshot player revision draft
        _<-E.evaluate $ BL.length $ encode value
        pure value
      pure (s,ok ["revision" .= Core.snapshotRevision snapshot,"recipient" .= player,"preview" .= preview])
    "view" -> do
      (_,Entry gid snapshot events) <- entryFor s o
      player <- field o "player"
      result <- case KM.lookup "sections" o of
        Nothing -> projectLocalResponse gid snapshot player events
        Just raw -> do
          names <- parse (parseJSON raw) :: ExceptT Text IO [Text]
          localSections gid snapshot player events names
      pure (s,result)
    "export" -> do
      (_,Entry gid snapshot _) <- entryFor s o
      value <- snapshotBytes snapshot >>= decodeValue
      pure (s,ok ["save" .= object ["saveFormat" .= (1::Int), "gameId" .= gid, "snapshot" .= value]])
    "import" -> do
      when (Map.size (sessions s) >= 16) $ throwE "Session limit reached"
      save <- field o "save" :: ExceptT Text IO Object
      format <- field save "saveFormat" :: ExceptT Text IO Int
      unless (format == 1) $ throwE "Unsupported native save format"
      gid <- field save "gameId"
      when (gid == UUID.nil) $ throwE "Nil game ID is invalid"
      value <- field save "snapshot" :: ExceptT Text IO Value
      snapshot <- checked $ Core.importSnapshot (encode value)
      when (Core.requiresEpicContext $ Core.snapshotGame snapshot) $ throwE "Epic member snapshots must be imported as a complete event"
      install s gid snapshot []
    "release" -> do
      (token,_) <- entryFor s o
      pure (s { sessions = Map.delete token (sessions s) },ok [])
    _ -> throwE "Unknown native method"

protect :: IO CInt -> IO CInt
protect action = do
  outcome <- E.try @E.SomeException (action >>= E.evaluate)
  case outcome of
    Right status -> pure status
    -- Keep the fatal transport policy, but do not silently discard the only
    -- actionable exception when a lazy encoder fails outside a rule boundary.
    Left exception -> do
      _ <- E.try @E.SomeException $ hPutStrLn stderr ("Native FFI internal error: " <> E.displayException exception)
      pure 4

ah_hs_call :: Ptr Word8 -> Word64 -> Ptr Word64 -> IO CInt
ah_hs_call input size output = protect $ do
  if output == nullPtr || (input == nullPtr && size /= 0) || size > fromIntegral maxRequest then pure 1 else do
    poke output 0
    bytes <- if size == 0 then pure BS.empty else BS.packCStringLen (castPtr input, fromIntegral size)
    token <- MV.modifyMVar store $ \s -> do
      decoded <- case TE.decodeUtf8' bytes of
        Left _ -> pure $ Left "Invalid UTF8"
        Right _ -> case validateJsonProperties bytes of
          Left reason -> pure $ Left reason
          Right () -> case eitherDecodeStrict' bytes of
            Left _ -> pure $ Left "Invalid JSON"
            Right value -> Core.profileNativeCall value $ runExceptT $ dispatch s value
      let (candidate,value) = either (\reason -> (s,reject reason)) id decoded
          encoded = BL.toStrict (encode value)
      length' <- E.evaluate (BS.length encoded)
      let liveBytes = sum (map (toInteger . BS.length) (Map.elems $ results candidate))
      if Map.size (results candidate) >= 64 || length' > maxResult || liveBytes + toInteger length' > toInteger maxResultBytes || nextId candidate == maxBound
        then pure (s,Nothing)
        else let resultId = nextId candidate in pure
          (candidate { nextId = resultId+1, results = Map.insert resultId encoded (results candidate) },Just resultId)
    case token of Nothing -> pure 5; Just resultId -> poke output resultId >> pure 0

ah_hs_result_size :: Word64 -> Ptr Word64 -> IO CInt
ah_hs_result_size token output = protect $ if output == nullPtr then pure 1 else do
  poke output 0
  MV.withMVar store $ \s -> case Map.lookup token (results s) of
    Nothing -> pure 3
    Just bytes -> poke output (fromIntegral $ BS.length bytes) >> pure 0

ah_hs_result_copy :: Word64 -> Ptr Word8 -> Word64 -> IO CInt
ah_hs_result_copy token output capacity = protect $ MV.withMVar store $ \s -> case Map.lookup token (results s) of
  Nothing -> pure 3
  Just bytes | output == nullPtr || capacity < fromIntegral (BS.length bytes) -> pure 1
  Just bytes -> BS.useAsCStringLen bytes (\(source,size) -> copyBytes output (castPtr source) size) >> pure 0

ah_hs_result_release :: Word64 -> IO CInt
ah_hs_result_release token = protect $ MV.modifyMVar store $ \s ->
  if Map.member token (results s) then pure (s { results = Map.delete token (results s) },0) else pure (s,3)

ah_hs_live_count :: Ptr Word64 -> IO CInt
ah_hs_live_count output = protect $ if output == nullPtr then pure 1 else do
  poke output 0
  MV.withMVar store $ \s -> poke output (fromIntegral $ Map.size (sessions s) + Map.size (results s)) >> pure 0

foreign export ccall ah_hs_call :: Ptr Word8 -> Word64 -> Ptr Word64 -> IO CInt
foreign export ccall ah_hs_result_size :: Word64 -> Ptr Word64 -> IO CInt
foreign export ccall ah_hs_result_copy :: Word64 -> Ptr Word8 -> Word64 -> IO CInt
foreign export ccall ah_hs_result_release :: Word64 -> IO CInt
foreign export ccall ah_hs_live_count :: Ptr Word64 -> IO CInt
