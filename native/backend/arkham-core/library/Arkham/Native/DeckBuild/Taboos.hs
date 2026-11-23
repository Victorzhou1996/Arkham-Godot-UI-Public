{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
{-# LANGUAGE TemplateHaskell #-}
module Arkham.Native.DeckBuild.Taboos where
import Prelude
import Control.Monad (unless,foldM)
import Data.Aeson
import Data.Aeson.Types (Parser,parseEither)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.ByteString qualified as BS
import Data.FileEmbed (embedFile,makeRelativeToProject)
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog

tabooBytes :: BS.ByteString
tabooBytes = $(makeRelativeToProject "native-data/deckbuild-taboos.json" >>= embedFile)
tabooDigest :: Text
tabooDigest = digest tabooBytes
data TabooData = TabooData {tabooSets :: [Value],tabooOverrides :: Object} deriving Show
whitelist :: [Text]
whitelist = ["back_text","code","customization_change","customization_options","customization_text"
  ,"deck_options","deck_requirements","exceptional","real_back_text","real_customization_change"
  ,"real_customization_text","real_taboo_text_change","real_text","taboo_set_id","taboo_text_change","taboo_xp","text"]
parseTabooData :: Value -> Parser TabooData
parseTabooData = withObject "Taboo catalog" $ \o -> do
  version <- o .: "schemaVersion" :: Parser Int
  unless (version==1) $ fail "Unknown taboo catalog version"
  sets <- o .: "sets"
  ids <- traverse (withObject "Taboo set" $ \s -> do
    i <- s .: "id" :: Parser Int
    _ <- s .: "name" :: Parser Text
    _ <- s .: "date" :: Parser Text
    count <- s .: "card_count" :: Parser Int
    unless (i>0 && count>=0) $ fail "Invalid taboo set"
    pure i) sets
  unless (Map.size (Map.fromList [(i,()) | i<-ids])==length ids) $ fail "Duplicate taboo set"
  overrides <- o .: "overrides"
  mapM_ (\(key,v) -> withObject "Taboo override" (\row -> do
    unless (all ((`elem` whitelist) . K.toText) $ KM.keys row) $ fail "Unknown taboo override field"
    code <- row .: "code" :: Parser Text
    i <- row .: "taboo_set_id" :: Parser Int
    unless (i `elem` ids && K.toText key==code<>"-"<>T.pack(show i)) $ fail "Invalid taboo override identity") v) $ KM.toList overrides
  pure $ TabooData sets overrides
tabooData :: Either Text TabooData
tabooData = case eitherDecodeStrict' tabooBytes of
  Left e -> Left $ T.pack e
  Right v -> either (Left . T.pack) Right $ parseEither parseTabooData v
tabooSchema :: Value
tabooSchema = case tabooData of
  Left e -> object ["supported" .= False,"error" .= e]
  Right d -> toJSON
    (object ["id" .= (0::Int),"name" .= ("No taboo"::Text),"date" .= Null]:tabooSets d)

-- Exposed pure seam for narrow synthetic absent/null/English tests.
applyOverride :: CatalogCard -> Object -> Either Text CatalogCard
applyOverride card row = do
  unless (all ((`elem` whitelist) . K.toText) $ KM.keys row) $ Left "Unknown taboo override field"
  unless (KM.lookup "code" row==Just (String $ cardCode card)) $ Left "Taboo override card mismatch"
  original <- case cardRaw card of Object o -> Right o; _ -> Left "Card raw is not object"
  let merged = KM.union row original -- left-biased: explicit null DOES replace.
      oldMissing = cardMissingEnglish card
  (english,missing) <- foldM normalize (merged,oldMissing) ["text","back_text"]
  let final = KM.insert "native_missing_english_fields" (toJSON missing) english
  either (Left . T.pack) Right $ parseEither parseJSON $ Object final
 where
  normalize (current,missing) field =
    let key=K.fromText field; real=K.fromText $ "real_"<>field in
    case KM.lookup real row of
      Just v@(String _) -> Right (KM.insert key v current,filter (/=field) missing)
      Just Null -> case KM.lookup key row of
        Just (String text) | not (T.null text) -> Right (KM.insert key Null current,field:filter (/=field) missing)
        _ -> Right (KM.insert key Null current,filter (/=field) missing)
      Just _ -> Left "Malformed English taboo text"
      Nothing -> absent current missing key field
  absent current missing key field = case KM.lookup key row of
    Nothing -> Right (current,missing)
    Just Null -> Right (KM.insert key Null current,filter (/=field) missing)
    Just (String "") -> Right (KM.insert key (String "") current,filter (/=field) missing)
    Just (String _) -> Right (KM.insert key Null current,field:filter (/=field) missing)
    _ -> Left "Malformed taboo text"

applyTabooCatalog :: Maybe Int -> Catalog -> Either Text Catalog
applyTabooCatalog Nothing cat = Right cat
applyTabooCatalog (Just 0) cat = Right cat
applyTabooCatalog (Just selected) cat = do
  d <- tabooData
  let hasId (Object o)=KM.lookup "id" o==Just(toJSON selected)
      hasId _=False
  unless (any hasId $ tabooSets d) $ Left "Unknown taboo set"
  Catalog <$> Map.traverseWithKey (\code card ->
    case KM.lookup (K.fromText $ code<>"-"<>T.pack(show selected)) $ tabooOverrides d of
      Nothing -> Right card
      Just (Object row) -> applyOverride card row
      _ -> Left "Malformed taboo override") (catalogCards cat)

variantAllowed :: Maybe Int -> Text -> Text -> Bool
variantAllowed selected code ident = case (selected,tabooData) of
  (Just n,Right d) | n>0 -> ident==code<>"-"<>T.pack(show n) && KM.member (K.fromText ident) (tabooOverrides d)
  _ -> False
