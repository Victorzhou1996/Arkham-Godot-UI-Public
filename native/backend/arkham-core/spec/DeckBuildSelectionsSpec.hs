{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module DeckBuildSelectionsSpec (deckBuildSelectionSpecs) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.ByteString.Lazy qualified as BL
import Data.Either (isLeft)
import Data.Foldable (toList)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding qualified as TE
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

request :: Text -> Value -> BuildRequest
request iid chosen = BuildRequest iid "initial" Nothing Nothing Nothing chosen

metaText :: Value -> Value
metaText = String . TE.decodeUtf8 . BL.toStrict . encode

-- Explicit adapter fixtures: real selector definitions, synthetic cards and
-- requirements to isolate conjunction/capacity. NOT legal completed game decks.
fixture :: Catalog -> Text -> Maybe Value -> IO Catalog
fixture original iid replacement = do
  inv <- maybe (fail "Investigator absent") pure $ Map.lookup iid $ catalogCards original
  let inv' = inv { cardRequirements = Just $ object ["size" .= (30 :: Int)]
                 ,cardOptions = maybe (cardOptions inv) Just replacement }
      card code factions types traits = inv
        { cardCode=code,cardName=code,cardType=types,cardSubtype=Nothing
        ,cardFactions=Set.fromList factions,cardTraits=Set.fromList traits
        ,cardLevel=Just 0,cardLimit=Just 100,cardPermanent=False
        ,cardRestrictions=Null,cardMissingEnglish=[],cardRequirements=Nothing
        ,cardOptions=Nothing }
  pure $ Catalog $ Map.fromList
    [(iid,inv'),("x-event",card "x-event" ["guardian"] "event" ["improvised"])
    ,("x-rogue",card "x-rogue" ["rogue"] "event" [])
    ,("x-asset",card "x-asset" ["guardian"] "asset" ["improvised"])
    ,("x-dual",card "x-dual" ["guardian","rogue"] "event" [])]

validate :: Catalog -> BuildRequest -> Value -> [(Text,Int)] -> Either Text Value
validate cat req meta slots = do
  schema <- schemaFor cat req
  sid <- case get "schemaId" schema of String t -> Right t; _ -> Left "No schema ID"
  let deck = DeckInput (investigator req) (Map.fromList slots) Map.empty Nothing
        (Just meta) Map.empty []
      original = object ["investigator_code" .= investigator req,"meta" .= meta,"slots" .= Map.fromList slots]
  validateWith cat $ ValidationRequest req sid deck original

hasError :: Text -> Value -> Bool
hasError code value = any ((== String code) . get "code") $ rows $ get "errors" value

deckBuildSelectionSpecs :: IO ()
deckBuildSelectionSpecs = do
  actual <- must catalogResult
  fields <- traverse (\(iid,n) -> do
      schema <- must $ schemaFor actual $ request iid $ object []
      pure $ length (rows $ get "selectionFields" schema) == n)
    [("06002",2),("06003",1),("09001",1),("09018",2),("11001",1)
    ,("11014",1),("90037",1),("90087",2),("98019",1)]
  check "selection catalog: nine actual investigators / twelve fields" $ and fields
  mandy <- fixture actual "06002" Nothing
  let mandyChoice = object ["deck_size_selected" .= ("40" :: Text),"faction_selected" .= ("rogue" :: Text)]
  mandyResult <- must $ validate mandy (request "06002" mandyChoice) (metaText mandyChoice) [("x-rogue",1)]
  check "Mandy selected target40 (not a card grant)" $
    get "requiredSize" (get "statistics" mandyResult) == toJSON (40 :: Int)
  let tonyChoice = object ["faction_selected" .= ("guardian" :: Text)]
  tony <- fixture actual "06003" Nothing
  ten <- must $ validate tony (request "06003" tonyChoice) (metaText tonyChoice) [("x-event",10),("x-rogue",20)]
  eleven <- must $ validate tony (request "06003" tonyChoice) (metaText tonyChoice) [("x-event",11),("x-rogue",19)]
  check "Tony shared grant quota10 allows ten / rejects eleven" $
    get "status" ten == String "valid" && hasError "deckOptionsCapacity" eleven
  wendy <- fixture actual "90037" Nothing
  let both = object ["option_selected" .= ("both" :: Text)]
  wendyResult <- must $ validate wendy (request "90037" both) (metaText both) [("x-event",1)]
  check "Wendy chosen both adds five" $
    get "requiredSize" (get "statistics" wendyResult) == toJSON (35 :: Int)
  let option child = toJSON [object ["type" .= (["event"] :: [Text]),"limit" .= (30 :: Int)
        ,"option_select" .= [object (["id" .= ("trait" :: Text),"trait" .= (["improvised"] :: [Text])] <> child)]]]
      trait = object ["option_selected" .= ("trait" :: Text)]
  conjunctive <- fixture actual "11001" $ Just $ option []
  good <- must $ validate conjunctive (request "11001" trait) (metaText trait) [("x-event",30)]
  bad <- must $ validate conjunctive (request "11001" trait) (metaText trait) [("x-asset",30)]
  check "selected child AND parent type; one grant node" $
    get "status" good == String "valid" && hasError "deckOptionsCapacity" bad
  dual <- fixture actual "06003" $ Just $ toJSON [object
    ["faction" .= (["rogue"] :: [Text]),"faction_select" .= (["guardian"] :: [Text])]]
  dualResult <- must $ validate dual (request "06003" tonyChoice) (metaText tonyChoice) [("x-dual",30)]
  check "AND does not intersect faction lists and lose dual-faction card" $ get "status" dualResult == String "valid"
  unknown <- fixture actual "11001" $ Just $ option ["text" .= ("unimplemented" :: Text)]
  unknownResult <- must $ validate unknown (request "11001" trait) (metaText trait) [("x-asset",30)]
  check "unknown child condition stays unsupported without guessed errors" $
    get "status" unknownResult == String "unsupported" && null (rows $ get "errors" unknownResult)
  check "actual meta differing from schema is rejected" $ isLeft $
    validate tony (request "06003" tonyChoice) (metaText $ object ["faction_selected" .= ("seeker" :: Text)]) [("x-event",30)]
  check "nonoffered choice rejected" $ isLeft $ schemaFor actual $
    request "06003" $ object ["faction_selected" .= ("neutral" :: Text)]
  missing <- must $ validate tony (request "06003" $ object []) (metaText $ object []) [("x-event",30)]
  check "missing selector is incomplete, not union of all grants" $ get "status" missing == String "unsupported"
  check "duplicate meta string accessor rejected" $ isLeft $ validate tony (request "06003" tonyChoice)
    (String "{\"faction_selected\":\"rogue\",\"faction_selected\":\"guardian\"}") [("x-event",30)]
  let rich = object ["faction_selected" .= ("guardian" :: Text),"cus_01001" .= ("0|1" :: Text)]
  richResult <- must $ validate tony (request "06003" tonyChoice) (metaText rich) [("x-event",30)]
  check "unknown rule metadata retained and unsupported" $
    get "status" richResult == String "unsupported" && get "meta" (get "deck" richResult) == metaText rich
  let inert = object ["faction_selected" .= ("guardian" :: Text),"intro_md" .= ("retained" :: Text)]
  inertResult <- must $ validate tony (request "06003" tonyChoice) (metaText inert) [("x-event",10),("x-rogue",20)]
  check "display metadata no longer blanket-rejected" $ get "status" inertResult == String "valid"
  let lolaChoices = object ["faction_1" .= ("guardian" :: Text),"faction_2" .= ("rogue" :: Text)]
  lola <- must $ schemaFor actual $ request "90087" lolaChoices
  check "parallel Lola selections and independent minimum supported" $ get "supportStatus" lola == String "supported"
