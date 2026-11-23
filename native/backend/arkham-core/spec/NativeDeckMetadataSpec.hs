{-# LANGUAGE NoImplicitPrelude #-}
module NativeDeckMetadataSpec (deckMetadataSpecs) where
import Prelude
import Data.Aeson
import Data.Text (Text)
import Data.Text qualified as T
import Data.Either (isRight,isLeft)
import Arkham.Native.DeckMetadata (deckMetadataValidate)

deckMetadataSpecs :: IO ()
deckMetadataSpecs = do
  let request metaText = object ["deck" .= object
        ["investigator_code" .= ("01001"::Text),"investigator_name" .= ("Roland Banks"::Text)
        ,"slots" .= object ["09022" .= (1::Int),"03264" .= (1::Int),"07303" .= (1::Int),"10079" .= (1::Int),"09077" .= (1::Int),"11080" .= (1::Int)]
        ,"meta" .= (metaText::Text)]]
      good label value = check label $ isRight $ deckMetadataValidate $ request value
      bad label value = check label $ isLeft $ deckMetadataValidate $ request value
  good "ordinary empty metadata" "{}"
  good "extra deck repetitions retain original quantity" "{\"extra_deck\":\"01001,01021,01021\"}"
  bad "extra deck empty item rejected by shared input guard" "{\"extra_deck\":\"01021,\"}"
  bad "extra deck unknown card rejected" "{\"extra_deck\":\"notARegisteredCard\"}"
  good "five original attachment fields" "{\"attachments_03264\":\"01021\",\"attachments_07303\":\"01021\",\"attachments_10079\":\"01021\",\"attachments_09077\":\"01021\",\"attachments_11080\":\"01021\"}"
  good "original customization parser" "{\"cus_09022\":\"0|1\"}"
  bad "customization duplicate index rejected" "{\"cus_09022\":\"0|1,0|2\"}"
  bad "customization trailing garbage rejected" "{\"cus_09022\":\"0|1garbage\"}"
  bad "customization integer bounded before original read" "{\"cus_09022\":\"0|999999999999999999999999999999999\"}"
  bad "customization index must exist on real card" "{\"cus_09022\":\"999|1\"}"
  bad "customization target must occur in deck" "{\"cus_01021\":\"0|1\"}"
  bad "duplicate metadata property rejected before Aeson" "{\"extra_deck\":\"01021\",\"extra_deck\":\"01022\"}"
  good "selectors are left for existing core selector gate" "{\"deck_size_selected\":\"40\"}"
  case deckMetadataValidate (request "{\"deck_size_selected\":\"40\"}") of
    Right value -> check "result reports unvalidated remaining key" $ T.isInfixOf "remainingKeys" (T.pack $ show value) && T.isInfixOf "deck_size_selected" (T.pack $ show value)
    Left reason -> ioError $ userError $ T.unpack reason
 where
  check label ok = if ok then putStrLn $ "PASS deck metadata " <> label else ioError $ userError label
