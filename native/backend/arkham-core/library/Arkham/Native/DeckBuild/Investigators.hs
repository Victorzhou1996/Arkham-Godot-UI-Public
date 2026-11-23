{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Investigators where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Maybe (fromMaybe)
import Data.Set qualified as Set
import Data.Text (Text)
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types

data ResolvedSides = ResolvedSides
  { identityCard :: CatalogCard, frontCard :: CatalogCard, backCard :: CatalogCard
  , sideChoices :: [Text]
  } deriving Show

rawField :: Text -> CatalogCard -> Value
rawField key card = case cardRaw card of
  Object o -> fromMaybe Null $ KM.lookup (K.fromText key) o
  _ -> Null

lookupCard :: Catalog -> Text -> Either Text CatalogCard
lookupCard cat code = maybe (Left $ "Unknown card: " <> code) Right $ Map.lookup code $ catalogCards cat

-- Only explicit cache duplicate edges; never name or numeric-prefix matching.
canonicalCode :: Catalog -> Text -> Either Text Text
canonicalCode cat = go Set.empty
 where
  go seen code = do
    unless (Set.notMember code seen) $ Left "Cyclic duplicate card relation"
    card <- lookupCard cat code
    case rawField "duplicate_of_code" card of
      Null -> Right code
      String "" -> Right code
      String next -> go (Set.insert code seen) (normalizeCode next)
      _ -> Left "Malformed duplicate card relation"

resolveSides :: Catalog -> BuildRequest -> Either Text ResolvedSides
resolveSides cat request = do
  original <- lookupCard cat $ investigator request
  unless (all (\c -> rawField "parallel" c `elem` [Bool True,Bool False])
    [c | c <- Map.elems $ catalogCards cat,cardType c=="investigator"]) $
    Left "Missing explicit pack/cycle-derived parallel projection"
  base <- canonicalCode cat $ cardCode original
  unless (cardType original == "investigator") $ Left "Deck owner is not an investigator"
  edges <- traverse edge [c | c <- Map.elems $ catalogCards cat,cardType c == "investigator"]
  let pairs = [p | Just p <- edges]
      related = Set.fromList $ base : [b | (a,b) <- pairs,a==base] <> [a | (a,b) <- pairs,b==base]
  candidates <- traverse (\c -> do canon <- canonicalCode cat $ cardCode c; pure (c,canon))
    [c | c <- Map.elems $ catalogCards cat,cardType c == "investigator"]
  let allowed = [cardCode c | (c,canon) <- candidates,Set.member canon related]
      side selected = do
        let code = fromMaybe (investigator request) selected
        unless (code `elem` allowed) $ Left "Investigator side is not an explicit related version"
        lookupCard cat code
  front <- side $ alternateFront request
  back <- side $ alternateBack request
  pure $ ResolvedSides original front back allowed
 where
  edge card
    | rawField "parallel" card == Bool True && rawField "alt_art_investigator" card == Bool True =
        case rawField "alternate_of_code" card of
          String code | code /= "" -> do
            a <- canonicalCode cat $ cardCode card
            b <- canonicalCode cat $ normalizeCode code
            other <- lookupCard cat b
            unless (cardType other == "investigator") $ Left "Parallel relation targets non-investigator"
            pure $ Just (a,b)
          _ -> Left "Parallel investigator missing exact base relation"
    | otherwise = Right Nothing

sideSchema :: Catalog -> ResolvedSides -> Value
sideSchema cat sides = object
  ["front" .= cardCode (frontCard sides),"back" .= cardCode (backCard sides)
  ,"choices" .= [object ["value" .= code,"label" .= maybe code cardName (Map.lookup code $ catalogCards cat)]
                 | code <- sideChoices sides]]

-- meta is already decoded with duplicate properties rejected by the caller.
-- Null/empty/absent means the original investigator; no silent fallback on bad IDs.
validateSideMeta :: BuildRequest -> Object -> Either Text ()
validateSideMeta request meta = mapM_ check
  [("alternate_front",alternateFront request),("alternate_back",alternateBack request)]
 where
  effective = fromMaybe (investigator request)
  check (key,selected) = do
    actual <- case KM.lookup key meta of
      Nothing -> Right Nothing
      Just Null -> Right Nothing
      Just (String "") -> Right Nothing
      Just (String code) -> Right $ Just $ normalizeCode code
      _ -> Left "Investigator side metadata must be a string or null"
    unless (effective actual == effective selected) $ Left "Investigator side metadata differs from schema"

-- Original deck-validation.ts requirements. Does NOT invent paired-set rules:
-- the original explicitly leaves signature pair validation TODO.
signatureErrors :: Catalog -> CatalogCard -> Int -> Map Text Int -> Either Text [Issue]
signatureErrors cat back size slots = do
  groups <- case cardRequirements back of
    Nothing -> Right []
    Just (Object req) -> case KM.lookup "card" req of
      Nothing -> Right []
      Just (Object groups) -> traverse group $ KM.toList groups
      _ -> Left "Malformed signature requirements"
    _ -> Left "Malformed deck requirements"
  normalized <- traverse (\(code,n) -> do canonical <- canonicalCode cat code; pure (canonical,n)) $ Map.toList slots
  let quantities = Map.fromListWith (+) normalized
      disciplines = length [() | (code,n) <- Map.toList slots,n>0
                           ,Just c <- [Map.lookup code $ catalogCards cat],cardName c=="Discipline"]
  fmap concat $ traverse (evaluate quantities disciplines) groups
 where
  group (key,Object alternatives) = do
    canonical <- traverse (canonicalCode cat . K.toText) $ KM.keys alternatives
    pure (K.toText key,Set.toList $ Set.fromList canonical)
  group _ = Left "Malformed signature alternatives"
  evaluate quantities disciplines (code,alternatives) = do
    required <- lookupCard cat code
    let actual = sum [Map.findWithDefault 0 c quantities | c <- alternatives]
        target | code=="06008" = (size-20) `div` 10
               | code=="08015" = max 1 disciplines
               | otherwise = fromMaybe 1 $ cardLimit required
        exact = code `elem` ["06008","08015"]
        satisfied = if exact then actual==target else actual>=target
    -- Mandy's frontend formula is fractional for unsupported arbitrary sizes.
    unless (code/="06008" || (size>=20 && (size-20) `mod` 10==0)) $
      Left "Occult Evidence quantity requires a supported deck size"
    pure [Issue "requiredSignature" code | not satisfied]

frontErrors :: Catalog -> CatalogCard -> Map Text Int -> Either Text [Issue]
frontErrors cat front slots = do
  code <- canonicalCode cat $ cardCode front
  normalized <- traverse (\(c,n) -> do k <- canonicalCode cat c; pure (k,n)) $ Map.toList slots
  let quantity c = sum [n | (k,n) <- normalized,k==c]
      directives = sum [n | (c,n) <- Map.toList slots,Just card <- [Map.lookup c $ catalogCards cat],cardName card=="Directive"]
  pure $ [Issue "parallelFrontRequirement" "90038" | code=="90037",quantity "90038"<=0]
      <> [Issue "parallelFrontRequirement" "90025: exactly three Directive cards" | code=="90024",directives/=3]

sideMetadataKeys :: [Text]
sideMetadataKeys = ["alternate_front","alternate_back"]
