{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildAttachmentsSpec (deckBuildAttachmentsSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither)
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Attachments
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Validate qualified as Validate
import Arkham.Native.DeckMetadata (deckMetadataValidate)

must :: Either Text a -> IO a
must=either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check name ok=unless ok(fail name)>>putStrLn("PASS "<>name)
bad :: Either a b -> Bool
bad (Left _) = True
bad _ = False
get key (Object o)=KM.lookup key o
get _ _=Nothing
deckBuildAttachmentsSpecs :: IO ()
deckBuildAttachmentsSpecs = do
  cat<-must catalogResult
  -- Actual integrated catalog must carry exact five definitions. No fixture injects them.
  let slots=Map.fromList [("03264",1),("09077",1),("10079",1),("11080",1),("07303",1)]
      meta owner wire=KM.singleton (Key.fromText $ attachmentKey owner) (String wire)
      plan inv ss m=must $ deriveAttachmentPlan cat inv ss m
      consumed owner p=attachmentKey owner `elem` validatedMetaKeys p
      status p s=any (\v->case v of Object o->KM.lookup "status" o==Just(String s);_->False) (fields p)
      findCard predicate=case filter predicate (Map.elems $ catalogCards cat) of c:_->pure c;_->fail "Missing actual candidate"
      trait s c=case rawField "traits" c of Just(String t)->s `T.isInfixOf` t;_->False
  p0<-plan "05002" slots KM.empty
  check "attachments all six actual definitions offered without metadata mutation" $ length(fields p0)==6 && null(issues p0) && null(validatedMetaKeys p0)
  tactic<-findCard $ \c->cardType c=="event" && trait "Tactic." c && fromMaybeLimit c>0 && cardCode c/="03264"
  let tc=cardCode tactic; withTactic=Map.insert tc 2 slots
  p1<-plan "01001" withTactic $ meta "03264" tc
  check "attachments partial original STTP preselection is fallback not invalid" $ consumed "03264" p1 && status p1 "fallback"
  p2<-plan "01001" withTactic $ meta "03264" (tc<>","<>tc)
  check "attachments STTP distinct-code limit rejects repeated target" $ not(consumed "03264" p2) && not(null $ issues p2)
  p3<-plan "01001" (Map.delete "03264" withTactic) $ meta "03264" tc
  check "attachments asset owner must actually be in main slots" $ not(consumed "03264" p3)
  p4<-plan "01001" slots $ meta "05002" "05010"
  p5<-plan "05002" (Map.insert "05010" 1 slots) $ meta "05002" "05010"
  check "attachments Joe owner is investigator and mandatory card remains literal partial wire" $ not(consumed "05002" p4) && consumed "05002" p5 && status p5 "fallback"
  insight<-findCard $ \c->cardType c=="event" && trait "Insight." c && cardCode c/="05010" && fromMaybeLimit c>0
  p6<-plan "05002" (Map.singleton (cardCode insight) 1) $ meta "05002" (cardCode insight)
  check "attachments missing Joe fixed card is not silently inserted or declared complete" $ consumed "05002" p6 && status p6 "fallback" && not(status p6 "complete")
  skill<-findCard $ \c->cardType c=="skill" && cardSubtype c `notElem` [Just "weakness",Just "basicweakness"]
  p7<-plan "01001" (Map.fromList [("07303",1),(cardCode skill,5)]) $ meta "07303" (T.intercalate "," $ replicate 5 $ cardCode skill)
  check "attachments AK uses actual instances not artificial printed copy cap" $ consumed "07303" p7 && status p7 "complete"
  p8<-plan "01001" slots $ meta "07303" "05010"
  check "attachments AK rejects weakness event and leaves key unconsumed" $ not(consumed "07303" p8)
  p9<-plan "01001" slots $ meta "09077" "unknown-card"
  check "attachments unknown target is not certified" $ not(consumed "09077" p9)
  check "attachments bounded string protocol rejects nonstring and empty tokens" $ all bad [decodeAttachmentCodes Null,decodeAttachmentCodes(String "01001,"),decodeAttachmentCodes(String " 01001")]
  -- Explicit synthetic shared-candidate isolator: retain real definitions, replace only candidate fields.
  let synthetic=tactic {cardCode="synthetic",cardType="event",cardLimit=Just 2,cardRaw=object ["traits" .= ("Tactic. Illicit."::Text)]}
      syntheticCat=cat {catalogCards=Map.insert "synthetic" synthetic $ catalogCards cat}
      both=KM.fromList [("attachments_03264",String "synthetic"),("attachments_09077",String "synthetic,synthetic")]
  p10<-must $ deriveAttachmentPlan syntheticCat "01001" (Map.insert "synthetic" 2 slots) both
  check "attachments shared-instance conflict invalidates both involved keys without clamping" $ not(consumed "03264" p10) && not(consumed "09077" p10)
  p11<-plan "01001" slots $ meta "11080" ""
  check "attachments empty draft remains unselected and no copy cap is derived here" $ consumed "11080" p11 && status p11 "unselected"
  let insightCodes=["01022","01036","01037","01038","02022"]
      joeSlots=Map.fromList $ ("05010",1):[(c,2) | c<-insightCodes]
      joeWire=T.intercalate "," $ "05010":concatMap (replicate 2) insightCodes
  completeJoe<-plan "05002" joeSlots $ meta "05002" joeWire
  check "attachments actual eleven Joe instances include fixed case and certify complete" $
    consumed "05002" completeJoe && status completeJoe "complete"
  wrongJoe<-plan "05002" (Map.insert "01064" 1 joeSlots) $ meta "05002"
    (T.intercalate "," $ "01064":concatMap (replicate 2) insightCodes)
  check "attachments eleven without Joe fixed case is unsupported not falsely fallback" $
    not(consumed "05002" wrongJoe) && status wrongJoe "unsupported"
  let transport inv = deckMetadataValidate $ object ["deck" .= object
        ["investigator_code" .= (inv::Text),"investigator_name" .= String "QA"
        ,"slots" .= object ["05010" .= (1::Int)],"meta" .= String "{\"attachments_05002\":\"05010\"}"]]
  check "attachments real metadata transport recognizes actual Joe but rejects foreign owner" $
    not(bad $ transport "05002") && bad(transport "01001")
  let validate catalog inv ss m = do
        let req=BuildRequest inv "initial" Nothing Nothing Nothing (object [])
        schema<-Validate.schemaFor catalog req
        sid<-maybe (Left "Missing schemaId") Right $ get "schemaId" schema
        input<-either (Left . T.pack) Right $ parseEither parseJSON $ object
          ["schema" .= req,"schemaId" .= sid,"deck" .= object
            ["investigator_code" .= inv,"slots" .= ss,"meta" .= m]]
        Validate.validateWith catalog input
  report<-must $ validate cat "05002" (object ["05010" .= (1::Int)])
    (object ["attachments_05002" .= String "05010"])
  check "attachments actual Build interprets partial Joe without unknown metadata and preserves size40" $
    maybe False (\v->get "requiredSize" v==Just(Number 40)) (get "statistics" report)
      && case get "unsupportedReasons" report of Just(Array a)->null a;_->False
  let spell=tactic {cardCode="synthetic",cardType="asset",cardLevel=Just 0,cardLimit=Just 2
        ,cardTraits=Set.fromList ["spell","trick"],cardRaw=object ["traits" .= String "Spell. Trick."]}
      spellCat=cat {catalogCards=Map.insert "synthetic" spell $ catalogCards cat}
      spellSlots=object ["11080" .= (1::Int),"10079" .= (1::Int),"synthetic" .= (1::Int)]
  conflict<-must $ validate spellCat "01004" spellSlots
    (object ["attachments_11080" .= String "synthetic","attachments_10079" .= String "synthetic"])
  check "attachments shared conflict prevents independent Owned branded-cap activation" $
    (get "attachments" conflict >>= get "appliedBrandedTarget")==Just Null
      && case get "unsupportedReasons" conflict of Just(Array a)->not(null a);_->False
  trickAsset<-findCard $ \c->cardCode c/="10079" && cardType c=="asset" && trait "Trick." c && fromMaybeLimit c>0
  trickPlan<-plan "01001" (Map.fromList [("10079",1),(cardCode trickAsset,1)]) $ meta "10079" (cardCode trickAsset)
  check "attachments original Bewitching permits actual non-event Trick asset" $ consumed "10079" trickPlan && null(issues trickPlan)
  putStrLn "DECKBUILD_ATTACHMENTS_COMPLETED"
 where
  fromMaybeLimit c=case cardLimit c of Just n->n;_->0
