{- | Putting your own cards into someone else's deck.

An overlay is a small edit applied to a decklist before it loads: swap the
investigator for one you built (bringing their signatures with them), swap a
card for another, add cards, take cards out. It is a transformation of the
decklist itself, so everything downstream -- deck loading, investigator
creation, card generation -- carries on unchanged, resolving custom codes
through "Arkham.Card.CustomCard"'s registry the way it resolves any other.
-}
module Arkham.Custom.Overlay (
  DeckOverlay (..),
  applyOverlay,
  investigatorOverlay,
  needsOverlayInvestigator,
  overlaySignatures,
  overlaySetAside,
  setAsideMetaKey,
  decklistCustomCards,
  CampaignOverlayPlan (..),
  campaignOverlayPlan,
) where

import Arkham.Card
import Arkham.Decklist.Type
import Arkham.Id
import Arkham.Prelude
import Data.Aeson.Types (parseMaybe)
import Data.Map.Strict qualified as Map
import Data.Aeson.KeyMap qualified as KM
import Control.Monad.Fail (fail)

data DeckOverlay = DeckOverlay
  { overlayInvestigator :: Maybe InvestigatorId
  -- ^ Replaces the deck's investigator, and their signatures with its own.
  , overlaySwaps :: Map CardCode CardCode
  -- ^ Replace one card with another, keeping the count.
  , overlayAdd :: Map CardCode Int
  , overlayRemove :: Map CardCode Int
  }
  -- 'Ord' and 'Data' because 'Message' carries one and derives both.
  deriving stock (Show, Eq, Ord, Data)

instance FromJSON DeckOverlay where
  parseJSON = withObject "DeckOverlay" \o -> do
    unless (all (`elem` ["investigator","swaps","add","remove"]) $ KM.keys o) $ fail "Unknown overlay field"
    DeckOverlay
      <$> o
      .:? "investigator"
      <*> o
      .:? "swaps"
      .!= mempty
      <*> o
      .:? "add"
      .!= mempty
      <*> o
      .:? "remove"
      .!= mempty

instance ToJSON DeckOverlay where
  toJSON o =
    object
      [ "investigator" .= overlayInvestigator o
      , "swaps" .= overlaySwaps o
      , "add" .= overlayAdd o
      , "remove" .= overlayRemove o
      ]

-- The campaign handler and native preflight share this exact card-list plan.
-- CardCode Eq intentionally differs from Ord for some paired faces, so a Map
-- simulation is not equivalent. All additions wait until original removals end.
data CampaignOverlayPlan = CampaignOverlayPlan
  { plannedCards :: [PlayerCard]
  , plannedAdds :: [(CardCode,Int)]
  , plannedSwaps :: [(CardCode,CardCode,Int)]
  , plannedRemovals :: [(CardCode,Int,Int)]
  }

campaignOverlayPlan :: DeckOverlay -> [PlayerCard] -> CampaignOverlayPlan
campaignOverlayPlan overlay original = CampaignOverlayPlan retained (swappedIn <> Map.toList (overlayAdd overlay)) swaps removals
 where
  matching code = (== code) . toCardCode
  (afterSwaps,swappedIn,swaps)=foldl' swapOut (original,[],[]) $ Map.toList $ overlaySwaps overlay
  swapOut (cards,adds,checks) (from,to) =
    let n=length $ filter (matching from) cards
     in (filter (not . matching from) cards,if n>0 then (to,n):adds else adds,checks<>[(from,to,n)])
  (retained,removals)=foldl' removeOut (afterSwaps,[]) $ Map.toList $ overlayRemove overlay
  removeOut (cards,checks) (code,n) =
    let available=length $ filter (matching code) cards
        after=foldr (\_ cs -> deleteFirstMatch (matching code) cs) cards [1..n]
     in (after,checks<>[(code,n,available)])

-- | The cards a custom investigator brings with them, from its @_signatures@ meta.
overlaySignatures :: CardDef -> [CardCode]
overlaySignatures = metaCardCodes "_signatures"

{- | Cards a custom card names that never go into a deck.

An investigator whose setup sets copies of something aside needs those defs
registered on the game -- they are made out of nothing when the game starts, and
nothing else in the decklist mentions them -- but they must not be added to the
deck the way a signature is. Hence a key of its own rather than more signatures.
-}
overlaySetAside :: CardDef -> [CardCode]
overlaySetAside = metaCardCodes setAsideMetaKey

setAsideMetaKey :: Text
setAsideMetaKey = "_setAside"

metaCardCodes :: Text -> CardDef -> [CardCode]
metaCardCodes key def =
  map sanitizeCustomCardCode
    $ fromMaybe []
    $ parseMaybe parseJSON
    =<< Map.lookup key (cdMeta def)

{- | The signature cards of whoever the deck belonged to.

Swapping the investigator has to take their signatures out, or the deck keeps
cards that were only ever theirs. A signature is a card restricted to that
investigator, which is what the deck restriction records.
-}
signaturesOf :: InvestigatorId -> Map CardCode Int -> [CardCode]
signaturesOf iid = filter restrictedToThem . Map.keys
 where
  restrictedToThem cardCode = case lookupCardDef cardCode of
    Nothing -> False
    Just def -> Signature iid `elem` cdDeckRestrictions def

applyOverlay :: DeckOverlay -> ArkhamDBDecklist -> ArkhamDBDecklist
applyOverlay overlay decklist =
  decklist {slots = slots', investigator_code = investigator', meta = meta'}
 where
  investigator' = fromMaybe (investigator_code decklist) (overlayInvestigator overlay)

  {- An alternate front outranks the deck's own investigator ('decklistInvestigatorId'),
  so leaving one in place would hand the engine the investigator this just swapped away.
  Only touched when the swap happens, and only to drop that one key. -}
  meta' = case overlayInvestigator overlay of
    Nothing -> meta decklist
    Just _ -> withoutAlternateFront <$> meta decklist

  withoutOldSignatures = case overlayInvestigator overlay of
    Nothing -> slots decklist
    Just _ ->
      foldl' (flip Map.delete) (slots decklist)
        $ signaturesOf (investigator_code decklist) (slots decklist)

  withNewSignatures = case overlayInvestigator overlay of
    Nothing -> withoutOldSignatures
    Just iid ->
      foldl' (\m cardCode -> Map.insertWith (+) cardCode 1 m) withoutOldSignatures
        $ maybe [] overlaySignatures (lookupCustomCardDef (toCardCode iid))

  swapped =
    Map.foldrWithKey
      ( \from replacement m -> maybe m (\n -> Map.insertWith (+) replacement n (Map.delete from m)) (Map.lookup from m)
      )
      withNewSignatures
      (overlaySwaps overlay)

  added = Map.foldrWithKey (Map.insertWith (+)) swapped (overlayAdd overlay)

  -- Removing more than the deck holds simply takes them all out.
  slots' =
    Map.filter (> 0)
      $ Map.foldrWithKey (\cardCode n m -> Map.adjust (subtract n) cardCode m) added (overlayRemove overlay)

withoutAlternateFront :: Text -> Text
withoutAlternateFront raw = fromMaybe raw do
  m <- decodeMeta raw
  guard $ Map.member "alternate_front" m
  pure $ encodeMeta (Map.delete "alternate_front" m)

decodeMeta :: Text -> Maybe (Map Text Value)
decodeMeta raw = decode (encodeUtf8 $ fromStrict raw)

encodeMeta :: Map Text Value -> Text
encodeMeta = toStrict . decodeUtf8 . encode

{- | A deck built on an arkham.build placeholder investigator -- it has none of
its own, and plays under whichever one an overlay lays over it.

Read off the deck's own @fan_made_content@ first, so a card the Investigator
Placeholders pack gains later counts without being listed here; the codes in
'arkhamBuildPlaceholderCodes' are what a deck arriving without that meta (an
older import, an upload) is recognized by.
-}
needsOverlayInvestigator :: ArkhamDBDecklist -> Bool
needsOverlayInvestigator decklist =
  isArkhamBuildPlaceholder cardCode || cardCode `elem` placeholdersInMeta
 where
  cardCode = toCardCode decklist.investigator
  placeholdersInMeta = fromMaybe [] do
    raw <- meta decklist
    fanMade <- Map.lookup "fan_made_content" =<< decodeMeta raw
    cards <- parseMaybe (withObject "fanMadeContent" (.: "cards")) fanMade
    pure
      [ arkhamBuildCustomCardCode cardId
      | (cardId, card) <- Map.toList (cards :: Map Text (Map Text Value))
      , Map.lookup "pack_code" card == Just (String arkhamBuildPlaceholderPack)
      ]

{- | The overlay that says only whose deck this is.

What a seat's upgrade carries when the deck it pulled brings no investigator of
its own: the investigator stays as it was, and its signatures -- which an
arkham.build list has no way to name -- come back with it.
-}
investigatorOverlay :: InvestigatorId -> DeckOverlay
investigatorOverlay iid = DeckOverlay (Just iid) mempty mempty mempty

{- | The custom cards a decklist names, so they can be recorded on the game.

The registry a decklist resolves against is process-global and rebuilt from the
game, so a card that arrives only through an overlay would be unknown to every
other client (and to this one after a restart). Registering them onto the game
puts their defs and art where everyone reads them from.
-}
decklistCustomCards :: ArkhamDBDecklist -> [CustomCard]
decklistCustomCards decklist = mapMaybe lookupCustomCard $ toList codes
 where
  codes = go (setFromList direct) direct
  direct =
    filter isCustomCardCode
      $ toCardCode (investigator_code decklist)
      : toCardCode decklist.investigator
      : Map.keys (slots decklist)
        <> Map.keys (sideSlots decklist)

  -- A custom investigator brings signatures, and cards its setup sets aside,
  -- that the decklist itself never names.
  go :: Set CardCode -> [CardCode] -> Set CardCode
  go seen [] = seen
  go seen (cardCode : rest) =
    let
      brought def = overlaySignatures def <> overlaySetAside def
      new = filter (`notMember` seen) $ maybe [] brought (lookupCustomCardDef cardCode)
     in
      go (seen <> setFromList new) (new <> rest)
