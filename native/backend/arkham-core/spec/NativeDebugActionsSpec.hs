module NativeDebugActionsSpec (nativeDebugActionsSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation
import Arkham.Question
import Arkham.Message (Message (..))
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.UUID qualified as UUID
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Text qualified as T
import Data.List qualified as List
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label good=if good then putStrLn ("PASS "<>label) else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right=either (\e->putStrLn e>>exitFailure) pure
field :: Text -> Value -> Value
field key (Object o)=fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _=Null
typedValue :: FromJSON a => Value -> a
typedValue value=case fromJSON value of Aeson.Success x->x;Aeson.Error reason->error $ fromString reason
nativeDebugActionsSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeDebugActionsSpecs player choose=do
  decks<-eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e->P.putStrLn e>>exitFailure) pure
  let deck=headEx decks
      schema s=field "actions" $ debugView (snapshotGame s) player
      cardReady s=field "cardActionsAvailable" (schema s)==Bool True
      testReady s=field "available" (field "test" $ schema s)==Bool True
      question s=unwrapQuestion $ fromJustNote "one actual recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s
      step s=do
        response<-choose player deck s
        fst <$> (applyAnswer s player (snapshotRevision s) response >>= right)
      untilReady predicate budget s
        | predicate s=pure s
        | budget<=(0::Int)=error "Representative debug adapter checkpoint not reached"
        | otherwise=step s >>= untilReady predicate (budget-1)
      apply s command=fst <$> (applyDebug s player (snapshotRevision s) True command >>= right)
      emit prefix value=BL.putStrLn $ BL.pack prefix<>encode value
      replay s command expected=do
        imported<-importSnapshot (exportSnapshot s)>>=right
        actual<-apply imported command
        when (exportSnapshot actual/=exportSnapshot expected) $ emit "DEBUG_ACTIONS_REPLAY_MISMATCH " $ object ["before" .= s,"command" .= command,"expected" .= expected,"actual" .= actual]
        assert "debug action exact whole-save replay through public core API" $ exportSnapshot actual==exportSnapshot expected
      rejects s command=do
        result<-applyDebug s player (snapshotRevision s) True command
        assert "debug action strict invalid command rejected without changing basis" $ either (const True) (const False) result
  (created,_)<-createSession (CreateRequest Nothing (Just "01104") 171020 [player] Standard False False)>>=right
  ready<-untilReady cardReady 120 created
  assert "debug action representative real scenario setup reached stable window" $ not (gameInSetup $ snapshotGame ready)
  let iid0=fst $ headEx $ Map.toList (gameEntities $ snapshotGame ready).investigators
      shuffleCommand=object ["operation" .= ("shuffleDeck"::Text),"deck" .= object ["tag" .= ("InvestigatorDeck"::Text),"contents" .= iid0]]
      drawCommand=object ["operation" .= ("selectDraw"::Text),"investigatorId" .= iid0]
      deckCards s=List.sort $ map encode $ typedValue @[Value] $ field "deck" $ toJSON $ snd $ headEx $ Map.toList (gameEntities $ snapshotGame s).investigators
  shuffled<-apply ready shuffleCommand
  assert "debug original shuffle preserves every physical deck card and returns to stable window" $ deckCards shuffled==deckCards ready && cardReady shuffled
  replay ready shuffleCommand shuffled
  drawing<-apply ready drawCommand
  assert "debug selected draw opens original search question instead of directly moving cards" $ not (Map.null $ gameQuestion $ snapshotGame drawing) && encode (gameQuestion $ snapshotGame drawing)/=encode (gameQuestion $ snapshotGame ready)
  replay ready drawCommand drawing
  rejects ready $ object ["operation" .= ("shuffleDeck"::Text),"deck" .= object ["tag" .= ("NoDeck"::Text)]]
  rejects ready $ object ["operation" .= ("selectDraw"::Text),"investigatorId" .= ("c99999"::Text)]
  let actor=snd $ headEx $ Map.toList (gameEntities $ snapshotGame ready).investigators
      hand=typedValue @[Value] $ field "hand" $ toJSON actor
      physicalId=field "id" $ field "contents" $ headEx hand
      move=object ["operation" .= ("moveCard"::Text),"cardId" .= physicalId,"destination" .= object ["tag" .= ("DebugCardRemovedFromGame"::Text)]]
      gain=object ["operation" .= ("gainActions"::Text),"investigatorId" .= iid0,"amount" .= (1::Int)]
      actionCount s=typedValue @Int $ field "remainingActions" $ toJSON $ snd $ headEx $ Map.toList (gameEntities $ snapshotGame s).investigators
      zoneCards name s=typedValue @[Value] $ field name $ toJSON $ snd $ headEx $ Map.toList (gameEntities $ snapshotGame s).investigators
      deckDiscardIds s=List.sort $ map (encode . field "id" . field "contents") $ zoneCards "deck" s <> zoneCards "discard" s
      discardMove=object ["operation" .= ("moveCard"::Text),"cardId" .= physicalId,"destination" .= object ["tag" .= ("DebugCardDiscard"::Text)]]
      shuffleDiscard=object ["operation" .= ("shufflePlayerDiscard"::Text),"investigatorId" .= iid0]
  gained<-apply ready gain
  assert "debug gain actions uses original message and returns to stable window" $ cardReady gained && actionCount gained==actionCount ready+1
  replay ready gain gained
  discarded<-apply ready discardMove
  washed<-apply discarded shuffleDiscard
  assert "debug player discard shuffle preserves all physical cards and empties discard" $ cardReady washed && null (zoneCards "discard" washed) && deckDiscardIds washed==deckDiscardIds discarded
  replay discarded shuffleDiscard washed
  rejects ready $ object ["operation" .= ("gainActions"::Text),"investigatorId" .= iid0,"amount" .= (0::Int)]
  rejects ready $ object ["operation" .= ("gainActions"::Text),"investigatorId" .= iid0,"amount" .= (6::Int)]
  rejects ready $ object ["operation" .= ("shufflePlayerDiscard"::Text),"investigatorId" .= ("c99999"::Text)]
  moved<-apply ready move
  let movedActor=snd $ headEx $ Map.toList (gameEntities $ snapshotGame moved).investigators
      movedHand=typedValue @[Value] $ field "hand" $ toJSON movedActor
  assert "debug move removes the selected hand card and returns a real current player window" $ cardReady moved && physicalId `notElem` map (field "id" . field "contents") movedHand
  replay ready move moved
  let wrapped=ready {snapshotGame=(snapshotGame ready){gameQuestion=Map.singleton player $ PayCostQuestion (typedValue $ object ["tag" .= ("Free"::Text)]) $ question ready}}
  rejects wrapped move
  emit "DEBUG_ACTIONS_CARD_FIXTURE " $ object ["before" .= ready,"command" .= move,"after" .= moved]
  choices<-maybe (error "Actual player choices missing") pure $ indexedChoices $ question ready
  let isInvestigate = \case AbilityLabel {ability}->"Investigate" `T.isInfixOf` decodeUtf8 (BL.toStrict $ encode ability); _->False
      index=fromJustNote "Actual offered investigate action" $ List.findIndex isInvestigate choices
  (started,_)<-applyAnswer ready player (snapshotRevision ready) (Answer $ QuestionResponse index (Just player) Nothing)>>=right
  pending<-untilReady testReady 40 started
  let sid=field "id" $ field "test" $ schema pending
      faces=fromMaybe [] $ Aeson.parseMaybe parseJSON $ field "faces" $ field "test" $ schema pending :: [Value]
      forced=object ["operation" .= ("setForcedDraws"::Text),"testId" .= sid,"faces" .= take 1 faces]
      clear=object ["operation" .= ("setForcedDraws"::Text),"testId" .= sid,"faces" .= ([]::[Value])]
      difficulty=object ["operation" .= ("adjustTest"::Text),"testId" .= sid,"field" .= ("difficulty"::Text),"amount" .= (-1::Int)]
      skill=object ["operation" .= ("adjustTest"::Text),"testId" .= sid,"field" .= ("skill"::Text),"amount" .= (1::Int)]
      reveal=object ["operation" .= ("setRevealStrategy"::Text),"testId" .= sid,"reveal" .= (2::Int),"choose" .= (1::Int)]
      outcome text=object ["operation" .= ("finishTest"::Text),"testId" .= sid,"outcome" .= (text::Text)]
  assert "debug actual skill-test id and bag faces supplied by core" $ sid/=Null && notNull faces
  let iid=fst $ headEx $ Map.toList (gameEntities $ snapshotGame pending).investigators
      xp=object ["operation" .= ("setExperience"::Text),"investigatorId" .= iid,"amount" .= (17::Int)]
      originalUndo basis=do
        env<-makeEnv (snapshotGame basis) $ ClearUI:UndoAction:snapshotQueue basis
        advance env
        fst <$> finish (snapshotHistoricalBootstrap basis) (snapshotRevision basis+1) env
  xpEdited<-apply pending xp
  replay pending xp xpEdited
  importedXp<-importSnapshot (exportSnapshot xpEdited)>>=right
  xpContinued<-apply xpEdited forced
  replay xpEdited forced xpContinued
  undoneMemory<-originalUndo xpEdited
  undoneImport<-originalUndo importedXp
  assert "original UndoAction exact after midaction direct debug edit and fresh import" $ exportSnapshot undoneMemory==exportSnapshot undoneImport
  assert "ended action undo checkpoint does not resurrect active test" $ isNothing $ gameSkillTest $ snapshotGame undoneMemory
  (edited,traces)<-foldM (\(basis,rows) command->do
      next<-apply basis command
      assert "debug test metadata retains actual current test and paused question" $ field "id" (field "test" $ schema next)==sid && not (Map.null $ gameQuestion $ snapshotGame next)
      replay basis command next
      pure (next,rows<>[object ["before" .= basis,"command" .= command,"after" .= next]])) (pending,[]::[Value]) [forced,clear,difficulty,skill,reveal]
  for_ ["pass","fail"] $ \result->do
    let command=outcome result
    resolved<-apply edited command
    assert "debug finish replaces the old commit question with original result flow" $ encode (gameQuestion $ snapshotGame resolved)/=encode (gameQuestion $ snapshotGame edited) && not (Map.null $ gameQuestion $ snapshotGame resolved)
    replay edited command resolved
    final<-untilReady cardReady 70 resolved
    assert "debug finished test normal answers return to player window" $ isNothing $ gameSkillTest $ snapshotGame final
    emit "DEBUG_ACTIONS_FINISH_FIXTURE " $ object ["before" .= edited,"command" .= command,"after" .= resolved,"continued" .= final]
  rejects pending $ object ["operation" .= ("adjustTest"::Text),"testId" .= sid,"field" .= ("source"::Text),"amount" .= (1::Int)]
  rejects pending $ object ["operation" .= ("finishTest"::Text),"testId" .= UUID.nil,"outcome" .= ("pass"::Text)]
  rejects pending $ object ["operation" .= ("finishTest"::Text),"testId" .= sid,"outcome" .= ("skip"::Text)]
  emit "DEBUG_ACTIONS_TEST_FIXTURE " $ object ["before" .= pending,"trace" .= traces,"after" .= edited]
  putStrLn "DEBUG_ACTIONS_INTEGRATION_COMPLETED"
