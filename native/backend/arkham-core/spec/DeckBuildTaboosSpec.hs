{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildTaboosSpec (deckBuildTaboosSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Taboos
import Arkham.Native.DeckBuild.Customizations qualified as Custom
import Arkham.Native.DeckBuild.Validate qualified as Validate
import Arkham.Native.DeckBuild.Types
import Data.Aeson.Types (parseEither)
must :: Either Text a -> IO a
must = either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS "<>label)
raw cat code = cardRaw <$> Map.lookup code (catalogCards cat)
field key c = case cardRaw c of Object o -> KM.lookup key o; _ -> Nothing
deckBuildTaboosSpecs :: IO ()
deckBuildTaboosSpecs = do
  cat <- must catalogResult
  d <- must tabooData
  check "taboo pinned ten sets and 559 overrides" $ length(tabooSets d)==10 && KM.size(tabooOverrides d)==559
  a <- must $ applyTabooCatalog Nothing cat
  b <- must $ applyTabooCatalog (Just 0) cat
  check "taboo None and zero preserve every raw card" $ fmap cardRaw(catalogCards a)==fmap cardRaw(catalogCards cat) && fmap cardRaw(catalogCards b)==fmap cardRaw(catalogCards cat)
  check "taboo unknown set rejected" $ case applyTabooCatalog (Just 9999) cat of Left _ -> True; _ -> False
  selected <- must $ applyTabooCatalog (Just 10) cat
  let card c code = maybe (fail "Missing actual fixture card") pure $ Map.lookup code $ catalogCards c
  unchanged <- card cat "01001"
  after <- card selected "01001"
  check "taboo absent actual override leaves card exact" $ cardRaw unchanged==cardRaw after
  mandy <- card selected "06002"
  baseMandy <- card cat "06002"
  check "taboo actual Mandy back requirements are rebuilt" $ cardRequirements mandy/=cardRequirements baseMandy
    && case cardRequirements mandy of Just(Object o)->KM.lookup "size" o==Just(Number 50); _->False
  power <- card selected "09081"
  basePower <- card cat "09081"
  check "taboo actual customization definition replaced before marks" $
    field "customization_options" power/=field "customization_options" basePower
      && cardLevel power==cardLevel basePower && cardTraits power==cardTraits basePower
      && field "tags" power==field "tags" basePower
  english <- must $ applyOverride unchanged $ KM.fromList
    [("code",String "01001"),("text",String "中文"),("real_text",String "English replacement"),("taboo_xp",Number 3)]
  missing <- must $ applyOverride unchanged $ KM.fromList [("code",String "01001"),("text",String "中文")]
  check "taboo English only and missing English reported without changing printed level" $
    field "text" english==Just(String "English replacement") && cardLevel english==cardLevel unchanged
      && "text" `elem` cardMissingEnglish missing && field "text" missing==Just Null
  cleared <- must $ applyOverride unchanged $ KM.fromList [("code",String "01001"),("deck_options",Null),("text",Null)]
  retained <- must $ applyOverride unchanged $ KM.singleton "code" (String "01001")
  englishCleared <- must $ applyOverride unchanged $ KM.fromList [("code",String "01001"),("real_text",Null)]
  check "taboo synthetic explicit null differs from omitted field and unknown override rejects" $
    cardOptions cleared==Nothing && field "text" cleared==Just Null && field "text" englishCleared==Just Null && cardOptions retained==cardOptions unchanged
      && case applyOverride unchanged (KM.fromList [("code",String "01001"),("xp",Number 9)]) of Left _->True; _->False
  customBase <- must $ Custom.projectSelectedCustomizations cat (KM.singleton "cus_09081" $ String "1|1") (Map.singleton "09081" 1)
  customTaboo <- must $ Custom.projectSelectedCustomizations selected (KM.singleton "cus_09081" $ String "1|1") (Map.singleton "09081" 1)
  baseEffect <- card (Custom.projectedCatalog customBase) "09081"
  tabooEffect <- card (Custom.projectedCatalog customTaboo) "09081"
  check "taboo actual selected customization uses derived definition first" $
    null (Custom.customizationIssues customTaboo) && field "text" tabooEffect/=field "text" baseEffect
      && cardLevel tabooEffect==Just 1 && cardRaw basePower/=cardRaw tabooEffect
  let get key (Object o)=KM.lookup key o; get _ _=Nothing
      request = BuildRequest "06002" "initial" (Just 10) Nothing Nothing (object [])
  schema <- must $ Validate.schemaFor cat request
  check "taboo integrated schema uses actual Mandy50 and declared set list" $
    maybe False (\v->get "size" v==Just(Number 50)) (get "requirements" schema)
      && case get "tabooSets" schema of Just(Array a)->length a==11; _->False
  let validate selectedId nativeId actualId = do
        let req=BuildRequest "01001" "initial" (Just selectedId) Nothing Nothing (object [])
        s <- Validate.schemaFor cat req
        sid <- maybe (Left "Missing schema ID") Right $ get "schemaId" s
        input <- either (Left . T.pack) Right $ parseEither parseJSON $ object
          ["schema" .= req,"schemaId" .= sid,"deck" .= object
            ["investigator_code" .= String "01001","slots" .= object ["09081" .= (1::Int)]
            ,"taboo_id" .= (actualId::Int),"native_build" .= object ["variants" .= object ["09081" .= (nativeId::Text)]]]]
        Validate.validateWith cat input
      pendingVariant report = case get "unsupportedReasons" report of
        Just(Array a)->any (\v->get "code" v==Just(String "variantUnsupported")) a
        _->False
  known <- must $ validate 10 "09081-10" 10
  otherList <- must $ validate 10 "09081-9" 10
  check "taboo exact selected variant allowed without cross-list or invented IDs" $
    not (pendingVariant known) && pendingVariant otherList
      && not (variantAllowed (Just 10) "01001" "01001-10")
  check "taboo actual deck and requested list mismatch rejected" $ case validate 10 "09081-10" 9 of Left _->True; _->False
  putStrLn "DECKBUILD_TABOOS_COMPLETED"
