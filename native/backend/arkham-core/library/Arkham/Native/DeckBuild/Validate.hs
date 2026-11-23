{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Validate
  ( deckBuildSchema, deckBuildValidate, schemaFor, validateWith ) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (Parser, parseEither)
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.List (foldl', nub)
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Maybe (fromMaybe, isJust)
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types
import Arkham.Native.DeckBuild.Selections qualified as Selection
import Arkham.Native.DeckBuild.Customizations qualified as Custom
import Arkham.Native.DeckBuild.Investigators qualified as Inv
import Arkham.Native.DeckBuild.Taboos qualified as Taboo
import Arkham.Native.DeckBuild.Owned qualified as Owned
import Arkham.Native.DeckBuild.Extra qualified as Extra
import Arkham.Native.DeckBuild.Attachments qualified as Attachment

policyIdentity :: Text
policyIdentity = "native-deck-build-v12-shared-attachments"

data Rule = Rule
  { ruleFactions :: Maybe [Text], ruleTraits :: Maybe [Text], ruleTypes :: Maybe [Text]
  , ruleLevel :: Maybe (Int,Int), ruleExcluded :: Bool, ruleLimit :: Maybe Int
  , ruleSupported :: Bool
  , ruleConjuncts :: [Rule]
  , ruleUses :: Maybe [Text]
  , ruleMinimum :: Maybe Minimum, ruleVirtual :: Bool
  , ruleTags :: Maybe [Text]
  , ruleSlots :: Maybe [Text], rulePermanent :: Maybe Bool
  } deriving Show

data Bucket = FactionBucket | TraitBucket | TypeBucket deriving Show
data Minimum = Minimum Bucket [Text] Int Int deriving Show

parseMinimum :: Object -> Value -> Parser Minimum
parseMinimum row = withObject "minimum category count" $ \o -> do
  exactFields ["min","factions","traits","types"] o
  n <- o .: "min"
  fs <- o .:? "factions"; ts <- o .:? "traits"; ys <- o .:? "types"
  (bucket,key,k) <- case [(FactionBucket,"faction",v) | Just v <- [fs]]
      <> [(TraitBucket,"trait",v) | Just v <- [ts]]
      <> [(TypeBucket,"type",v) | Just v <- [ys]] of
    [x] -> pure x
    _ -> fail "Minimum needs exactly one category kind"
  targets <- row .: key :: Parser [Text]
  unless (n > 0 && n <= (500 :: Int) && k > 0 && k <= length targets
    && not (null targets) && all (not . T.null) targets
    && length (nub $ map T.toCaseFold targets) == length targets) $
      fail "Unsupported minimum category bounds"
  pure $ Minimum bucket (map T.toCaseFold targets) n k

data Policy = Policy
  { rules :: [Rule], requiredGroups :: [[Text]], requiredSize :: Int
  , randomWeaknesses :: Int, policyUnsupported :: [Issue], signatureSize :: Int
  }

readValue :: FromJSON a => Value -> Either Text a
readValue = either (Left . T.pack) Right . parseEither parseJSON

parseWith :: (Value -> Parser a) -> Value -> Either Text a
parseWith p = either (Left . T.pack) Right . parseEither p

parseRule :: Value -> Parser (Rule,[Issue])
parseRule = withObject "deck option" $ \o -> do
  tags <- o .:? "tag" :: Parser (Maybe [Text])
  let activeTags = maybe False (not . null) tags
      knownTags = maybe True (all (`elem` ["hh","hd","pa","se"])) tags
      -- Original parseOption suppresses text/text_exact when tag is nonempty.
      -- Unknown tag still leaves the complete option unsupported, never a
      -- partially interpreted text or level grant.
      supported = ["faction","trait","type","level","not","limit","name","error","uses","base_level","atleast","virtual","tag","slot","permanent"]
        <> [key | activeTags && knownTags,key <- ["text","text_exact"]]
      unknown = filter (`notElem` supported) $ map Key.toText $ KM.keys o
      pending = [Issue "unsupportedOption" k | k <- unknown]
  fs <- o .:? "faction"; ts <- o .:? "trait"; tys <- o .:? "type"
  uses <- o .:? "uses" :: Parser (Maybe [Text])
  slots <- o .:? "slot" :: Parser (Maybe [Text])
  permanent <- o .:? "permanent" :: Parser (Maybe Bool)
  -- Original parseOption uses base_level ?? level (not an intersection).
  baseLevel <- o .:? "base_level"
  ordinaryLevel <- o .:? "level"
  lvl <- traverse (withObject "level range" (\l -> do
    exactFields ["min","max"] l
    lo <- l .: "min"; hi <- l .: "max"
    unless (lo >= (0 :: Int) && hi >= lo && hi <= 5) $ fail "Unsupported level range"
    pure (lo,hi))) (case baseLevel of Just v -> Just v; Nothing -> ordinaryLevel)
  minimumRule <- o .:? "atleast" >>= traverse (parseMinimum o)
  virtual <- o .:? "virtual" .!= False
  excluded <- o .:? "not" .!= False
  lim <- o .:? "limit"
  unless (maybe True (\n -> n >= 0 && n <= (500 :: Int)) lim) $ fail "Unsupported option limit"
  let lower = fmap $ map T.toCaseFold
      conflict = [Issue "unsupportedOption" "Excluded option with a limit" | excluded && isJust lim]
      emptyMatcher = [Issue "unsupportedOption" "Option has no recognized matching predicate"
                     | all not [isJust fs,isJust ts,isJust tys,isJust lvl,isJust uses,activeTags,isJust slots,isJust permanent]]
      unknownUses = [Issue "unsupportedOption" "uses: only charge/charges are implemented"
                    | maybe False (any (`notElem` ["charge","charges"])) uses]
      constraintProblems =
        [Issue "unsupportedOption" "Virtual row requires a minimum or a finite cap"
        | virtual && not excluded && not (isJust minimumRule) && not (isJust lim)]
        <> [Issue "unsupportedOption" "Combined minimum and cap/exclusion"
           | isJust minimumRule && (isJust lim || excluded)]
      problems = pending <> conflict <> emptyMatcher <> unknownUses <> constraintProblems
        <> [Issue "unsupportedOption" "tag: only hh/hd/pa/se are implemented" | not knownTags]
  pure (Rule (lower fs) (lower ts) (lower tys) lvl excluded lim (null problems) []
    (fmap (map normalizeUse) uses) minimumRule virtual
    (if activeTags then tags else Nothing) slots permanent,problems)

normalizeUse :: Text -> Text
normalizeUse "charge" = "charges"
normalizeUse x = x

-- Original BuildReact cardUses / REGEX_USES: case-sensitive Uses, first
-- English line only, single whitespace, ASCII digit+/word+, unanchored.
-- Do not accept a later prose mention or search translated display text.
cardUses :: CatalogCard -> Maybe Text
cardUses c = case cardRaw c of
  Object o -> case KM.lookup "text" o of
    Just (String t) -> search $ T.unpack $ T.takeWhile (/= '\n') t
    _ -> Nothing
  _ -> Nothing
 where
  digit x = x >= '0' && x <= '9'
  word x = digit x || x >= 'A' && x <= 'Z' || x >= 'a' && x <= 'z' || x == '_'
  space x = x `elem` ['\t','\n','\v','\f','\r',' ','\xa0','\x1680','\x2028','\x2029','\x202f','\x205f','\x3000','\xfeff']
    || x >= '\x2000' && x <= '\x200a'
  search [] = Nothing
  search xs = case parse xs of
    Just result -> Just result
    Nothing -> search $ drop 1 xs
  parse ('U':'s':'e':'s':s:'(':xs) | space s =
    let (digits,rest) = span digit xs in
    if null digits then Nothing else case rest of
      gap:ys | space gap -> case span word ys of
        (name,')':_) | not (null name) -> Just $ normalizeUse $ T.toLower $ T.pack name
        _ -> Nothing
      _ -> Nothing
  parse _ = Nothing

-- The fixed original frontend explicitly ignores its duplicate uc grant.
-- Bind only Akachi's exact row AND its exact companion; never ignore arbitrary
-- tags, altered limits/exclusions, or an orphaned tag-only authorization.
reviewedOptions :: CatalogCard -> Value -> Value
reviewedOptions c (Array rows)
  | cardCode c == "03004", companion `elem` rows =
      toJSON $ filter (/= duplicate) $ foldr (:) [] rows
 where
  level = object ["min" .= (0 :: Int),"max" .= (4 :: Int)]
  companion = object ["uses" .= (["charges","charge"] :: [Text]),"level" .= level]
  duplicate = object ["tag" .= (["uc"] :: [Text]),"level" .= level]
reviewedOptions _ value = value

-- The fixed frontend has NO ignore_match consumer. Retain its actual behavior
-- only for the exact known 90087 row; never infer a generic skip-grant rule.
constraintOptions :: CatalogCard -> Value -> Value
constraintOptions c (Array rows) = toJSON $ map clean $ foldr (:) [] rows
 where
  reviewed = object
    ["error" .= ("You must have at least 6 cards from each faction." :: Text)
    ,"atleast" .= object ["min" .= (6 :: Int),"factions" .= (5 :: Int)]
    ,"faction" .= (["survivor","guardian","seeker","rogue","mystic"] :: [Text])
    ,"ignore_match" .= True]
  clean (Object o) | cardCode c == "90087", Object o == reviewed = Object $ KM.delete "ignore_match" o
  clean v = v
constraintOptions _ v = v

-- Selection keeps a conservative vocabulary. This interpreter now validates
-- uses itself; suppress only that redundant generic diagnostic, not others.
selectionProblems :: [Issue] -> [Issue]
selectionProblems = filter (`notElem`
  [Issue "unsupportedOption" key | key <- ["uses","tag","text","text_exact"]])

-- Explicit tags:null means faithfully projected absence. A missing property
-- is not equivalent: older projections dropped all tags. No IO/registry.
officialTags :: CatalogCard -> Either Text [Text]
officialTags c = case cardRaw c of
  Object o | KM.lookup "official" o == Just (Bool True) -> case KM.lookup "tags" o of
    Just Null -> Right []
    Just value -> readValue value
    Nothing -> Left "Missing projected tags"
  _ -> Left "Nonofficial or unknown provenance requires tag fallback"

needsTags :: Rule -> Bool
needsTags r = isJust (ruleTags r) || any needsTags (ruleConjuncts r)

tagInputIssues :: Policy -> [(CatalogCard,Int)] -> [Issue]
tagInputIssues p selected =
  [Issue "tagDataUnsupported" (cardCode c <> ": " <> reason)
  | any (\r -> ruleSupported r && needsTags r) (rules p)
  ,(c,_) <- selected,Left reason <- [officialTags c]]

parseRequirements :: Value -> Parser (Int,[[Text]],Int,[Issue])
parseRequirements = withObject "deck requirements" $ \o -> do
  size <- o .: "size"
  unless (size > (0 :: Int) && size <= 200) $ fail "Unsupported required size"
  groupObject <- o .:? "card" .!= KM.empty
  groups <- traverse (\(k,v) -> withObject "signature alternatives" (\a -> do
    -- Values are code strings, not quantities. Do not interpret them as counts.
    pairs <- traverse (\(code,raw) -> do
      text <- parseJSON raw
      unless (normalizeCode text == normalizeCode (Key.toText code)) $
        fail "Unsupported signature alternative payload"
      pure $ normalizeCode text) (KM.toList a)
    pure $ nub $ normalizeCode (Key.toText k) : pairs) v) (KM.toList groupObject)
  randoms <- o .:? "random" .!= []
  randomKinds <- traverse (withObject "random requirement" $ \r -> do
    target <- r .: "target" :: Parser Text
    val <- r .: "value" :: Parser Text
    pure $ target == "subtype" && val == "basicweakness" && KM.size r == 2) randoms
  let unknown = filter (`notElem` ["size","card","random"]) $ map Key.toText $ KM.keys o
  pure (size,groups,length randoms,
        [Issue "unsupportedRequirement" k | k <- unknown]
        <> [Issue "unsupportedRequirement" "Random requirement other than basic weakness" | not $ and randomKinds])

compilePolicy :: CatalogCard -> Value -> Either Text Policy
compilePolicy c chosen = compileOwnedPolicy c chosen [] 0

compileOwnedPolicy :: CatalogCard -> Value -> [Value] -> Int -> Either Text Policy
compileOwnedPolicy = compileTargetPolicy True

compileTargetPolicy :: Bool -> CatalogCard -> Value -> [Value] -> Int -> Either Text Policy
compileTargetPolicy mainTarget c chosen extra sizeDelta = do
  opts <- maybe (Left "Investigator has no declared deck_options") Right $ cardOptions c
  plan <- Selection.resolveSelections (constraintOptions c $ reviewedOptions c opts) chosen
  additional <- traverse (\v -> Selection.resolveSelections (toJSON [v]) (object [])) extra
  parsed <- traverse compileGrant $ concatMap Selection.resolvedOptions (plan:additional)
  req <- maybe (Left "Investigator has no declared deck_requirements") Right $ cardRequirements c
  (size,groups,weak,problems) <- parseWith parseRequirements req
  let selectedSize = fromMaybe size (Selection.selectedDeckSize plan)
      targetSize = selectedSize + Selection.selectedSizeDelta plan + sizeDelta
  unless (targetSize > 0 && targetSize <= 200) $ Left "Unsupported selected target size"
  unless (length groups <= 32 && sum (map length groups) <= 200) $ Left "Too many signature alternatives"
  let overlap = length (concat groups) /= Set.size (Set.fromList $ concat groups)
  -- Original configure() adds this independent cap to every main deck.
  let covenant = Rule Nothing (Just ["covenant"]) Nothing Nothing False
        (Just 1) True [] Nothing Nothing True Nothing Nothing Nothing
  pure $ Policy ([covenant | mainTarget] <> map fst parsed) groups targetSize weak
    (selectionProblems (concatMap Selection.selectionIssues (plan:additional)) <> concatMap snd parsed <> problems
    <> [Issue "unsupportedRequirement" "Overlapping signature alternative groups" | overlap]) selectedSize
 where
  compileGrant option = do
    terms <- traverse (parseWith parseRule) $ Selection.optionTerms option
    let rootVirtual = any (ruleVirtual . fst) terms
        minima = [m | (r,_) <- terms, Just m <- [ruleMinimum r]]
        constraintProblems =
          [Issue "unsupportedOption" "Nested minimum constraints" | length minima > 1]
          <> [Issue "unsupportedOption" "Combined minimum and cap/exclusion"
             | not (null minima) && (isJust (Selection.optionLimit option) || Selection.optionExcluded option)]
        -- Selection moves limit/not to the grant root, so its complete shape
        -- must be checked here rather than inferred from an individual term.
        termProblems = filter (/= Issue "unsupportedOption" "Virtual row requires a minimum or a finite cap") $ concatMap snd terms
        pending = selectionProblems (Selection.optionUnsupported option) <> termProblems <> constraintProblems
          <> [Issue "unsupportedOption" "Virtual row requires a minimum or a finite cap"
             | rootVirtual && not (Selection.optionExcluded option) && null minima && not (isJust $ Selection.optionLimit option)]
    -- One node/capacity for the complete conjunction, never one node per term.
    pure (Rule Nothing Nothing Nothing Nothing (Selection.optionExcluded option)
      (Selection.optionLimit option) (null pending) (map fst terms) Nothing
      (case minima of [] -> Nothing; m:_ -> Just m) rootVirtual Nothing Nothing Nothing,pending)

requestIssues :: BuildRequest -> [Issue]
requestIssues r =
  [Issue "upgradeXpUnsupported" "Upgrade purchase costs and existing-deck history are not evaluated" | mode r == "upgrade"]

schemaIdFor :: BuildRequest -> Text
schemaIdFor r = valueDigest $ object
  ["policy" .= policyIdentity,"selectionPolicy" .= Selection.selectionPolicyIdentity
  ,"catalog" .= catalogDigest,"tabooCatalog" .= Taboo.tabooDigest,"request" .= r]

getInvestigator :: Catalog -> BuildRequest -> Either Text CatalogCard
getInvestigator cat r = do
  c <- maybe (Left "Unknown investigator in frozen catalog") Right $ Map.lookup (investigator r) $ catalogCards cat
  unless (cardType c == "investigator") $ Left "The requested card is not an investigator"
  pure c

schemaFor :: Catalog -> BuildRequest -> Either Text Value
schemaFor original r = do
  cat <- Taboo.applyTabooCatalog (taboo r) original
  sides <- Inv.resolveSides cat r
  let c = Inv.backCard sides
  opts <- maybe (Left "Investigator has no declared deck_options") Right $ cardOptions c
  plan <- Selection.resolveSelections (constraintOptions c $ reviewedOptions c opts) (selections r)
  extra <- Extra.deriveExtraPlan cat c KM.empty
  let compiled = compilePolicy c (selections r)
      pending = requestIssues r <> either (\e -> issue "unsupportedDefinition" e) policyUnsupported compiled
        <> [Issue "missingEnglishFields" (T.intercalate "," $ cardMissingEnglish c) | not $ null $ cardMissingEnglish c]
  pure $ object
    ["schemaVersion" .= (2 :: Int),"schemaId" .= schemaIdFor r,"catalogSha256" .= catalogDigest
    ,"policyIdentity" .= policyIdentity,"request" .= r
    ,"scope" .= ("Base deckbuilding from English rule fields in the shipped Oct15 Build cache; no campaign/challenge/collection context" :: Text)
    ,"supportStatus" .= (if null pending then "supported" else "unsupported" :: Text)
    ,"unsupportedReasons" .= pending,"requirements" .= cardRequirements c,"options" .= cardOptions c
    ,"investigatorSides" .= Inv.sideSchema cat sides
    ,"tabooSets" .= Taboo.tabooSchema,"selectedTabooId" .= fromMaybe 0 (taboo r),"tabooCatalogSha256" .= Taboo.tabooDigest
    ,"selectionFields" .= Selection.selectionFields plan
    ,"extraDeck" .= extraDefinition extra
    ,"selectionValues" .= Object (Selection.selectionValues plan)
    ,"randomness" .= ("No random weakness is chosen by preview or validation" :: Text)]

deckBuildSchema :: Value -> Either Text Value
deckBuildSchema v = do
  r <- readValue v
  cat <- catalogResult
  schemaFor cat r

matches :: Rule -> CatalogCard -> Bool
matches r c =
  all (`matches` c) (ruleConjuncts r)
  && maybe True (any (`Set.member` cardFactions c)) (ruleFactions r)
  && maybe True (any (`Set.member` cardTraits c)) (ruleTraits r)
  && maybe True (T.toCaseFold (cardType c) `elem`) (ruleTypes r)
  && maybe True (\allowed -> maybe False (`elem` allowed) $ cardUses c) (ruleUses r)
  && maybe True (\allowed -> either (const False) (any (`elem` allowed)) $ officialTags c) (ruleTags r)
  && maybe True (\(lo,hi) -> maybe False (\n -> n >= lo && n <= hi) $ cardLevel c) (ruleLevel r)
  && maybe True (\allowed -> any (`Owned.hasSlot` c) allowed) (ruleSlots r)
  && maybe True (== cardPermanent c) (rulePermanent r)

-- Integral max-flow. A copy can be assigned to any matching positive option.
-- Independent minimum counts use all supplied card quantities, including
-- signatures; a multicolor card contributes to EVERY matching faction bucket.
-- Do not filter them by this row's level/grant matcher or max-flow allocation.
minimumErrors :: [(CatalogCard,Int)] -> Rule -> [Issue]
minimumErrors cards r = case ruleMinimum r of
  Just (Minimum bucket targets n k) | ruleSupported r ->
    let has c t = case bucket of
          FactionBucket -> t `Set.member` cardFactions c
          TraitBucket -> t `Set.member` cardTraits c
          TypeBucket -> t == T.toCaseFold (cardType c)
        totals = [sum [q | (c,q) <- cards,has c t] | t <- targets]
        reached = length $ filter (>= n) totals
    in [Issue "atLeast" ("Need " <> showText k <> " categories with " <> showText n
          <> " cards each; found " <> showText reached) | reached < k]
  _ -> []

virtualErrors :: [(CatalogCard,Int)] -> Rule -> [Issue]
virtualErrors ordinary r =
  [Issue "virtualLimit" ("Maximum " <> showText cap <> ", found " <> showText count)
  | ruleSupported r,ruleVirtual r,not (isJust $ ruleMinimum r)
  ,Just cap <- [ruleLimit r]
  ,let count = sum [q | (c,q) <- ordinary,matches r c],count > cap]

-- Reverse residual edges allow reassignment when a later card needs a scarce
-- option, so overlapping limited grants are NOT evaluated greedily.
assignedCopies :: [(CatalogCard,Int)] -> [Rule] -> Int
assignedCopies cards grants = augment initial 0
 where
  n = length cards
  sink = n + length grants + 1
  edges = [(0,i,q) | (i,(_,q)) <- zip [1..] cards]
    <> [(i,n+j,500) | (i,(c,_)) <- zip [1..] cards,(j,r) <- zip [1..] grants,matches r c]
    <> [(n+j,sink,fromMaybe 500 $ ruleLimit r) | (j,r) <- zip [1..] grants]
  initial = Map.fromListWith (+) [((a,b),q) | (a,b,q) <- edges]
  neighbors a residual = [b | ((from,b),q) <- Map.toList residual,from == a,q > 0]
  path residual = bfs [0] (Set.singleton 0) Map.empty
   where
    bfs [] _ _ = Nothing
    bfs (a:rest) seen parents
      | a == sink = Just $ unwind sink parents
      | otherwise =
          let ns = filter (`Set.notMember` seen) $ neighbors a residual
          in bfs (rest <> ns) (foldr Set.insert seen ns)
               (foldr (\b -> Map.insert b a) parents ns)
    unwind b parents = case Map.lookup b parents of
      Nothing -> []
      Just a -> (a,b) : unwind a parents
  augment residual total = case path residual of
    Nothing -> total
    Just [] -> total
    Just route ->
      let amount = minimum [Map.findWithDefault 0 edge residual | edge <- route]
          next = foldl' (\m (a,b) ->
            Map.insertWith (+) (b,a) amount $ Map.adjust (subtract amount) (a,b) m) residual route
      in augment next (total + amount)

isWeakness :: CatalogCard -> Bool
isWeakness c = cardSubtype c `elem` [Just "weakness",Just "basicweakness"]

restrictionProblems :: [Text] -> CatalogCard -> ([Issue],[Issue])
restrictionProblems identities c = case cardRestrictions c of
  Null -> ([],[])
  Object o | KM.null o -> ([],[])
  Object o | KM.size o == 1, Just (Object owners) <- KM.lookup "investigator" o ->
    let allowed = map (normalizeCode . Key.toText) $ KM.keys owners
    in ([Issue "investigatorRestricted" (cardCode c) | not $ any (`elem` allowed) identities],[])
  _ -> ([],issue "unsupportedCardRestriction" $ cardCode c)

copyErrorsFor :: Owned.OwnedConstruction -> [(CatalogCard,Int)] -> [Issue]
copyErrorsFor owned selected =
  [Issue "copyLimit" title
  | (title,cs) <- Map.toList $ Map.fromListWith (<>)
      [(either (const $ cardName c) id $ Owned.copyGroupName c,[(c,q)])
      | (c,q) <- selected,isJust $ cardLevel c]
  ,let limits=[lim | (c,_)<-cs,Just lim<-[Owned.copyLimitFor owned c]]
  ,not(null limits),sum(map snd cs)>minimum limits]

extraDefinition :: Extra.ExtraPlan -> Value
extraDefinition p = object
  ["enabled" .= Extra.extraEnabled p,"target" .= String "extraDeck"
  ,"options" .= Extra.extraOptions p,"requirements" .= Extra.extraRequirements p
  ,"targetCount" .= Extra.extraTargetCount p,"perCodeLimit" .= Extra.extraPerCodeLimit p
  ,"selectedCodes" .= Extra.extraWireCodes p]

-- Same finite matcher/capacity/signature/copy helpers as MAIN, separate target
-- quantities and original side definitions. Never inherit main owned grants.
validateExtra :: Catalog -> CatalogCard -> [Text] -> Owned.OwnedConstruction -> Extra.ExtraPlan
  -> Either Text ([Issue],[Issue],Value)
validateExtra cat back identities owned plan
  | not $ Extra.extraEnabled plan = pure ([],Extra.extraIssues plan,extraDefinition plan)
  | otherwise = do
      let sideBack=back {cardOptions=Just $ toJSON $ Extra.extraOptions plan,cardRequirements=Extra.extraRequirements plan}
      p <- compileTargetPolicy False sideBack (object []) [] 0
      let slots=Extra.extraQuantities plan
          selected=[(c,q) | (code,q)<-Map.toList slots,Just c<-[Map.lookup code $ catalogCards cat]]
          canonicalCode code=either (const code) id $ Inv.canonicalCode cat code
          required=Set.fromList $ map canonicalCode $ concat $ requiredGroups p
          isRequired c=canonicalCode(cardCode c) `Set.member` required
          ordinary=[(c,q) | (c,q)<-selected,not(isRequired c),not(isWeakness c)]
          exclusions=[Issue "excludedCard" (cardCode c) | (c,_)<-ordinary
            ,any (\r->ruleSupported r && ruleExcluded r && matches r c) $ rules p]
          grants=filter (\r->ruleSupported r && not(ruleExcluded r) && not(ruleVirtual r)) $ rules p
          permitted=[(c,q) | (c,q)<-ordinary,not $ any (\r->ruleSupported r && ruleExcluded r && matches r c) $ rules p]
          assigned=assignedCopies permitted grants
          target=fromMaybe (requiredSize p+1) $ Extra.extraTargetCount plan
          count=sum $ Map.elems slots
          (restrictionErrors,restrictionPending)=unzip [restrictionProblems identities c | (c,_)<-selected]
          pending=Extra.extraIssues plan <> policyUnsupported p <> concat restrictionPending <> tagInputIssues p selected
            <> [Issue "missingEnglishFields" (cardCode c) | (c,_)<-selected,not $ null $ cardMissingEnglish c]
            <> [Issue "missingCopyLimit" (cardCode c) | (c,_)<-selected,isJust(cardLevel c),not(isJust $ cardLimit c)]
            <> [Issue "cardProvenanceUnsupported" (cardCode c) | (c,_)<-selected,not(isRequired c)
              ,isWeakness c || cardLevel c==Nothing || cardType c `notElem` ["asset","event","skill"]]
          candidates=[cardCode c | c<-Map.elems $ catalogCards cat
            ,any (`matches` c) grants,not $ any (\r->ruleSupported r && ruleExcluded r && matches r c) $ rules p
            ,not(isWeakness c),cardType c `elem` ["asset","event","skill"]
            ,null(fst $ restrictionProblems identities c),null(snd $ restrictionProblems identities c)
            ,not $ case Owned.rawField "text" c of Just(String t)->"Forbidden." `T.isInfixOf` t;_->False]
      signatures <- Inv.signatureErrors cat sideBack (signatureSize p) slots
      let errors=signatures <> copyErrorsFor owned selected <> concat restrictionErrors <> exclusions
            <> [Issue "deckSize" ("Expected "<>showText target<>", found "<>showText count) | count/=target]
            <> [Issue "deckOptionsCapacity" "Extra cards cannot all be allocated to side options" | assigned/=sum(map snd permitted)]
            <> [Issue "copyLimit" (cardCode c) | (c,q)<-selected,Just cap<-[Extra.extraPerCodeLimit plan]
              ,isJust(cardLevel c),cardSubtype c/=Just "basicweakness",q>cap]
            <> [Issue "forbiddenCard" (cardCode c) | (c,_)<-selected,Just(String t)<-[Owned.rawField "text" c],"Forbidden." `T.isInfixOf` t]
            <> concatMap (minimumErrors selected) (rules p) <> concatMap (virtualErrors ordinary) (rules p)
          details=case extraDefinition plan of
            Object o -> Object $ KM.union (KM.fromList
              [("candidateCodes",toJSON candidates),("requiredCodes",toJSON $ Set.toList required)
              ,("selectedCodes",toJSON $ Extra.extraWireCodes plan),("countedSize",toJSON count)
              ,("errors",toJSON errors),("unsupportedReasons",toJSON pending)]) o
            v->v
      pure (errors,pending,details)

validateWith :: Catalog -> ValidationRequest -> Either Text Value
validateWith original input = do
  let r = schemaRequest input; deck = submittedDeck input
  cat <- Taboo.applyTabooCatalog (taboo r) original
  unless (suppliedSchemaId input == schemaIdFor r) $ Left "Stale or foreign deck-building schema"
  sides <- Inv.resolveSides cat r
  let inv = Inv.backCard sides
  unless (deckInvestigator deck == investigator r) $ Left "Deck investigator differs from schema"
  unless (deckTaboo deck == taboo r || all (`elem` [Nothing,Just 0]) [deckTaboo deck,taboo r]) $
    Left "Deck taboo differs from schema"
  opts <- maybe (Left "Investigator has no declared deck_options") Right $ cardOptions inv
  requestedPlan <- Selection.resolveSelections opts (selections r)
  actualSelections <- Selection.selectedMeta opts (deckMeta deck)
  actualPlan <- Selection.resolveSelections opts actualSelections
  unless (Selection.selectionValues requestedPlan == Selection.selectionValues actualPlan) $
    Left "Deck meta selections differ from schema"
  meta <- Selection.decodeDeckMeta $ deckMeta deck
  Inv.validateSideMeta r meta
  extra <- Extra.deriveExtraPlan cat inv meta
  projection <- Custom.projectSelectedCustomizations cat meta $ Map.unionsWith (+)
    [deckSlots deck,deckSideSlots deck,Extra.extraQuantities extra]
  let working = Custom.projectedCatalog projection
  attachments <- Attachment.deriveAttachmentPlan working (cardCode $ Inv.frontCard sides) (deckSlots deck) meta
  ownedRaw <- Owned.deriveOwnedConstruction working (deckSlots deck) meta
  let owned=ownedRaw
        { Owned.brandedTarget=if "attachments_11080" `elem` Attachment.validatedMetaKeys attachments
            then Owned.brandedTarget ownedRaw else Nothing
        , Owned.validatedMetaKeys=filter (not . T.isPrefixOf "attachments_") $ Owned.validatedMetaKeys ownedRaw
        }
  (extraErrors,extraPending,extraDetails) <- validateExtra working inv (Inv.sideChoices sides) owned extra
  let
      finish errors pending totals = case result input errors pending totals of
        Object o -> Object $ KM.insert "attachments" (object ["fields" .= Attachment.fields attachments
            ,"validatedKeys" .= Attachment.validatedMetaKeys attachments,"issues" .= Attachment.issues attachments
            ,"appliedBrandedTarget" .= Owned.brandedTarget owned])
          $ KM.insert "extraDeck" extraDetails $ KM.insert "ownedConstruction" (Owned.ownedProvenance owned)
          $ KM.insert "customizations" (Custom.customizationDetails projection) o
        value -> value
      compiled = compileOwnedPolicy inv actualSelections (Owned.additionalOptions owned) (Owned.sizeDelta owned)
      unknown = Map.keys $ Map.difference (deckSlots deck) (catalogCards working)
      selected = [(c,q) | (code,q) <- Map.toList $ deckSlots deck,Just c <- [Map.lookup code $ catalogCards working]]
      canonical code = either (const code) id $ Inv.canonicalCode cat code
      frontCode = canonical $ cardCode $ Inv.frontCard sides
      frontRequired = case frontCode of
        "90037" -> ["90038"]
        "90024" -> ["90025","90026","90027","90028","90029"]
        _ -> []
      isFrontRequired c = canonical (cardCode c) `elem` frontRequired
      sideIssues = [Issue "sideDeckUnsupported" "Side-deck allocation is not validated" | not $ Map.null $ deckSideSlots deck]
      metaIssues = [Issue "deckMetaUnsupported" key
                   | rawKey <- KM.keys meta, let key = Key.toText rawKey
                   , key `notElem` Selection.selectionKeys actualPlan
                   , key `notElem` Custom.customizationValidatedKeys projection
                   , key `notElem` Inv.sideMetadataKeys
                   , key `notElem` Owned.validatedMetaKeys owned
                   , key `notElem` Extra.extraValidatedMetaKeys extra
                   , key `notElem` Attachment.validatedMetaKeys attachments
                   , not (key `elem` ["banner_url","intro_md"] || "annotation_" `T.isPrefixOf` key)]
      (restrictionErrors,restrictionUnsupported) = unzip [restrictionProblems (Inv.sideChoices sides) c | (c,_) <- selected]
      customUnknown = filter (T.isPrefixOf "*") unknown
      unavailable = [Issue "unknownCard" code | code <- unknown,code `notElem` customUnknown]
      baseUnsupported = requestIssues r <> sideIssues <> metaIssues <> concat restrictionUnsupported
        <> Custom.customizationIssues projection
        <> Owned.ownedIssues owned
        <> extraPending
        <> Attachment.issues attachments
        <> [Issue "missingEnglishFields" (cardCode c <> ": " <> T.intercalate "," (cardMissingEnglish c)) | (c,_) <- selected,not $ null $ cardMissingEnglish c]
        <> [Issue "unknownDeckField" field | field <- deckUnknownFields deck]
        <> [Issue "variantUnsupported" (code <> " -> " <> ident)
           | (code,ident) <- Map.toList $ deckVariants deck,normalizeCode ident /= code,not $ Taboo.variantAllowed (taboo r) code ident]
        <> [Issue "customDefinitionUnsupported" code | code <- customUnknown]
        <> [Issue "randomWeaknessUnresolved" "01000 is a placeholder, not a selected basic weakness"
           | Map.member "01000" $ deckSlots deck]
        <> [Issue "missingCopyLimit" (cardCode c) | (c,_) <- selected,isJust $ cardLevel c,not $ isJust $ cardLimit c]
        <> [Issue "missingEnglishFields" (cardCode c <> ": real_subname")
           | (c,_) <- selected,Left _ <- [Owned.copyGroupName c]]
      -- Group variants by ORIGINAL title, never localized name.
      -- Original DeckLimitsValidator.add skips XP-less signatures entirely;
      -- Directive/Discipline titles must not share an invented ordinary cap.
      copyErrors = copyErrorsFor owned selected
      forbiddenErrors = [Issue "forbiddenCard" (cardCode c) | (c,_) <- selected
                        ,Just (String text) <- [Owned.rawField "text" c],"Forbidden." `T.isInfixOf` text]
      basicErrors = unavailable <> concat restrictionErrors <> copyErrors <> forbiddenErrors
        <> [Issue code ("extra_deck: "<>detail) | Issue code detail <- extraErrors]
  case compiled of
    Left reason -> pure $ finish unavailable
      (baseUnsupported <> issue "unsupportedDefinition" reason) Nothing
    Right p -> do
      let required = Set.fromList $ concat $ requiredGroups p
          requiredCanonical = Set.map canonical required
          isRequired c = canonical (cardCode c) `Set.member` requiredCanonical || isFrontRequired c
          ordinary = [(c,q) | (c,q) <- selected,not (isRequired c),not (isWeakness c)]
          counted = sum [q | (c,q) <- ordinary,Owned.countsForSize c]
          knownSlots = Map.fromList [(cardCode c,q) | (c,q) <- selected]
          signatureResult = (<>) <$> Inv.signatureErrors working inv (signatureSize p) knownSlots
            <*> Inv.frontErrors working (Inv.frontCard sides) knownSlots
          signatureProblems = either (issue "unsupportedRequirement") (const []) signatureResult
          signatureErrors = either (const []) id signatureResult
          -- Pair consistency remains the original Build's explicit TODO; no
          -- invented exact-one or speculative replacement-pair rule is added.
          weakCount = sum [q | (c,q) <- selected,cardSubtype c == Just "basicweakness",cardCode c /= "01000"]
          weakErrors = [Issue "basicWeakness" "Selected basic weakness count does not match the initial requirement"
                       | weakCount /= randomWeaknesses p]
          sizeErrors = [Issue "deckSize" $ "Expected " <> showText (requiredSize p) <> ", found " <> showText counted
                       | counted /= requiredSize p]
          exclusions = [Issue "excludedCard" (cardCode c) | (c,_) <- ordinary
                       ,any (\rule -> ruleSupported rule && ruleExcluded rule && matches rule c) $ rules p]
          grants = filter (\rule -> ruleSupported rule && not (ruleExcluded rule) && not (ruleVirtual rule)) $ rules p
          permitted = [(c,q) | (c,q) <- ordinary
                      ,not $ any (\rule -> ruleSupported rule && ruleExcluded rule && matches rule c) $ rules p]
          assigned = assignedCopies permitted grants
          allowedCount = sum $ map snd permitted
          allocationErrors = [Issue "deckOptionsCapacity" "Cards cannot all be allocated to the available grants/limits"
                             | assigned /= allowedCount]
          levelErrors = [Issue "initialLevel" (cardCode c) | mode r == "initial"
                        ,(c,_) <- ordinary,maybe False (>0) $ cardLevel c]
          -- XP-less story/bonded cards and additional signature weaknesses need
          -- an explicit provenance context, not accidental broad acceptance.
          provenance = [Issue "cardProvenanceUnsupported" (cardCode c)
                       | (c,_) <- selected,not (isRequired c)
                       ,cardSubtype c /= Just "basicweakness"
                       ,isWeakness c || cardLevel c == Nothing
                         || cardType c `notElem` ["asset","event","skill"]]
          pending = baseUnsupported <> policyUnsupported p <> provenance <> signatureProblems
            <> tagInputIssues p selected
          -- An unknown option/variant/permanent may alter size, grants, limits,
          -- or requirements. Do not present partial-rule calculations as
          -- established illegality. Retain only missing catalog identities;
          -- all numerical output is explicitly provisional in this case.
          errors = if null pending
            then basicErrors <> signatureErrors <> weakErrors <> sizeErrors <> exclusions
              <> allocationErrors <> levelErrors
              <> concatMap (minimumErrors selected) (rules p)
              <> concatMap (virtualErrors ordinary) (rules p)
            else unavailable
      pure $ finish errors pending $ Just $ object
        ["provisional" .= not (null pending),"countedSize" .= counted,"requiredSize" .= requiredSize p
        ,"basicWeaknessCount" .= weakCount,"assignedOrdinaryCopies" .= assigned
        ,"ownedSizeAdjustment" .= Owned.sizeDelta owned,"signatureDeckSize" .= signatureSize p]

showText :: Show a => a -> Text
showText = T.pack . show

result :: ValidationRequest -> [Issue] -> [Issue] -> Maybe Value -> Value
result input errors pending totals = object
  ["schemaVersion" .= (2 :: Int),"schemaId" .= suppliedSchemaId input,"catalogSha256" .= catalogDigest
  ,"status" .= (if not (null pending) then "unsupported" else if null errors then "valid" else "invalid" :: Text)
  ,"errors" .= nub errors,"unsupportedReasons" .= nub pending,"statistics" .= totals
  ,"deck" .= originalDeck input
  ,"scope" .= ("Base construction from English rule fields in the shipped Oct15 Build cache; no campaign/challenge/collection legality or upgrade XP certification" :: Text)]

deckBuildValidate :: Value -> Either Text Value
deckBuildValidate v = do
  input <- readValue v
  cat <- catalogResult
  validateWith cat input
