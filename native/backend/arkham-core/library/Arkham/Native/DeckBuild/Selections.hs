{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Selections
  ( SelectionPlan (..), ResolvedOption (..)
  , resolveSelections, decodeDeckMeta, selectedMeta, mergeSelectionMeta
  , selectionPolicyIdentity
  ) where

import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.ByteString.Lazy qualified as BL
import Data.List (nub)
import Data.Maybe (catMaybes)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding qualified as TE
import Arkham.Native.DeckBuild.Types (Issue (..))

-- Pure adaptation: no registry, IO, random selection or mutation of the source deck.
selectionPolicyIdentity :: Text
selectionPolicyIdentity = "native-deck-selection-meta-scalars-v2-constraint-terms"

-- All terms must match ONE card. Do NOT intersect faction/trait lists:
-- a dual-faction card can satisfy two different faction predicates.
-- Each option is ONE max-flow grant with ONE shared capacity.
data ResolvedOption = ResolvedOption
  { optionTerms :: [Value]
  , optionLimit :: Maybe Int
  , optionExcluded :: Bool
  , optionUnsupported :: [Issue]
  } deriving Show

data SelectionPlan = SelectionPlan
  { selectionFields :: [Value]
  , selectionValues :: Object
  , selectionKeys :: [Text]
  , resolvedOptions :: [ResolvedOption]
  , selectedDeckSize :: Maybe Int
  , selectedSizeDelta :: Int
  , selectionIssues :: [Issue]
  } deriving Show

data Spec = Spec Text Text Text [(Text,Text,Maybe Object)] Object

readAs :: FromJSON a => Value -> Either Text a
readAs = either (Left . T.pack) Right . parseEither parseJSON

lookupText :: Text -> Object -> Either Text (Maybe Text)
lookupText key o = case KM.lookup (K.fromText key) o of
  Nothing -> Right Nothing
  Just Null -> Right Nothing
  Just v -> Just <$> readAs v

remove :: [Text] -> Object -> Object
remove keys o = foldr (KM.delete . K.fromText) o keys

-- Parse the actual ArkhamDB string wire. Object form is accepted for local
-- callers; its raw JSON duplicate-property checks belong at the host boundary.
-- For string meta, duplicate TOP-LEVEL accessors are rejected before using Map.
decodeDeckMeta :: Maybe Value -> Either Text Object
decodeDeckMeta Nothing = Right KM.empty
decodeDeckMeta (Just Null) = Right KM.empty
decodeDeckMeta (Just (Object o)) = Right o
decodeDeckMeta (Just (String raw))
  | T.null (T.strip raw) = Right KM.empty
  | T.length raw > 262144 = Left "Deck meta is too large"
  | otherwise = do
      value <- either (const $ Left "Invalid deck meta JSON") Right $
        eitherDecodeStrict' (TE.encodeUtf8 raw)
      o <- readAs value
      let body = T.dropEnd 1 $ T.drop 1 $ T.strip raw
      keys <- if T.null (T.strip body) then Right [] else traverse memberKey $ splitTop ',' body
      unless (length keys == length (nub keys)) $ Left "Duplicate deck meta key"
      pure o
 where
  memberKey :: Text -> Either Text Text
  memberKey member = case splitTop ':' member of
    [key,_] -> either (const $ Left "Invalid deck meta key") Right $
      eitherDecodeStrict' (TE.encodeUtf8 key)
    _ -> Left "Invalid deck meta member"
decodeDeckMeta _ = Left "Deck meta must be a JSON object string"

-- Used only AFTER Aeson has validated the JSON. Separators inside strings,
-- arrays or nested objects cannot split a top-level member.
splitTop :: Char -> Text -> [Text]
splitTop separator = map (T.pack . reverse) . reverse . finish .
  T.foldl' step (False,False,0 :: Int,[],[])
 where
  step (quoted,escaped,depth,current,parts) c
    | quoted && escaped = (True,False,depth,c:current,parts)
    | quoted && c == '\\' = (True,True,depth,c:current,parts)
    | c == '"' = (not quoted,False,depth,c:current,parts)
    | quoted = (True,False,depth,c:current,parts)
    | c == '{' || c == '[' = (False,False,depth+1,c:current,parts)
    | c == '}' || c == ']' = (False,False,depth-1,c:current,parts)
    | c == separator && depth == 0 = (False,False,depth,[],current:parts)
    | otherwise = (False,False,depth,c:current,parts)
  finish (_,_,_,current,parts) = current:parts

specFor :: Object -> Either Text (Maybe Spec)
specFor o = case filter (\k -> KM.member (K.fromText k) o)
  ["faction_select","option_select","deck_size_select"] of
    [] -> Right Nothing
    [field] -> do
      let (kind,fallback) = case field of
            "faction_select" -> ("faction","faction_selected")
            "option_select" -> ("option","option_selected")
            _ -> ("deckSize","deck_size_selected")
      key <- maybe fallback id <$> lookupText "id" o
      label <- maybe key id <$> lookupText "name" o
      unless (not (T.null key) && T.length key <= 160) $ Left "Invalid selection accessor"
      raw <- maybe (Left "Missing selector") Right $ KM.lookup (K.fromText field) o
      choices <- if field == "option_select" then do
        children <- readAs raw :: Either Text [Object]
        traverse (\child -> do
          ident <- maybe (Left "Option choice has no ID") Right =<< lookupText "id" child
          name <- maybe ident id <$> lookupText "name" child
          pure (ident,name,Just child)) children
        else do
          values <- case raw of
            String s | field == "deck_size_select" -> Right [s]
            _ -> readAs raw
          pure [(v,v,Nothing) | v <- values]
      let ids = [v | (v,_,_) <- choices]
      unless (not (null ids) && length ids <= 64 && all (\v -> not (T.null v) && T.length v <= 160) ids
              && length ids == length (nub ids)) $ Left "Invalid or duplicate offered selection values"
      pure $ Just $ Spec key kind label choices o
    _ -> Left "Multiple selector kinds on one option are unsupported"

numberString :: Text -> Either Text Int
numberString text = case reads (T.unpack text) of
  [(n,"")] | n > 0 && n <= 200 && T.pack (show (n :: Int)) == text -> Right n
  _ -> Left "Unsupported selected deck size"

-- Select only offered accessors, preserving all OTHER meta in the caller.
selectedMeta :: Value -> Maybe Value -> Either Text Value
selectedMeta options meta = do
  rows <- readAs options :: Either Text [Object]
  specs <- catMaybes <$> traverse specFor rows
  o <- decodeDeckMeta meta
  pure $ Object $ KM.fromList
    [(K.fromText key,v) | Spec key _ _ _ _ <- specs
      , Just v <- [KM.lookup (K.fromText key) o], v /= Null, v /= String ""]

-- UI storage helper. Retain advanced/inert JSON fields; don't discard a draft's
-- customizations/annotations merely because validation does not support them.
mergeSelectionMeta :: Value -> Maybe Value -> Value -> Either Text Text
mergeSelectionMeta options original selections = do
  plan <- resolveSelections options selections
  -- Saving an incomplete/unsupported draft is allowed. This function is NOT
  -- validation approval. Clearing a selection removes its old meta value.
  old <- decodeDeckMeta original
  let merged = KM.union (selectionValues plan) (remove (selectionKeys plan) old)
  pure $ TE.decodeUtf8 $ BL.toStrict $ encode $ Object merged

resolveSelections :: Value -> Value -> Either Text SelectionPlan
resolveSelections options chosen = do
  rows <- readAs options :: Either Text [Object]
  unless (not (null rows) && length rows <= 32) $ Left "Invalid option count"
  specs <- traverse specFor rows
  supplied <- readAs chosen :: Either Text Object
  let keys = [key | Just (Spec key _ _ _ _) <- specs]
  unless (length keys == length (nub keys)) $ Left "Ambiguous repeated selection accessor"
  unless (all ((`elem` keys) . K.toText) $ KM.keys supplied) $ Left "Unknown selection accessor"
  parts <- traverse (resolveRow supplied) $ zip rows specs
  let sizes = catMaybes [s | (_,_,_,s,_,_) <- parts]
      delta = sum [d | (_,_,_,_,d,_) <- parts]
  unless (length sizes <= 1) $ Left "Multiple size selectors are unsupported"
  unless (delta <= 200) $ Left "Selected size adjustment is unsupported"
  pure $ SelectionPlan
    [f | (Just f,_,_,_,_,_) <- parts]
    (KM.fromList [v | (_,Just v,_,_,_,_) <- parts])
    keys (concat [rs | (_,_,rs,_,_,_) <- parts])
    (case sizes of [] -> Nothing; x:_ -> Just x) delta
    (concat [problems | (_,_,_,_,_,problems) <- parts])

resolveRow :: Object -> (Object,Maybe Spec)
  -> Either Text (Maybe Value,Maybe (K.Key,Value),[ResolvedOption],Maybe Int,Int,[Issue])
resolveRow _ (o,Nothing) = do
  rule <- makeOption [o] []
  pure (Nothing,Nothing,[rule],Nothing,0,optionUnsupported rule)
resolveRow supplied (_,Just (Spec key kind label choices o)) = do
  value <- lookupText key supplied
  let selected = case value of Just "" -> Nothing; other -> other
      field = object ["key" .= key,"kind" .= kind,"label" .= label,"required" .= True
        ,"choices" .= [object ["value" .= v,"label" .= name] | (v,name,_) <- choices]
        ,"selectedValue" .= selected]
      shell = remove ["faction_select","option_select","deck_size_select"] o
  case selected of
    Nothing -> pure (Just field,Nothing,[],Nothing,0,[Issue "selectionRequired" key])
    Just v -> do
      child <- case [c | (ident,_,c) <- choices,ident == v] of
        [c] -> Right c
        _ -> Left $ "Invalid offered selection: " <> key
      if kind == "deckSize" then do
        size <- numberString v
        -- The real Mandy row uses faction:[] as a non-grant marker.
        let remainder = remove ["id","name","error","faction"] shell
            safeFaction = KM.lookup "faction" shell `elem` [Nothing,Just (toJSON ([] :: [Text]))]
            pending = [Issue "unsupportedSelection" key | not (KM.null remainder && safeFaction)]
        pure (Just field,Just (K.fromText key,String v),[],Just size,0,pending)
      else do
        let nested = maybe False (\c -> any (`KM.member` c)
              ["faction_select","option_select","deck_size_select"]) child
            sizeValue = child >>= KM.lookup "size"
        delta <- case sizeValue of
          Nothing -> Right 0
          Just x -> do
            n <- readAs x
            unless (n >= (0 :: Int) && n <= 200) $ Left "Unsupported option size adjustment"
            pure n
        let terms = if kind == "faction"
              then [shell,KM.singleton "faction" (toJSON [v])]
              else shell : maybe [] (pure . KM.delete "size") child
            problems = [Issue "unsupportedSelection" "Nested selection" | nested]
        rule <- makeOption terms problems
        pure (Just field,Just (K.fromText key,String v),[rule],Nothing,delta,optionUnsupported rule)

makeOption :: [Object] -> [Issue] -> Either Text ResolvedOption
makeOption objects initial = do
  limits <- traverse readAs [v | o <- objects,Just v <- [KM.lookup "limit" o]]
  flags <- traverse readAs [v | o <- objects,Just v <- [KM.lookup "not" o]]
  unless (all (\n -> n >= (0 :: Int) && n <= 500) limits) $ Left "Unsupported selection limit"
  let excluded = or flags
      predicates = filter (not . KM.null) $ map (remove ["id","name","error","limit","not"]) objects
      -- Validate parses shapes and separates independent constraints from grants.
      -- ignore_match deliberately remains unknown unless the exact reviewed row
      -- was stripped by the investigator-bound adapter before resolution.
      known = ["faction","trait","type","level","uses","base_level","atleast","virtual","slot","permanent"]
      unknown = [K.toText k | o <- predicates,k <- KM.keys o,K.toText k `notElem` known]
      problems = initial
        <> [Issue "unsupportedOption" key | key <- nub unknown]
        <> [Issue "unsupportedSelection" "Nested capacities or exclusions"
           | length limits > 1 || (length objects > 1 && excluded) || (excluded && not (null limits))]
        <> [Issue "unsupportedOption" "No matching predicate" | null predicates]
  pure $ ResolvedOption (map Object predicates)
    (case limits of [] -> Nothing; n:_ -> Just n) excluded problems
