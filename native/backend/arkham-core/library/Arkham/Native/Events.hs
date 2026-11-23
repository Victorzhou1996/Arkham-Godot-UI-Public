module Arkham.Native.Events (eventJSON, eventJSONForPlayer) where

import Arkham.Prelude
import Arkham.Classes.GameLogger
import Arkham.Id (PlayerId)
import Arkham.Log.Entry (LogEntry (..), LogAudience (..))
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM

-- Trusted-host envelopes. Seat-only events must be recipient-filtered before UI delivery.
eventJSON :: ClientMessage -> Value
eventJSON = \case
  ClientText text -> envelope "text" ["text" .= text]
  ClientLogEntry entry -> envelope "log" ["entry" .= entry]
  ClientRetractLog tag -> envelope "retractLog" ["tag" .= tag]
  ClientError text -> envelope "error" ["text" .= text]
  ClientCard text card -> envelope "card" ["text" .= text, "card" .= card]
  ClientCardOnly seat text card -> envelope "card" ["seat" .= seat, "text" .= text, "card" .= card]
  ClientDrewCards seat text cards kind -> envelope "draw" ["seat" .= seat, "text" .= text, "cards" .= cards, "kind" .= kind]
  ClientTarot value -> envelope "tarot" ["value" .= value]
  ClientShowDiscard iid -> envelope "showDiscard" ["investigator" .= iid]
  ClientShowUnder iid -> envelope "showUnder" ["investigator" .= iid]
  ClientUI text -> envelope "ui" ["text" .= text]
  ClientAudio name -> envelope "audio" ["name" .= name]
  ClientPlayabilityReport card text checks -> envelope "playability" ["card" .= card, "text" .= text, "checks" .= checks]
  ClientCustomCardIssue code detail value -> envelope "customCardIssue" ["code" .= code, "detail" .= detail, "value" .= value]
 where
  envelope kind fields = object $ "type" .= (kind :: Text) : fields

-- Shared delivery projection. The host must still filter old persisted streams
-- on read. This filters log audiences, not the entire trusted-local Game view.
eventJSONForPlayer :: PlayerId -> Value -> Either Text (Maybe Value)
eventJSONForPlayer player (Object o) = do
  visible <- case KM.lookup "seat" o of
    Nothing -> pure True
    Just value -> case Aeson.parseEither parseJSON value of
      Left _ -> Left "Malformed native event recipient"
      Right recipient -> pure $ recipient == player
  if not visible then pure Nothing else case KM.lookup "type" o of
    Just (String "log") -> do
      value <- maybe (Left "Missing structured log entry") Right $ KM.lookup "entry" o
      entry <- case Aeson.parseEither parseJSON value of
        Left _ -> Left "Malformed structured log entry"
        Right parsed -> Right parsed
      pure $ (\filtered -> object ["type" .= ("log" :: Text),"entry" .= filtered]) <$> visibleEntry player entry
    Just (String "draw") -> case KM.lookup "seat" o of
      Nothing -> Left "Draw notification requires a recipient"
      Just _ -> pure $ Just $ Object o
    Just (String kind)
      | kind `elem` ["text","retractLog","error","card","tarot","showDiscard","showUnder","ui","audio","playability","customCardIssue"] -> pure $ Just $ Object o
    _ -> Left "Unsupported native event type"
eventJSONForPlayer _ _ = Left "Malformed native event envelope"

visibleEntry :: PlayerId -> LogEntry -> Maybe LogEntry
visibleEntry player entry = do
  guard $ case logEntryAudience entry of Everyone -> True; OnlyPlayer recipient -> recipient == player
  pure entry {logEntryChildren = mapMaybe (visibleEntry player) $ logEntryChildren entry}
