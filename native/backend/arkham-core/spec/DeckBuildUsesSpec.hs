{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildUsesSpec (deckBuildUsesSpecs) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Validate

get :: Text -> Value -> Value
get key (Object o) = maybe Null id $ KM.lookup (K.fromText key) o
get _ _ = Null

must :: Either Text a -> IO a
must = either (fail . T.unpack) pure

check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS deck " <> label)

deckBuildUsesSpecs :: IO ()
deckBuildUsesSpecs = do
  actual <- must catalogResult
  akachi <- maybe (fail "Missing Akachi") pure $ Map.lookup "03004" $ catalogCards actual
  let req = BuildRequest "03004" "initial" Nothing Nothing Nothing $ object []
      level = object ["min" .= (0 :: Int),"max" .= (4 :: Int)]
      uses values = object ["uses" .= (values :: [Text]),"level" .= level]
      duplicate = object ["tag" .= (["uc"] :: [Text]),"level" .= level]
      schema options = schemaFor (Catalog $ Map.singleton "03004" $ akachi {cardOptions=Just options}) req
      -- Adapter-only one-card catalogs isolate access, not legal game decks.
      probe c = do
        let inv = akachi {cardRequirements=Just $ object ["size" .= (1 :: Int)]}
            cat = Catalog $ Map.fromList [("03004",inv),(cardCode c,c)]
            deck = DeckInput "03004" (Map.singleton (cardCode c) 1) Map.empty Nothing Nothing Map.empty []
        s <- must $ schemaFor cat req
        sid <- case get "schemaId" s of String t -> pure t; _ -> fail "Missing schema ID"
        result <- must $ validateWith cat $ ValidationRequest req sid deck $ object []
        pure result
      actualCard code = maybe (fail $ "Missing " <> T.unpack code) pure $ Map.lookup code $ catalogCards actual
      granted result = get "assignedOrdinaryCopies" (get "statistics" result) == Number 1
      synthetic text = akachi {cardCode="uses-probe",cardName="uses-probe",cardType="asset"
        ,cardSubtype=Nothing,cardFactions=Set.singleton "guardian",cardTraits=Set.empty
        ,cardLevel=Just 0,cardLimit=Just 2,cardPermanent=False,cardRestrictions=Null
        ,cardOptions=Nothing,cardRequirements=Nothing,cardMissingEnglish=[]
        ,cardRaw=object ["text" .= (text :: Text)]}
      textGrants text = granted <$> probe (synthetic text)
  s <- must $ schemaFor actual req
  check "uses actual Akachi schema supported (exact duplicate companion)" $ get "supportStatus" s == String "supported"
  cards <- traverse actualCard ["03266","03192","04026"]
  results <- traverse probe cards
  check "uses actual cross-faction charge cards including prefixed and zero uses" $ all granted results
  levelFive <- probe $ (synthetic "Uses (3 charges).") {cardLevel=Just 5}
  check "uses remains conjunctive with level 0-4" $ not $ granted levelFive
  positive <- traverse textGrants ["Uses (1 charge).","Uses (12 CHARGES).","Prefix. Uses\t(0 charges)."]
  check "uses singular normalization word case digits and single whitespace" $ and positive
  negative <- traverse textGrants
    ["Uses (3 ammo).","Mentions charges.\nUses (3 charges).","uses (3 charges)."
    ,"Uses  (3 charges).","Uses (3  charges).","Uses (X charges).","Uses (-1 charges)."
    ,"Uses ( charges).","Uses (3 charge-s).","Add 1 charge to an asset with uses (charges)."]
  check "uses first-line case-sensitive finite original regex boundaries" $ not $ or negative
  unknown <- must $ schema $ toJSON [uses ["ammo"]]
  orphan <- must $ schema $ toJSON [duplicate]
  changed <- must $ schema $ toJSON [object ["tag" .= (["uc"] :: [Text]),"level" .= level,"limit" .= (1 :: Int)],uses ["charges","charge"]]
  check "uses unknown types orphan uc and changed uc remain unsupported" $
    all ((== String "unsupported") . get "supportStatus") [unknown,orphan,changed]
  let another = akachi {cardCode="another",cardOptions=Just $ toJSON [duplicate,uses ["charges","charge"]]}
      otherReq = BuildRequest "another" "initial" Nothing Nothing Nothing $ object []
  other <- must $ schemaFor (Catalog $ Map.singleton "another" another) otherReq
  check "uses duplicate exemption is bound to Akachi identity" $ get "supportStatus" other == String "unsupported"
  putStrLn "DECKBUILD_USES_COMPLETED"
