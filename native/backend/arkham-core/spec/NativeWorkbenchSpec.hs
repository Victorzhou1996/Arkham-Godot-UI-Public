module NativeWorkbenchSpec (nativeWorkbenchSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Native.Answer
import Arkham.Native.Session
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.ByteString.Lazy.Char8 qualified as BL
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label good = if good then putStrLn ("PASS "<>label) else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right = either (\e->putStrLn e>>exitFailure) pure
field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null

snapshotAfter :: Value -> Snapshot
snapshotAfter row = toResult $ field "after" row

indexedWire :: Answer -> Value
indexedWire (Answer (QuestionResponse choice who version)) = object
  ["tag" .= ("Answer"::Text),"contents" .= object ["choice" .= choice,"playerId" .= who,"questionVersion" .= version]]
indexedWire _ = error "Original customization helper returned a non-indexed answer"

nativeWorkbenchSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeWorkbenchSpecs player choose = do
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e->P.putStrLn e>>exitFailure) pure
  let deck = headEx decks
      schema s = field "workbench" $ debugView (snapshotGame s) player
      ready s = field "available" (schema s)==Bool True
      step s = choose player deck s >>= \response -> fst <$> (applyAnswer s player (snapshotRevision s) response >>= right)
      untilReady budget s
        | ready s = pure s
        | budget<=(0::Int) = error "Actual workbench checkpoint not reached"
        | otherwise = step s >>= untilReady (budget-1)
      continueCustom budget s
        | ready s = pure (s,[]::[Value])
        | budget<=(0::Int) = error "Original customization did not return to player window"
        | otherwise = do
            answer <- choose player deck s
            (next,_) <- applyAnswer s player (snapshotRevision s) answer >>= right
            imported <- importSnapshot (exportSnapshot s)>>=right
            (replayed,_) <- applyAnswer imported player (snapshotRevision imported) answer >>= right
            assert "original customization answer exact full-save replay" $ exportSnapshot next==exportSnapshot replayed
            (completed,rest) <- continueCustom (budget-1) next
            pure (completed,object ["before" .= s,"answer" .= indexedWire answer,"after" .= next]:rest)
      apply s command = fst <$> (applyDebug s player (snapshotRevision s) True command >>= right)
      rejects s command = applyDebug s player (snapshotRevision s) True command >>= \result -> assert "workbench invalid/foreign command rejected" $ either (const True) (const False) result
      trace basis commands = foldM (\(before,rows) command->do
        after <- apply before command
        imported <- importSnapshot (exportSnapshot before)>>=right
        replayed <- apply imported command
        assert "workbench full Game queue Int64 RNG exact after fresh import" $ exportSnapshot after==exportSnapshot replayed
        assert "workbench one revision and real pending question" $ snapshotRevision after==snapshotRevision before+1 && not (Map.null $ gameQuestion $ snapshotGame after)
        pure (after,rows<>[object ["before" .= before,"command" .= command,"after" .= after]])) (basis,[]::[Value]) commands
      emit before rows after = BL.putStrLn $ "WORKBENCH_FIXTURE "<>encode (object ["before" .= before,"trace" .= rows,"after" .= after])
      create code placement iid destination = object ["operation" .= ("createCard"::Text),"cardCode" .= (code::Text),"placement" .= (placement::Text),"investigatorId" .= iid,"deck" .= destination]
  (created,_) <- createSession (CreateRequest Nothing (Just "01104") 201020 [player] Standard False False)>>=right
  basis <- untilReady 120 created
  let iid = fst $ headEx $ Map.toList (gameEntities $ snapshotGame basis).investigators
      handCommand = create "c01088" "hand" (toJSON iid) Null
  assert "workbench schema exposes twenty closed commands" $ case field "operations" $ schema basis of Array a -> length a==20;_ -> False
  (handed,handTrace) <- trace basis [handCommand]
  let newCards = Map.keys (gameCards $ snapshotGame handed) \\ Map.keys (gameCards $ snapshotGame basis)
  assert "core generated and registered exactly one physical card identity" $ length newCards==1
  emit basis handTrace handed
  (played,playTrace) <- trace handed [create "c01020" "play" (toJSON iid) Null]
  let newAssets = Map.keys (gameEntities $ snapshotGame played).assets \\ Map.keys (gameEntities $ snapshotGame handed).assets
  assert "created card and original debug placement share one transaction" $ length newAssets==1
  let aid = headEx newAssets
      target = object ["tag" .= ("AssetTarget"::Text),"contents" .= aid]
      readyCommand flag = object ["operation" .= ("setReady"::Text),"target" .= target,"ready" .= flag]
  (readied,readyTrace) <- trace played [readyCommand False,readyCommand True]
  let exhausted s = field "exhausted" $ toJSON $ fromJustNote "created physical asset" $ Map.lookup aid (gameEntities $ snapshotGame s).assets
  assert "ready and exhaust commands actually change selected existing asset" $ exhausted played==Bool False && exhausted (snapshotAfter $ headEx readyTrace)==Bool True && exhausted readied==Bool False
  emit handed (playTrace<>readyTrace) readied
  let tokenChange token amount = object ["operation" .= ("adjustTokens"::Text),"target" .= target,"token" .= token,"amount" .= (amount::Int)]
      clear targetValue = object ["operation" .= ("clearEntityTokens"::Text),"target" .= targetValue]
  (cleared,clearTrace) <- trace readied [tokenChange ("Damage"::Text) 2,tokenChange ("Horror"::Text) 1,tokenChange ("Ammo"::Text) 3,tokenChange ("Secret"::Text) 2,clear target]
  assert "clear-token button removes all asset token types in one original transaction" $ field "tokens" (toJSON $ fromJustNote "clear target" $ Map.lookup aid (gameEntities $ snapshotGame cleared).assets)==toJSON ([]::[(Text,Int)])
  emit readied clearTrace cleared
  (enemyCreated,_) <- trace cleared [create "c01160" "play" (toJSON iid) Null]
  enemyReady <- untilReady 60 enemyCreated
  let enemies = Map.keys (gameEntities $ snapshotGame enemyReady).enemies
      eid = headEx enemies
      enemyTarget = object ["tag" .= ("EnemyTarget"::Text),"contents" .= eid]
      damage amount = object ["operation" .= ("dealEnemyDamage"::Text),"enemyId" .= eid,"investigatorId" .= iid,"amount" .= (amount::Int)]
  (damaged,damageTrace) <- trace enemyReady [damage 1]
  assert "enemy damage button uses original damage resolution" $ lookup "Damage" (toResult @[(Text,Int)] $ field "tokens" $ toJSON $ fromJustNote "damaged enemy" $ Map.lookup eid (gameEntities $ snapshotGame damaged).enemies)==Just 1
  (enemyCleared,enemyClearTrace) <- trace damaged [clear enemyTarget]
  assert "clear-token button removes enemy damage in one original transaction" $ field "tokens" (toJSON $ fromJustNote "cleared enemy" $ Map.lookup eid (gameEntities $ snapshotGame enemyCleared).enemies)==toJSON ([]::[(Text,Int)])
  emit enemyReady (damageTrace<>enemyClearTrace) enemyCleared
  rejects enemyReady $ damage 0
  rejects enemyReady $ damage 6
  rejects readied $ clear $ object ["tag" .= ("InvestigatorTarget"::Text),"contents" .= iid]
  let edit action = object ["operation" .= ("editChaosBag"::Text),"action" .= (action::Text),"face" .= ("PlusOne"::Text)]
      difficulty = object ["operation" .= ("setDifficulty"::Text),"difficulty" .= ("Hard"::Text)]
  (bagged,bagTrace) <- trace readied [edit "add",edit "remove",difficulty]
  let plusCount s = case field "bagTokens" $ schema s of Array tokens -> length $ filter ((==String "PlusOne") . field "chaosTokenFace") $ toList tokens; _ -> 0
  assert "bag commands actually add then remove one supplied face" $ plusCount (snapshotAfter $ headEx bagTrace)==plusCount readied+1 && plusCount bagged==plusCount readied
  assert "difficulty command reaches actual original scenario field" $ field "difficulty" (field "That" $ toJSON $ gameMode $ snapshotGame bagged)==String "Hard"
  emit readied bagTrace bagged
  (quill,quillTrace) <- trace bagged [create "c09042" "hand" (toJSON iid) Null]
  let quillIds = Map.keys (gameCards $ snapshotGame quill) \\ Map.keys (gameCards $ snapshotGame bagged)
      customize = object ["operation" .= ("customizeCard"::Text),"investigatorId" .= iid,"cardId" .= headEx quillIds]
  (customized,customTrace) <- trace quill [customize]
  assert "original customization ask replaces old player window" $ encode (gameQuestion $ snapshotGame customized)/=encode (gameQuestion $ snapshotGame quill)
  (completed,answerTrace) <- continueCustom 30 customized
  assert "one actual original customization selection returns to player window" $ ready completed
  BL.putStrLn $ "WORKBENCH_CUSTOM_CONTINUATION "<>encode (object ["before" .= customized,"trace" .= answerTrace,"after" .= completed])
  emit bagged (quillTrace<>customTrace) customized
  rejects customized difficulty
  rejects basis $ create "cDoesNotExist" "hand" (toJSON iid) Null
  rejects basis $ create "c01088" "hand" (toJSON iid) (String "EncounterDeck")
  rejects basis $ create "c01088" "unknown" (toJSON iid) Null
  rejects basis $ create "c01088" "campaignDeck" (toJSON iid) Null
  rejects basis $ object ["operation" .= ("editChaosBag"::Text),"action" .= ("add"::Text),"face" .= object ["tag" .= ("CustomToken"::Text),"contents" .= (":not-in-this-bag:"::Text)]]
  rejects basis $ object ["operation" .= ("setReady"::Text),"target" .= object ["tag" .= ("LocationTarget"::Text),"contents" .= ("00000000-0000-4000-8000-000000000099"::Text)],"ready" .= True]
  disabled <- applyDebug basis player (snapshotRevision basis) False handCommand
  assert "workbench disabled rejects without changing old checkpoint" $ either (const True) (const False) disabled
  stale <- applyDebug basis player (snapshotRevision basis+1) True handCommand
  assert "workbench stale revision rejects before materialization" $ either (const True) (const False) stale
  putStrLn "WORKBENCH_INTEGRATION_COMPLETED"
