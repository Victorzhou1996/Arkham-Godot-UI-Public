{-# LANGUAGE AllowAmbiguousTypes #-}

module Arkham.Native.Answer where

import Arkham.Prelude hiding (error)
import Relude (error)
import Control.Monad.Fail (fail)

import Arkham.Campaign.Option
import Arkham.Campaign.Types (Campaign (..), campaignLog, campaignOverlays, campaignUnavailableInvestigators)
import Arkham.Campaign.Overlay (CampaignOverlay (..))
import Arkham.Campaigns.TheScarletKeys.Key qualified as Scarlet
import Arkham.CampaignLog
import Arkham.CampaignLogKey
import Arkham.CampaignStep qualified as CS
import Arkham.Campaigns.EdgeOfTheEarth.Key
import Arkham.Campaigns.EdgeOfTheEarth.Partner
import Arkham.Campaigns.TheCircleUndone.Memento
import Arkham.Campaigns.TheInnsmouthConspiracy.Memory
import Arkham.Card
import Arkham.Card.Settings (AbilityTriggerMode)
import Arkham.Classes.Entity
import Arkham.Cost
import Arkham.Custom.Overlay (DeckOverlay (..), CampaignOverlayPlan (..), campaignOverlayPlan, decklistCustomCards)
import Arkham.Decklist
import Arkham.Helpers (Deck (..))
import Arkham.Entities
import Arkham.Game
import Arkham.Native.Settings.Types (SettingsFormRequest)
import Arkham.Native.History.Types (HistoricalDraft)
import Arkham.Game.State (isChooseDecks)
import Arkham.Helpers.Message (holdsPendingWindowEffects)
import Arkham.Id
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.Investigator.Cards qualified as InvestigatorCards
import Arkham.Message
import Arkham.PlayerCard (allPlayerCards)
import Arkham.Name (toTitle)
import Arkham.Scenario qualified as Scenario
import Arkham.SideStory.Definition
import Arkham.Source
import Arkham.Target
import Arkham.Token
import Arkham.Window qualified as Window
import Control.Exception qualified as E
import Data.Aeson
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.ByteString.Lazy qualified as BL
import Data.Either (isRight)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Text qualified as T
import Data.These
import Data.UUID (UUID)
import Data.UUID qualified as UUID
import Json

-- Host deck IDs are UUIDs. The host resolves them to DeckListAnswer before rules execute.
type ArkhamDeckId = UUID

data Answer
  = Answer QuestionResponse
  | {- | One answer that resolves a whole @ChooseOneAtATime@ in the order the
    player arranged it, rather than one card per round trip. Every choice is
    named exactly once, so the sequence runs in a single queue pass and lands as
    a single step: one undo takes the whole placement back. Answered card by
    card it left a step per card, and undoing into the middle of a placement
    stranded the rest of it.
    -}
    OrderedAnswer OrderedResponse
  | Raw Message
  | SetTriggerModeAnswer TriggerModePreference
  | AutoSkipTriggersAnswer Int
  | ResumeAutomaticTriggersAnswer AutomaticTriggersResume
  | PaymentAmountsAnswer PaymentAmountsResponse
  | AmountsAnswer AmountsResponse
  | StandaloneSettingsAnswer [StandaloneSetting]
  | CampaignSettingsAnswer CampaignSettings
  | SettingsFormAnswer SettingsFormRequest
  | HistoricalSettingsAnswer HistoricalDraft
  | DeckAnswer {deckId :: ArkhamDeckId, playerId :: PlayerId, overlay :: Maybe DeckOverlay}
  | DeckListAnswer {deckList :: ArkhamDBDecklist, playerId :: PlayerId}
  | UpgradeDeckAnswer UpgradeDeckRequest
  | ReplaceInvestigatorAnswer UpgradeDeckRequest
  | JoinLocalCampaignAnswer LocalJoinRequest
  | BeginUpgradeAnswer
  | BeginSideStoryAnswer SideStoryRequest
  | PickDestinyAnswer [DestinyDrawing]
  | CampaignSpecificAnswer Text Value
  | ScenarioSpecificAnswer Text Value
  | ExchangeAmountsAnswer
      { source :: Source
      , fromInvestigator :: InvestigatorId
      , toInvestigator :: InvestigatorId
      , token :: Token
      , amount :: Int
      }
  | CampaignStepAnswer CS.CampaignStep
  | CampaignStepAnswerFor PlayerId Bool CS.CampaignStep

  | -- Between-scenario roster changes, all answered from the continuation screen.
    RetireInvestigatorAnswer {investigatorId :: InvestigatorId}
  | RejoinInvestigatorAnswer {investigatorId :: InvestigatorId}
  | {- | Lay your own cards over an investigator's deck for the rest of the
    campaign. Answered from the same screen, and like the roster changes it
    leaves the continuation question standing to be answered after.
    -}
    ApplyOverlayAnswer {investigatorId :: InvestigatorId, overlay :: Maybe DeckOverlay}
  | JoinCampaignAnswer
  deriving stock (Show, Generic)
  deriving anyclass FromJSON

-- Explicit user resume after an Undo/import. The caller supplies neither a
-- choice index nor messages, a target seat list, or a saved eligibility flag.
data AutomaticTriggersResume = AutomaticTriggersResume
  deriving stock (Show, Generic)

instance FromJSON AutomaticTriggersResume where
  parseJSON = withObject "resume automatic triggers" \o -> do
    unless (KM.null o) $ fail "Automatic resume accepts no fields"
    pure AutomaticTriggersResume

-- Explicit target plus optional complete deck: Nothing means keep the current
-- deck, not re-load it and re-run purchase effects. No raw messages or URLs.
data UpgradeDeckRequest = UpgradeDeckRequest
  { upgradeTarget :: InvestigatorId
  , upgradeDeck :: Maybe ArkhamDBDecklist
  }
  deriving stock Show

instance FromJSON UpgradeDeckRequest where
  parseJSON = withObject "native upgrade request" \o -> do
    unless (sort (KM.keys o) == sort ["investigatorId","deck"]) $ fail "Unknown or missing upgrade field"
    raw <- o .: "investigatorId"
    target <- parseJSON raw
    unless (toJSON (target :: InvestigatorId) == raw) $ fail "Upgrade target must use an exact canonical identity"
    UpgradeDeckRequest target <$> o .: "deck"

newtype LocalJoinRequest = LocalJoinRequest {joinPlayer :: PlayerId}
  deriving stock Show

instance FromJSON LocalJoinRequest where
  parseJSON = withObject "native join request" \o -> do
    unless (KM.keys o == ["playerId"]) $ fail "Join accepts only a new local player identity"
    raw <- o .: "playerId"
    player <- parseJSON raw
    unless (toJSON (player :: PlayerId) == raw) $ fail "Join player identity must be canonical"
    pure $ LocalJoinRequest player

-- Only a registered exact identity crosses the boundary. Return steps, costs,
-- continuation flags and messages are always computed from the actual game.
newtype SideStoryRequest = SideStoryRequest {sideStoryScenario :: ScenarioId}
  deriving stock Show

instance FromJSON SideStoryRequest where
  parseJSON = withObject "native side-story request" \o -> do
    unless (KM.keys o == ["scenarioId"]) $ fail "Side-story selection accepts only scenarioId"
    raw <- o .: "scenarioId"
    sid <- parseJSON raw
    unless (toJSON (sid :: ScenarioId) == raw) $ fail "Side-story identity must be canonical"
    pure $ SideStoryRequest sid

-- One finite preference, never an arbitrary setting map or rule message.
data TriggerModePreference = TriggerModePreference
  { triggerInvestigator :: InvestigatorId
  , triggerCardCode :: CardCode
  , triggerAbilityIndex :: Int
  , triggerMode :: AbilityTriggerMode
  }
  deriving stock Show

instance FromJSON TriggerModePreference where
  parseJSON = withObject "native trigger preference" \o -> do
    unless (sort (KM.keys o) == sort ["investigatorId","cardCode","abilityIndex","mode"]) $ fail "Unknown or missing trigger preference field"
    rawInvestigator <- o .: "investigatorId"
    rawCard <- o .: "cardCode"
    investigator <- parseJSON rawInvestigator
    card <- parseJSON rawCard
    unless (toJSON (investigator :: InvestigatorId) == rawInvestigator && toJSON (card :: CardCode) == rawCard) $ fail "Trigger identities must use exact canonical wire values"
    TriggerModePreference investigator card <$> o .: "abilityIndex" <*> o .: "mode"

data QuestionResponse = QuestionResponse
  { qrChoice :: Int
  , qrPlayerId :: Maybe PlayerId
  , qrQuestionVersion :: Maybe Int
  }
  deriving stock (Show, Generic)

data OrderedResponse = OrderedResponse
  { orChoices :: [Int]
  -- ^ Indices into the question as it was asked, in the order to resolve them.
  , orPlayerId :: Maybe PlayerId
  , orQuestionVersion :: Maybe Int
  }
  deriving stock (Show, Generic)

data PaymentAmountsResponse = PaymentAmountsResponse
  { parAmounts :: Map UUID Int
  , parQuestionVersion :: Maybe Int
  , parPlayerId :: Maybe PlayerId
  }
  deriving stock (Show, Generic)

data AmountsResponse = AmountsResponse
  { arAmounts :: Map UUID Int
  , arQuestionVersion :: Maybe Int
  , arPlayerId :: Maybe PlayerId
  }
  deriving stock (Show, Generic)

data PartnerDetailsResponse = PartnerDetailsResponse
  { damage :: Int
  , horror :: Int
  , status :: PartnerStatus
  }
  deriving stock (Show, Generic)
  deriving anyclass FromJSON

instance FromJSON QuestionResponse where
  parseJSON = withObject "QuestionResponse" \o -> do
    qrChoice <- o .: "choice"
    qrPlayerId <- o .:? "playerId"
    qrQuestionVersion <- o .:? "questionVersion"
    pure QuestionResponse {..}

instance FromJSON OrderedResponse where
  parseJSON = withObject "OrderedResponse" \o -> do
    orChoices <- o .: "choices"
    orPlayerId <- o .:? "playerId"
    orQuestionVersion <- o .:? "questionVersion"
    pure OrderedResponse {..}

instance FromJSON PaymentAmountsResponse where
  parseJSON = withObject "PaymentAmountsResponse" \o -> do
    parAmounts <- o .: "amounts"
    parQuestionVersion <- o .:? "questionVersion"
    parPlayerId <- o .:? "playerId"
    pure PaymentAmountsResponse {..}

instance FromJSON AmountsResponse where
  parseJSON = withObject "AmountsResponse" \o -> do
    arAmounts <- o .: "amounts"
    arQuestionVersion <- o .:? "questionVersion"
    arPlayerId <- o .:? "playerId"
    pure AmountsResponse {..}

data StandaloneSetting
  = SetKey CampaignLogKey Bool
  | SetRecorded CampaignLogKey SomeRecordableType [SetRecordedEntry]
  | SetOption CampaignOption Bool
  | ChooseNum CampaignLogKey Int
  | NoChooseRecord
  | StandaloneSetPartnerStatus Int Int PartnerStatus Bool CardCode
  | SettingsGroup [StandaloneSetting]
  deriving stock Show

data SetRecordedEntry
  = SetAsCrossedOut Json.Value
  | SetAsRecorded Json.Value
  | DoNotRecord Json.Value
  deriving stock Show

makeStandaloneCampaignLog :: [StandaloneSetting] -> CampaignLog
makeStandaloneCampaignLog = foldl' applySetting mkCampaignLog
 where
  applySetting :: CampaignLog -> StandaloneSetting -> CampaignLog
  applySetting cl NoChooseRecord = cl
  applySetting cl (ChooseNum k n) = setCampaignLogRecordedCount k n cl
  applySetting cl (SetKey k True) = setCampaignLogKey k cl
  applySetting cl (SetKey k False) = deleteCampaignLogKey k cl
  applySetting cl (SetOption k True) = setCampaignLogOption k cl
  applySetting cl (SetOption _ False) = cl
  applySetting cl (StandaloneSetPartnerStatus dmg hrr status crash cCode) =
    if crash
      then
        setCampaignLogRecorded (EdgeOfTheEarthKey WasKilledInThePlaneCrash) [recorded cCode]
          $ setCampaignLogPartnerStatus dmg hrr status cCode cl
      else setCampaignLogPartnerStatus dmg hrr status cCode cl
  applySetting c1 (SettingsGroup xs) = foldl' applySetting c1 xs
  applySetting cl (SetRecorded k rt vs) = case rt of
    (SomeRecordableType RecordableCardCode) ->
      let entries = mapMaybe (toEntry @CardCode) vs
       in setCampaignLogRecorded k entries cl
    (SomeRecordableType RecordableMemento) ->
      let entries = mapMaybe (toEntry @Memento) vs
       in setCampaignLogRecorded k entries cl
    (SomeRecordableType RecordableMemory) ->
      let entries = mapMaybe (toEntry @Memory) vs
       in setCampaignLogRecorded k entries cl
    (SomeRecordableType RecordableGeneric) ->
      let entries = mapMaybe (toEntry @Value) vs
       in setCampaignLogRecorded k entries cl
  toEntry :: forall a. Recordable a => SetRecordedEntry -> Maybe SomeRecorded
  toEntry (SetAsRecorded e) = case fromJSON @a e of
    Success a -> Just (recorded a)
    Error err -> error $ "Failed to parse " <> tshow e <> ": " <> T.pack err
  toEntry (SetAsCrossedOut e) = case fromJSON @a e of
    Success a -> Just (crossedOut a)
    Error err -> error $ "Failed to parse " <> tshow e <> ": " <> T.pack err
  toEntry (DoNotRecord _) = Nothing

instance FromJSON StandaloneSetting where
  parseJSON = withObject "StandaloneSetting" $ \o -> do
    t <- o .: "type"
    case t of
      "ChooseRecord" -> do
        mSelected <- o .: "selected"
        case mSelected of
          Nothing -> pure NoChooseRecord
          Just selected -> SetRecorded selected <$> o .: "recordable" <*> ((: []) . SetAsRecorded <$> o .: "key")
      "ToggleKey" -> SetKey <$> o .: "key" <*> o .: "content"
      "ToggleOption" -> do
        keyVal <- o .: "key"
        key <- case keyVal of
          String s -> parseJSON (object ["tag" .= String s])
          _ -> parseJSON keyVal
        SetOption key <$> o .: "content"
      "PickKey" -> (`SetKey` True) <$> o .: "content"
      "ToggleCrossedOut" -> do
        k <- o .: "key"
        rt <- o .: "recordable"
        CrossedOutResults v <- o .: "content"
        pure $ SetRecorded k rt v
      "ToggleRecords" -> SetRecorded <$> o .: "key" <*> o .: "recordable" <*> o .: "content"
      "ChooseNum" -> ChooseNum <$> o .: "key" <*> o .: "content"
      "Group" -> SettingsGroup <$> o .: "content"
      "SetPartnerKilled" -> StandaloneSetPartnerStatus 0 0 Eliminated True <$> o .: "content"
      "SetPartnerDetails" -> do
        details :: PartnerDetailsResponse <- o .: "content"
        cCode <- o .: "value"
        pure
          $ StandaloneSetPartnerStatus details.damage details.horror details.status False
          $ if details.status == Resolute then toResolute cCode else cCode
      _ -> fail $ "No such standalone setting " <> t

instance FromJSON SetRecordedEntry where
  parseJSON = withObject "SetRecordedEntry" $ \o -> do
    k <- o .: "key"
    v <- o .: "content"
    pure $ if v then SetAsRecorded k else DoNotRecord k

newtype CrossedOutResults = CrossedOutResults [SetRecordedEntry]
  deriving stock Show

instance FromJSON CrossedOutResults where
  parseJSON jdata = do
    xs <- parseJSON jdata
    let
      toCrossedOutVersion = \case
        DoNotRecord k -> SetAsRecorded k
        SetAsCrossedOut a -> SetAsCrossedOut a
        SetAsRecorded a -> SetAsCrossedOut a
    pure $ CrossedOutResults $ map toCrossedOutVersion xs

data CampaignRecorded = CampaignRecorded
  { recordable :: SomeRecordableType
  , entries :: [CampaignRecordedEntry]
  }
  deriving stock Show

data CampaignRecordedEntry
  = CampaignEntryRecorded Json.Value
  | CampaignEntryCrossedOut Json.Value
  deriving stock Show

instance FromJSON CampaignRecordedEntry where
  parseJSON = withObject "CampaignRecordedEntry" $ \o -> do
    t :: Text <- o .: "tag"
    case t of
      "CrossedOut" -> CampaignEntryCrossedOut <$> o .: "value"
      "Recorded" -> CampaignEntryRecorded <$> o .: "value"
      _ -> fail $ "Invalid key" <> T.unpack t

data CampaignSettings = CampaignSettings
  { keys :: [CampaignLogKey]
  , counts :: Map CampaignLogKey Int
  , sets :: Map CampaignLogKey CampaignRecorded
  , options :: [CampaignOption]
  }
  deriving stock Show

instance FromJSON CampaignSettings where
  parseJSON = withObject "CampaignSettings" $ \o -> do
    options <- o .: "options" >>= traverse parseOption
    CampaignSettings
      <$> (o .: "keys")
      <*> (o .: "counts")
      <*> (o .: "sets")
      <*> pure options
   where
    parseOption = \case
      String s -> parseJSON (object ["tag" .= String s])
      v -> parseJSON v

instance FromJSON CampaignRecorded where
  parseJSON = withObject "CampaignRecorded" $ \o ->
    CampaignRecorded
      <$> (o .: "recordable")
      <*> (o .: "entries")

makeCampaignLog :: CampaignSettings -> CampaignLog
makeCampaignLog settings =
  mkCampaignLog
    { campaignLogRecorded = Set.fromList settings.keys
    , campaignLogRecordedCounts = counts settings
    , campaignLogRecordedSets = fmap toSomeRecorded $ sets settings
    , campaignLogOrderedKeys = settings.keys
    , campaignLogOptions = Set.fromList (options settings)
    }
 where
  toSomeRecorded :: CampaignRecorded -> [SomeRecorded]
  toSomeRecorded (CampaignRecorded rt entries) =
    case rt of
      (SomeRecordableType RecordableCardCode) -> map (toEntry @CardCode) entries
      (SomeRecordableType RecordableMemento) -> map (toEntry @Memento) entries
      (SomeRecordableType RecordableMemory) -> map (toEntry @Memory) entries
      (SomeRecordableType RecordableGeneric) -> map (toEntry @Value) entries
  toEntry :: forall a. Recordable a => CampaignRecordedEntry -> SomeRecorded
  toEntry (CampaignEntryRecorded e) = case fromJSON @a e of
    Success a -> recorded a
    Error err -> error $ "Failed to parse " <> tshow e <> ": " <> T.pack err
  toEntry (CampaignEntryCrossedOut e) = case fromJSON @a e of
    Success a -> crossedOut a
    Error err -> error $ "Failed to parse " <> tshow e <> ": " <> T.pack err

answerPlayer :: Answer -> Maybe PlayerId
answerPlayer = \case
  Answer response -> qrPlayerId response
  OrderedAnswer response -> orPlayerId response
  Raw _ -> Nothing
  SetTriggerModeAnswer _ -> Nothing
  AutoSkipTriggersAnswer _ -> Nothing
  ResumeAutomaticTriggersAnswer _ -> Nothing
  AmountsAnswer response -> arPlayerId response
  PaymentAmountsAnswer response -> parPlayerId response
  StandaloneSettingsAnswer _ -> Nothing
  CampaignSettingsAnswer _ -> Nothing
  SettingsFormAnswer _ -> Nothing
  HistoricalSettingsAnswer _ -> Nothing
  CampaignSpecificAnswer {} -> Nothing
  ScenarioSpecificAnswer {} -> Nothing
  DeckAnswer _ pid _ -> Just pid
  DeckListAnswer _ pid -> Just pid
  UpgradeDeckAnswer _ -> Nothing
  ReplaceInvestigatorAnswer _ -> Nothing
  JoinLocalCampaignAnswer _ -> Nothing
  BeginUpgradeAnswer -> Nothing
  BeginSideStoryAnswer _ -> Nothing
  PickDestinyAnswer _ -> Nothing
  ExchangeAmountsAnswer {} -> Nothing
  CampaignStepAnswer _ -> Nothing
  CampaignStepAnswerFor pid _ _ -> Just pid

  ApplyOverlayAnswer {} -> Nothing
  RetireInvestigatorAnswer _ -> Nothing
  RejoinInvestigatorAnswer _ -> Nothing
  JoinCampaignAnswer -> Nothing

playerInvestigator :: Entities -> PlayerId -> InvestigatorId
playerInvestigator Entities {..} pid = case find ((== pid) . attr investigatorPlayerId) (toList entitiesInvestigators) of
  Just investigator -> toId investigator
  Nothing -> error $ "No investigator for player " <> tshow pid

data Reply = Handled [Message] | Unhandled Text

handled :: Applicative m => [Message] -> m Reply
handled = pure . Handled

unhandled :: Applicative m => Text -> m Reply
unhandled = pure . Unhandled

-- | A scenario is played by one to four investigators.
maxInvestigators :: Int
maxInvestigators = 4

unwrapQuestion :: Question Message -> Question Message
unwrapQuestion = \case
  QuestionLabel _ _ q -> unwrapQuestion q
  PayCostQuestion _ q -> unwrapQuestion q
  QuestionWithSource _ _ q -> unwrapQuestion q
  q -> q

handleCampaignStepAnswer :: GameMode -> CS.CampaignStep -> IO Reply
handleCampaignStepAnswer gameMode k = case gameMode of
  This c -> case c.step of
    CS.ContinueCampaignStep {} -> handled [NextCampaignStep (Just k)]
    CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep {}) -> handled [NextCampaignStep (Just k)]
    _ -> handled []
  These c s -> case s.step of
    Just (CS.ContinueCampaignStep {}) -> handled [NextScenarioCampaignStep (Just k)]
    Just (CS.ScenarioStepWithOptions {}) -> handled [ScenarioCampaignStep k.normalize]
    _ -> case c.step of
      CS.ContinueCampaignStep {} -> handled [NextCampaignStep (Just k)]
      CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep {}) -> handled [NextCampaignStep (Just k)]
      _ -> handled []
  That s -> case s.step of
    Just (CS.ContinueCampaignStep {}) -> handled [NextScenarioCampaignStep (Just k)]
    _ -> handled []

{- | Is the table sitting on the /campaign's/ continuation screen?

A scenario can raise a continuation of its own mid-scenario (Fortune and Folly's
checkpoint), which looks identical from the question alone. Players only join or
leave between scenarios, so those answers must not be accepted there.
-}
atCampaignContinuation :: Game -> Bool
atCampaignContinuation g = case gameMode g of
  That _ -> False
  This c -> isContinuation c.step
  These c s -> case s.step of
    Just (CS.ContinueCampaignStep {}) -> False
    Just (CS.ScenarioStepWithOptions {}) -> False
    _ -> isContinuation c.step
 where
  isContinuation = \case
    CS.ContinueCampaignStep {} -> True
    CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep {}) -> True
    CS.StandaloneScenarioStepWithOptions _ (CS.ContinueCampaignStep {}) _ -> True
    _ -> False

isContinueCampaignAsk :: Game -> PlayerId -> Bool
isContinueCampaignAsk g pid = case unwrapQuestion <$> Map.lookup pid (gameQuestion g) of
  Just ContinueCampaign -> True
  _ -> False

{- | Why this seat may not play @iid@, if it may not.

Only a seat joining mid-campaign is restricted: the rules let a new player pick
only an investigator nobody has used during this campaign.
-}
joinDeckRejection :: Game -> PlayerId -> InvestigatorId -> Maybe Text
joinDeckRejection g pid iid = case unwrapQuestion <$> Map.lookup pid (gameQuestion g) of
  Just (ChooseJoinDeck used) | any (sameInvestigatorIdentity iid) (nub $ used <> usedCampaignInvestigators g) -> Just "That investigator has already played in this campaign"
  _ -> Nothing

usedCampaignInvestigators :: Game -> [InvestigatorId]
usedCampaignInvestigators game = case gameMode game of
  This campaign -> campaignUnavailableInvestigators (toAttrs campaign) present retired
  These campaign _ -> campaignUnavailableInvestigators (toAttrs campaign) present retired
  That _ -> nub $ present <> retired
 where
  present=Map.keys (gameEntities game).investigators
  retired=Map.keys $ gameRetiredInvestigators game

{- | The messages that start this seat's deck-setup sub-flow.

Inside a multi-seat barrier the sub-flow is self-contained and ends in
'SeatResolved', which drops this seat's slot, re-parks the seats still waiting
(each from its own durable slot) and, once every seat has resolved, runs the
barrier's continuation. The barrier owns re-parking, so this must NOT re-push an
'AskMap': that would park the remaining seats /ahead/ of the rest of THIS seat's
sub-flow and strand its tail behind them.

Outside a barrier the old re-push is kept: 'ChooseUpgradeDeck' (migrated in phase
2) and The Dream Eaters' hand-rolled sequential deck prompts still use it.
-}
deckChosen :: Game -> PlayerId -> ArkhamDBDecklist -> [Message]
deckChosen game playerId dl = completeDeckWork game playerId [LoadDecklist playerId dl]

-- The seat completion wrapper is shared by initial/join setup and upgrades.
-- A barrier owns re-parking; outside it keep the original retained AskMap.
completeDeckWork :: Game -> PlayerId -> [Message] -> [Message]
completeDeckWork game playerId messages = messages <> case barrierSeat playerId game of
  Just (bid, _) -> [SeatResolved bid playerId]
  Nothing -> reAskOthers game playerId

-- A real upgrade can occur during scenario setup/resolution as well as between
-- scenarios. Its own question and IsChooseDecks, not guessed campaign steps,
-- are authoritative. Exactly one current investigator must belong to this seat.
upgradeInvestigator :: Game -> PlayerId -> Either Text InvestigatorId
upgradeInvestigator game player = do
  unless (player `elem` gamePlayers game) $ Left "Unknown player"
  unless ((unwrapQuestion <$> Map.lookup player (gameQuestion game)) == Just ChooseUpgradeDeck) $ Left "No upgrade is offered to this seat"
  unless (isChooseDecks $ gameGameState game) $ Left "Game is not selecting decks"
  iid <- case Map.keys $ Map.filter ((== player) . attr investigatorPlayerId) (gameEntities game).investigators of
    [target] -> Right target
    _ -> Left "Upgrade requires exactly one investigator owned by this seat"
  _ <- maybe (Left "Upgrade requires the existing campaign deck") Right $ campaignDeck game iid
  pure iid

campaignDeck :: Game -> InvestigatorId -> Maybe (Deck PlayerCard)
campaignDeck game iid = case gameMode game of
  This campaign -> Map.lookup iid campaign.decks
  These campaign _ -> Map.lookup iid campaign.decks
  That _ -> Nothing

-- Preserve the original API's alternate-art equivalence, not display-name or
-- prefix heuristics. Other investigators require the separate replacement flow.
sameDeckInvestigator :: InvestigatorId -> ArkhamDBDecklist -> Bool
sameDeckInvestigator iid deck = sameInvestigatorIdentity iid deck.investigator

sameInvestigatorIdentity :: InvestigatorId -> InvestigatorId -> Bool
sameInvestigatorIdentity iid submitted = toJSON iid == toJSON submitted
  || maybe False ((toCardCode iid `elem`) . (.cardCodes))
    (Map.lookup (toCardCode submitted) $ InvestigatorCards.allInvestigatorCards <> InvestigatorCards.allEncounterInvestigatorCards)

validateDeckIdentity :: Game -> PlayerId -> ArkhamDBDecklist -> Either Text ()
validateDeckIdentity game player deck = do
  let code=toCardCode deck.investigator
      known=Map.member code (InvestigatorCards.allInvestigatorCards <> InvestigatorCards.allEncounterInvestigatorCards)
        || maybe False ((== InvestigatorType) . cdCardType) (lookupCustomCardDef code)
      occupied=Map.keys $ Map.filter ((/= player) . attr investigatorPlayerId) (gameEntities game).investigators
  unless known $ Left "Unknown investigator definition"
  when (any (`sameDeckInvestigator` deck) occupied) $ Left "That investigator is already in another active seat"
  when (any (`sameDeckInvestigator` deck) $ Map.keys $ gameRetiredInvestigators game) $ Left "That investigator is retired; use the explicit rejoin operation"

validateLocalDeckSelection :: Game -> PlayerId -> ArkhamDBDecklist -> Either Text ()
validateLocalDeckSelection game player deck = do
  case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
    Just ChooseDeck -> pure ()
    Just ChooseJoinDeck {} -> pure ()
    _ -> Left "No initial or joining deck is offered"
  validateLocalDeckInput deck
  validateDeckIdentity game player deck
  maybe (Right ()) Left $ joinDeckRejection game player deck.investigator

-- Resource bounds / resolvable card identities, NOT full deck-building or XP
-- budget validation. The existing UpgradeDeck rules still own purchase effects.
validateLocalDeckInput :: ArkhamDBDecklist -> Either Text ()
validateLocalDeckInput deck = do
  unless (BL.length (encode deck) <= 1048576) $ Left "Local deck input is too large"
  for_ [(False,deck.slots),(True,deck.sideSlots)] \(side,rows) -> do
    unless (Map.size rows <= 500 && all (\n -> n >= 0 && n <= 100) (Map.elems rows)
      && sum (map toInteger $ Map.elems rows) <= 2000) $ Left "Local deck counts exceed supported bounds"
    for_ (Map.keys rows) \code ->
      unless (knownPlayer code || side && Map.member code InvestigatorCards.allInvestigatorCards) $ Left $ "Unknown local player card: " <> tshow code
  for_ deck.meta \raw -> do
    unless (T.length raw <= 65536) $ Left "Deck metadata is too large"
    value <- first T.pack $ eitherDecode $ encodeUtf8 $ fromStrict raw
    extra <- first T.pack $ Aeson.parseEither (withObject "Deck metadata" (.:? "extra_deck")) value
    for_ extra \codes -> do
      let entries = T.splitOn "," codes
      unless (length entries <= 500 && all (not . T.null) entries) $ Left "Extra deck has empty or excessive entries"
      for_ entries \entry -> do
        let code = CardCode entry
        unless (knownPlayer code || Map.member code InvestigatorCards.allInvestigatorCards) $ Left "Unknown extra-deck card"
 where
  knownPlayer code = Map.member code allPlayerCards
    || maybe False ((`elem` playerCardTypes) . cdCardType) (lookupCustomCardDef code)

validateUpgradeRequest :: Game -> PlayerId -> UpgradeDeckRequest -> Either Text InvestigatorId
validateUpgradeRequest game player UpgradeDeckRequest {..} = do
  iid <- upgradeInvestigator game player
  unless (toJSON upgradeTarget == toJSON iid) $ Left "Upgrade target does not belong to the current seat"
  for_ upgradeDeck \deck -> do
    unless (sameDeckInvestigator iid deck) $ Left "Use the explicit replacement operation to change investigators"
    validateLocalDeckInput deck
  pure iid

validateReplacementRequest :: Game -> PlayerId -> UpgradeDeckRequest -> Either Text InvestigatorId
validateReplacementRequest game player UpgradeDeckRequest {..} = do
  iid <- upgradeInvestigator game player
  unless (toJSON upgradeTarget == toJSON iid) $ Left "Replacement target does not belong to this seat"
  deck <- maybe (Left "Replacement requires a complete deck") Right upgradeDeck
  when (sameDeckInvestigator iid deck) $ Left "The same investigator must use upgrade, not replacement"
  validateLocalDeckInput deck
  validateDeckIdentity game player deck
  when (any (`sameDeckInvestigator` deck) $ usedCampaignInvestigators game) $ Left "That investigator has already been used in this campaign"
  pure iid

-- Roster mutations belong to the actual campaign continuation recipient, not
-- whichever seat happens to be displayed. No scenario-local continuation.
validateRosterManager :: Game -> PlayerId -> Either Text ()
validateRosterManager game player = do
  unless (player `elem` gamePlayers game && isContinueCampaignAsk game player) $ Left "Only the current campaign continuation recipient manages the team"
  unless (atCampaignContinuation game) $ Left "Team management is only offered between campaign scenarios"
  unless (null $ filter (/= player) $ Map.keys $ gameQuestion game) $ Left "Finish the other outstanding questions first"

hasRosterCapacity :: Game -> Bool
hasRosterCapacity game = all (< maxInvestigators) [length $ gamePlayers game,gamePlayerCount game,Map.size (gameEntities game).investigators]

-- A finite entry into the author's upgrade workflow. The UI provides no step;
-- after upgrading, the exact complete continuation is restored, not started.
offeredUpgrade :: Game -> PlayerId -> Either Text CS.Continuation
offeredUpgrade game player = do
  validateRosterManager game player
  campaign <- case gameMode game of
    This c -> Right c
    These c _ -> Right c -- validateRosterManager already excludes scenario-first routes
    That _ -> Left "Standalone scenarios have no campaign upgrade route"
  continuation <- case campaign.step of
    CS.ContinueCampaignStep cont -> Right cont
    CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep cont) -> Right cont
    CS.StandaloneScenarioStepWithOptions _ (CS.ContinueCampaignStep cont) _ -> Right cont
    _ -> Left "No campaign upgrade continuation"
  unless continuation.canUpgradeDecks $ Left "This continuation does not allow upgrading"
  let played=any (isJust . (.scenario)) campaign.completedSteps
  case continuation.nextStep of
    CS.CampaignSpecificStep {} -> pure ()
    CS.ScenarioStep {} -> unless played $ Left "Manual upgrading is not offered before the first scenario"
    CS.ScenarioStepWithOptions {} -> unless played $ Left "Manual upgrading is not offered before the first scenario"
    CS.StandaloneScenarioStep {} -> unless played $ Left "Manual upgrading is not offered before the first scenario"
    CS.StandaloneScenarioStepWithOptions {} -> unless played $ Left "Manual upgrading is not offered before the first scenario"
    _ -> Left "This campaign step offers no manual upgrade"
  pure continuation

-- The same finite admission logic drives UI quotes, selection validation and
-- the second Continue immediately before the real runner charges the group.
campaignSideStoryContinuation :: Game -> PlayerId -> Either Text (Campaign,CS.Continuation)
campaignSideStoryContinuation game player = do
  validateRosterManager game player
  campaign <- case gameMode game of
    This c -> Right c
    These c _ -> Right c
    That _ -> Left "Standalone scenarios have no campaign side-story route"
  continuation <- case campaign.step of
    CS.ContinueCampaignStep cont -> Right cont
    CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep cont) -> Right cont
    CS.StandaloneScenarioStepWithOptions _ (CS.ContinueCampaignStep cont) _ -> Right cont
    _ -> Left "No campaign side-story continuation"
  pure (campaign,continuation)

data SideStoryQuote = SideStoryQuote
  { quoteContinuation :: CS.Continuation
  , quoteUsesTime :: Bool
  , quoteCost :: Int
  , quoteRemainingTime :: Maybe Integer
  , quoteOverlay :: Maybe Text
  , quotePayments :: [(InvestigatorId,PlayerId,Int,Int)]
  }

sideStoryQuote :: Game -> PlayerId -> Bool -> ScenarioId -> Either Text SideStoryQuote
sideStoryQuote game player selecting sid = do
  (campaign,continuation) <- campaignSideStoryContinuation game player
  when (selecting && not continuation.canChooseSideStory) $ Left "This continuation cannot insert another side story"
  definition <- maybe (Left "Unknown side story") Right $ sideStoryDefinition sid
  unless (Map.member (unScenarioId sid) Scenario.allScenarios) $ Left "Side story has no registered scenario implementation"
  let completed = mapMaybe (\case
        CS.StandaloneScenarioStep s _ -> Just s
        CS.StandaloneScenarioStepWithOptions s _ _ -> Just s
        _ -> Nothing) campaign.completedSteps
  when (sid `elem` completed) $ Left "This side story has already been completed"
  when (any (`elem` completed) $ sideStoryNotAfter definition) $ Left "The later part of this side story has already been completed"
  let actors = Map.toList (gameEntities game).investigators
      signatures = [iid | (iid,actor) <- actors
        , Just (toTitle actor) == sideStoryInvestigator definition
        , let attrs = toAttrs actor
        , not $ or [investigatorKilled attrs,investigatorDrivenInsane attrs,investigatorResigned attrs,investigatorDefeated attrs,investigatorEliminated attrs]]
      hasParallel = any (hasSideStoryParallelContent . toCardCode . snd) actors
      usesTime = campaign.id == CampaignId "09"
      overlays = case campaign of Campaign c -> campaignOverlays c
      overlay = find (\o -> o.available && o.scenario == sid) overlays
      cost = if usesTime then sideStoryCost definition else maybe (sideStoryCost definition) (.xpCost) overlay
      clock = Map.lookup (TheScarletKeysKey Scarlet.Time) $ campaignLogRecordedCounts $ campaignLog $ toAttrs campaign
  when (sideStoryParallelContent definition && not hasParallel) $ Left "This side story requires an investigator with parallel content"
  distributed <- sideStoryPayments definition cost signatures (map fst actors)
  let payments = [(iid,investigatorPlayerId attrs,charge,investigatorXp attrs)
        | (iid,charge) <- distributed, Just actor <- [Map.lookup iid (gameEntities game).investigators], let attrs = toAttrs actor]
  remaining <- if usesTime
    then do
      elapsed <- maybe (Left "Scarlet side-story entry requires its actual campaign clock") (Right . toInteger) clock
      let available = 35 - elapsed
      when (elapsed < 0 || toInteger cost > available) $ Left "Insufficient campaign time for this side story"
      pure $ Just available
    else do
      when (any (\(_,_,charge,xp) -> xp < charge) payments) $ Left "An investigator cannot afford the side-story experience cost"
      pure Nothing
  pure $ SideStoryQuote continuation usesTime cost remaining
    (if usesTime then Nothing else (.name) <$> overlay) payments

offeredSideStory :: Game -> PlayerId -> SideStoryRequest -> Either Text CS.CampaignStep
offeredSideStory game player (SideStoryRequest sid) = do
  quote <- sideStoryQuote game player True sid
  let original = quoteContinuation quote
      returning = CS.ContinueCampaignStep $ original {CS.chooseSideStory = False,CS.canChooseSideStory = False}
      next = CS.StandaloneScenarioStep sid returning
  pure $ CS.ContinueCampaignStep $ original
    { CS.nextStep = next, CS.canUpgradeDecks = True, CS.chooseSideStory = False, CS.canChooseSideStory = False }

validateSideStoryEntry :: Game -> PlayerId -> CS.CampaignStep -> Either Text ()
validateSideStoryEntry game player = \case
  CS.StandaloneScenarioStep sid _ -> void $ sideStoryQuote game player False sid
  CS.StandaloneScenarioStepWithOptions sid _ _ -> void $ sideStoryQuote game player False sid
  _ -> case campaignSideStoryContinuation game player of
    Right (_,continuation) | continuation.chooseSideStory -> Left "This campaign continuation requires choosing a side story"
    _ -> pure ()

sideStoryView :: Game -> PlayerId -> Value
sideStoryView game player = case campaignSideStoryContinuation game player of
  Left _ -> Null
  Right (_,continuation) -> object
    [ "schemaVersion" .= (1 :: Int), "canChoose" .= continuation.canChooseSideStory
    , "mustChoose" .= continuation.chooseSideStory, "answerTag" .= ("BeginSideStoryAnswer" :: Text)
    , "entries" .= [entry definition | definition <- sideStoryDefinitions]
    , "pendingEntry" .= (case continuation.nextStep of
        CS.StandaloneScenarioStep sid _ -> pending sid
        CS.StandaloneScenarioStepWithOptions sid _ _ -> pending sid
        _ -> Null)
    ]
 where
  entry definition = let sid = sideStoryId definition in case sideStoryQuote game player True sid of
    Left reason -> object ["scenarioId" .= sid,"baseCost" .= sideStoryCost definition,"available" .= False,"reason" .= reason]
    Right quote -> quoteView sid quote
  pending sid = case sideStoryQuote game player False sid of
    Left reason -> object ["scenarioId" .= sid,"available" .= False,"reason" .= reason]
    Right quote -> quoteView sid quote
  quoteView sid quote = object
      [ "scenarioId" .= sid,"baseCost" .= (sideStoryCost <$> sideStoryDefinition sid),"available" .= True,"costKind" .= (if quoteUsesTime quote then "time" else "experience" :: Text)
      , "cost" .= quoteCost quote,"remainingTime" .= quoteRemainingTime quote,"overlay" .= quoteOverlay quote
      , "payments" .= [object ["investigatorId" .= iid,"playerId" .= pid,"cost" .= (if quoteUsesTime quote then 0 else cost),"experience" .= xp] | (iid,pid,cost,xp) <- quotePayments quote]
      ]

validateLocalJoin :: Game -> PlayerId -> LocalJoinRequest -> Either Text ()
validateLocalJoin game player (LocalJoinRequest joining) = do
  validateRosterManager game player
  unless (hasRosterCapacity game) $ Left "A campaign has at most four local seats"
  when (unPlayerId joining == UUID.nil || joining `elem` gamePlayers game
    || any ((== joining) . attr investigatorPlayerId) (Map.elems (gameEntities game).investigators)
    || any ((== joining) . attr investigatorPlayerId) (Map.elems $ gameRetiredInvestigators game)) $ Left "New seat identity is nil, existing, or reserved by a retired investigator"

validateRetire :: Game -> PlayerId -> InvestigatorId -> Either Text ()
validateRetire game player iid = do
  validateRosterManager game player
  unless (Map.member iid (gameEntities game).investigators) $ Left "Unknown active investigator"
  unless (Map.size (gameEntities game).investigators > 1) $ Left "The last investigator cannot leave"

validateRejoin :: Game -> PlayerId -> InvestigatorId -> Either Text ()
validateRejoin game player iid = do
  validateRosterManager game player
  unless (hasRosterCapacity game) $ Left "A campaign has at most four local seats"
  actor <- maybe (Left "That investigator has not retired") Right $ Map.lookup iid $ gameRetiredInvestigators game
  let pid=attr investigatorPlayerId actor
  when (pid `elem` gamePlayers game || any (sameInvestigatorIdentity iid) (Map.keys (gameEntities game).investigators)) $ Left "The retired investigator's seat or identity is already occupied"

validateLocalOverlay :: Game -> PlayerId -> InvestigatorId -> Maybe DeckOverlay -> Either Text DeckOverlay
validateLocalOverlay game player iid supplied = do
  validateRosterManager game player
  unless (Map.member iid (gameEntities game).investigators) $ Left "Unknown overlay investigator"
  original <- maybe (Left "Missing campaign deck") Right $ campaignDeck game iid
  overlay <- maybe (Left "No custom replacement was supplied") Right supplied
  unless (isNothing $ overlayInvestigator overlay) $ Left "Campaign overlays cannot replace an investigator"
  unless (sum (map Map.size [overlayAdd overlay,overlayRemove overlay]) + Map.size (overlaySwaps overlay) <= 500) $ Left "Overlay has too many entries"
  unless (all (\n -> n > 0 && n <= 100) $ Map.elems (overlayAdd overlay) <> Map.elems (overlayRemove overlay)) $ Left "Overlay counts must be positive and bounded"
  let plan=campaignOverlayPlan overlay $ unDeck original
      remaining=Map.fromListWith (+) [(toCardCode card,1 :: Int) | card <- plannedCards plan]
      added=Map.fromListWith (+) $ plannedAdds plan
  for_ (plannedSwaps plan) \(from,to,n) ->
    unless (n > 0 && from /= to) $ Left "Overlay swap source is absent or unchanged"
  for_ (plannedRemovals plan) \(_,wanted,available) ->
    unless (wanted <= available) $ Left "Overlay removal exceeds the retained original copies"
  when (Map.null added && Map.null (overlayRemove overlay)) $ Left "Overlay contains no changes"
  -- Validate every referenced card using the same local player-card bounds.
  let input=ArkhamDBDecklist (Map.unionWith (+) added remaining) mempty iid "native overlay" Nothing Nothing Nothing Nothing Nothing
  validateLocalDeckInput input
  unless (sum (map toInteger $ Map.elems $ Map.unionWith (+) added remaining) <= 2000) $ Left "Resulting overlay deck is too large"
  pure overlay

campaignManagementView :: Game -> PlayerId -> Value
campaignManagementView game player
  | not (atCampaignContinuation game) || null managers = Null
  | otherwise = object
      [ "schemaVersion" .= (1 :: Int),"manager" .= headMay managers
      , "canManage" .= isRight (validateRosterManager game player)
      , "canJoin" .= (isRight (validateRosterManager game player) && hasRosterCapacity game)
      , "canUpgrade" .= isRight (offeredUpgrade game player)
      , "sideStories" .= sideStoryView game player
      , "capacity" .= maxInvestigators
      , "active" .= [object ["investigatorId" .= iid,"playerId" .= attr investigatorPlayerId actor
          ,"canRetire" .= isRight (validateRetire game player iid)] | (iid,actor) <- Map.toList (gameEntities game).investigators]
      , "retired" .= [object ["investigatorId" .= iid,"playerId" .= attr investigatorPlayerId actor
          ,"canRejoin" .= isRight (validateRejoin game player iid)] | (iid,actor) <- Map.toList $ gameRetiredInvestigators game]
      , "overlayPolicy" .= ("campaign-deck-only-no-purchase" :: Text) ]
 where
  managers=filter (isContinueCampaignAsk game) $ gamePlayers game

localDeckView :: Game -> PlayerId -> Value
localDeckView game player = case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
  Just ChooseUpgradeDeck -> case upgradeInvestigator game player of
    Left reason -> schema "upgrade" False ["reason" .= reason]
    Right iid -> schema "upgrade" True
      [ "investigatorId" .= iid
      , "currentCards" .= maybe mempty (Map.fromListWith (+) . map (\card -> (toCardCode card,1 :: Int)) . unDeck) (campaignDeck game iid)
      , "canKeepDeck" .= True, "replacementSupported" .= True
      , "purchaseBudgetValidated" .= False ]
  Just ChooseDeck -> schema "initial" True []
  Just (ChooseJoinDeck used) -> schema "join" True ["usedInvestigators" .= nub (used <> usedCampaignInvestigators game)]
  _ -> Null
 where
  schema task supported fields = object $ ["schemaVersion" .= (1 :: Int),"task" .= (task :: Text),"supported" .= supported] <> fields

{- | Re-park the seats still owed a question after @playerId@ answered.

Empty for a seat inside a multi-seat barrier: the barrier republishes the seats
still waiting from their own durable slots when this seat emits 'SeatResolved'.
Re-pushing here would instead park them /ahead/ of the rest of THIS seat's
sub-flow, stranding its tail behind them.
-}
reAskOthers :: Game -> PlayerId -> [Message]
reAskOthers game playerId
  | isJust (barrierSeat playerId game) = []
  | otherwise =
      let question' = Map.delete playerId (gameQuestion game)
          -- keep a retained ask retained; re-parking it bare would make the flag
          -- survive exactly one answer and then drop the remaining seats
          retain = if gameRetainedQuestion game then Retain else id
       in [retain (AskMap question') | not (Map.null question')]

{- | Like 'handleAnswer' but with no DB access. Returns 'Unhandled' for
'DeckAnswer' / 'DeckListAnswer', which require updating an 'ArkhamPlayer'
row. Used by the headless replay CLI.
-}
handleAnswerPure :: Game -> PlayerId -> Answer -> IO Reply
handleAnswerPure game@Game {..} playerId = \case
  SetTriggerModeAnswer {} -> unhandled "Trigger preference must use the native preference path"
  AutoSkipTriggersAnswer index -> handleAnswerPure game playerId $ Answer $ QuestionResponse index (Just playerId) (Just gameScenarioSteps)
  ResumeAutomaticTriggersAnswer {} -> unhandled "Automatic resume requires the native rules advancement loop"
  DeckAnswer {} -> unhandled "DeckAnswer requires database access"
  DeckListAnswer dl _ -> case unwrapQuestion <$> Map.lookup playerId gameQuestion of
    Just ChooseDeck -> loadLocalDeck dl
    Just ChooseUpgradeDeck -> case upgradeInvestigator game playerId of
      Left reason -> unhandled reason
      Right iid -> upgradeLocalDeck $ UpgradeDeckRequest iid (Just dl)
    Just ChooseJoinDeck {} -> loadLocalDeck dl
    _ -> unhandled "Wrong question type for deck selection"
  UpgradeDeckAnswer request -> upgradeLocalDeck request
  ReplaceInvestigatorAnswer request@UpgradeDeckRequest {..} -> case validateReplacementRequest game playerId request of
    Left reason -> unhandled reason
    Right iid -> handled $ completeDeckWork game playerId
      $ maybe [] (\dl -> map DebugRegisterCustomCard (decklistCustomCards dl) <> [ReplaceInvestigator iid dl]) upgradeDeck
  JoinLocalCampaignAnswer request@(LocalJoinRequest joining) ->
    either unhandled (const $ handled [JoinCampaign joining]) $ validateLocalJoin game playerId request
  BeginUpgradeAnswer -> either unhandled
    (\continuation -> handled [NextCampaignStep $ Just $ CS.UpgradeDeckStep $ CS.ContinueCampaignStep continuation])
    $ offeredUpgrade game playerId
  BeginSideStoryAnswer request -> either unhandled
    (\step -> handled [NextCampaignStep $ Just step]) $ offeredSideStory game playerId request
  JoinCampaignAnswer -> unhandled "JoinCampaignAnswer requires database access"
  RetireInvestigatorAnswer iid -> either unhandled (const $ handled [LeaveCampaign iid]) $ validateRetire game playerId iid
  ApplyOverlayAnswer iid overlay -> either unhandled (\chosen -> handled [ApplyDeckOverlay iid chosen]) $ validateLocalOverlay game playerId iid overlay
  RejoinInvestigatorAnswer iid -> either unhandled (const $ handled [UnretireInvestigator iid]) $ validateRejoin game playerId iid
  SettingsFormAnswer {} -> unhandled "Settings form requires the native prepared-answer boundary"
  HistoricalSettingsAnswer {} -> unhandled "Historical settings require the native bootstrap transaction boundary"
  StandaloneSettingsAnswer settings' -> do
    let standaloneCampaignLog = makeStandaloneCampaignLog settings'
    handled [SetCampaignLog standaloneCampaignLog]
  CampaignSettingsAnswer settings' -> do
    let campaignLog' = makeCampaignLog settings'
    handled [SetCampaignLog campaignLog']
  CampaignSpecificAnswer k v -> do
    let
      unwrap = \case
        QuestionLabel _ _ q' -> unwrap q'
        PayCostQuestion _ q' -> unwrap q'
        QuestionWithSource _ _ q' -> unwrap q'
        q' -> q'
    case unwrap <$> Map.lookup playerId gameQuestion of
      Just (PickCampaignSpecific {}) -> handled $ CampaignSpecific k v : reAskOthers game playerId
      _ -> unhandled "Wrong question type"
  ScenarioSpecificAnswer k v -> do
    let
      unwrap = \case
        QuestionLabel _ _ q' -> unwrap q'
        PayCostQuestion _ q' -> unwrap q'
        QuestionWithSource _ _ q' -> unwrap q'
        q' -> q'
    case unwrap <$> Map.lookup playerId gameQuestion of
      Just (PickScenarioSpecific {}) -> handled $ ScenarioSpecific k v : reAskOthers game playerId
      _ -> unhandled "Wrong question type"
  CampaignStepAnswer k -> do
    case unwrapQuestion <$> Map.lookup playerId gameQuestion of
      Just ContinueCampaign -> handleCampaignStepAnswer gameMode k
      _ -> unhandled "Wrong question type"
  CampaignStepAnswerFor pid _ k -> do
    case unwrapQuestion <$> Map.lookup pid gameQuestion of
      Just ContinueCampaign -> handleCampaignStepAnswer gameMode k
      _ -> unhandled "Wrong question type"
  PickDestinyAnswer choices -> do
    handled [SetDestiny $ Map.fromList $ map (\(DestinyDrawing scope card) -> (scope, card)) choices]
  ExchangeAmountsAnswer source fromInvestigator toInvestigator token n -> do
    if n < 0
      then handled [MoveTokens source (toSource toInvestigator) (toTarget fromInvestigator) token (abs n)]
      else handled [MoveTokens source (toSource fromInvestigator) (toTarget toInvestigator) token n]
  AmountsAnswer response ->
    case arQuestionVersion response of
      Just v | v /= gameScenarioSteps -> unhandled "Stale question"
      _ -> do
        let
          doResolve choices target = do
            let nameMap = Map.fromList $ map (\(AmountChoice cId lbl _ _) -> (cId, lbl)) choices
            let lookupChoice (uuid, n) =
                  (\lbl -> (NamedUUID lbl uuid, n)) <$> Map.lookup uuid nameMap
            case traverse lookupChoice (Map.toList $ arAmounts response) of
              Nothing -> unhandled "Wrong choice id"
              Just amounts ->
                handled
                  $ ResolveAmounts (playerInvestigator gameEntities playerId) amounts target
                  : reAskOthers game playerId
        case unwrapQuestion <$> Map.lookup playerId gameQuestion of
          Just (ChooseAmounts _ _ choices target) -> doResolve choices target
          _ -> unhandled "Wrong question type"
  PaymentAmountsAnswer response ->
    case parQuestionVersion response of
      Just v | v /= gameScenarioSteps -> unhandled "Stale question"
      _ -> case unwrapQuestion <$> Map.lookup playerId gameQuestion of
        Just (ChoosePaymentAmounts _ _ info) -> do
          let costMap = Map.fromList $ map (\(PaymentAmountChoice cId _ _ _ _ cost) -> (cId, cost)) info
          let
            combinePaymentAmounts n = \case
              PayCost acId iid skip (UseCost aMatcher uType m) -> [PayCost acId iid skip (UseCost aMatcher uType (n * m))]
              PayCost acId iid skip (ResourceCost _) | n == 0 -> [PayCost acId iid skip (ResourceCost 0)]
              PayCost acId iid skip other -> [PayCost acId iid skip (fold (replicate n other :: [Cost]))]
              payMsg -> replicate n payMsg
          let handleCost (cId, n) = combinePaymentAmounts n $ Map.findWithDefault Noop cId costMap
          handled $ concatMap handleCost $ Map.toList (parAmounts response)
        _ -> unhandled "Wrong question type"
  Raw message -> do
    let inFastWindow =
          maybe
            False
            (any (any (\w -> Window.windowType w == Window.FastPlayerWindow)))
            gameWindowStack
    if not (Map.null gameQuestion) && not (any isRegeneratedWindowChoose $ toList gameQuestion)
      then case message of
        PassSkillTest -> handled [message]
        FailSkillTest -> handled [message]
        ForceChaosTokenDraw _ -> handled [message]
        -- Settings updates regenerate the pending question themselves when a
        -- fast player window is open (UpdateGlobalSetting re-runs runWindow);
        -- skip the stale AskMap so it doesn't clobber the regenerated one.
        UpdateGlobalSetting {} | inFastWindow -> handled [message]
        UpdateCardSetting {} | inFastWindow -> handled [message]
        SetCardSilenced {} | inFastWindow -> handled [message]
        SetAsIfRuling {} | inFastWindow -> handled [message]
        _ -> handled [message, AskMap gameQuestion]
      else handled [message]
  Answer response ->
    case qrQuestionVersion response of
      Just v | v /= gameScenarioSteps -> unhandled "Stale question"
      _ ->
        maybe
          (unhandled "Player not being asked")
          ( \q -> do
              result <- E.try @SomeException $ E.evaluate $ go id q response
              case result of
                Left _ -> unhandled "Wrong question type"
                Right msgs -> do
                  -- Re-ask ONLY leftover deck-selection seats. ChooseDeck has no
                  -- regeneration path: if it is dropped when another seat answers,
                  -- the campaign starts a man down. Every other multi-seat ask is
                  -- either rebuilt by the queue (PlayerWindow re-pushes itself,
                  -- WindowAsk queues a trailing Do (CheckWindows), the skill-test
                  -- loop re-asks the commit window) or was satisfied by this answer
                  -- (story Read continues for the table). Re-parking those hands a
                  -- stale, decline-less question to the other player -- forcing a
                  -- Joey ability (#5159), a commit to a finished test (#5164), or a
                  -- second copy of every rules-book entry -- so they are dropped,
                  -- which was the pre-#5151 behavior for all seats.
                  --
                  -- A seat inside a multi-seat barrier is exempt: the barrier
                  -- republishes the seats still waiting from their own durable slots
                  -- when this seat emits SeatResolved. Re-pushing here would park them
                  -- ahead of the rest of THIS seat's sub-flow and strand its tail.
                  -- (Unreachable while ChooseDeck -- a barrier's only question today --
                  -- is answered via DeckAnswer; needed once phase 2/3 put
                  -- ChooseUpgradeDeck / Read, which answer through here, in a barrier.)
                  --
                  -- A Retain-published ask is the third case: it is neither rebuilt
                  -- by the queue nor barriered, and its seats hold baked message
                  -- lists rather than a re-enumerable set of choices, so nothing is
                  -- stale about re-parking them -- dropping them just destroys the
                  -- messages (#4787). Every seat survives, this one included if it
                  -- still has choices left.
                  finish msgs
          )
          $ Map.lookup playerId gameQuestion
  OrderedAnswer response ->
    case orQuestionVersion response of
      Just v | v /= gameScenarioSteps -> unhandled "Stale question"
      _ ->
        maybe
          (unhandled "Player not being asked")
          ( \q -> case resolveOrdered q (orChoices response) of
              Nothing -> unhandled "Wrong question type"
              Just msgs -> finish msgs
          )
          $ Map.lookup playerId gameQuestion
 where
  upgradeLocalDeck request@UpgradeDeckRequest {..} = case validateUpgradeRequest game playerId request of
    Left reason -> unhandled reason
    Right iid -> handled $ completeDeckWork game playerId
      $ maybe [] (\dl -> map DebugRegisterCustomCard (decklistCustomCards dl) <> [UpgradeDecklist iid dl]) upgradeDeck

  loadLocalDeck dl = case validateLocalDeckSelection game playerId dl of
    Left reason -> unhandled reason
    Right () -> handled $ map DebugRegisterCustomCard (decklistCustomCards dl) <> deckChosen game playerId dl

  finish msgs = do
    let retained = gameRetainedQuestion
        others
          | isJust (barrierSeat playerId game) = mempty
          | retained = Map.delete playerId gameQuestion
          | otherwise = Map.filter survivesAnotherSeat $ Map.delete playerId gameQuestion
    if retained
      then do
        -- Fold this seat's own re-ask into the same map. Emitting it as a
        -- separate `Ask` would park it ahead of the other seats, serialising
        -- a question whose whole point is that the table resolves it in an
        -- order of its choosing.
        let (ran, reask) = case reverse msgs of
              (Ask pid reasked : rest) | pid == playerId -> (reverse rest, Just reasked)
              _ -> (msgs, Nothing)
            question' = maybe others (\reasked -> Map.insert playerId reasked others) reask
        handled $ ran <> [Retain (AskMap question') | not (Map.null question')]
      else handled $ msgs <> [AskMap others | not (Map.null others)]

  -- \| Every choice of a one-at-a-time question, run in the order given. The
  --  order must name each choice exactly once: a partial order would leave choices
  --  needing a re-ask, and this exists precisely so that no re-ask happens and the
  --  whole placement is one step. Anything else is rejected rather than half
  --  resolved. `ChooseOneAtATimeWithAuto`'s auto option already resolves them all
  --  in one answer this way; this only adds the player's ordering.
  --
  resolveOrdered :: Question Message -> [Int] -> Maybe [Message]
  resolveOrdered q order = case q of
    QuestionLabel _ _ q' -> resolveOrdered q' order
    PayCostQuestion _ q' -> resolveOrdered q' order
    QuestionWithSource _ _ q' -> resolveOrdered q' order
    ChooseOneAtATime msgs -> pick msgs
    ChooseOneAtATimeWithAuto _ msgs -> pick msgs
    _ -> Nothing
   where
    pick msgs
      | sort order == [0 .. length msgs - 1] = traverse (fmap uiToRun . (msgs !!?)) order
      | otherwise = Nothing

  -- Seats the queue rebuilds on its own: PlayerWindow re-pushes itself, and a
  -- WindowChooseOne is followed by the Do (CheckWindows ws) that WindowAsk
  -- queues behind it. Re-parking either hands back a stale question (#5160).
  -- An ask holding a materialised set's pending window effects is the exception:
  -- 'WindowAsk' deliberately queues no trailing check behind it, so nothing rebuilds it,
  -- and those effects exist nowhere else. #5798
  isRegeneratedWindowChoose q = case q of
    PlayerWindowChooseOne _ -> not (holdsPendingWindowEffects q)
    WindowChooseOne _ -> not (holdsPendingWindowEffects q)
    _ -> False

  -- \| Seats that must survive another seat answering.
  --
  --  A deck selection has no regeneration path at all. An ask holding a materialised set's
  --  pending window effects is the other case: it is NOT rebuilt by the queue, because
  --  'WindowAsk' deliberately queues no trailing @Do (CheckWindows ws)@ behind it, and those
  --  effects exist nowhere else (#5798). It is also not stale the way #5159/#5164 were: the
  --  set re-filters itself against 'initiationIsLive' every round, so a consumed or dead
  --  initiation drops out on its own. Dropping it instead strands the window with no
  --  question and nothing to rebuild one.
  survivesAnotherSeat q = isDeckQuestion q || holdsPendingWindowEffects q
  go
    :: (Question Message -> Question Message)
    -> Question Message
    -> QuestionResponse
    -> [Message]
  go f q response = case q of
    QuestionLabel lbl mCard q' -> go (QuestionLabel lbl mCard) q' response
    PayCostQuestion cost q' -> go (PayCostQuestion cost) q' response
    QuestionWithSource s tt q' -> go (QuestionWithSource s tt) q' response
    Read t (BasicReadChoices qs) mcs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ Read t (BasicReadChoices qs) mcs]
      Just msg -> [uiToRun msg]
    Read t (BasicReadChoicesN n qs) mcs -> do
      let (mm, msgs') = extract (qrChoice response) qs
      case (mm, msgs') of
        (Just m', []) -> [uiToRun m']
        (Just m', _) ->
          if n - 1 == 0
            then [uiToRun m']
            else [uiToRun m', Ask playerId $ f $ Read t (BasicReadChoicesN (n - 1) msgs') mcs]
        (Nothing, msgs'') -> [Ask playerId $ f $ Read t (BasicReadChoicesN n msgs'') mcs]
    Read t (BasicReadChoicesUpToN n qs) mcs -> do
      let (mm, msgs') = extract (qrChoice response) qs
      case (mm, msgs') of
        (Just m', []) -> [uiToRun m']
        (Just m'@(Done _), _) -> [uiToRun m']
        (Just m', [Done _]) -> [uiToRun m']
        (Just m', msgs'') ->
          if n - 1 == 0
            then [uiToRun m']
            else [uiToRun m', Ask playerId $ f $ Read t (BasicReadChoicesUpToN (n - 1) msgs'') mcs]
        (Nothing, msgs'') -> [Ask playerId $ f $ Read t (BasicReadChoicesUpToN n msgs'') mcs]
    Read t (LeadInvestigatorMustDecide qs) mcs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ Read t (LeadInvestigatorMustDecide qs) mcs]
      Just msg -> [uiToRun msg]
    ChooseOneWizard flavor qs confirm back -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ ChooseOneWizard flavor qs confirm back]
      Just WizardChoice {messages} -> [Run messages]
    ChooseFromCollection iid source codes -> case codes !!? qrChoice response of
      Nothing -> [Ask playerId $ f q]
      Just code -> [HandleTargetChoice iid source (CardCodeTarget code)]
    ChooseOne qs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ ChooseOne qs]
      Just msg -> [uiToRun msg]
    PlayerWindowChooseOne qs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ PlayerWindowChooseOne qs]
      Just msg -> [uiToRun msg]
    WindowChooseOne qs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ WindowChooseOne qs]
      Just msg -> [uiToRun msg]
    ChooseOneFromEach qs -> case concat qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ ChooseOneFromEach qs]
      Just msg ->
        let
          removeSublistAtIndex :: Int -> [[a]] -> [[a]]
          removeSublistAtIndex idx xss = removeSublist idx xss 0
          removeSublist _ [] _ = []
          removeSublist n (ys : yss) currentIdx
            | n < currentIdx + length ys = yss
            | otherwise = ys : removeSublist n yss (currentIdx + length ys)
          remaining = removeSublistAtIndex (qrChoice response) qs
         in
          uiToRun msg : [Ask playerId $ f $ ChooseOneFromEach remaining | not (null remaining)]
    ChooseN n qs -> do
      let (mm, msgs') = extract (qrChoice response) qs
      case (mm, msgs') of
        (Just m', []) -> [uiToRun m']
        (Just m', msgs''@(m1 : mrest)) ->
          if n - 1 == 0
            then [uiToRun m']
            else
              -- it is possible that every choice in qs is the same, in which case we can just run them all
              if all (== m1) mrest
                then uiToRun m' : map uiToRun (take (n - 1) msgs'')
                else [uiToRun m', Ask playerId $ f $ ChooseN (n - 1) msgs'']
        (Nothing, msgs'') -> [Ask playerId $ f $ ChooseN n msgs'']
    ChooseUpToN n qs -> do
      let (mm, msgs') = extract (qrChoice response) qs
      case (mm, msgs') of
        (Just m', []) -> [uiToRun m']
        (Just m'@(Done _), _) -> [uiToRun m']
        (Just m', [Done _]) -> [uiToRun m']
        (Just m', msgs'') ->
          if n - 1 == 0
            then [uiToRun m']
            else [uiToRun m', Ask playerId $ f $ ChooseUpToN (n - 1) msgs'']
        (Nothing, msgs'') -> [Ask playerId $ f $ ChooseUpToN n msgs'']
    ChooseOneAtATime msgs -> do
      let (mm, msgs') = extract (qrChoice response) msgs
      case (mm, msgs') of
        (Just m', []) -> [uiToRun m']
        (Just m', msgs'') ->
          [uiToRun m', Ask playerId $ f $ ChooseOneAtATime msgs'']
        (Nothing, msgs'') ->
          [Ask playerId $ f $ ChooseOneAtATime msgs'']
    ChooseOneAtATimeWithAuto k msgs -> do
      -- Choice 0 is the auto ("resolve everything still listed") option, so the real
      -- choices are offset by one. The auto option only earns its place while more
      -- than one is left: against a single option it is a second button that does
      -- exactly what the first one does.
      let reask rest = if length rest > 1 then ChooseOneAtATimeWithAuto k rest else ChooseOneAtATime rest
      if qrChoice response == 0
        then map uiToRun msgs
        else do
          let (mm, msgs') = extract (qrChoice response - 1) msgs
          case (mm, msgs') of
            (Just m', []) -> [uiToRun m']
            (Just m', msgs'') -> [uiToRun m', Ask playerId $ f $ reask msgs'']
            (Nothing, msgs'') -> [Ask playerId $ f $ reask msgs'']
    ChooseSome msgs -> do
      let (mm, msgs') = extract (qrChoice response) msgs
      case (mm, msgs') of
        (Just (Done _), _) -> []
        (Just m', msgs'') -> case msgs'' of
          [] -> [uiToRun m']
          [Done _] -> [uiToRun m']
          rest -> [uiToRun m', Ask playerId $ f $ ChooseSome rest]
        (Nothing, msgs'') -> [Ask playerId $ f $ ChooseSome msgs'']
    ChooseSome1 doneMsg msgs -> do
      let (mm, msgs') = extract (qrChoice response) msgs
      case (mm, msgs') of
        (Just (Done _), _) -> []
        (Just m', msgs'') -> case msgs'' of
          [] -> [uiToRun m']
          [Done _] -> [uiToRun m']
          rest -> [uiToRun m', Ask playerId $ f $ ChooseSome $ Done doneMsg : rest]
        (Nothing, msgs'') -> [Ask playerId $ f $ ChooseSome $ Done doneMsg : msgs'']
    PickSupplies remaining chosen qs rs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ PickSupplies remaining chosen qs rs]
      Just msg -> [uiToRun msg]
    DropDown qs -> case qs !!? qrChoice response of
      Nothing -> [Ask playerId $ f $ DropDown qs]
      Just (_, msg) -> [msg]
    _ -> error "Wrong question type"

extract :: Int -> [a] -> (Maybe a, [a])
extract n xs = let a = xs !!? n in (a, [x | (i, x) <- zip [0 ..] xs, i /= n])
