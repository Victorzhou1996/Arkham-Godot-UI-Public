{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildTagsSpec (deckBuildTagsSpecs) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Foldable (toList)
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Validate

get :: Text -> Value -> Value
get k (Object o) = maybe Null id $ KM.lookup (K.fromText k) o
get _ _ = Null
rows :: Value -> [Value]
rows (Array a) = toList a
rows _ = []
must :: Either Text a -> IO a
must = either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS " <> label)
field :: Text -> Value -> CatalogCard -> CatalogCard
field key value c = c {cardRaw=case cardRaw c of
  Object o -> Object $ KM.insert (K.fromText key) value o
  _ -> object [K.fromText key .= value]}

deckBuildTagsSpecs :: IO ()
deckBuildTagsSpecs = do
  actual <- must catalogResult
  let card code = maybe (fail $ "Missing " <> T.unpack code) pure $ Map.lookup code $ catalogCards actual
      req iid = BuildRequest iid "initial" Nothing Nothing Nothing $ object []
      supported s = get "supportStatus" s == String "supported"
      status name result = get "status" result == String name
      hasUnsupported key result = any ((== String key) . get "code") $ rows $ get "unsupportedReasons" result
      range = object ["min" .= (0 :: Int),"max" .= (5 :: Int)]
      option tag = object ["tag" .= ([tag] :: [Text]),"level" .= range
        ,"text" .= (["THIS_TEXT_MUST_NOT_BE_REQUIRED"] :: [Text])]
  template <- card "05001"
  let probe options c meta = do
        let inv = template {cardOptions=Just $ toJSON options
              ,cardRequirements=Just $ object ["size" .= (1 :: Int)]}
            cat = Catalog $ Map.fromList [("05001",inv),(cardCode c,c)]
            request = req "05001"
            deck = DeckInput "05001" (Map.singleton (cardCode c) 1) Map.empty Nothing meta Map.empty []
        schema <- must $ schemaFor cat request
        sid <- case get "schemaId" schema of String t -> pure t; _ -> fail "Missing schemaId"
        must $ validateWith cat $ ValidationRequest request sid deck $ object []
  schemas <- traverse (must . schemaFor actual . req) ["05001","98010","09004","10009","90081"]
  check "tags five actual investigator schemas supported" $ all supported schemas
  tagged <- traverse (\(tag,code) -> do c <- card code; probe [option tag] c Nothing)
    [("hh","09084"),("hd","09046"),("pa","09028"),("se","07019")]
  check "tags actual four catalog tags authorize without redundant text" $ all (status "valid") tagged
  shrivelling <- card "01060"
  noTag <- probe [option "hh"] shrivelling Nothing
  check "tags actual untagged official card is a definite nonmatch" $ status "invalid" noTag
  chalice <- card "09084"
  conjunct <- probe [object ["tag" .= (["hh"] :: [Text]),"level" .= object
    ["min" .= (1 :: Int),"max" .= (5 :: Int)]]] chalice Nothing
  check "tags remain AND with level" $ status "invalid" conjunct
  unknown <- probe [option "fa"] chalice Nothing
  emptyTag <- probe [object ["tag" .= ([] :: [Text]),"text" .= (["heal"] :: [Text])]] chalice Nothing
  check "tags unknown fa and text with empty tags remain unsupported" $ all (status "unsupported") [unknown,emptyTag]
  unofficial <- probe [option "hh"] (field "official" (Bool False) chalice) Nothing
  check "tags nonofficial fallback missing is unsupported not false" $
    status "unsupported" unofficial && hasUnsupported "tagDataUnsupported" unofficial
      && null (rows $ get "errors" unofficial)
  let omitted = chalice {cardRaw=case cardRaw chalice of Object o -> Object $ KM.delete "tags" o; v -> v}
  absent <- probe [option "hh"] omitted Nothing
  malformed <- probe [option "hh"] (field "tags" (Number 1) chalice) Nothing
  check "tags omitted or malformed projection cannot certify" $ all (status "unsupported") [absent,malformed]
  powerWord <- card "09081"
  unselected <- probe [option "hh"] powerWord Nothing
  check "tags unselected customization additions do not authorize" $
    status "invalid" unselected && not (null $ rows $ get "customization_options" $ cardRaw powerWord)
  selected <- probe [option "hh"] powerWord $ Just $ String "{\"cus_09081\":\"0|1\"}"
  check "tags selected customization projects without inventing healing access" $
    status "invalid" selected && not (hasUnsupported "deckMetaUnsupported" selected)
      && not (null $ rows $ get "customizations" selected)
  let contradictory = field "text" (String "This card heals horror.") shrivelling
  falseText <- probe [option "hh"] contradictory Nothing
  check "tags official tag absence overrides tempting prose" $ status "invalid" falseText
  putStrLn "DECKBUILD_TAGS_COMPLETED"
