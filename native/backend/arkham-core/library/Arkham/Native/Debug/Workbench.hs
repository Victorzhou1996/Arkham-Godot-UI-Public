module Arkham.Native.Debug.Workbench where

import Arkham.Prelude
import Arkham.Game (Game (..))
import Arkham.Game.Utils (modeScenario)
import Arkham.GameEnv (runWithEnv)
import Arkham.Native.Env (CoreM)
import Arkham.Native.Debug.Actions qualified as Actions
import Arkham.Card
import Arkham.Classes.Entity (toAttrs)
import Arkham.Id
import Arkham.Message hiding (EvadeEnemy, EngageEnemy)
import Arkham.DamageEffect qualified as Damage
import Arkham.Exhaust (mkExhaustion)
import Arkham.Source
import Arkham.Target
import Arkham.Deck
import Arkham.Debug.CardDestination (cardDefCanEnterDeck)
import Arkham.Difficulty
import Arkham.Location.FloodLevel
import Arkham.Asset.Types (AssetAttrs (..))
import Arkham.Entities (Entities (..))
import Arkham.Matcher (CardMatcher (CardWithAvailableCustomization))
import Arkham.Enemy.Types (EnemyAttrs (..))
import Arkham.Scenario.Types (ScenarioAttrs (..))
import Arkham.ChaosBag.Base
import Arkham.ChaosToken.Types
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.Aeson.Key (Key)
import Control.Monad.Fail (fail)

-- Plans are private server values. Neither an arbitrary Message nor a Source,
-- effect window, queue, UUID or expression is part of the public command wire.
data Placement
  = IntoHand InvestigatorId | IntoPlay InvestigatorId
  | IntoCampaign InvestigatorId | IntoEncounter DeckSignifier
  deriving stock (Show, Eq)
data Plan = Messages [Message] | CreateAt CardDef Placement

data Command
  = SetReady Target Bool | TakeControl InvestigatorId AssetId
  | DiscardEntity Target | DefeatAsset AssetId | DefeatEnemyFor EnemyId InvestigatorId
  | EvadeEnemy EnemyId InvestigatorId | EngageEnemy EnemyId InvestigatorId
  | RevealLocationFor LocationId | ResetLocation LocationId
  | FlipEntity InvestigatorId Target | FloodLocation LocationId FloodLevel
  | SealToken ChaosTokenId Target | UnsealToken ChaosTokenId
  | EditBag Text ChaosTokenFace | ShuffleDiscard DeckSignifier
  | SetDifficulty Difficulty | Customize InvestigatorId CardId
  | Create CardCode Text (Maybe InvestigatorId) (Maybe DeckSignifier)
  | ClearEntityTokens Target | DealEnemyDamage EnemyId InvestigatorId Int

instance FromJSON Command where
  parseJSON = withObject "native debug workbench command" $ \o -> do
    operation <- o .: "operation" :: Aeson.Parser Text
    let exact = Actions.exact o
        canonical :: (FromJSON a, ToJSON a) => Key -> Aeson.Parser a
        canonical key = Actions.canonical =<< o .: key
    case operation of
      "setReady" -> exact ["operation","target","ready"] >> (SetReady <$> canonical "target" <*> o .: "ready")
      "clearEntityTokens" -> exact ["operation","target"] >> (ClearEntityTokens <$> canonical "target")
      "dealEnemyDamage" -> exact ["operation","enemyId","investigatorId","amount"] >> (DealEnemyDamage <$> canonical "enemyId" <*> canonical "investigatorId" <*> o .: "amount")
      "takeControl" -> exact ["operation","investigatorId","assetId"] >> (TakeControl <$> canonical "investigatorId" <*> canonical "assetId")
      "discardEntity" -> exact ["operation","target"] >> (DiscardEntity <$> canonical "target")
      "defeatAsset" -> exact ["operation","assetId"] >> (DefeatAsset <$> canonical "assetId")
      "defeatEnemy" -> exact ["operation","enemyId","investigatorId"] >> (DefeatEnemyFor <$> canonical "enemyId" <*> canonical "investigatorId")
      "evadeEnemy" -> exact ["operation","enemyId","investigatorId"] >> (EvadeEnemy <$> canonical "enemyId" <*> canonical "investigatorId")
      "engageEnemy" -> exact ["operation","enemyId","investigatorId"] >> (EngageEnemy <$> canonical "enemyId" <*> canonical "investigatorId")
      "revealLocation" -> exact ["operation","locationId"] >> (RevealLocationFor <$> canonical "locationId")
      "resetLocation" -> exact ["operation","locationId"] >> (ResetLocation <$> canonical "locationId")
      "flipEntity" -> exact ["operation","investigatorId","target"] >> (FlipEntity <$> canonical "investigatorId" <*> canonical "target")
      "setFloodLevel" -> exact ["operation","locationId","level"] >> (FloodLocation <$> canonical "locationId" <*> o .: "level")
      "sealToken" -> exact ["operation","tokenId","target"] >> (SealToken <$> canonical "tokenId" <*> canonical "target")
      "unsealToken" -> exact ["operation","tokenId"] >> (UnsealToken <$> canonical "tokenId")
      "editChaosBag" -> exact ["operation","action","face"] >> (EditBag <$> o .: "action" <*> o .: "face")
      "shuffleEncounterDiscard" -> exact ["operation","deck"] >> (ShuffleDiscard <$> canonical "deck")
      "setDifficulty" -> exact ["operation","difficulty"] >> (SetDifficulty <$> o .: "difficulty")
      "customizeCard" -> exact ["operation","investigatorId","cardId"] >> (Customize <$> canonical "investigatorId" <*> canonical "cardId")
      "createCard" -> do
        exact ["operation","cardCode","placement","investigatorId","deck"]
        Create <$> canonical "cardCode" <*> o .: "placement" <*> o .: "investigatorId" <*> o .: "deck"
      _ -> fail "Unsupported finite workbench command"

bagTokens :: Game -> [ChaosToken]
bagTokens game = maybe [] (chaosBagChaosTokens . scenarioChaosBag . toAttrs) $ modeScenario $ gameMode game

ordinaryFaces :: [ChaosTokenFace]
ordinaryFaces = [PlusOne,Zero,MinusOne,MinusTwo,MinusThree,MinusFour,MinusFive,MinusSix,MinusSeven,MinusEight,Skull,Cultist,Tablet,ElderThing,AutoFail,ElderSign,BlessToken,CurseToken]

sealedTokens :: Game -> [ChaosToken]
sealedTokens game = concatMap (assetSealedChaosTokens . toAttrs) (Map.elems (gameEntities game).assets)
  <> concatMap (enemySealedChaosTokens . toAttrs) (Map.elems (gameEntities game).enemies)

encounterDecks :: Game -> [DeckSignifier]
encounterDecks game = [d | d <- Actions.deckChoices game, case d of EncounterDeck -> True; EncounterDeckByKey {} -> True; _ -> False]

flipTargets :: Game -> [Target]
flipTargets game =
  [AssetTarget aid | (aid,a) <- Map.toList (gameEntities game).assets, twoSided $ toCardDef a]
  -- A normal location's two faces mean unrevealed/revealed, NOT a generic
  -- Flip handler. Use reveal/reset until a concrete special consumer is listed.
 where twoSided def = cdDoubleSided def || isJust (cdOtherSide def)

prepare :: Game -> PlayerId -> Value -> Either Text Plan
prepare game player raw = do
  Actions.stablePlayerWindow game player
  command <- first T.pack $ Aeson.parseEither parseJSON raw
  let entities = gameEntities game
      actor iid = unless (Map.member iid entities.investigators) $ Left "Unknown current investigator"
      asset aid = unless (Map.member aid entities.assets) $ Left "Unknown current asset"
      enemy eid = unless (Map.member eid entities.enemies) $ Left "Unknown current enemy"
      location lid = unless (Map.member lid $ entitiesLocations entities) $ Left "Unknown current location"
      presentTarget target = case target of
        AssetTarget aid -> asset aid
        EnemyTarget eid -> enemy eid
        EventTarget eid -> unless (Map.member eid $ entitiesEvents entities) $ Left "Unknown current event"
        TreacheryTarget tid -> unless (Map.member tid $ entitiesTreacheries entities) $ Left "Unknown current treachery"
        _ -> Left "Unsupported workbench entity target"
      readyTarget target = case target of AssetTarget aid -> asset aid; EnemyTarget eid -> enemy eid; _ -> Left "Only current assets and enemies can ready/exhaust"
      one message = pure $ Messages [message]
  case command of
    ClearEntityTokens target -> do
      case target of
        AssetTarget aid -> asset aid
        EnemyTarget eid -> enemy eid
        LocationTarget lid -> location lid
        _ -> Left "Unsupported clear-token entity"
      one $ ClearTokens target
    DealEnemyDamage eid iid amount -> do
      enemy eid
      actor iid
      unless (amount `elem` [1,5]) $ Left "Enemy damage accepts the offered one/five point buttons only"
      one $ DealDamage (EnemyTarget eid) $ Damage.isDirect $ Damage.nonAttack Nothing (InvestigatorSource iid) amount
    SetReady target ready -> readyTarget target >> one (if ready then Ready target else Exhaust $ mkExhaustion GameSource target)
    TakeControl iid aid -> actor iid >> asset aid >> one (TakeControlOfAsset iid aid)
    DiscardEntity target -> presentTarget target >> one (Discard Nothing GameSource target)
    DefeatAsset aid -> asset aid >> one (AssetDefeated GameSource aid)
    DefeatEnemyFor eid iid -> enemy eid >> actor iid >> one (DefeatEnemy eid iid $ InvestigatorSource iid)
    EvadeEnemy eid iid -> enemy eid >> actor iid >> one (EnemyEvaded iid eid)
    EngageEnemy eid iid -> enemy eid >> actor iid >> one (EnemyEngageInvestigator eid iid)
    RevealLocationFor lid -> location lid >> one (RevealLocation Nothing lid)
    ResetLocation lid -> location lid >> one (Reset $ LocationTarget lid)
    FlipEntity iid target -> do
      actor iid
      unless (target `elem` flipTargets game) $ Left "Target is not an offered two-sided entity"
      one $ Flip iid GameSource target
    FloodLocation lid level -> location lid >> one (SetFloodLevel lid level)
    SealToken tid target -> do
      readyTarget target
      unless (any ((==tid) . (.id)) $ bagTokens game) $ Left "Token is no longer in this bag"
      one $ DebugSealChaosToken tid target
    UnsealToken tid -> do
      token <- maybe (Left "Token is no longer sealed on an offered entity") Right $ find ((==tid) . (.id)) $ sealedTokens game
      one $ UnsealChaosToken token
    EditBag action face -> do
      unless (action `elem` ["add","remove"]) $ Left "Bag action must be add or remove"
      let faces = map (.face) $ bagTokens game
      unless (face `elem` (ordinaryFaces<>faces)) $ Left "Unknown bag face; custom faces must already exist in this bag"
      when (action=="remove" && face `notElem` faces) $ Left "Face is absent from this bag"
      unless (length faces<200 || action=="remove") $ Left "Local debug bag limit reached"
      one $ if action=="add" then AddChaosToken face else RemoveChaosToken face
    ShuffleDiscard deck -> do
      unless (deck `elem` encounterDecks game) $ Left "Encounter deck is not present"
      case deck of EncounterDeck -> one ShuffleEncounterDiscardBackIn; EncounterDeckByKey key -> one $ ShuffleEncounterDiscardBackInByKey key; _ -> Left "Unsupported encounter deck"
    SetDifficulty difficulty -> one $ SetScenarioDifficulty difficulty
    Customize iid cid -> do
      actor iid
      card <- maybe (Left "Unknown existing physical card") Right $ Map.lookup cid $ gameCards game
      unless (cardMatch card CardWithAvailableCustomization) $ Left "This card has no remaining customization options"
      unless (toCardOwner card==Just iid) $ Left "Customization requires the card's actual owner"
      one $ DebugCustomize iid cid
    Create code placement investigator deck -> do
      def <- maybe (Left "Unknown card in the current core catalog") Right $ lookupCardDef code
      let forActor constructor = do
            unless (isNothing deck) $ Left "This placement must not specify a deck"
            iid <- maybe (Left "This placement requires an investigator") Right investigator
            actor iid
            pure $ CreateAt def $ constructor iid
      case placement of
        "hand" -> do
          unless (cdCardType def `elem` playerCardTypes) $ Left "Only player cards can be created into a hand"
          forActor IntoHand
        "play" -> do
          unless (cdCardType def `elem` (playerCardTypes<>[LocationType,EnemyLocationCardType,EnemyType,TreacheryType,StoryType,EncounterAssetType,EncounterEventType])) $ Left "Card has no supported debug play path"
          forActor IntoPlay
        "campaignDeck" -> do
          unless (isJust (case gameMode game of These c _ -> Just c; _ -> Nothing) && cdCardType def `elem` playerCardTypes) $ Left "Campaign player-card placement requires an actual campaign"
          forActor IntoCampaign
        "encounterDeck" -> do
          unless (isNothing investigator) $ Left "Encounter placement must not specify an investigator"
          destination <- maybe (Left "Encounter placement requires an offered deck") Right deck
          unless (destination `elem` encounterDecks game && cardDefCanEnterDeck def destination) $ Left "Card cannot enter the offered encounter deck"
          pure $ CreateAt def $ IntoEncounter destination
        _ -> Left "Unknown card creation placement"

-- Use the existing GameT CardGen, sharing candidate refs and RNG. No UUID is
-- supplied by the UI and creation + placement form one native transaction.
materialize :: Plan -> CoreM [Message]
materialize (Messages messages) = pure messages
materialize (CreateAt def placement) = do
  card <- runWithEnv $ genCard def
  let cid = toCardId card
  pure [case placement of
    IntoHand iid -> DebugAddToHand iid cid
    IntoPlay iid -> DebugPlaceCard iid cid
    IntoCampaign iid -> DebugAddToCampaignDeck iid cid
    IntoEncounter deck -> DebugAddToEncounterDeck deck cid]

view :: Game -> PlayerId -> Value
view game player = object
  ["schemaVersion" .= (1::Int),"available" .= either (const False) (const True) availability
  ,"reason" .= either id (const "") availability
  ,"execution" .= ("original-rule-effects-not-direct-numeric-edit"::Text)
  ,"investigators" .= Map.keys entities.investigators
  ,"assets" .= Map.keys entities.assets,"enemies" .= Map.keys entities.enemies,"locations" .= Map.keys (entitiesLocations entities)
  ,"discardTargets" .= (map (toJSON . AssetTarget) (Map.keys entities.assets)<>map (toJSON . EnemyTarget) (Map.keys entities.enemies)<>map (toJSON . EventTarget) (Map.keys $ entitiesEvents entities)<>map (toJSON . TreacheryTarget) (Map.keys $ entitiesTreacheries entities))
  ,"readyTargets" .= (map (toJSON . AssetTarget) (Map.keys entities.assets)<>map (toJSON . EnemyTarget) (Map.keys entities.enemies))
  ,"flipTargets" .= flipTargets game,"bagTokens" .= bagTokens game,"sealedTokens" .= sealedTokens game
  ,"faces" .= nub (ordinaryFaces<>map (.face) (bagTokens game))
  ,"encounterDecks" .= encounterDecks game,"difficulties" .= [Easy,Standard,Hard,Expert]
  ,"floodLevels" .= [Unflooded,PartiallyFlooded,FullyFlooded]
  ,"customizableCards" .= [object ["cardId" .= cid,"cardCode" .= toCardCode card,"investigatorId" .= iid] | (cid,card)<-Map.toList $ gameCards game,cardMatch card CardWithAvailableCustomization,Just iid<-[toCardOwner card],Map.member iid entities.investigators]
  ,"operations" .= operationNames]
 where
  entities = gameEntities game
  availability = Actions.stablePlayerWindow game player

operationNames :: [Text]
operationNames = ["setReady","takeControl","discardEntity","defeatAsset","defeatEnemy","evadeEnemy","engageEnemy","revealLocation","resetLocation","flipEntity","setFloodLevel","sealToken","unsealToken","editChaosBag","shuffleEncounterDiscard","setDifficulty","customizeCard","createCard","clearEntityTokens","dealEnemyDamage"]
