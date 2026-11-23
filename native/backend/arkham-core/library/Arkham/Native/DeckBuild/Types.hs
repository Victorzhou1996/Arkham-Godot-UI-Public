{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Types where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (Parser)
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T

-- No dependency on mutable card registries or the Game interpreter.
data BuildRequest = BuildRequest
  { investigator :: Text
  , mode :: Text
  , taboo :: Maybe Int
  , alternateFront :: Maybe Text
  , alternateBack :: Maybe Text
  , selections :: Value
  } deriving (Show, Eq)

exactFields :: [Text] -> Object -> Parser ()
exactFields allowed o =
  unless (all ((`elem` allowed) . Key.toText) $ KM.keys o) $
    fail "Unknown request field"

-- Accept the original c-prefixed official CardCode JSON or ArkhamDB code.
-- Do not strip a prefix from arbitrary homebrew/custom identities.
normalizeCode :: Text -> Text
normalizeCode t = case T.uncons t of
  Just ('c', rest) | not (T.null rest), T.all (\c -> c >= '0' && c <= '9' || c `elem` ("abcd" :: String)) rest -> rest
  _ -> t

instance FromJSON BuildRequest where
  parseJSON = withObject "DeckBuild schema request" $ \o -> do
    exactFields ["investigatorCode","mode","tabooId","alternateFront","alternateBack","selections"] o
    i <- normalizeCode <$> o .: "investigatorCode"
    m <- o .:? "mode" .!= "initial"
    unless (m `elem` ["initial","upgrade"]) $ fail "Unknown deck-building mode"
    t <- o .:? "tabooId"
    f <- fmap normalizeCode <$> o .:? "alternateFront"
    b <- fmap normalizeCode <$> o .:? "alternateBack"
    s <- o .:? "selections" .!= object []
    unless (T.length i > 0 && T.length i <= 160) $ fail "Invalid investigator identity"
    pure $ BuildRequest i m t f b s

instance ToJSON BuildRequest where
  toJSON r = object
    ["investigatorCode" .= investigator r, "mode" .= mode r, "tabooId" .= taboo r
    ,"alternateFront" .= alternateFront r, "alternateBack" .= alternateBack r
    ,"selections" .= selections r]

data DeckInput = DeckInput
  { deckInvestigator :: Text
  , deckSlots :: Map Text Int
  , deckSideSlots :: Map Text Int
  , deckTaboo :: Maybe Int
  , deckMeta :: Maybe Value
  , deckVariants :: Map Text Text
  , deckUnknownFields :: [Text]
  } deriving Show

parseSlots :: Value -> Parser (Map Text Int)
parseSlots = withObject "Slots" $ \o -> do
  unless (KM.size o <= 200) $ fail "At most 200 distinct slots"
  pairs <- traverse (\(k,v) -> do
    n <- parseJSON v
    unless (n > (0 :: Int) && n <= 100) $ fail "Slot quantities must be integers from 1 to 100"
    let code = normalizeCode $ Key.toText k
    unless (not (T.null code) && T.length code <= 160) $ fail "Invalid slot identity"
    pure (code,n)) (KM.toList o)
  let result = Map.fromList pairs
  unless (Map.size result == length pairs) $ fail "Duplicate normalized slot identity"
  unless (sum (map (toInteger . snd) pairs) <= 500) $ fail "At most 500 card copies"
  pure result

instance FromJSON DeckInput where
  parseJSON = withObject "ArkhamDB deck" $ \o -> do
    -- The complete input remains in ValidationRequest.originalDeck. Known
    -- descriptive fields are inert; other fields are retained and reported as
    -- unsupported rather than dropped or treated as effective rule settings.
    let known = ["slots","sideSlots","investigator_code","investigator_name","taboo_id"
                ,"meta","url","id","name","description_md","native_build"
                ,"date_creation","date_update","user_id","user_name","version"
                ,"tags","source_deck","previous_deck","next_deck","description"]
        extra = filter (`notElem` known) $ map Key.toText $ KM.keys o
    i <- normalizeCode <$> o .: "investigator_code"
    ss <- o .: "slots" >>= parseSlots
    side <- o .:? "sideSlots" .!= object [] >>= parseSlots
    t <- o .:? "taboo_id"
    meta <- o .:? "meta"
    native <- o .:? "native_build" .!= object []
    (variants,nativeExtra) <- withObject "native_build" (\b -> do
      raw <- b .:? "variants" .!= KM.empty
      unless (KM.size raw <= 401) $ fail "Too many native variant references"
      pairs <- traverse (\(k,v) -> do
        ident <- parseJSON v
        let code = normalizeCode $ Key.toText k
        unless (not (T.null ident) && T.length ident <= 160) $ fail "Invalid native variant ID"
        unless (code == i || Map.member code ss || Map.member code side) $
          fail "Native variant must refer to the deck investigator or an existing slot"
        pure (code,ident)) (KM.toList raw)
      let resolved = Map.fromList pairs
      unless (Map.size resolved == length pairs) $ fail "Duplicate normalized native variant code"
      pure (resolved,["native_build." <> Key.toText k | k <- KM.keys b,k /= "variants"])) native
    pure $ DeckInput i ss side t meta variants (extra <> nativeExtra)

data ValidationRequest = ValidationRequest
  { schemaRequest :: BuildRequest
  , suppliedSchemaId :: Text
  , submittedDeck :: DeckInput
  , originalDeck :: Value
  } deriving Show

instance FromJSON ValidationRequest where
  parseJSON = withObject "DeckBuild validation request" $ \o -> do
    exactFields ["schema","schemaId","deck"] o
    r <- o .: "schema"
    sid <- o .: "schemaId"
    raw <- o .: "deck"
    deck <- parseJSON raw
    pure $ ValidationRequest r sid deck raw

data Issue = Issue Text Text deriving (Show, Eq, Ord)
instance ToJSON Issue where
  toJSON (Issue code detail) = object ["code" .= code,"detail" .= detail]

issue :: Text -> Text -> [Issue]
issue c d = [Issue c d]
