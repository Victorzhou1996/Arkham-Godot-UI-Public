{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildCustomizationsSpec (deckBuildCustomizationsSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Data.Either (isLeft)
import Arkham.Native.DeckMetadata
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Customizations

must :: Either Text a -> IO a
must = either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS " <> label)
get :: Text -> Value -> Value
get k (Object o) = maybe Null id $ KM.lookup (K.fromText k) o
get _ _ = Null

deckBuildCustomizationsSpecs :: IO ()
deckBuildCustomizationsSpecs = do
  cat <- must catalogResult
  let run code text = must $ projectSelectedCustomizations cat
        (KM.singleton (K.fromText $ "cus_" <> code) $ String text) (Map.singleton code 1)
      resolve p code = maybe (fail "Projected card absent") pure $ Map.lookup code $ catalogCards $ projectedCatalog p
      successful code text = do
        p <- run code text
        unless (null $ customizationIssues p) $ fail $ show $ customizationIssues p
        resolve p code
      tags c = case get "tags" $ cardRaw c of Array a -> foldr (:) [] a; _ -> []
      textOf c = case get "text" $ cardRaw c of String t -> t; _ -> ""
  partial <- successful "09022" "5|1"
  check "customizations partial XP affects level without activating tags" $
    cardLevel partial == Just 1 && String "hh" `notElem` tags partial
  mixed <- successful "09022" "1|1,5|1"
  check "customizations completed tags plus partial total XP" $
    cardLevel mixed == Just 1 && String "hh" `elem` tags mixed && String "hd" `elem` tags mixed
  ordered <- successful "09022" "5|3,1|1"
  reordered <- successful "09022" "1|1,5|3"
  check "customizations original definition order independent of wire order" $
    cardRaw ordered == cardRaw reordered && cardLevel ordered == Just 2
  armor <- successful "09021" "0|1,2|2,3|2,4|2"
  check "customizations trait replacement slot and health sanity original bases" $
    Set.member "relic" (cardTraits armor) && get "real_slot" (cardRaw armor) == String "Arcane"
      && get "health" (cardRaw armor) == Number 4 && get "sanity" (cardRaw armor) == Number 4
      && get "cost" (cardRaw armor) == Number 3
  limit <- successful "09061" "6|3"
  power <- successful "09081" "7|3"
  check "customizations actual copy limit replacements" $ cardLimit limit == Just 3 && cardLimit power == Just 3
  trait <- successful "09060" "0|0|Monster"
  baseTrait <- maybe (fail "Missing base") pure $ Map.lookup "09060" $ catalogCards cat
  check "customizations chosen trait changes text not card traits" $
    cardTraits trait == cardTraits baseTrait && "[[Monster]]" `T.isInfixOf` textOf trait
  skill <- successful "09079" "0|0|willpower"
  check "customizations typed skill selection" $ "[willpower]" `T.isInfixOf` textOf skill
  chosen <- successful "09042" "0|0|01060"
  check "customizations typed selected card matcher and English name" $ "<u>Shrivelling</u>" `T.isInfixOf` textOf chosen
  slot <- successful "09080" "5|2|0"
  check "customizations original zero-based slot removal" $ get "real_slot" (cardRaw slot) == String "Arcane"
  absent <- run "09079" "1|1"
  wrong <- run "09042" "0|0|01016"
  check "customizations unresolved or mismatched choices do not certify" $
    not (null $ customizationIssues absent) && not (null $ customizationIssues wrong)
  check "customizations shared parser preserves strict EOF duplicate and bounds" $
    all (isLeft . validatedCustomizations "09022") ["1|1,1|1","1|1000001","99|1","1|1 trailing"]
  let cases = [("09021",""),("09022",""),("09023",""),("09040",""),("09041","")
        ,("09042","0|0|01060"),("09059",""),("09060","0|0|Monster"),("09061","")
        ,("09079","0|0|willpower"),("09080",""),("09081",""),("09099","")
        ,("09100",""),("09101","0|0|Monster^Humanoid"),("09119","")]
  allCards <- traverse (\(code,t) -> run code t) cases
  check "customizations all sixteen fixed definition indices project without global mutation" $
    all (null . customizationIssues) allCards && Map.size (catalogCards cat) == 6047
  putStrLn "DECKBUILD_CUSTOMIZATIONS_COMPLETED"
