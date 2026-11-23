{-# LANGUAGE NoImplicitPrelude #-}
module Arkham.Native.DeckMetadata (deckMetadataValidate, ValidatedCustomization(..), validatedCustomizations) where

import Prelude
import Control.Monad (unless, forM_)
import Data.Aeson
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (parseEither)
import Data.Text (Text)
import Data.Text qualified as T
import Data.Text.Encoding qualified as TE
import Data.Map.Strict qualified as Map
import Data.IntMap.Strict qualified as IntMap
import Data.Set qualified as Set
import Data.Char (isDigit)
import Text.Read (readMaybe)
import Text.Parsec qualified as P
import Arkham.Card.CardCode (CardCode(..))
import Arkham.Card.CardDef (CardDef(..))
import Arkham.Decklist (parseCustomizations)
import Arkham.Customization (CustomizationChoice)
import Arkham.Decklist.Type (ArkhamDBDecklist(..))
import Arkham.PlayerCard (allPlayerCards)
import Arkham.Investigator.Cards (allInvestigatorCards)
import Arkham.Native.Answer (validateLocalDeckInput)
import Arkham.Native.StrictJSON (validateJsonProperties)

-- Transport metadata validation only: preserve the original ArkhamDB string.
-- Remaining keys still require the caller's selector/base metadata policy.
deckMetadataValidate :: Value -> Either Text Value
deckMetadataValidate request = do
  raw <- case request of
    Object o | KM.size o == 1, Just d <- KM.lookup "deck" o -> Right d
    _ -> Left "Expected exactly deck"
  clean <- case raw of
    Object o -> do
      unless (all (\k -> not $ KM.member k o) ["customCards","authorCards","packageText","packageSha256"])
        $ Left "Author definitions are not accepted by metadata validation"
      forM_ (KM.lookup "meta" o) $ \v -> case v of
        String t -> validateJsonProperties (TE.encodeUtf8 t)
        Null -> Right ()
        _ -> Left "Metadata must use the original JSON string"
      -- Description is not game metadata. Do not let the legacy decoder
      -- reserialize the original metadata string while checking it.
      Right $ Object $ KM.delete "description_md" o
    _ -> Left "Expected deck object"
  deck <- either (Left . T.pack) Right $ parseEither parseJSON clean
  validateLocalDeckInput deck
  -- This endpoint never installs author definitions or consults a game registry.
  forM_ (Map.keys (slots deck)) $ official False
  forM_ (Map.keys (sideSlots deck)) $ official True
  values <- case meta deck of
    Nothing -> Right KM.empty
    Just t -> do
      validateJsonProperties (TE.encodeUtf8 t)
      v <- either (Left . T.pack) Right $ eitherDecodeStrict' (TE.encodeUtf8 t)
      case v of Object o -> Right o; _ -> Left "Expected metadata object"
  let attachments = ["attachments_03264","attachments_05002","attachments_07303","attachments_10079","attachments_09077","attachments_11080"]
      recognized k = k == "extra_deck" || k `elem` attachments || "cus_" `T.isPrefixOf` k
      rows = [(Key.toText k,v) | (k,v) <- KM.toList values]
      deckCodes = Set.fromList $ Map.keys (slots deck) <> Map.keys (sideSlots deck)
      extraCodes = case KM.lookup "extra_deck" values of
        Just (String t) | not(T.null t) -> Set.fromList $ map CardCode $ T.splitOn "," t
        _ -> Set.empty
  forM_ rows $ \(key,value) -> if not (recognized key) then Right () else do
    text <- case value of String t -> Right t; _ -> Left "Gameplay metadata must be a string"
    unless (T.length text <= 16384) $ Left "Gameplay metadata is too large"
    if key == "extra_deck" then codes True True text else
      if key `elem` attachments then do
        let owner = CardCode $ T.drop (T.length "attachments_") key
        unless (if key=="attachments_05002" then deck.investigator=="05002"
                else Set.member owner deckCodes) $ Left "Attachment owner is not in this deck"
        codes False True text
      else do
        let code = CardCode $ T.drop 4 key
        unless (Set.member code $ Set.union deckCodes extraCodes) $ Left "Customization card is not in this deck"
        _ <- validatedCustomizations code text
        Right ()
  pure $ object
    [ "schemaVersion" .= (1::Int)
    , "validatedKeys" .= [k | (k,_) <- rows, recognized k]
    , "remainingKeys" .= [k | (k,_) <- rows, not $ recognized k]
    , "preservesOriginalMeta" .= True
    , "validatesExperienceBudget" .= False
    ]
 where
  official allowInvestigator code =
    unless (Map.member code allPlayerCards || allowInvestigator && Map.member code allInvestigatorCards)
      $ Left "Unknown official deck metadata card"
  codes allowInvestigator allowEmpty text = do
    let parts = if allowEmpty && T.null text then [] else T.splitOn "," text
    unless (length parts <= 500 && all (not . T.null) parts) $ Left "Invalid metadata card list"
    forM_ parts $ official allowInvestigator . CardCode
-- Preserve both original choices and the original rule parser's typed result.
-- This is syntax/index validation, not purchase-budget or choice-completeness.
data ValidatedCustomization = ValidatedCustomization
  { customizationIndex :: Int, customizationMarks :: Int
  , customizationRawChoice :: Maybe Text, customizationChoices :: [CustomizationChoice]
  } deriving Show

validatedCustomizations :: CardCode -> Text -> Either Text [ValidatedCustomization]
validatedCustomizations code text = do
  unless (T.length text <= 16384) $ Left "Gameplay metadata is too large"
  def <- maybe (Left "Unknown customization card") Right $ Map.lookup code allPlayerCards
  unless (not $ Map.null $ cdCustomizations def) $ Left "Card has no customizations"
  let entries = if T.null text then [] else T.splitOn "," text
  unless (length entries <= 64) $ Left "Too many customization entries"
  indices <- traverse (entry $ Map.size $ cdCustomizations def) entries
  unless (Set.size (Set.fromList indices) == length indices) $ Left "Duplicate customization index"
  parsed <- either (const $ Left "Invalid customization syntax or choice") Right
    $ P.parse (parseCustomizations <* P.eof) "metadata" text
  traverse (\(raw,i) -> case IntMap.lookup (fromInteger i) parsed of
    Nothing -> Left "Customization parser index mismatch"
    Just (marks,choices) -> Right $ ValidatedCustomization (fromInteger i) marks
      (case T.splitOn "|" raw of [_,_,choice] -> Just choice; _ -> Nothing) choices)
    (zip entries indices)
 where
  number t = case readMaybe (T.unpack t) :: Maybe Integer of
    Just n | not (T.null t), T.all (\c -> c >= '0' && c <= '9') t, n <= 1000000 -> Right n
    _ -> Left "Customization integer exceeds product input bound"
  entry count text = case T.splitOn "|" text of
    parts@(index:marks:choices) | length parts <= 3 -> do
      i <- number index
      _ <- number marks
      unless (i < toInteger count) $ Left "Customization index is not offered by this card"
      -- A numeric leading choice invokes parseIndex before parseCardCodes.
      forM_ choices $ \choice -> case T.uncons choice of
        Just (c,_) | isDigit c -> do
          _ <- number $ T.takeWhile (\x -> x >= '0' && x <= '9') choice
          Right ()
        _ -> Right ()
      Right i
    _ -> Left "Expected index|marks[|choices]"
