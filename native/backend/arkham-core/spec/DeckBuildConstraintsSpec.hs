{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildConstraintsSpec (deckBuildConstraintsSpecs) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Foldable (toList)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
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
hasError :: Text -> Value -> Bool
hasError key = any ((== String key) . get "code") . rows . get "errors"
request :: Text -> Value -> BuildRequest
request iid chosen = BuildRequest iid "initial" Nothing Nothing Nothing chosen

-- Explicit adapter-only catalogs, NOT completed/legal game decks. All source
-- investigator definitions below are loaded from the fixed embedded catalog.
deckBuildConstraintsSpecs :: IO ()
deckBuildConstraintsSpecs = do
  actual <- must catalogResult
  template <- maybe (fail "Missing Lola") pure $ Map.lookup "03006" $ catalogCards actual
  let makeCard code factions traits typ = template
        {cardCode=code,cardName=code,cardType=typ,cardSubtype=Nothing
        ,cardFactions=Set.fromList factions,cardTraits=Set.fromList traits
        ,cardLevel=Just 0,cardLimit=Just 100,cardPermanent=False,cardRestrictions=Null
        ,cardOptions=Nothing,cardRequirements=Nothing,cardMissingEnglish=[]
        ,cardRaw=object ["text" .= ("Adapter fixture" :: Text)]}
      a = makeCard "fixture-a" ["guardian","seeker"] ["insight"] "event"
      b = makeCard "fixture-b" ["rogue"] [] "skill"
      base = object ["type" .= (["event","skill","asset"] :: [Text])]
      minimumRow virtual = object
        ["faction" .= (["guardian","seeker","rogue"] :: [Text])
        ,"atleast" .= object ["min" .= (2 :: Int),"factions" .= (3 :: Int)]
        ,"virtual" .= virtual]
      run options cards signature = do
        let size = sum [n | (c,n) <- cards,Just (cardCode c) /= signature]
            signatureValue = case signature of
              Nothing -> object []
              Just code -> object [K.fromText code .= object [K.fromText code .= code]]
            inv = template {cardOptions=Just $ toJSON options
              ,cardRequirements=Just $ object ["size" .= size,"card" .= signatureValue]}
            cat = Catalog $ Map.fromList $ ("03006",inv) : [(cardCode c,c) | (c,_) <- cards]
            req = request "03006" $ object []
            slots = Map.fromList [(cardCode c,n) | (c,n) <- cards]
            deck = DeckInput "03006" slots Map.empty Nothing Nothing Map.empty []
        schema <- must $ schemaFor cat req
        sid <- case get "schemaId" schema of String t -> pure t; _ -> fail "Missing schemaId"
        must $ validateWith cat $ ValidationRequest req sid deck $ object []
      actualSchema iid selected = must $ schemaFor actual $ request iid selected
  supported <- traverse (\(iid,selected) -> actualSchema iid selected)
    [("03006",object []),("05002",object [])
    ,("90087",object ["faction_1" .= ("guardian" :: Text),"faction_2" .= ("seeker" :: Text)])]
  check "constraints three real investigator schemas supported" $ all ((== String "supported") . get "supportStatus") supported
  positive <- run [base,minimumRow True] [(a,2),(b,2)] Nothing
  check "constraints quantities and dual faction each count" $ get "status" positive == String "valid"
  negative <- run [base,minimumRow True] [(a,2),(b,1)] Nothing
  check "constraints each bucket threshold not aggregate sum" $ hasError "atLeast" negative
  let insight = object ["trait" .= (["Insight"] :: [Text]),"virtual" .= True
        ,"atleast" .= object ["min" .= (3 :: Int),"traits" .= (1 :: Int)]]
      signature = (makeCard "fixture-signature" ["neutral"] ["insight"] "event") {cardLevel=Nothing}
  signatures <- run [base,insight] [(a,2),(signature,1)] $ Just "fixture-signature"
  check "constraints required signatures participate in minimum count" $ get "status" signatures == String "valid"
  noGrant <- run [insight] [(a,3)] Nothing
  check "constraints virtual minimum never authorizes a card" $ hasError "deckOptionsCapacity" noGrant && not (hasError "atLeast" noGrant)
  normalGrant <- run [minimumRow False] [(a,2),(b,2)] Nothing
  check "constraints nonvirtual minimum retains original grant" $ get "status" normalGrant == String "valid"
  let cap = object ["trait" .= (["insight"] :: [Text]),"virtual" .= True,"limit" .= (1 :: Int)]
  capResult <- run [base,cap] [(a,2)] Nothing
  check "constraints independent virtual cap" $ hasError "virtualLimit" capResult
  let range lo hi = object ["min" .= (lo :: Int),"max" .= (hi :: Int)]
      overridden = object ["type" .= (["event"] :: [Text]),"level" .= range 5 5,"base_level" .= range 0 0]
  overrideResult <- run [overridden] [(a,1)] Nothing
  check "constraints base level replaces rather than intersects level" $ get "status" overrideResult == String "valid"
  complex <- actualSchema "89001" $ object []
  check "constraints unreached permanent text policy remains unsupported" $ get "supportStatus" complex == String "unsupported"
  original <- maybe (fail "Missing parallel investigator") pure $ Map.lookup "90087" $ catalogCards actual
  let mutate (Object o) | KM.member "ignore_match" o = Object $ KM.insert "ignore_match" (Bool False) o
      mutate v = v
      changed = original {cardOptions=fmap (toJSON . map mutate . rows) $ cardOptions original}
      changedCat = Catalog $ Map.insert "90087" changed $ catalogCards actual
  unknown <- must $ schemaFor changedCat $ request "90087" $ object
    ["faction_1" .= ("guardian" :: Text),"faction_2" .= ("seeker" :: Text)]
  check "constraints changed ignore_match is not silently consumed" $ get "supportStatus" unknown == String "unsupported"
  let badMinimum = object ["type" .= (["event"] :: [Text]),"atleast" .= object
        ["min" .= (1 :: Int),"types" .= (1 :: Int),"traits" .= (1 :: Int)]]
      badInv = template {cardOptions=Just $ toJSON [badMinimum]}
  bad <- must $ schemaFor (Catalog $ Map.singleton "03006" badInv) $ request "03006" $ object []
  check "constraints ambiguous minimum domain remains unsupported" $ get "supportStatus" bad == String "unsupported"
  putStrLn "DECKBUILD_CONSTRAINTS_COMPLETED"
