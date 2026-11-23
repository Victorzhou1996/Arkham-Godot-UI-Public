{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
{-# LANGUAGE TemplateHaskell #-}
module Arkham.Native.DeckBuild.Catalog where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (Parser, parseEither)
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.ByteString qualified as BS
import Data.ByteString.Lazy qualified as BL
import Data.ByteString.Base16 qualified as B16
import Crypto.Hash.SHA256 qualified as SHA
import Data.FileEmbed (embedFile, makeRelativeToProject)
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Set (Set)
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding qualified as TE
import Arkham.Native.DeckBuild.Types (normalizeCode)

data CatalogCard = CatalogCard
  { cardCode :: Text, cardName :: Text, cardType :: Text
  , cardSubtype :: Maybe Text, cardFactions :: Set Text, cardLevel :: Maybe Int
  , cardTraits :: Set Text, cardLimit :: Maybe Int, cardPermanent :: Bool
  , cardRestrictions :: Value, cardOptions :: Maybe Value
  , cardRequirements :: Maybe Value, cardRaw :: Value, cardMissingEnglish :: [Text]
  } deriving Show

instance FromJSON CatalogCard where
  parseJSON v = withObject "Frozen card catalog entry" (\o -> do
    code <- normalizeCode <$> o .: "code"
    name <- o .: "name"
    ty <- o .: "type_code"
    sub <- o .:? "subtype_code"
    f1 <- o .:? "faction_code"; f2 <- o .:? "faction2_code"; f3 <- o .:? "faction3_code"
    level <- o .:? "xp"
    traits <- o .:? "traits" .!= ""
    lim <- o .:? "deck_limit"
    permanent <- o .:? "permanent" .!= False
    restrictions <- o .:? "restrictions" .!= Null
    options <- o .:? "deck_options"
    requirements <- o .:? "deck_requirements"
    missing <- o .:? "native_missing_english_fields" .!= []
    pure $ CatalogCard code name ty sub
      (Set.fromList [T.toCaseFold f | Just f <- [f1,f2,f3]]) level
      (Set.fromList $ filter (not . T.null) $ map (T.toCaseFold . T.strip) $ T.splitOn "." traits)
      lim permanent restrictions options requirements v missing) v

newtype Catalog = Catalog { catalogCards :: Map Text CatalogCard } deriving Show

catalogBytes :: BS.ByteString
-- Generated from the shipped Oct15 Build cache, selecting exact id == code.
-- Rule-facing name/traits/text are English real_* fields, never translations.
-- The original six published data files remain untouched.
catalogBytes = $(makeRelativeToProject "native-data/deckbuild-catalog.json" >>= embedFile)

digest :: BS.ByteString -> Text
digest = TE.decodeUtf8 . B16.encode . SHA.hash

catalogDigest :: Text
catalogDigest = digest catalogBytes

parseCatalog :: Value -> Parser Catalog
parseCatalog v = do
  cards <- parseJSON v
  let pairs = [(cardCode c,c) | c <- cards]
  unless (Map.size (Map.fromList pairs) == length pairs) $ fail "Duplicate frozen card code"
  pure $ Catalog $ Map.fromList pairs

catalogResult :: Either Text Catalog
catalogResult = case eitherDecodeStrict' catalogBytes of
  Left reason -> Left $ T.pack reason
  Right v -> either (Left . T.pack) Right $ parseEither parseCatalog v

-- Canonical hash representation: distinguish objects from arrays and ignore
-- JSON object insertion order. This is for hashing, not the external wire.
canonical :: Value -> Value
canonical (Object o) = toJSON ("object" :: Text,
  [(k, canonical v) | (k,v) <- Map.toAscList $ Map.fromList [(Key.toText k,v) | (k,v) <- KM.toList o]])
canonical (Array a) = toJSON ("array" :: Text, map canonical $ foldr (:) [] a)
canonical v = toJSON ("scalar" :: Text,v)

valueDigest :: Value -> Text
valueDigest = digest . BL.toStrict . encode . canonical
