{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildOwnedSpec (deckBuildOwnedSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither)
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Owned
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Validate qualified as Validate
must :: Either Text a -> IO a
must= either (fail . T.unpack) pure
check label ok=unless ok (fail label)>>putStrLn("PASS "<>label)
get key (Object o)=KM.lookup key o
get _ _=Nothing
hasCode key code report=case get key report of Just(Array a)->any (\v->get "code" v==Just(String code)) a; _->False
stat key report=get "statistics" report >>= get key
deckBuildOwnedSpecs :: IO ()
deckBuildOwnedSpecs = do
  cat<-must catalogResult
  let run cards meta=must $ deriveOwnedConstruction cat (Map.fromList cards) meta
      card code=maybe (error "Missing actual card") id $ Map.lookup code $ catalogCards cat
      validate who choice slots meta = do
        let req=BuildRequest who "initial" Nothing Nothing Nothing choice
        schema<-Validate.schemaFor cat req
        sid<-maybe (Left "Missing schemaId") Right $ get "schemaId" schema
        input<-either (Left . T.pack) Right $ parseEither parseJSON $ object
          ["schema" .= req,"schemaId" .= sid,"deck" .= object
            ["investigator_code" .= who,"slots" .= slots,"meta" .= meta]]
        Validate.validateWith cat input
      simple slots=must $ validate "01001" (object []) slots (object [])
  allSeven<-run [(c,1) | c<-["06167","07303","08031","08046","09077","12181","53010"]] KM.empty
  check "owned seven actual declarations produce signed total35 and four options" $
    sizeDelta allSeven==35 && length(additionalOptions allSeven)==4 && null(ownedIssues allSeven)
  versatile<-run [("06167",2)] KM.empty
  check "owned repeats limited grants per physical copy" $ sizeDelta versatile==10 && length(additionalOptions versatile)==2
  single<-run [("08046",1)] KM.empty
  check "owned exact Underworld Support gives negative size and singleton" $ sizeDelta single==(-5) && singletonCap single==Just 1
  learning<-run [("08031",1)] KM.empty
  check "owned size-only card needs no invented grant" $ sizeDelta learning==15 && null(additionalOptions learning)
  let changed=(card "06167") {cardRequirements=Just $ object ["size" .= (5::Int),"unknown" .= True]}
  unknown<-must $ deriveOwnedConstruction (Catalog $ Map.insert "06167" changed $ catalogCards cat) (Map.singleton "06167" 1) KM.empty
  check "owned unknown requirement prevents certification" $ not(null $ ownedIssues unknown)
  brand<-run [("11080",1),("01060",1)] $ KM.singleton "attachments_11080" $ String "01060"
  check "owned brand exact eligible target consumes only its metadata key" $ brandedTarget brand==Just "01060" && validatedMetaKeys brand==["attachments_11080"] && null(ownedIssues brand)
  doubled<-run [("11080",1),("01060",2)] $ KM.singleton "attachments_11080" $ String "01060,01060"
  absent<-run [("11080",1)] $ KM.singleton "attachments_11080" $ String "01060"
  check "owned brand invalid imported count or absent target stays pending" $ all (not . null . ownedIssues) [doubled,absent] && null(validatedMetaKeys doubled)
  unrelated<-run [("11080",1)] $ KM.singleton "attachments_09077" $ String "01060"
  check "owned does not consume another attachment contract" $ null(validatedMetaKeys unrelated)
  empty<-run [("11080",1)] $ KM.singleton "attachments_11080" $ String ""
  noBrand<-run [] $ KM.singleton "attachments_11080" $ String ""
  check "owned original empty brand list accepted only with owner" $ null(ownedIssues empty) && not(null $ ownedIssues noBrand)
  one<-simple $ object ["06167" .= (1::Int),"01060" .= (1::Int)]
  two<-simple $ object ["06167" .= (2::Int),"01060" .= (2::Int)]
  three<-simple $ object ["06167" .= (2::Int),"01060" .= (2::Int),"01046" .= (1::Int)]
  check "owned actual integration per-copy off-class capacity and target size" $
    stat "requiredSize" one==Just(Number 35) && stat "requiredSize" two==Just(Number 40)
      && not(hasCode "errors" "deckOptionsCapacity" one) && not(hasCode "errors" "deckOptionsCapacity" two)
      && hasCode "errors" "deckOptionsCapacity" three && not(hasCode "unsupportedReasons" "permanentRulesUnsupported" one)
  minimumReport<-simple $ object ["07303" .= (1::Int)]
  check "owned actual Ancestral Knowledge reuses virtual types minimum without becoming grant" $
    stat "requiredSize" minimumReport==Just(Number 35) && hasCode "errors" "atLeast" minimumReport
      && not(hasCode "unsupportedReasons" "unsupportedOption" minimumReport)
  excluded<-simple $ object ["53010" .= (1::Int),"01033" .= (1::Int)]
  check "owned actual virtual Ally exclusion and precise original slot predicates" $
    hasCode "errors" "excludedCard" excluded && not(hasCode "unsupportedReasons" "unsupportedOption" excluded)
      && hasSlot "Ally" (card "01033") && not(hasSlot "ally" (card "01033"))
      && hasSlot "none" (card "06167") && not(hasSlot "none" (card "07154"))
  let myriad=maybe (error "Missing myriad") id $ case filter (\c->rawField "myriad" c==Just(Bool True)) $ Map.elems $ catalogCards cat of c:_->Just c; _->Nothing
  check "owned original myriad and singleton and brand cap precedence" $
    copyLimitFor empty myriad==Just 3 && copyLimitFor single myriad==Just 1
      && copyLimitFor brand (card "01060")==Just 1
  precious<-traverse (must . copyGroupName . card) ["08114","08115"]
  check "owned original Precious Memento subtitles keep distinct copy groups" $ length precious==2 && head precious/=last precious
  let selection=object ["deck_size_selected" .= String "40","faction_selected" .= String "mystic"]
  mandy<-must $ validate "06002" selection (object ["08031" .= (1::Int),"06008" .= (2::Int),"06009" .= (1::Int)]) selection
  check "owned actual Mandy signature count retains selected40 despite adjusted55" $
    stat "requiredSize" mandy==Just(Number 55) && stat "signatureDeckSize" mandy==Just(Number 40)
      && not(hasCode "errors" "requiredSignature" mandy)
  branded<-must $ validate "01004" (object []) (object ["11080" .= (1::Int),"01060" .= (2::Int)])
    (object ["attachments_11080" .= String "01060"])
  check "owned actual branded spell copy error and finite shared choices" $
    hasCode "errors" "copyLimit" branded && not(hasCode "unsupportedReasons" "deckMetaUnsupported" branded)
      && case get "ownedConstruction" branded >>= get "attachmentFields" of Just(Array a)->length a==1; _->False
  check "owned exact normal size excludes encounter and signatures and permanents" $
    countsForSize (card "01060") && not(countsForSize $ card "01012") && not(countsForSize $ card "12181")
  let forbidden=(card "01060") {cardRaw=case cardRaw(card "01060") of Object o->Object $ KM.insert "text" (String "Forbidden.") o;v->v}
      changedCat=Catalog $ Map.insert "01060" forbidden $ catalogCards cat
      req=BuildRequest "01004" "initial" Nothing Nothing Nothing (object [])
  schema<-must $ Validate.schemaFor changedCat req
  input<-must $ either (Left . T.pack) Right $ parseEither parseJSON $ object
    ["schema" .= req,"schemaId" .= get "schemaId" schema,"deck" .= object ["investigator_code" .= String "01004","slots" .= object ["01060" .= (1::Int)]]]
  forbiddenReport<-must $ Validate.validateWith changedCat input
  check "owned original exact English Forbidden marker rejects without prose guessing" $ hasCode "errors" "forbiddenCard" forbiddenReport
  putStrLn "DECKBUILD_OWNED_COMPLETED"
