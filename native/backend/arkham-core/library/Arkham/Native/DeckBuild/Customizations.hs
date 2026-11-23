{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Customizations
  (CustomizationProjection(..), projectSelectedCustomizations) where

import Prelude
import Control.Monad (unless, foldM)
import Data.Aeson hiding (decode)
import Data.Aeson.Types (parseEither)
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.List (sortOn)
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Ord (Down(..))
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Card.CardCode (CardCode(..))
import Arkham.Card.CardDef (cdCustomizations)
import Arkham.PlayerCard (allPlayerCards)
import Arkham.Customization (CustomizationChoice(..))
import Arkham.Native.DeckMetadata
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types

data CustomizationProjection = CustomizationProjection
  { projectedCatalog :: Catalog, customizationValidatedKeys :: [Text]
  , customizationIssues :: [Issue], customizationDetails :: Value
  } deriving Show

decode :: FromJSON a => Value -> Either Text a
decode = either (Left . T.pack) Right . parseEither parseJSON
obj :: Value -> Either Text Object
obj (Object o) = Right o
obj _ = Left "Expected customization definition object"
required :: FromJSON a => Text -> Object -> Either Text a
required key o = maybe (Left $ "Missing customization field: " <> key) decode $ KM.lookup (K.fromText key) o
optional :: FromJSON a => Text -> Object -> Either Text (Maybe a)
optional key o = case KM.lookup (K.fromText key) o of Nothing -> Right Nothing; Just Null -> Right Nothing; Just v -> Just <$> decode v
strings :: Text -> Object -> Either Text [Text]
strings key o = maybe [] id <$> optional key o
put :: ToJSON a => Text -> a -> Object -> Object
put key v = KM.insert (K.fromText key) (toJSON v)

projectSelectedCustomizations :: Catalog -> Object -> Map Text Int -> Either Text CustomizationProjection
projectSelectedCustomizations cat meta owned = do
  let work = [(K.toText k,v) | (k,v) <- KM.toList meta,"cus_" `T.isPrefixOf` K.toText k]
      one (key,v) = do
        let code = T.drop 4 key
        unless (Map.findWithDefault 0 code owned > 0) $ Left "Customization owner is absent"
        raw <- decode v :: Either Text Text
        entries <- validatedCustomizations (CardCode code) raw
        card <- maybe (Left "Customization owner missing from Build catalog") Right $ Map.lookup code $ catalogCards cat
        result <- projectCard cat card entries
        pure (key,code,result,sum $ map customizationMarks entries)
      outcomes = [(key,one row) | row@(key,_) <- work]
      good = [v | (_,Right v) <- outcomes]
      errors = [Issue "customizationProjectionUnsupported" (key <> ": " <> e) | (key,Left e) <- outcomes]
      cards = foldr (\(_,code,c,_) -> Map.insert code c) (catalogCards cat) good
  pure $ CustomizationProjection (Catalog cards) [k | (k,_,_,_) <- good] errors $
    toJSON [object ["key" .= k,"cardCode" .= code,"marks" .= marks,"level" .= cardLevel c]
           | (k,code,c,marks) <- good]

-- Request-local projection of original BuildReact applyCustomizations. No rule
-- messages, registry installation, XP spending or stored deck text mutation.
projectCard :: Catalog -> CatalogCard -> [ValidatedCustomization] -> Either Text CatalogCard
projectCard cat card entries = do
  unless (null $ cardMissingEnglish card) $ Left "Missing English customization source fields"
  original <- obj $ cardRaw card
  defs <- required "customization_options" original :: Either Text [Value]
  ruleDef <- maybe (Left "No static rule customization definition") Right $ Map.lookup (CardCode $ cardCode card) allPlayerCards
  unless (length defs == Map.size (cdCustomizations ruleDef)) $ Left "Build/rule customization index mismatch"
  text <- required "text" original
  changes <- T.splitOn "\n" <$> required "real_customization_change" original
  _ <- required "real_customization_text" original :: Either Text Text
  unless (length changes >= length defs) $ Left "Customization text/index mismatch"
  -- These explicit fields distinguish the new projection from an old directory
  -- which omitted data. Null is allowed exactly where the original allows it.
  unless (all (\k -> KM.member k original) ["tags","cost","health","sanity","real_slot"]) $
    Left "Incomplete customization field projection"
  let selected = Map.fromList [(customizationIndex e,e) | e <- entries]
  (raw,lines0,inserts) <- foldM (step original changes selected) (original,T.splitOn "\n" text,[]) $ zip [0..] defs
  let lines1 = foldl (\ls (position,_,edit) -> splice (position+1) 0 edit ls) lines0 $
        sortOn (\(position,index,_) -> (Down position,Down index)) inserts
      marks = sum $ map customizationMarks entries
      level = if marks == 0 then cardLevel card else Just $ (marks+1) `div` 2
  traits <- required "traits" raw
  limit <- optional "deck_limit" raw
  pure card {cardRaw=Object $ put "customization_xp" marks $ put "text" (T.intercalate "\n" lines1) raw
    ,cardTraits=Set.fromList $ filter (not . T.null) $ map (T.toCaseFold . T.strip) $ T.splitOn "." traits
    ,cardLimit=limit,cardLevel=level}
 where
  step original changes selected state@(raw,ls,inserts) (index,value) = do
    definition <- obj value
    unless (all ((`elem` ["xp","health","sanity","cost","deck_limit","real_slot","real_traits","tags"
      ,"real_text","choice","quantity","card","text_change","position"]) . K.toText) $ KM.keys definition) $
      Left "Unknown customization effect field"
    threshold <- required "xp" definition
    unless (threshold >= (0 :: Int) && threshold <= 1000000) $ Left "Invalid customization threshold"
    case Map.lookup index selected of
      Nothing | threshold == 0 && KM.member "choice" definition -> Left "Unresolved zero-cost customization choice"
      Nothing -> pure state
      Just entry | customizationMarks entry < threshold -> pure state
      Just entry -> do
        updated <- fields original raw definition
        (chosen,edit) <- choiceEdit cat original updated definition entry (changes !! index)
        mode <- optional "text_change" definition :: Either Text (Maybe Text)
        position <- maybe 0 id <$> (optional "position" definition :: Either Text (Maybe Int))
        case mode of
          Nothing -> pure (chosen,ls,inserts)
          Just "trait" -> pure (chosen,ls,inserts)
          Just "append" -> pure (chosen,ls <> [edit],inserts)
          Just "replace" -> pure (chosen,splice position 1 edit ls,inserts)
          Just "insert" -> pure (chosen,ls,(position,index,edit):inserts)
          _ -> Left "Unknown customization text change"

-- JS splice start clamping, including negative positions used in actual data.
splice :: Int -> Int -> Text -> [Text] -> [Text]
splice position count edit xs = take start xs <> [edit] <> drop (start+count) xs
 where start = if position < 0 then max 0 (length xs+position) else min (length xs) position

fields :: Object -> Object -> Object -> Either Text Object
fields original raw definition = do
  a <- foldM stat raw ["health","sanity"]
  cost <- optional "cost" definition :: Either Text (Maybe Int)
  oldCost <- maybe 0 id <$> (optional "cost" a :: Either Text (Maybe Int))
  let b = maybe a (\n -> if n == 0 then a else put "cost" (oldCost+n) a) cost
  limit <- optional "deck_limit" definition :: Either Text (Maybe Int)
  let c = maybe b (\n -> if n == 0 then b else put "deck_limit" n b) limit
  d <- foldM replaceText c [("real_traits","traits"),("real_slot","real_slot")]
  added <- optional "tags" definition :: Either Text (Maybe [Text])
  previous <- strings "tags" d
  pure $ maybe d (\ts -> put "tags" (previous <> ts) d) added
 where
  stat state key = do
    delta <- optional key definition :: Either Text (Maybe Int)
    base <- maybe 0 id <$> (optional key original :: Either Text (Maybe Int))
    pure $ maybe state (\n -> if n == 0 then state else put key (base+n) state) delta
  replaceText state (source,target) = do
    value <- optional source definition :: Either Text (Maybe Text)
    pure $ maybe state (\t -> if T.null t then state else put target t state) value

choiceEdit :: Catalog -> Object -> Object -> Object -> ValidatedCustomization -> Text -> Either Text (Object,Text)
choiceEdit cat original raw definition entry edit = do
  kind <- optional "choice" definition :: Either Text (Maybe Text)
  let choices = customizationChoices entry
      rawParts = maybe [] (T.splitOn "^") $ customizationRawChoice entry
      replace value = let (before,after) = T.breakOn "_____" edit in
        if T.null after then edit else before <> value <> T.drop 5 after
      quantity def = do
        count <- maybe def id <$> (optional "quantity" definition :: Either Text (Maybe Int))
        unless (count > 0 && length rawParts == count && all (not . T.null) rawParts) $ Left "Unresolved customization choice quantity"
  case kind of
    Nothing -> do
      unless (null choices) $ Left "Unexpected choice on customization without choice"
      pure (raw,edit)
    Just "choose_trait" -> do
      quantity 1
      unless (length choices == length rawParts && all isTrait choices) $ Left "Expected original typed trait choices"
      pure (raw,replace $ T.intercalate ", " ["[[" <> t <> "]]" | t <- rawParts])
    Just "choose_skill" -> do
      quantity 1
      unless (length choices == 1 && all isSkill choices) $ Left "Expected original typed skill choice"
      pure (raw,replace $ "[" <> head rawParts <> "]")
    Just "choose_card" -> do
      quantity 1
      unless (length choices == length rawParts && all isCard choices) $ Left "Expected original typed card choices"
      predicate <- required "card" definition >>= obj
      unless (all (`elem` ["type","trait"]) $ map K.toText $ KM.keys predicate) $ Left "Unknown customization card matcher"
      types <- strings "type" predicate
      traits <- map T.toCaseFold <$> strings "trait" predicate
      cards <- traverse (\code -> maybe (Left "Unknown customization selected card") Right $ Map.lookup code $ catalogCards cat) rawParts
      unless (all (\c -> (null types || cardType c `elem` types) &&
        (null traits || any (`Set.member` cardTraits c) traits)) cards) $ Left "Customization selected card does not match offered definition"
      pure (raw,edit <> " " <> T.intercalate ", " ["<u>" <> cardName c <> "</u>" | c <- cards])
    Just "remove_slot" -> do
      index <- case choices of [ChosenIndex n] -> Right n; _ -> Left "Expected original slot index"
      slots <- T.splitOn "." <$> required "real_slot" original
      unless (index >= 0 && index < length slots && not (T.null $ T.strip $ slots !! index)) $ Left "Slot index is not an actual original slot"
      let selected = [(i,T.strip t) | (i,t) <- zip [0..] slots,i /= index]
          result = foldl (\acc (_,t) -> if T.null acc then t else acc <> ". " <> t) "" selected
      pure (put "real_slot" result raw,edit)
    _ -> Left "Unknown customization choice type"
 where
  isTrait (ChosenTrait _) = True; isTrait _ = False
  isSkill (ChosenSkill _) = True; isSkill _ = False
  isCard (ChosenCard _) = True; isCard _ = False
