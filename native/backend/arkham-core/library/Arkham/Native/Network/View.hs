-- Independent seat projection. Never serialize PublicGame, an entity's whole
-- attrs, a deck, a modifier cache, or the global card lookup map here.
module Arkham.Native.Network.View (networkView, networkViewWithCards, placementView, skillTestView, choiceCardsView) where

import Arkham.Prelude
import Arkham.Game (Game (..))
import Arkham.Entities
import Arkham.Classes.Entity
import Arkham.Card
import Arkham.Helpers (unDeck)
import Arkham.Id
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Location.Types (LocationAttrs (..))
import Arkham.Enemy.Types (EnemyAttrs (..))
import Arkham.Asset.Types (AssetAttrs (..))
import Arkham.Act.Types (ActAttrs (..))
import Arkham.Agenda.Types (AgendaAttrs (..))
import Arkham.Placement
import Arkham.Target (Target (..))
import Arkham.Name (display, toName)
import Arkham.Native.Network.Question (questionView)
import Arkham.Question (Question)
import Arkham.Native.Validation (interactionView)
import Arkham.Classes.HasGame (HasGame, getGame)
import Arkham.Helpers.SkillTest (getSkillTestModifiedSkillValue, getSkillTestDifficulty, getSkillTestSkillTypes, getCommittableCards)
import Arkham.SkillTest.Base (SkillTest (..))
import Arkham.ChaosToken.Types (ChaosToken (..))
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson (Result (Success))
import Control.Monad.Fail (MonadFail)
import Control.Monad.Fail (fail)

-- Placement can itself contain a complete hidden swarm Card or arbitrary
-- Target. Only explicit public relationships are serialized.
placementView :: Placement -> Value
placementView = \case
  AtLocation ident -> ref "Location" ident
  AtLocations ids -> object ["kind" .= ("Locations"::Text),"ids" .= ids]
  AttachedToLocation ident -> ref "Location" ident
  BetweenLocations a b -> object ["kind" .= ("BetweenLocations"::Text),"ids" .= [a,b]]
  InPlayArea ident -> ref "Investigator" ident
  InThreatArea ident -> ref "Investigator" ident
  AttachedToInvestigator ident -> ref "Investigator" ident
  AttachedToEnemy ident -> ref "Enemy" ident
  AttachedToAsset ident _ -> ref "Asset" ident
  InVehicle ident -> ref "Asset" ident
  AttachedToAct ident -> ref "Act" ident
  AttachedToAgenda ident -> ref "Agenda" ident
  AsSwarm ident _ -> ref "Enemy" ident
  AsSelfLocation label -> object ["kind" .= ("SelfLocation"::Text),"label" .= label]
  _ -> Null

ref :: ToJSON a => Text -> a -> Value
ref kind ident = object ["kind" .= kind,"id" .= ident]

cardView :: Int -> Card -> Value
cardView ordinal card
  | cardHidden card = object ["ordinal" .= ordinal,"visibility" .= ("back"::Text)]
  | otherwise = object ["ordinal" .= ordinal,"visibility" .= ("face"::Text)
      ,"cardId" .= toCardId card,"cardCode" .= toCardCode card,"name" .= display (toName card)]

cardHidden :: Card -> Bool
cardHidden = \case
  PlayerCard c -> pcFacedown c==Just True
  EncounterCard c -> ecFacedown c==Just True || ecIsFlipped c==Just True
  VengeanceCard _ -> True

-- Read the finite display tree, not its Message payload. Only explicit
-- TargetLabel(CardIdTarget) values in this recipient's current offered UI count.
offeredCardIds :: Question msg -> Set CardId
offeredCardIds = Set.fromList . collect . questionView target
 where
  target (CardIdTarget ident) = ref "Card" ident
  target _ = Null
  collect (Array xs) = concatMap collect xs
  collect (Object o) = here <> concatMap collect (KM.elems o)
   where
    here = case (KM.lookup "tag" o,KM.lookup "target" o) of
      (Just (String "TargetLabel"),Just (Object t)) -> case (KM.lookup "kind" t,KM.lookup "id" t) of
        (Just (String "Card"),Just raw) -> case fromJSON raw of Success ident -> [ident]; _ -> []
        _ -> []
      _ -> []
  collect _ = []

-- No global card-registry search. The original rule helper supplies unusual
-- "as if in hand" candidates; intersect with actual offered IDs BEFORE encoding.
-- Already-public committed cards remain available even when not committable now.
choiceCardsView :: (HasGame m, MonadFail m) => PlayerId -> m ([CardId],Value)
choiceCardsView recipient = do
  game <- getGame
  unless (recipient `elem` gamePlayers game) $ fail "Unknown card-choice recipient"
  let offered=maybe Set.empty offeredCardIds $ Map.lookup recipient $ gameQuestion game
      own=filter ((==recipient) . investigatorPlayerId) $ map toAttrs $ Map.elems $ entitiesInvestigators $ gameEntities game
      owners=map investigatorId own
      committed=maybe [] (\test -> concat [Map.findWithDefault [] iid $ skillTestCommittedCards test | iid <- owners]) $ gameSkillTest game
      visible=filter (not . cardHidden) $ committed <> concat [investigatorHand i <> map PlayerCard (investigatorDiscard i) | i <- own]
      known=Set.fromList $ map toCardId visible
      missing=offered `Set.difference` known
  unless (Set.size offered <= 4096) $ fail "Too many offered card references"
  extra <- if Set.null missing || isNothing (gameSkillTest game) then pure []
    else concat <$> traverse (getCommittableCards . investigatorId) own
  let selected=Map.fromList [(toCardId card,card) | card <- visible <> extra
        ,not (cardHidden card),toCardId card `Set.member` offered]
      rows=[object ["cardId" .= ident,"cardCode" .= toCardCode card,"name" .= display (toName card),"visibility" .= ("face"::Text)]
        | (ident,card) <- Map.toList selected]
  pure (Map.keys selected,toJSON rows)

-- Public test display only. Effective numbers come from the existing rule
-- queries, not a second calculation in the client. No source payload, token
-- set-aside/to-resolve pool, subscribers, modifiers or RNG is transported.
skillTestView :: HasGame m => m Value
skillTestView = do
  game <- getGame
  case gameSkillTest game of
    Nothing -> pure Null
    Just test -> do
      value <- getSkillTestModifiedSkillValue
      difficulty <- getSkillTestDifficulty
      skills <- getSkillTestSkillTypes
      pure $ object
        ["schemaVersion" .= (1::Int),"id" .= skillTestId test
        ,"investigatorId" .= skillTestInvestigator test,"step" .= skillTestStep test
        ,"skills" .= skills,"modifiedSkillValue" .= value,"modifiedDifficulty" .= difficulty
        ,"revealedChaosTokens" .= map tokenView (skillTestRevealedChaosTokens test)
        ,"additionalRevealedChaosTokens" .= map tokenView (skillTestAdditionalRevealedChaosTokens test)
        ,"result" .= skillTestResult test
        ,"committedCards" .= [object ["investigatorId" .= iid,"cards" .= zipWith cardView [0..] cards]
          | (iid,cards) <- Map.toList $ skillTestCommittedCards test]]
 where
  tokenView token = object ["chaosTokenFace" .= chaosTokenFace token,"cancelled" .= chaosTokenCancelled token]

networkView :: Game -> PlayerId -> Either Text Value
networkView = networkViewWithCards []

-- Extra IDs must come from choiceCardsView for this exact game/recipient/basis,
-- never client input. They add only offered references, not registry lookup.
networkViewWithCards :: [CardId] -> Game -> PlayerId -> Either Text Value
networkViewWithCards offeredFaces game recipient = do
  unless (recipient `elem` gamePlayers game) $ Left "Unknown network seat"
  pure $ object
    ["schemaVersion" .= (1::Int),"scope" .= ("network-seat"::Text),"recipient" .= recipient
    ,"phase" .= gamePhase game,"inSetup" .= gameInSetup game,"state" .= gameGameState game
    ,"activeInvestigatorId" .= gameActiveInvestigatorId game,"leadInvestigatorId" .= gameLeadInvestigatorId game
    ,"scenarioSteps" .= gameScenarioSteps game
    ,"board" .= object
      ["investigators" .= map investigatorView investigators
      ,"locations" .= map locationView locations,"enemies" .= map enemyView enemies
      ,"assets" .= map assetView assets
      ,"acts" .= [object ["id" .= actId a,"sequence" .= actSequence a,"tokens" .= actTokens a
          ,"flipped" .= actFlipped a] | a <- map toAttrs $ Map.elems $ entitiesActs entities]
      ,"agendas" .= [object ["id" .= agendaId a,"sequence" .= agendaSequence a,"tokens" .= agendaTokens a
          ,"doom" .= agendaDoom a,"flipped" .= agendaFlipped a] | a <- map toAttrs $ Map.elems $ entitiesAgendas entities]]
    ,"own" .= [object ["investigatorId" .= investigatorId i
        ,"hand" .= zipWith cardView [0..] (investigatorHand i)
        ,"discard" .= zipWith cardView [0..] (map PlayerCard $ investigatorDiscard i)
        ,"deckCount" .= length (unDeck $ investigatorDeck i)] | i <- investigators, investigatorPlayerId i==recipient]
    ,"question" .= fmap (questionView targetView) (Map.lookup recipient $ gameQuestion game)
    -- Only the existing two bounded Specific schemas; never its arbitrary
    -- original Value. Unknown variants retain explicit unsupported metadata.
    ,"interactionSchema" .= interactionView game recipient
    ,"capabilities" .= object ["privateDeckOrder" .= False,"genericScenarioMetadata" .= False
      ,"costDetails" .= True,"costDetailsComplete" .= False,"arbitrarySpecific" .= False]]
 where
  entities=gameEntities game
  investigators=map toAttrs $ Map.elems $ entitiesInvestigators entities
  locations=filter (not . locationOutOfGame) $ map toAttrs $ Map.elems $ entitiesLocations entities
  enemies=filter (\e -> isInPlayPlacement (enemyPlacement e) && not (isHiddenPlacement $ enemyPlacement e)) $
    map toAttrs $ Map.elems $ entitiesEnemies entities
  assets=filter (\a -> assetVisible a && isInPlayPlacement (assetPlacement a)
    && not (isHiddenPlacement $ assetPlacement a)) $ map toAttrs $ Map.elems $ entitiesAssets entities
  investigatorView i = object
    ["id" .= investigatorId i,"playerId" .= investigatorPlayerId i,"name" .= display (toName i)
    ,"cardCode" .= investigatorCardCode i,"tokens" .= investigatorTokens i
    ,"placement" .= placementView (investigatorPlacement i),"remainingActions" .= investigatorRemainingActions i
    ,"handCount" .= length (investigatorHand i),"deckCount" .= length (unDeck $ investigatorDeck i)
    ,"discardCount" .= length (investigatorDiscard i),"defeated" .= investigatorDefeated i
    ,"resigned" .= investigatorResigned i]
  locationView l = object $
    ["id" .= locationId l,"revealed" .= locationRevealed l,"tokens" .= locationTokens l
    ,"position" .= locationPosition l]
    <> if locationRevealed l then ["cardCode" .= locationCardCode l,"label" .= locationLabel l
      ,"name" .= display (toName l)] else []
  enemyView e = object $
    ["id" .= enemyId e,"placement" .= placementView (enemyPlacement e)
    ,"tokens" .= enemyTokens e,"exhausted" .= enemyExhausted e,"flipped" .= enemyFlipped e]
    <> if enemyFlipped e then [] else ["cardCode" .= enemyCardCode e,"name" .= display (toName e)]
  assetView a = object $
    ["id" .= assetId a,"placement" .= placementView (assetPlacement a)
    ,"tokens" .= assetTokens a,"controller" .= assetController a,"flipped" .= assetFlipped a]
    <> if assetFlipped a then [] else ["cardCode" .= assetCardCode a,"name" .= display (toName a)]
  targetView = \case
    InvestigatorTarget ident | any ((==ident) . investigatorId) investigators -> ref "Investigator" ident
    LocationTarget ident | any ((==ident) . locationId) locations -> ref "Location" ident
    EnemyTarget ident | any ((==ident) . enemyId) enemies -> ref "Enemy" ident
    AssetTarget ident | any ((==ident) . assetId) assets -> ref "Asset" ident
    CardIdTarget ident | any ((==ident) . toCardId) ownCards || ident `elem` offeredFaces -> ref "Card" ident
    _ -> Null
  ownCards=concat [investigatorHand i <> map PlayerCard (investigatorDiscard i)
    | i <- investigators,investigatorPlayerId i==recipient]
