{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildExtraSpec (deckBuildExtraSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither)
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Extra
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Validate qualified as Validate
import Arkham.Native.DeckMetadata (deckMetadataValidate)
must :: Either Text a -> IO a
must=either (fail . T.unpack) pure
check label ok=unless ok(fail label)>>putStrLn("PASS "<>label)
bad (Left _)=True
bad _=False
get key (Object o)=KM.lookup key o
get _ _=Nothing
extraErrors report=case get "extraDeck" report >>= get "errors" of Just(Array a)->foldr (:) [] a;_->[]
hasError code report=any (\v->get "code" v==Just(String code)) $ extraErrors report
deckBuildExtraSpecs :: IO ()
deckBuildExtraSpecs = do
  cat<-must catalogResult
  let card code=maybe (fail "Missing actual catalog fixture") pure $ Map.lookup code $ catalogCards cat
      meta wire=KM.singleton "extra_deck" $ String wire
  jim<-card "90049"
  ordinary<-card "02004"
  defaults<-must $ deriveExtraPlan cat jim KM.empty
  check "extra actual Jim definition demands ten total and per-code cap1" $
    extraEnabled defaults && extraTargetCount defaults==Just 10 && extraPerCodeLimit defaults==Just 1
      && length(extraOptions defaults)==1 && null(extraValidatedMetaKeys defaults)
  duplicate<-must $ deriveExtraPlan cat jim $ meta "01033,01033,90053"
  check "extra original wire repetitions and order survive decoding" $
    extraWireCodes duplicate==["01033","01033","90053"] && Map.lookup "01033" (extraQuantities duplicate)==Just 2
  empty<-must $ deriveExtraPlan cat ordinary $ meta ""
  absent<-must $ deriveExtraPlan cat ordinary KM.empty
  check "extra ordinary empty is explicit no-op while absent stays absent" $
    not(extraEnabled empty) && extraValidatedMetaKeys empty==["extra_deck"] && null(extraValidatedMetaKeys absent)
  unsupported<-must $ deriveExtraPlan cat ordinary $ meta "90053"
  check "extra ordinary nonempty is unsupported and not consumed" $
    not(null $ extraIssues unsupported) && null(extraValidatedMetaKeys unsupported)
  check "extra malformed scalar empty token whitespace and input bounds reject" $
    all bad [decodeExtraDeck Null,decodeExtraDeck $ String "01033,",decodeExtraDeck $ String " 01033"
      ,decodeExtraDeck $ String $ T.intercalate "," $ replicate 501 "01033"]
  check "extra unknown card cannot be certified" $ bad $ deriveExtraPlan cat jim $ meta "unknown-card"
  let changed=jim {cardRaw=case cardRaw jim of Object o->Object $ KM.insert "side_deck_requirements" (object ["size" .= (8::Int)]) o;v->v}
  unknown<-must $ deriveExtraPlan cat changed $ meta "90053"
  check "extra changed definition remains pending not inferred" $ not(null $ extraIssues unknown) && null(extraValidatedMetaKeys unknown)
  let validate codes = do
        let req=BuildRequest "90049" "initial" Nothing Nothing Nothing (object [])
        schema<-Validate.schemaFor cat req
        sid<-maybe (Left "Missing schemaId") Right $ get "schemaId" schema
        input<-either (Left . T.pack) Right $ parseEither parseJSON $ object
          ["schema" .= req,"schemaId" .= sid,"deck" .= object
            ["investigator_code" .= String "90049","slots" .= object ["01063" .= (2::Int)]
            ,"meta" .= object ["extra_deck" .= T.intercalate "," codes]]]
        Validate.validateWith cat input
      legal=["01018","01021","01032","01033","01048","01055","01063","01076","01082","90053"]
  good<-must $ validate legal
  check "extra actual original ten-card side is independently legal including level1 cards" $
    null(extraErrors good) && case get "extraDeck" good >>= get "countedSize" of Just(Number 10)->True;_->False
  duplicateReport<-must $ validate ("01018":tail legal)
  doubled<-must $ validate ("01018":"01018":drop 2 legal)
  check "extra per-code duplicate rejected without mixing main and extra copies" $
    null(extraErrors duplicateReport) && hasError "copyLimit" doubled
  wrongTrait<-must $ validate ("01060":tail legal)
  wrongLevel<-must $ validate ("01691":tail legal)
  check "extra actual wrong trait or level violates shared side permission" $
    hasError "deckOptionsCapacity" wrongTrait && hasError "deckOptionsCapacity" wrongLevel
  missing<-must $ validate $ filter (/="90053") legal
  check "extra actual missing Shade and total are side-specific errors" $
    hasError "requiredSignature" missing && hasError "deckSize" missing
  let metadata value=deckMetadataValidate $ object ["deck" .= object
        ["investigator_code" .= String "02004","investigator_name" .= String "Jim Culver","slots" .= object [],"meta" .= String value]]
  check "extra gameplay guard rejects raw empty wire but key deletion is accepted" $
    bad(metadata "{\"extra_deck\":\"\"}") && not(bad $ metadata "{}")
  putStrLn "DECKBUILD_EXTRA_COMPLETED"
