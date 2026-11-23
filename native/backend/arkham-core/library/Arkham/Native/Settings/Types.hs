-- The form protocol is independent of Answer/Session and contains no messages,
-- log keys or arbitrary JSON supplied by the client. Choice identities name
-- entries in a resolved core schema, never values to write into CampaignLog.
module Arkham.Native.Settings.Types where

import Arkham.Prelude
import Arkham.CampaignLog (PartnerStatus (..))
import Control.Monad.Fail (fail)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (Parser, modifyFailure)
import Data.Map.Strict qualified as Map
import Data.Text qualified as T

data FormValue
  = FormBoolean Bool
  | FormInteger Int
  | FormChoice (Maybe Text)
  | FormEntries (Map Text Bool)
  | FormPartner Int Int PartnerStatus
  deriving stock (Show, Eq)

data SettingsFormRequest = SettingsFormRequest
  { formSchemaId :: Text
  , valuesByRowId :: Map Text FormValue
  }
  deriving stock (Show, Eq)

-- Ordered arrays of named entries are deliberate: duplicate row/entry IDs
-- must be rejected BEFORE Map construction, rather than Aeson keeping one
-- property from a duplicate-key JSON object. A raw-byte duplicate-property
-- guard is additionally required at the FFI boundary before dispatch.
instance FromJSON SettingsFormRequest where
  parseJSON value = modifyFailure (const "Invalid settings form") $ decodeFormRequest value

decodeFormRequest :: Value -> Parser SettingsFormRequest
decodeFormRequest = withObject "settings form" \o -> do
    exactFields ["schemaId", "valuesByRowId"] o
    formSchemaId <- boundedText =<< o .: "schemaId"
    raw <- o .: "valuesByRowId" :: Parser [Value]
    when (length raw > 512) $ fail "Too many settings rows"
    entries <- for raw $ withObject "settings row value" \r -> do
      exactFields ["rowId", "value"] r
      rowId <- boundedText =<< r .: "rowId"
      value <- r .: "value"
      pure (rowId, value)
    valuesByRowId <- uniqueMap entries
    pure SettingsFormRequest {..}

instance ToJSON SettingsFormRequest where
  toJSON SettingsFormRequest {..} = object
    [ "schemaId" .= formSchemaId
    , "valuesByRowId" .= [object ["rowId" .= k, "value" .= v] | (k,v) <- Map.toAscList valuesByRowId]
    ]

instance FromJSON FormValue where
  parseJSON value = modifyFailure (const "Invalid settings value") $ decodeFormValue value

decodeFormValue :: Value -> Parser FormValue
decodeFormValue = withObject "settings value" \o -> do
    kind <- o .: "kind" :: Parser Text
    case kind of
      "boolean" -> exactFields ["kind","value"] o >> FormBoolean <$> o .: "value"
      "integer" -> exactFields ["kind","value"] o >> FormInteger <$> o .: "value"
      "choice" -> do
        exactFields ["kind","value"] o
        selected <- o .: "value"
        FormChoice <$> traverse boundedText selected
      "entries" -> do
        exactFields ["kind","value"] o
        raw <- o .: "value" :: Parser [Value]
        when (length raw > 512) $ fail "Too many settings entries"
        entries <- for raw $ withObject "settings entry" \e -> do
          exactFields ["id","checked"] e
          ident <- boundedText =<< e .: "id"
          checked <- e .: "checked"
          pure (ident,checked)
        FormEntries <$> uniqueMap entries
      "partner" -> do
        exactFields ["kind","damage","horror","status"] o
        damage <- o .: "damage"
        horror <- o .: "horror"
        status <- o .: "status"
        unless (damage >= 0 && horror >= 0 && status `elem` [Safe,Resolute,Eliminated]) $
          fail "Invalid editable partner details"
        pure $ FormPartner damage horror status
      _ -> fail "Unknown settings value kind"

instance ToJSON FormValue where
  toJSON = \case
    FormBoolean b -> object ["kind" .= ("boolean" :: Text),"value" .= b]
    FormInteger n -> object ["kind" .= ("integer" :: Text),"value" .= n]
    FormChoice choice -> object ["kind" .= ("choice" :: Text),"value" .= choice]
    FormEntries entries -> object
      [ "kind" .= ("entries" :: Text)
      , "value" .= [object ["id" .= k,"checked" .= b] | (k,b) <- Map.toAscList entries]
      ]
    FormPartner damage horror status -> object
      ["kind" .= ("partner" :: Text),"damage" .= damage,"horror" .= horror,"status" .= status]

exactFields :: [K.Key] -> Object -> Parser ()
exactFields expected o = unless (sort (KM.keys o) == sort expected) $
  fail "Unknown or missing settings field"

boundedText :: Text -> Parser Text
boundedText value = do
  unless (not (T.null value) && T.length value <= 512 && not (T.any (< ' ') value)) $
    fail "Invalid settings identity"
  pure value

uniqueMap :: Ord k => [(k,a)] -> Parser (Map k a)
uniqueMap entries = do
  let result = Map.fromList entries
  unless (Map.size result == length entries) $ fail "Duplicate settings identity"
  pure result
