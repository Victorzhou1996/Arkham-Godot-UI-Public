{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildInvestigatorsSpec (deckBuildInvestigatorsSpecs) where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Investigators
import Arkham.Native.DeckBuild.Validate qualified as Validate
import Data.Aeson.Types (parseEither)

must :: Either Text a -> IO a
must = either (fail . T.unpack) pure
check :: String -> Bool -> IO ()
check label ok = unless ok (fail label) >> putStrLn ("PASS "<>label)
bad :: Either a b -> Bool
bad (Left _) = True
bad _ = False

deckBuildInvestigatorsSpecs :: IO ()
deckBuildInvestigatorsSpecs = do
  cat <- must catalogResult
  let request front back = BuildRequest "01001" "initial" Nothing front back (object [])
  ordinary <- must $ resolveSides cat $ request Nothing Nothing
  check "sides absent selects original" $ cardCode (frontCard ordinary)=="01001" && cardCode (backCard ordinary)=="01001"
  mixed <- must $ resolveSides cat $ request Nothing (Just "90024")
  check "sides exact parallel back independently selected" $ cardCode (frontCard mixed)=="01001" && cardCode (backCard mixed)=="90024"
  reversed <- must $ resolveSides cat $ request (Just "90024") Nothing
  check "sides exact parallel front independently selected" $ cardCode (frontCard reversed)=="90024" && cardCode (backCard reversed)=="01001"
  check "sides foreign investigator rejected" $ bad $ resolveSides cat $ request (Just "01002") Nothing
  check "sides actual meta must equal request" $
    validateSideMeta (request Nothing (Just "90024")) (KM.fromList [("alternate_back",String "90024")])==Right ()
      && bad (validateSideMeta (request Nothing (Just "90024")) KM.empty)
  wendy <- must $ lookupCard cat "90037"
  w0 <- must $ frontErrors cat wendy Map.empty
  w1 <- must $ frontErrors cat wendy $ Map.singleton "90038" 1
  check "sides Wendy front requires Tidal Memento" $ not (null w0) && null w1
  roland <- must $ lookupCard cat "90024"
  r2 <- must $ frontErrors cat roland $ Map.singleton "90025" 2
  r3 <- must $ frontErrors cat roland $ Map.singleton "90025" 3
  check "sides Roland counts exactly three directives" $ not (null r2) && null r3
  let requirement card key alternatives = card {cardRequirements=Just $ object
        ["card" .= object [key .= object [k .= String "present" | k <- alternatives]]]}
      base = requirement (backCard ordinary) "01006" ["01006","98005"]
  replacement <- must $ signatureErrors cat base 30 $ Map.fromList [("01006",1),("98005",1)]
  check "signatures original plus replacement is not exact-one" $ null replacement
  missing <- must $ signatureErrors cat base 30 Map.empty
  check "signatures missing group rejected" $ not $ null missing
  let mandy = requirement (backCard ordinary) "06008" ["06008"]
  m2 <- must $ signatureErrors cat mandy 40 $ Map.singleton "06008" 2
  m1 <- must $ signatureErrors cat mandy 40 $ Map.singleton "06008" 1
  check "signatures Mandy uses chosen size exact quantity" $ null m2 && not (null m1)
  let burden = requirement (backCard ordinary) "08015" ["08015"]
      disciplines = take 2 [cardCode c | c <- Map.elems $ catalogCards cat,cardName c=="Discipline"]
      slots = Map.fromList $ ("08015",2):[(c,1) | c <- disciplines]
  b <- must $ signatureErrors cat burden 30 slots
  check "signatures Burden counts selected Discipline definitions" $ length disciplines==2 && null b
  let original = cardCode $ backCard ordinary
      duplicate = (backCard ordinary) {cardCode="fixture-duplicate",cardRaw=object ["duplicate_of_code" .= original]}
      duplicateCat = Catalog $ Map.insert "fixture-duplicate" duplicate $ catalogCards cat
  check "signatures duplicate relation is explicit" $ canonicalCode duplicateCat "fixture-duplicate"==Right original
  schema <- must $ Validate.schemaFor cat $ request Nothing Nothing
  let get key (Object o) = KM.lookup key o
      get _ _ = Nothing
      sides = get "investigatorSides" schema
  check "sides schema exposes finite actual related versions" $
    maybe False (\v -> get "front" v==Just (String "01001") && get "back" v==Just (String "01001")) sides
  let validate req meta slots = do
        s <- Validate.schemaFor cat req
        sid <- maybe (Left "Missing schemaId") Right $ get "schemaId" s
        input <- either (Left . T.pack) Right $ parseEither parseJSON $ object
          ["schema" .= req,"schemaId" .= sid,"deck" .= object
            ["investigator_code" .= investigator req,"slots" .= slots,"meta" .= meta]]
        Validate.validateWith cat input
      issues key report = case get key report of Just (Array values) -> foldr (:) [] values; _ -> []
      has code report = any (\v -> get "code" v==Just (String code)) $ issues "errors" report
      noPending report = null $ issues "unsupportedReasons" report
  directive <- must $ validate (request (Just "90024") Nothing)
    (object ["alternate_front" .= String "90024"])
    (Map.fromList [("90025",1),("90026",1),("90027",1),("01006",1),("01007",1)] :: Map.Map Text Int)
  check "sides integrated Directive definitions excluded from size and shared-title cap" $
    noPending directive && not (has "copyLimit" directive) && not (has "parallelFrontRequirement" directive)
      && maybe False (\v -> get "countedSize" v==Just (Number 0)) (get "statistics" directive)
  combined <- must $ validate (request Nothing Nothing) (object [])
    (Map.fromList [("01006",1),("98005",1),("01007",1)] :: Map.Map Text Int)
  check "signatures integrated original plus replacement no guessed pair rejection" $
    noPending combined && not (has "requiredSignature" combined)
  check "sides integrated mismatched metadata rejected" $ bad $ validate
    (request Nothing (Just "90024")) (object []) (Map.empty :: Map.Map Text Int)
  otherPermanent <- must $ validate (request Nothing Nothing) (object []) (Map.singleton "02158" 1 :: Map.Map Text Int)
  check "sides front permanent exemption does not blanket-authorize other permanents" $
    any (\v -> get "code" v==Just (String "permanentRulesUnsupported")) $ issues "unsupportedReasons" otherPermanent
  putStrLn "DECKBUILD_INVESTIGATORS_COMPLETED"
