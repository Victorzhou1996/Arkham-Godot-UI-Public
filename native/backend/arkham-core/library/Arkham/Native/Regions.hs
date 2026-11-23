module Arkham.Native.Regions (regionsView) where

import Arkham.Prelude
import Arkham.Asset.Cards qualified as Cards
import Arkham.Asset.Types (Asset, AssetAttrs (..))
import Arkham.Card
import Arkham.Classes.Entity
import Arkham.Classes.HasGame
import Arkham.Classes.Query (select)
import Arkham.Entities
import Arkham.Enemy.Types (EnemyAttrs (..))
import Arkham.Event.Types (EventAttrs (..))
import Arkham.Game (Game (..))
import Arkham.Helpers (unDeck)
import Arkham.Helpers.Modifiers (getModifiers, ModifierType (..))
import Arkham.Id
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Location.Types (LocationAttrs (..))
import Arkham.Matcher
import Arkham.Name (display, toName)
import Arkham.Placement
import Arkham.Scenario.Deck (ScenarioEncounterDeckKey (RegularEncounterDeck))
import Arkham.Scenario.Types (ScenarioAttrs (..))
import Arkham.Skill.Types (SkillAttrs (..))
import Arkham.Treachery.Types (TreacheryAttrs (..))
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Char (isDigit)
import Data.Text qualified as T

idText :: ToJSON a => a -> Text
idText value = case toJSON value of String text -> text; _ -> error "Native region ID must serialize as text"

owner :: ToJSON a => Text -> a -> Value
owner kind ident = object ["kind" .= kind,"id" .= ident]

publicOwner :: ToJSON a => Text -> a -> Text -> CardCode -> Value
publicOwner kind ident title code = object ["kind" .= kind,"id" .= ident,"title" .= title,"cardCode" .= code]

zone :: Text -> Text -> Value -> Text -> Text -> Maybe Int -> [Value] -> Value
zone ident kind entity status order count items = object
  ["id" .= ident,"kind" .= kind,"owner" .= entity,"status" .= status,"order" .= order,"count" .= count,"items" .= items]

unsupported :: Text -> Text -> Value -> Text -> Maybe Int -> Value
unsupported ident kind entity reason count = case zone ident kind entity "unsupported" "unknown" count [] of
  Object o -> Object $ KM.insert "reasonKey" (String reason) o
  _ -> error "Native region object invariant"

cardFaceDown :: Card -> Bool
cardFaceDown = \case PlayerCard c -> pcFacedown c == Just True; EncounterCard c -> ecFacedown c == Just True; VengeanceCard c -> cardFaceDown c

cardKind :: Card -> Text
cardKind = \case PlayerCard _ -> "player"; EncounterCard _ -> "encounter"; VengeanceCard c -> cardKind c

cardFlipped :: Card -> Bool
cardFlipped = \case EncounterCard c -> ecIsFlipped c == Just True; VengeanceCard c -> cardFlipped c; _ -> False

vengeance :: Card -> Bool
vengeance = \case VengeanceCard _ -> True; _ -> False

-- A hidden item has only a presentation ordinal, never the actual card UUID or
-- a name/art/code which could be searched or looked up in a global registry.
cardItem :: Text -> Int -> Bool -> Card -> Value
cardItem zid ordinal forcedBack card = cardItemFace zid ordinal forcedBack (cardFlipped card) card

visibleArt :: Bool -> Card -> Maybe Text
visibleArt flipped card =
  let code=unCardCode $ toCardCode card;art=cdArt $ toCardDef card
      pairs :: [(Text,Text)]
      pairs=[("05178b","05178a"),("05178d","05178c"),("05178f","05178e"),("05178h","05178g"),("05178j","05178i"),("05178l","05178k"),("54038b","54038"),("54039b","54039")]
   in case T.stripSuffix "b" code >>= (\base -> base <$ lookup base pairs) of
        Just canonical -> Just canonical
        Nothing -> case lookup code pairs of
          Just front -> Just $ if flipped then code else front
          Nothing | not flipped -> Just art
          Nothing | cdDoubleSided (toCardDef card) && T.all isDigit art -> Just $ art <> "b"
          _ -> Nothing

cardItemFace :: Text -> Int -> Bool -> Bool -> Card -> Value
cardItemFace zid ordinal forcedBack flipped card
  | forcedBack || cardFaceDown card || vengeance card = object
      ["key" .= (zid <> ":" <> tshow ordinal),"visibility" .= ("back" :: Text),"backKind" .= cardKind card,"vengeance" .= vengeance card,"inspectAllowed" .= False]
  | isNothing (visibleArt flipped card) = object
      ["key" .= (zid <> ":" <> tshow ordinal),"visibility" .= ("back" :: Text),"backKind" .= cardKind card
      ,"inspectAllowed" .= False,"status" .= ("unsupported" :: Text),"reasonKey" .= ("unrecognizedFlippedCardFace" :: Text)]
  | otherwise = object
      ["key" .= (zid <> ":" <> tshow ordinal),"visibility" .= ("face" :: Text),"cardId" .= toCardId card
      ,"cardCode" .= toCardCode card,"art" .= fromJustNote "verified region card art" (visibleArt flipped card)
      ,"name" .= display (toName card),"face" .= (if flipped then "reverse" else "current" :: Text)
      ,"vengeance" .= False,"inspectAllowed" .= True]

-- Preserve each physical instance and original index, including duplicate code.
listed :: Text -> Text -> Value -> Text -> [Card] -> Value
listed zid kind entity order cards = zone zid kind entity "supported" order (Just $ length cards) $ zipWith (\i c -> cardItem zid i False c) [0..] cards

counted :: Text -> Text -> Value -> Int -> Value
counted zid kind entity count = case zone zid kind entity "supported" "unknown" (Just count) [] of
  Object o -> Object $ KM.insert "access" (String "countOnly") o
  _ -> error "Native region object invariant"

topZone :: Text -> Text -> Value -> Bool -> [Card] -> Value
topZone zid kind entity reveal cards = zone zid kind entity "supported" "topFirst" (Just $ length cards)
  [cardItem zid 0 False c | reveal, c <- take 1 cards]

entityItem :: Text -> Int -> Text -> Value -> Bool -> Bool -> CardCode -> CardId -> Value
entityItem zid ordinal kind reference hidden flipped code cid =
  let card = lookupCard code cid
      value = cardItemFace zid ordinal hidden flipped card
   in case value of
        Object o | KM.lookup "visibility" o == Just (String "face") ->
          Object $ KM.insert "entityRef" reference $ KM.insert "entityKind" (String kind) o
        _ -> value

regionsView :: HasGame m => Game -> PlayerId -> m Value
regionsView game recipient
  | recipient `notElem` gamePlayers game = pure $ object ["schemaVersion" .= (1 :: Int),"recipient" .= recipient,"supported" .= False,"reasonKey" .= ("unknownRecipient" :: Text),"zones" .= ([] :: [Value])]
  | otherwise = do
      let entities=gameEntities game
          investigators=map toAttrs $ Map.elems $ entitiesInvestigators entities
          own=filter ((== recipient) . investigatorPlayerId) investigators
      modifiers <- traverse (\i -> (investigatorId i,) <$> getModifiers (investigatorId i)) investigators
      personal <- concatForM own $ investigatorZones game modifiers
      let encounterTop=any (elem TopCardOfEncounterDeckIsRevealed . snd) modifiers
          scenario=case gameMode game of This _ -> Nothing; That s -> Just $ toAttrs s; These _ s -> Just $ toAttrs s
          scenarioZones=maybe [] (scenarioRegions encounterTop) scenario
          locations=map locationRegion $ filter (not . locationOutOfGame) $ map toAttrs $ Map.elems $ entitiesLocations entities
          assets=concatMap (assetRegions entities recipient investigators) $ Map.elems $ entitiesAssets entities
          removed=listed "game:removed" "removed" (object ["kind" .= ("Game" :: Text)]) "listed" $ gameRemovedFromPlay game
      pure $ object ["schemaVersion" .= (1 :: Int),"recipient" .= recipient,"supported" .= True,"zones" .= (personal <> locations <> assets <> scenarioZones <> [removed])]

investigatorZones :: HasGame m => Game -> [(InvestigatorId,[ModifierType])] -> InvestigatorAttrs -> m [Value]
investigatorZones game modifiers attrs = do
  let iid=investigatorId attrs;ref=publicOwner "Investigator" iid (display $ toName attrs) (investigatorCardCode attrs);prefix="investigator:" <> idText iid <> ":"
      entities=gameEntities game;as=map toAttrs $ Map.elems $ entitiesAssets entities
      es=map toAttrs $ Map.elems $ entitiesEnemies entities;ts=map toAttrs $ Map.elems $ entitiesTreacheries entities
      events=map toAttrs $ Map.elems $ entitiesEvents entities
      skills=map toAttrs $ Map.elems $ entitiesSkills entities
      play a=assetVisible a && assetPlacement a `elem` [InPlayArea iid,AttachedToInvestigator iid]
      threat p=p `elem` [InThreatArea iid,FacedownInThreatArea iid]
      assetList=[entityItem (prefix <> "assets") n "Asset" (owner "Asset" $ assetId a) False (assetFlipped a) (assetCardCode a) (assetCardId a) | (n,a) <- zip [0..] $ filter play as]
      eventList=[entityItem (prefix <> "assets:event") n "Event" (owner "Event" $ eventId e) False False (eventCardCode e) (eventCardId e) | (n,e) <- zip [0..] $ filter (\e -> eventPlacement e `elem` [InPlayArea iid,AttachedToInvestigator iid]) events]
      skillList=[entityItem (prefix <> "assets:skill") n "Skill" (owner "Skill" $ skillId s) False False (skillCardCode s) (skillCardId s) | (n,s) <- zip [0..] $ filter (\s -> skillPlacement s == InPlayArea iid) skills]
  engaged <- select $ EnemyIsEngagedWith $ IncludeEliminated $ InvestigatorWithId iid
  let enemyThreat=[entityItem (prefix <> "threat:enemy") n "Enemy" (owner "Enemy" $ enemyId e) (isHiddenPlacement $ enemyPlacement e) (enemyFlipped e) (enemyCardCode e) (enemyCardId e) | (n,e) <- zip [0..] $ filter (\e -> enemyId e `elem` engaged || threat (enemyPlacement e)) es]
      assetThreat=[entityItem (prefix <> "threat:asset") n "Asset" (owner "Asset" $ assetId a) (isHiddenPlacement $ assetPlacement a) (assetFlipped a) (assetCardCode a) (assetCardId a) | (n,a) <- zip [0..] $ filter (\a -> assetVisible a && threat (assetPlacement a)) as]
      treacheryThreat=[entityItem (prefix <> "threat:treachery") n "Treachery" (owner "Treachery" $ treacheryId t) (isHiddenPlacement $ treacheryPlacement t) False (treacheryCardCode t) (treacheryCardId t) | (n,t) <- zip [0..] $ filter (\t -> threat (treacheryPlacement t) || treacheryPlacement t == AttachedToInvestigator iid) ts]
      eventThreat=[entityItem (prefix <> "threat:event") n "Event" (owner "Event" $ eventId e) (isHiddenPlacement $ eventPlacement e) False (eventCardCode e) (eventCardId e) | (n,e) <- zip [0..] $ filter (threat . eventPlacement) events]
      threatItems=enemyThreat <> assetThreat <> treacheryThreat <> eventThreat
      ownedMods=fromMaybe [] $ lookup iid modifiers
      public =
        [listed (prefix <> "hand") "hand" ref "listed" $ investigatorHand attrs
        ,listed (prefix <> "discard") "discard" ref "topFirst" $ map PlayerCard $ investigatorDiscard attrs
        ,listed (prefix <> "under") "under" ref "listed" $ investigatorCardsUnderneath attrs
        ,listed (prefix <> "bonded") "bonded" ref "listed" $ investigatorBondedCards attrs
        ,topZone (prefix <> "deck") "deck" ref (TopCardOfDeckIsRevealed `elem` ownedMods) $ map PlayerCard $ unDeck $ investigatorDeck attrs
        ,zone (prefix <> "assets") "assets" ref "supported" "unordered" (Just $ length assetList+length eventList+length skillList) (assetList <> eventList <> skillList)
        ,zone (prefix <> "threat") "threat" ref "supported" "unordered" (Just $ length threatItems) threatItems]
      side=maybe [] (\cards -> [unsupported (prefix <> "side") "sideDeck" ref "sideDeckPolicyPending" $ Just $ length cards]) $ investigatorSideDeck attrs
  pure $ public <> side

locationRegion :: LocationAttrs -> Value
locationRegion attrs =
  let zid="location:" <> idText (locationId attrs) <> ":under"
      ref=if locationRevealed attrs then publicOwner "Location" (locationId attrs) (display $ toName attrs) (locationCardCode attrs) else owner "Location" (locationId attrs)
      cards=locationCardsUnderneath attrs
      ordinary=[c | c@PlayerCard {} <- cards]
   in if any cardFaceDown cards
      then case counted zid "under" ref (length cards) of Object o -> Object $ KM.insert "reasonKey" (String "locationUnderContainsFaceDown") o; _ -> error "Native region object invariant"
      else if cardCodeExactEq (locationCardCode attrs) "11685"
        then unsupported zid "under" ref "shuffledShopStackInspectionPolicyPending" $ Just $ length cards
        else listed zid "under" ref "listed" ordinary

assetRegions :: Entities -> PlayerId -> [InvestigatorAttrs] -> Asset -> [Value]
assetRegions entities recipient investigators asset
  | not (assetVisible attrs) || not (isInPlayPlacement $ assetPlacement attrs) = []
  | assetFlipped attrs = [unsupported (prefix <> "under") "under" ref "flippedAssetUnderPolicyPending" Nothing]
  | otherwise = [listed (prefix <> "under") "under" ref "listed" $ assetCardsUnderneath attrs,attachments] <> special
 where
  attrs=toAttrs asset
  ref=if assetFlipped attrs then owner "Asset" (assetId attrs) else publicOwner "Asset" (assetId attrs) (display $ toName attrs) (assetCardCode attrs)
  prefix="asset:" <> idText (assetId attrs) <> ":"
  controls=assetController attrs `elem` map (Just . investigatorId) (filter ((== recipient) . investigatorPlayerId) investigators)
  attached = \case AttachedToAsset target _ -> target == assetId attrs; _ -> False
  -- Direct attachments are a separate public relationship, never disguised as
  -- underneath cards. Each visible parent gets its own zone, so deeper visible
  -- asset attachments remain independently navigable without recursive walks.
  attachmentItems =
    [entityItem (prefix <> "attachments:asset") n "Asset" (owner "Asset" $ assetId a) (isHiddenPlacement $ assetPlacement a) (assetFlipped a) (assetCardCode a) (assetCardId a)
      | (n,a) <- zip [0..] $ filter (\a -> assetVisible a && attached (assetPlacement a)) $ map toAttrs $ Map.elems $ entitiesAssets entities]
    <> [entityItem (prefix <> "attachments:enemy") n "Enemy" (owner "Enemy" $ enemyId e) (isHiddenPlacement $ enemyPlacement e) (enemyFlipped e) (enemyCardCode e) (enemyCardId e)
      | (n,e) <- zip [0..] $ filter (attached . enemyPlacement) $ map toAttrs $ Map.elems $ entitiesEnemies entities]
    <> [entityItem (prefix <> "attachments:event") n "Event" (owner "Event" $ eventId e) (isHiddenPlacement $ eventPlacement e) False (eventCardCode e) (eventCardId e)
      | (n,e) <- zip [0..] $ filter (attached . eventPlacement) $ map toAttrs $ Map.elems $ entitiesEvents entities]
    <> [entityItem (prefix <> "attachments:treachery") n "Treachery" (owner "Treachery" $ treacheryId t) (isHiddenPlacement $ treacheryPlacement t) False (treacheryCardCode t) (treacheryCardId t)
      | (n,t) <- zip [0..] $ filter (attached . treacheryPlacement) $ map toAttrs $ Map.elems $ entitiesTreacheries entities]
  attachments=zone (prefix <> "attachments") "attachments" ref "supported" "unordered" (Just $ length attachmentItems) attachmentItems
  metadata=case toJSON asset of Object o -> o; _ -> mempty
  readCards key=KM.lookup (K.fromText key) metadata >>= Aeson.parseMaybe parseJSON
  special
    | cardCodeExactEq (assetCardCode attrs) (toCardCode Cards.underworldMarket2) && controls =
        case (readCards "marketDeck",readCards "knownMarketDeck") of
          (Just hidden,Just known) -> [counted (prefix <> "market") "marketDeck" ref (length (hidden :: [Card])),listed (prefix <> "knownMarket") "knownMarketDeck" ref "bottomKnownSequence" known]
          _ -> [unsupported (prefix <> "market") "marketDeck" ref "marketMetadataUnavailable" Nothing]
    | cardCodeExactEq (assetCardCode attrs) (toCardCode Cards.theBeyondBleakNetherworld) =
        case readCards "spiritDeck" of
          Just cards -> [topZone (prefix <> "spirit") "spiritDeck" ref (case cards of EncounterCard {} : _ -> True; _ -> False) cards]
          Nothing -> [unsupported (prefix <> "spirit") "spiritDeck" ref "spiritMetadataUnavailable" Nothing]
    | otherwise = []

scenarioRegions :: Bool -> ScenarioAttrs -> [Value]
scenarioRegions reveal attrs =
  let ref=owner "Scenario" $ scenarioId attrs;prefix="scenario:" <> idText (scenarioId attrs) <> ":"
      base=[listed (prefix <> "encounterDiscard:regular") "encounterDiscard" ref "topFirst" $ map EncounterCard $ scenarioDiscard attrs
           ,topZone (prefix <> "encounterDeck:regular") "encounterDeck" ref reveal $ map EncounterCard $ unDeck $ scenarioEncounterDeck attrs
           ,listed (prefix <> "victory") "victory" ref "listed" $ scenarioVictoryDisplay attrs
           -- Vue's ordinary out-of-play viewer lists these cards. Its enemy
           -- deduplication is only needed because that viewer ALSO renders
           -- out-of-play Enemy entities. Regions has no such second listing:
           -- filtering here would lose those cards. Keep physical instances
           -- once in source order, with the shared face/privacy projection.
           ,listed (prefix <> "setAside") "setAside" ref "listed" $ scenarioSetAsideCards attrs]
      secondary=concat [[listed (prefix <> "encounterDiscard:" <> tshow key) "encounterDiscard" ref "topFirst" $ map EncounterCard discarded
                        ,counted (prefix <> "encounterDeck:" <> tshow key) "encounterDeck" ref $ length $ unDeck deck] | (key,(deck,discarded)) <- Map.toList $ scenarioEncounterDecks attrs,key /= RegularEncounterDeck]
      otherDecks=[counted (prefix <> "deck:" <> idText key) "scenarioDeck" ref $ length cards | (key,cards) <- Map.toList $ scenarioDecks attrs]
   in base <> secondary <> otherDecks
