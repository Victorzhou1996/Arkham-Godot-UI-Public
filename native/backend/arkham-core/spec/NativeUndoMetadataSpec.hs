module NativeUndoMetadataSpec (nativeUndoMetadataSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id (EnemyId (..), InvestigatorId (..), PlayerId)
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Native.Events (eventJSON)
import Arkham.Native.Session
import Arkham.Question (Component (..), Question (..), UI (..))
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.UUID qualified as UUID
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn $ "PASS " <> label else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\reason -> putStrLn reason >> exitFailure) pure

typed :: FromJSON a => Value -> IO a
typed = either (\reason -> P.putStrLn reason >> exitFailure) pure . Aeson.parseEither parseJSON

field :: Text -> Value -> Value
field name (Object fields) = fromMaybe Null $ KM.lookup (K.fromText name) fields
field _ _ = Null

reachPlayerWindow :: (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> PlayerId -> ArkhamDBDecklist -> Int -> Snapshot -> IO Snapshot
reachPlayerWindow chooseRealAnswer player deck = go
 where
  go budget snapshot = case Map.lookup player $ gameQuestion $ snapshotGame snapshot of
    Just wrapped | PlayerWindowChooseOne {} <- unwrapQuestion wrapped -> pure snapshot
    _ | budget <= 0 -> error "Undo metadata fixture did not reach the original PlayerWindow within its setup budget"
    _ -> do
      answer <- chooseRealAnswer player deck snapshot
      (next, _) <- applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
      go (budget - 1) next

drawActionIndex :: InvestigatorId -> Question Message -> Maybe Int
drawActionIndex investigator = \case
  PlayerWindowChooseOne choices -> listToMaybe
    [ index
    | (index, ComponentLabel (InvestigatorDeckComponent owner) [DrawCards drawer _]) <- zip [0..] choices
    , owner == investigator
    , drawer == investigator
    ]
  _ -> Nothing

nativeUndoMetadataSpecs :: (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> PlayerId -> ArkhamDBDecklist -> IO ()
nativeUndoMetadataSpecs chooseRealAnswer player deck = do
  let investigator = InvestigatorId "01104"
  source <- typed $ object ["tag" .= ("GameSource" :: Text)]
  cardDrew <- typed $ object
    [ "cardDrewSource" .= object ["tag" .= ("GameSource" :: Text)]
    , "cardDrewDeck" .= object ["tag" .= ("InvestigatorDeck" :: Text), "contents" .= ("01104" :: Text)]
    , "cardDrewCards" .= ([] :: [Value])
    , "cardDrewAction" .= False
    , "cardDrewRules" .= ([] :: [Value])
    , "cardDrewTarget" .= Null
    ]
  encounter <- typed $ object
    [ "id" .= ("00000000-0000-0000-0000-000000000000" :: Text)
    , "cardCode" .= ("c00000" :: Text)
    , "originalCardCode" .= ("c00000" :: Text)
    , "isFlipped" .= Null
    , "facedown" .= Null
    , "addedPeril" .= False
    , "owner" .= Null
    ]
  token <- typed $ object
    [ "chaosTokenId" .= ("00000000-0000-0000-0000-000000000000" :: Text)
    , "chaosTokenFace" .= ("Zero" :: Text)
    , "chaosTokenRevealedBy" .= Null
    , "chaosTokenCancelled" .= False
    , "chaosTokenSealed" .= False
    ]
  let draw = DrawChaosToken investigator token
      drewCards = DrewCards investigator cardDrew
      randomArms =
        [ drewCards
        , InvestigatorDrewEncounterCard investigator encounter
        , InvestigatorDrewEncounterCardFrom investigator encounter Nothing
        , InvestigatorDrawEnemy investigator $ EnemyId UUID.nil
        , draw
        , RevealChaosToken source investigator token
        , SilentRevealChaosToken source investigator token
        , RequestedChaosTokens source Nothing [token]
        ]
      fallbackMessages =
        [ After draw
        , After $ RevealChaosToken source investigator token
        , After $ InvestigatorDrewEncounterCard investigator encounter
        , After $ InvestigatorDrawEnemy investigator $ EnemyId UUID.nil
        ]
  assert "undo metadata random classifier leaves Noop false" $ not $ isRandomOutcomeMessage Noop
  for_ (zip [1 :: Int ..] randomArms) $ \(index, message) ->
    assert ("undo metadata Shared random classifier direct arm " <> tshow index) $ isRandomOutcomeMessage message
  for_ (zip [1 :: Int ..] fallbackMessages) $ \(index, message) ->
    assert ("undo metadata Shared messageType fallback arm " <> tshow index) $ isRandomOutcomeMessage message
  assert "undo metadata empty requested-token message is not random" $ not $ isRandomOutcomeMessage $ RequestedChaosTokens source Nothing []

  step <- typed $ object ["tag" .= ("PrologueStep" :: Text)]
  let phaseA = toEnum 0
      phaseB = toEnum 1
      observedBegins = [phaseB, phaseB, phaseA, phaseB]
      metadata = finishUndoMetadata True phaseA False observedBegins
      metadataJson = toJSON metadata
      expectedEdges = toJSON
        [ object ["from" .= ("MythosPhase" :: Text), "to" .= ("InvestigationPhase" :: Text)]
        , object ["from" .= ("InvestigationPhase" :: Text), "to" .= ("InvestigationPhase" :: Text)]
        , object ["from" .= ("InvestigationPhase" :: Text), "to" .= ("MythosPhase" :: Text)]
        , object ["from" .= ("MythosPhase" :: Text), "to" .= ("InvestigationPhase" :: Text)]
        ]
  assert "undo metadata phase edges preserve observed order and repeated phase" $ field "phaseEdges" metadataJson == expectedEdges
  assert "undo metadata wire explicitly contains complete schema fields" $ case metadataJson of
    Object fields -> all (`KM.member` fields) $ map K.fromText
      ["schemaVersion", "metadataComplete", "hasRandomResult", "intermissionBoundary", "phaseEdges"]
    _ -> False
  assert "undo metadata reports schema 1 and complete transaction" $ field "schemaVersion" metadataJson == toJSON (1 :: Int) && field "metadataComplete" metadataJson == Bool True
  assert "undo metadata only marks the caller-supplied typed boundary" $ field "intermissionBoundary" metadataJson == Bool True && field "intermissionBoundary" (toJSON $ finishUndoMetadata False phaseA False observedBegins) == Bool False
  assert "undo metadata random flag accumulates as an OR across observed messages" $
    hasRandomResult (finishUndoMetadata False phaseA True observedBegins)

  assert "undo metadata finalize boundary requires typed true flag" $
    isIntermissionAnswer (CampaignStepAnswerFor player True step)
      && not (isIntermissionAnswer $ CampaignStepAnswerFor player False step)
      && not (isIntermissionAnswer $ CampaignStepAnswer step)

  let modeRequests =
        [ ("standard", "StandardUndo"), ("full", "FullUndo"), ("light", "LightUndo")
        , ("hardcore", "HardcoreUndo"), ("expert", "ExpertUndo")
        ]
  parsedRequests <- for modeRequests $ \(wireMode, expected) -> do
    request <- typed @CreateRequest $ object
      ["scenario" .= ("01104" :: Text), "seed" .= (20261101 :: Int), "players" .= [player], "undoMode" .= (wireMode :: Text)]
    assert ("undo mode request parses " <> wireMode) $ show (createUndoMode request) == expected
    pure request
  let defaultRequest = object ["scenario" .= ("01104" :: Text), "seed" .= (20261101 :: Int), "players" .= [player]]
  defaultParsed <- typed @CreateRequest defaultRequest
  assert "undo mode omitted from create request keeps original full default" $ show (createUndoMode defaultParsed) == "FullUndo"
  let selected = fromJustNote "parsed hardcore undo mode" $ find ((== "HardcoreUndo") . show . createUndoMode) parsedRequests
  (initial, _) <- createSession selected >>= right
  assert "undo mode create request initializes existing game setting" $
    field "settingsUndoMode" (toJSON $ gameSettings $ snapshotGame initial) == String "hardcore"
  observedEnv <- makeEnv (snapshotGame initial) []
  for_ [[Begin phaseB], [draw, Begin phaseB, Begin phaseA, Begin phaseB]] $ \pass ->
    traverse_ (observeUndoMessage observedEnv) pass
  accumulated <- undoMetadataForAnswer False observedEnv
  assert "undo metadata observer accumulates random and duplicate Begin entries across delivery batches" $
    hasRandomResult accumulated
      && phaseEdges accumulated ==
        [(tshow $ gamePhase $ snapshotGame initial, "InvestigationPhase")
        ,("InvestigationPhase", "InvestigationPhase")
        ,("InvestigationPhase", "MythosPhase"), ("MythosPhase", "InvestigationPhase")]
  simultaneousEnv <- makeEnv (snapshotGame initial) []
  observeUndoMessage simultaneousEnv $ Simultaneously [Begin phaseB, Begin phaseB, draw, Begin phaseA]
  simultaneousMetadata <- undoMetadataForAnswer False simultaneousEnv
  assert "undo metadata observes ordered direct Simultaneously children and random result" $
    hasRandomResult simultaneousMetadata
      && phaseEdges simultaneousMetadata ==
        [(tshow $ gamePhase $ snapshotGame initial, "InvestigationPhase")
        ,("InvestigationPhase", "InvestigationPhase")
        ,("InvestigationPhase", "MythosPhase")]
  nestedSimultaneousEnv <- makeEnv (snapshotGame initial) []
  observeUndoMessage nestedSimultaneousEnv $ Simultaneously [Simultaneously [draw, Begin phaseB]]
  nestedMetadata <- undoMetadataForAnswer False nestedSimultaneousEnv
  assert "undo metadata does not infer nested Simultaneously children that were not observed by this hook" $
    not (hasRandomResult nestedMetadata) && null (phaseEdges nestedMetadata)
  answer <- chooseRealAnswer player deck initial
  let offeredQuestion = fromJustNote "metadata test offered question" $ Map.lookup player $ gameQuestion $ snapshotGame initial
      questionTag = T.takeWhile (/= ' ') $ tshow $ unwrapQuestion offeredQuestion
      answerTag = T.takeWhile (/= ' ') $ tshow answer
  putStrLn $ "UNDO_METADATA_TEST_OFFER questionTag=" <> questionTag <> " answerTag=" <> answerTag
  (withMetadata, events, answerMetadata) <- applyAnswerWithMetadata initial player (snapshotRevision initial) answer >>= right
  (withoutSidecar, plainEvents) <- applyAnswer initial player (snapshotRevision initial) answer >>= right
  let sidecarJson = toJSON answerMetadata
  assert "undo metadata answer result is complete and explicitly versioned" $
    field "metadataComplete" sidecarJson == Bool True && field "schemaVersion" sidecarJson == toJSON (1 :: Int)
  assert "undo metadata answer transaction preserves existing game undo mode" $
    field "settingsUndoMode" (toJSON $ gameSettings $ snapshotGame withMetadata) == String "hardcore"
  assert "undo metadata sidecar does not change snapshot, events, or save bytes" $
    exportSnapshot withMetadata == exportSnapshot withoutSidecar && encode (map eventJSON events) == encode (map eventJSON plainEvents)
  assert "undo metadata is not serialized into ordinary snapshot/save JSON" $
    not $ "undoMetadata" `isInfixOf` decodeUtf8 (BL.toStrict $ exportSnapshot withMetadata)

  ready <- reachPlayerWindow chooseRealAnswer player deck 80 withMetadata
  let readyQuestion = fromJustNote "original 01104 PlayerWindow question" $ Map.lookup player $ gameQuestion $ snapshotGame ready
      actualInvestigators = field "investigators" $ toJSON $ gameEntities $ snapshotGame ready
      actualInvestigator = case actualInvestigators of
        Object values -> fromMaybe (error "Actual player has no investigator in the ready snapshot")
          $ listToMaybe [value | value <- KM.elems values, field "playerId" value == toJSON player]
        _ -> error "Actual investigator map was not an object"
      topCard = case field "deck" actualInvestigator of
        Array cards -> headEx cards
        _ -> error "Actual 01104 investigator deck was not an array"
  actualDrawInvestigator <- typed @InvestigatorId $ field "id" actualInvestigator
  let actualDrawIndex = fromJustNote "original PlayerWindow DrawCards action" $ drawActionIndex actualDrawInvestigator $ unwrapQuestion readyQuestion
      actualDrawAnswer = Answer $ QuestionResponse actualDrawIndex (Just player) Nothing
  actualDrawResult <- typed @Message $ object
    [ "tag" .= ("InvestigatorDrewPlayerCard" :: Text)
    , "contents" .= [toJSON actualDrawInvestigator, topCard]
    ]
  assert "undo metadata direct classifier recognizes typed actual PlayerCard draw result" $
    isRandomOutcomeMessage actualDrawResult
  (drawWithMetadata, drawEvents, drawMetadata) <-
    applyAnswerWithMetadata ready player (snapshotRevision ready) actualDrawAnswer >>= right
  (drawWithoutSidecar, drawEventsWithoutSidecar) <-
    applyAnswer ready player (snapshotRevision ready) actualDrawAnswer >>= right
  let drawMetadataJson = toJSON drawMetadata
  assert "undo metadata actual 01104 PlayerWindow DrawCards answer is complete and random" $
    field "metadataComplete" drawMetadataJson == Bool True
      && field "hasRandomResult" drawMetadataJson == Bool True
  assert "undo metadata actual DrawCards answer preserves Save bytes and events" $
    exportSnapshot drawWithMetadata == exportSnapshot drawWithoutSidecar
      && encode (map eventJSON drawEvents) == encode (map eventJSON drawEventsWithoutSidecar)
  putStrLn "UNDO_METADATA_INTEGRATION_COMPLETED"
