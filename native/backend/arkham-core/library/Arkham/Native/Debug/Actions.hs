module Arkham.Native.Debug.Actions where

import Arkham.Prelude
import Arkham.Game (Game (..))
import Arkham.Game.Utils (modeScenario)
import Arkham.Game.State (GameState (IsActive))
import Arkham.Id
import Arkham.Message
import Arkham.Source
import Arkham.Target
import Arkham.Question
import Arkham.Card
import Arkham.Classes.Entity (toAttrs)
import Arkham.Debug.CardDestination
import Arkham.Deck
import Arkham.Scenario.Types (ScenarioAttrs (..))
import Arkham.Investigator.Types (InvestigatorAttrs (..))
import Arkham.SkillTest.Base
import Arkham.SkillTest.Step
import Arkham.ChaosBag.Base
import Arkham.ChaosBagStepState (ChaosBagStepState (Resolved))
import Arkham.ChaosBag.RevealStrategy
import Arkham.ChaosToken.Types
import Arkham.Modifier
import Arkham.Effect.Window
import Arkham.EffectMetadata
import Arkham.Helpers.Message (holdsPendingWindowEffects)
import Arkham.Search qualified as Search
import Arkham.Matcher (ExtendedCardMatcher (BasicCardMatch), CardMatcher (AnyCard))
import Arkham.Zone (Zone (FromDeck))
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Key (Key)
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Control.Monad.Fail (fail)

data DebugAction
  = SetForcedDraws SkillTestId [ChaosTokenFace]
  | ForceDebugChaosDraw ChaosTokenFace
  | AdjustTest SkillTestId Text Int
  | SetRevealStrategy SkillTestId Int Int
  | FinishTest SkillTestId Text
  | MoveCard CardId DebugCardDestination
  | ShuffleDebugDeck DeckSignifier
  | SelectDebugDraw InvestigatorId
  | SelectEncounterDebugDraw InvestigatorId DeckSignifier
  | GainDebugActions InvestigatorId Int
  | ShuffleDebugDiscard InvestigatorId
  deriving stock Show

exact :: Object -> [Key] -> Aeson.Parser ()
exact o fields=unless (sort (KM.keys o)==sort fields) $ fail "Unknown or missing debug action field"
canonical :: (FromJSON a,ToJSON a) => Value -> Aeson.Parser a
canonical raw=do
  result<-parseJSON raw
  unless (toJSON result==raw) $ fail "Canonical debug action identity required"
  pure result
instance FromJSON DebugAction where
  parseJSON=withObject "finite native debug action" $ \o -> do
    operation<-o .: "operation" :: Aeson.Parser Text
    case operation of
      "setForcedDraws" -> exact o ["operation","testId","faces"] >> (SetForcedDraws <$> (canonical =<< o .: "testId") <*> o .: "faces")
      "forceChaosTokenDraw" -> exact o ["operation","face"] >> (ForceDebugChaosDraw <$> (canonical =<< o .: "face"))
      "adjustTest" -> exact o ["operation","testId","field","amount"] >> (AdjustTest <$> (canonical =<< o .: "testId") <*> o .: "field" <*> o .: "amount")
      "setRevealStrategy" -> exact o ["operation","testId","reveal","choose"] >> (SetRevealStrategy <$> (canonical =<< o .: "testId") <*> o .: "reveal" <*> o .: "choose")
      "finishTest" -> exact o ["operation","testId","outcome"] >> (FinishTest <$> (canonical =<< o .: "testId") <*> o .: "outcome")
      "moveCard" -> exact o ["operation","cardId","destination"] >> (MoveCard <$> (canonical =<< o .: "cardId") <*> (canonical =<< o .: "destination"))
      "shuffleDeck" -> exact o ["operation","deck"] >> (ShuffleDebugDeck <$> (canonical =<< o .: "deck"))
      "selectDraw" -> exact o ["operation","investigatorId"] >> (SelectDebugDraw <$> (canonical =<< o .: "investigatorId"))
      "selectEncounterDraw" -> exact o ["operation","investigatorId","deck"] >> (SelectEncounterDebugDraw <$> (canonical =<< o .: "investigatorId") <*> (canonical =<< o .: "deck"))
      "gainActions" -> exact o ["operation","investigatorId","amount"] >> (GainDebugActions <$> (canonical =<< o .: "investigatorId") <*> o .: "amount")
      "shufflePlayerDiscard" -> exact o ["operation","investigatorId"] >> (ShuffleDebugDiscard <$> (canonical =<< o .: "investigatorId"))
      _ -> fail "Unsupported finite debug action"

-- Wrappers are classified only. The complete original pending question remains
-- authoritative, including materialised pending effects and cost ownership.
inner :: Question a -> Maybe (Question a)
inner = \case
  QuestionLabel _ _ q -> inner q
  QuestionWithSource _ _ q -> inner q
  PayCostQuestion {} -> Nothing
  q -> Just q
safeQuestion :: Question Message -> Bool
safeQuestion q=isJust (inner q) && not (holdsPendingWindowEffects q)

stablePlayerWindow :: Game -> PlayerId -> Either Text ()
stablePlayerWindow game player=do
  unless (gameGameState game==IsActive && not (gameInSetup game) && isJust (modeScenario $ gameMode game)
    && isNothing (gameSkillTest game) && Map.null (gameSimultaneousAsks game)
    && not (gameInAction game) && isNothing (gameResolvingCard game) && null (gameActiveAbilities game)) $
    Left "Debug card actions need a stable player window without tests or barriers"
  case Map.toList $ gameQuestion game of
    [(who,q)] | who==player && safeQuestion q -> case inner q of
      Just PlayerWindowChooseOne {} -> pure ()
      _ -> Left "Debug card actions require the actual player window"
    _ -> Left "Debug card actions require a unique current recipient"

currentTest :: Game -> PlayerId -> SkillTestId -> Either Text SkillTest
currentTest game player sid=do
  test<-maybe (Left "No current skill test") Right $ gameSkillTest game
  let offeredStart = test.step<RevealChaosTokenStep && case Map.lookup player (gameQuestion game) >>= inner of
        Just (ChooseOne choices) -> [iid | StartSkillTestButton iid<-choices]==[test.investigator]
        _ -> False
  unless (gameGameState game==IsActive && Map.member player (gameQuestion game)
    && Map.size (gameQuestion game)==1
    && (Map.null (gameActiveCost game) || offeredStart) && Map.null (gameSimultaneousAsks game)
    && all safeQuestion (Map.elems $ gameQuestion game)) $ Left "Finish active costs or simultaneous questions before test debug"
  unless (test.id==sid && test.step<ApplySkillTestResultsStep) $ Left "Stale or already resolving debug test"
  pure test

-- The original ChaosBag button exists only beside the actual StartSkillTest
-- choice. ForceChaosTokenDraw appends one face, then starts that test; replacing
-- the forced queue or changing an in-flight bag choice would not be equivalent.
forceChaosDraw :: Game -> PlayerId -> ChaosTokenFace -> Either Text Message
forceChaosDraw game player face=do
  test<-maybe (Left "No current skill test") Right $ gameSkillTest game
  _<-currentTest game player test.id
  unless (not (gameInSetup game) && test.step<RevealChaosTokenStep
    && null (skillTestSetAsideChaosTokens test) && null (skillTestRevealedChaosTokens test)
    && null (skillTestAdditionalRevealedChaosTokens test) && null (skillTestResolvedChaosTokens test)
    && null (skillTestToResolveChaosTokens test) && skillTestRevealedChaosTokensCount test==0) $
    Left "Forced draw requires the current test's unrevealed start choice"
  actor<-maybe (Left "Unknown test investigator") Right $ Map.lookup test.investigator (gameEntities game).investigators
  unless (investigatorPlayerId (toAttrs actor)==player) $ Left "Forced draw belongs to the current test investigator's player"
  case Map.lookup player (gameQuestion game) >>= inner of
    Just (ChooseOne choices) | [iid]<-[iid | StartSkillTestButton iid<-choices],iid==test.investigator -> pure ()
    _ -> Left "Forced draw requires the unique offered StartSkillTest button"
  scenario<-maybe (Left "No scenario bag") Right $ modeScenario $ gameMode game
  let bag=scenarioChaosBag $ toAttrs scenario
      drawInProgress=case chaosBagChoice bag of
        Nothing -> False
        Just (Resolved _) -> False
        Just _ -> True
  unless (not drawInProgress && null (chaosBagSetAsideChaosTokens bag)
    && null (chaosBagRevealedChaosTokens bag)) $ Left "Finish the in-flight chaos bag draw before forcing a token"
  unless (length (chaosBagForceDraw bag)<20) $ Left "Forced draw queue is already full"
  unless (face `elem` map (.face) (chaosBagChaosTokens bag)) $ Left "Forced face is absent from the current bag"
  pure $ ForceChaosTokenDraw face

regeneratedPause :: Game -> Bool
regeneratedPause game=case Map.elems $ gameQuestion game of
  [q] | safeQuestion q -> case inner q of
    Just PlayerWindowChooseOne {} -> True
    Just WindowChooseOne {} -> True
    _ -> False
  _ -> False

deckChoices :: Game -> [DeckSignifier]
deckChoices game=
  concat [[InvestigatorDeck iid,InvestigatorDiscard iid] <> map (InvestigatorDeckByKey iid) (Map.keys $ investigatorDecks $ toAttrs actor)
    | (iid,actor)<-Map.toList (gameEntities game).investigators]
  <> case modeScenario $ gameMode game of
    Nothing -> []
    Just scenario -> let attrs=toAttrs scenario in [EncounterDeck,EncounterDiscard]
      <> map ScenarioDeckByKey (Map.keys $ scenarioDecks attrs)
      <> map EncounterDeckByKey (Map.keys $ scenarioEncounterDecks attrs)

destinations :: Game -> Card -> [DebugCardDestination]
destinations game card=
  [DebugCardRemovedFromGame,DebugCardSetAside]
  <> [DebugCardDiscard | case card of VengeanceCard {} -> False; _ -> True]
  <> map DebugCardHand (Map.keys (gameEntities game).investigators)
  <> [DebugCardDeck deck position | deck<-deckChoices game,cardDefCanEnterDeck (toCardDef card) deck
       ,position<-[DebugDeckTop,DebugDeckBottom,DebugDeckShuffle],position==DebugDeckShuffle || deckSupportsEnds deck]

-- Only server-built messages cross this boundary. Original handleAnswerPure's
-- debug routing supplies the original pause/continuation behavior; public Raw
-- stays forbidden. No client Message/Source/Modifier/EffectWindow is accepted.
prepareAction :: Game -> PlayerId -> Value -> Either Text Message
prepareAction game player raw=do
  unless (player `elem` gamePlayers game) $ Left "Unknown local debug player"
  action<-first T.pack $ Aeson.parseEither parseJSON raw
  case action of
    ForceDebugChaosDraw face -> forceChaosDraw game player face
    GainDebugActions iid amount -> do
      stablePlayerWindow game player
      unless (Map.member iid (gameEntities game).investigators && amount >= 1 && amount <= 5) $
        Left "Debug action gain requires an existing investigator and between1and5actions"
      pure $ GainActions iid GameSource amount
    ShuffleDebugDiscard iid -> do
      stablePlayerWindow game player
      unless (Map.member iid (gameEntities game).investigators) $ Left "Unknown investigator discard"
      pure $ ShuffleDiscardBackIn iid
    ShuffleDebugDeck deck -> do
      stablePlayerWindow game player
      unless (deck `elem` deckChoices game && case deck of InvestigatorDeck {} -> True; EncounterDeck -> True; EncounterDeckByKey {} -> True; _ -> False) $
        Left "Debug shuffle requires an existing player or encounter draw deck"
      pure $ ShuffleDeck deck
    SelectDebugDraw iid -> do
      stablePlayerWindow game player
      unless (Map.member iid (gameEntities game).investigators) $ Left "Unknown investigator for debug draw"
      pure $ SearchMessage $ Search_ $ Search.mkSearch Search.Looking iid GameSource (InvestigatorTarget iid)
        [(FromDeck,ShuffleBackIn)] (BasicCardMatch AnyCard) (DrawFound iid 1)
    SelectEncounterDebugDraw iid deck -> do
      stablePlayerWindow game player
      unless (Map.member iid (gameEntities game).investigators && deck `elem` deckChoices game) $ Left "Unknown debug encounter draw target"
      case deck of
        EncounterDeck -> pure $ FindAndDrawEncounterCard iid AnyCard ExcludeDiscard
        EncounterDeckByKey key -> pure $ FindAndDrawEncounterCardWithDeckKey iid AnyCard ExcludeDiscard key
        _ -> Left "Debug encounter draw requires an existing encounter draw deck"
    MoveCard cid destination -> do
      stablePlayerWindow game player
      card<-maybe (Left "Unknown existing physical card") Right $ Map.lookup cid $ gameCards game
      unless (destination `elem` destinations game card) $ Left "Card destination is not offered for this card and current decks"
      pure $ DebugMoveCard cid destination
    SetForcedDraws sid faces -> do
      test<-currentTest game player sid
      unless (test.step<RevealChaosTokenStep && length faces<=20) $ Left "Forced draws must be set before reveal and have at most20entries"
      scenario<-maybe (Left "No scenario bag") Right $ modeScenario $ gameMode game
      let available=map (.face) $ chaosBagChaosTokens $ scenarioChaosBag $ toAttrs scenario
      unless (all (`elem` available) faces) $ Left "Forced face is absent from the current bag"
      pure $ DebugSetForcedChaosTokenDraws faces
    AdjustTest sid field amount -> do
      test<-currentTest game player sid
      unless (amount/=0 && abs (toInteger amount)<=99 && field `elem` ["difficulty","skill"]) $ Left "Unknown test field or out-of-range adjustment"
      let (target,modifier)=if field=="difficulty" then (SkillTestTarget sid,Difficulty amount) else (InvestigatorTarget test.investigator,AnySkillValue amount)
      pure $ testModifier sid target modifier
    SetRevealStrategy sid reveal choose -> do
      test<-currentTest game player sid
      unless (test.step<RevealChaosTokenStep && reveal>=1 && reveal<=10 && choose>=0 && choose<=reveal) $ Left "Reveal strategy is only editable before reveal with bounded counts"
      pure $ testModifier sid (SkillTestTarget sid) $ ChangeRevealStrategy $ if choose>0 && choose<reveal then RevealAndChoose reveal choose else Reveal reveal
    FinishTest sid outcome -> do
      _<-currentTest game player sid
      case outcome of
        "pass" -> pure PassSkillTest
        "fail" -> pure FailSkillTest
        _ -> Left "Debug test outcome must be pass or fail"
 where
  testModifier sid target modifier=CreateWindowModifierEffect (EffectSkillTestWindow sid)
    (EffectModifiers [Modifier GameSource modifier False Nothing Nothing]) GameSource target

actionView :: Game -> PlayerId -> Value
actionView game player=object
  [ "cards" .= [object ["id" .= cid,"cardCode" .= toCardCode card,"destinations" .= destinations game card] | (cid,card)<-Map.toList $ gameCards game]
  , "cardActionsAvailable" .= either (const False) (const True) (stablePlayerWindow game player)
  , "test" .= case gameSkillTest game of
      Nothing -> Null
      Just test -> object ["id" .= test.id,"step" .= test.step,"beforeReveal" .= (test.step<RevealChaosTokenStep)
        ,"available" .= either (const False) (const True) (currentTest game player test.id)
        ,"faces" .= maybe [] (nub . map (.face) . chaosBagChaosTokens . scenarioChaosBag . toAttrs) (modeScenario $ gameMode game)]
  ]
