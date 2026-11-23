module NativeScenarioDebugSpec (nativeScenarioDebugSpecs) where

import Arkham.Prelude hiding (assert,put)
import Arkham.Game (Game (..))
import Arkham.Difficulty
import Arkham.Id
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Debug.Scenario qualified as Scenario
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Either (isLeft,isRight)
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert name good=if good then putStrLn ("PASS "<>name) else putStrLn ("FAIL "<>name)>>exitFailure
right :: Either Text a -> IO a
right=either (\reason->putStrLn reason>>exitFailure) pure
field :: Text -> Value -> Value
field name (Object o)=fromMaybe Null $ KM.lookup (K.fromText name) o
field _ _=Null
put :: Text -> Value -> Value -> Value
put name value (Object o)=Object $ KM.insert (K.fromText name) value o
put _ _ _=error "Expected controlled object fixture"
rows :: Value -> [Value]
rows (Array values)=toList values
rows _=[]

nativeScenarioDebugSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeScenarioDebugSpecs player choose=do
  decks<-eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason->putStrLn (fromString reason)>>exitFailure) pure
  let schema s=field "scenario" $ debugView (snapshotGame s) player
      ready s=field "available" (schema s)==Bool True
      untilReady budget s
        |ready s=pure s
        |budget<=(0::Int)=assert "scenario adapter reaches real player window" False>>pure s
        |otherwise=do
          answer<-choose player (headEx decks) s
          (next,_)<-applyAnswer s player (snapshotRevision s) answer >>= right
          untilReady (budget-1) next
      apply s command=fst <$> (applyDebug s player (snapshotRevision s) True command >>= right)
      trace before commands=foldM (\(basis,steps) command->do
        let old=exportSnapshot basis
        after<-apply basis command
        imported<-importSnapshot old >>= right
        replayed<-apply imported command
        assert "scenario adapter exact full Game queue Int64 RNG replay" $ exportSnapshot after==exportSnapshot replayed
        assert "scenario adapter one native revision and old basis unchanged" $ snapshotRevision after==snapshotRevision basis+1 && exportSnapshot basis==old
        pure (after,steps<>[object ["before" .= basis,"command" .= command,"after" .= after]])) (before,[]::[Value]) commands
      emit label before steps after=BL.putStrLn $ "SCENARIO_DEBUG_FIXTURE "<>encode (object
        ["label" .= (label::Text),"before" .= before,"trace" .= steps,"after" .= after
        ,"adapterFixtureNotNaturalScenarioCompletion" .= True])
      scenarioEdit f s=s {snapshotGame=toResult $ put "gameMode" (put "That" (f $ field "That" mode) mode) $ toJSON $ snapshotGame s}
        where mode=toJSON $ gameMode $ snapshotGame s
      withScenario sid basis=do
        (original,_)<-createSession (CreateRequest Nothing (Just sid) 20261021 [player] Standard False False) >>= right
        pure basis {snapshotGame=toResult $ put "gameMode" (toJSON $ gameMode $ snapshotGame original) $ toJSON $ snapshotGame basis}
  (initial,_)<-createSession (CreateRequest Nothing (Just "01104") 20261020 [player] Standard False False) >>= right
  base<-untilReady 120 initial
  assert "scenario adapter exposes five closed operations" $ length (rows $ field "operations" $ schema base)==5
  let agenda=headEx $ rows $ field "agendas" $ schema base
      doom amount=object ["operation" .= ("agendaDoom"::Text),"agendaId" .= field "id" agenda,"amount" .= (amount::Int)]
  (doomed,doomTrace)<-trace base [doom 1,doom (-1)]
  assert "scenario adapter uses actual original agenda token effects" $
    field "doom" (headEx $ rows $ field "agendas" $ schema $ toResult $ field "after" $ headEx doomTrace)==toJSON (1::Int)
      && field "doom" (headEx $ rows $ field "agendas" $ schema doomed)==toJSON (0::Int)
  emit "actual-gathering-agenda" base doomTrace doomed
  let token ident face=object ["bagTokenId" .= (ident::Text),"bagTokenFace" .= (face::Text)]
      bag=object ["bagTokens" .= [token "10000000-0000-4000-8000-000000000001" "Zero",token "10000000-0000-4000-8000-000000000002" "Skull"]
        ,"bagSetAside" .= ([]::[Value]),"bagCurrentToken" .= Null,"bagCancelNext" .= False,"bagDebugNext" .= Null]
      bagBase=scenarioEdit (put "customChaosBags" $ object ["adapter-test" .= bag]) base
      bagCommand action face tid=object ["operation" .= ("editAuxiliaryBag"::Text),"bag" .= ("scenario:adapter-test"::Text)
        ,"action" .= (action::Text),"face" .= face,"tokenId" .= tid]
      tid=String "10000000-0000-4000-8000-000000000001"
      bagState s=field "state" $ headEx $ rows $ field "bags" $ schema s
  assert "scenario adapter exposes original named bag separately from skill test bag" $ length (rows $ field "bags" $ schema bagBase)==1
  (bagAfter,bagTrace)<-trace bagBase [bagCommand "next" (String "Zero") Null,bagCommand "setAside" Null tid
    ,bagCommand "return" Null tid,bagCommand "add" (String "PlusOne") Null,bagCommand "clear" Null Null]
  assert "scenario adapter named bag original override set-aside return and random add run" $
    length (rows $ field "bagTokens" $ bagState bagAfter)==3 && null (rows $ field "bagSetAside" $ bagState bagAfter)
      && field "bagDebugNext" (bagState bagAfter)==Null
  assert "scenario adapter bag removal never edits current reveal" $ isLeft $ Scenario.prepare (snapshotGame bagBase) player $ bagCommand "remove" Null (String "10000000-0000-4000-8000-000000000099")
  assert "scenario adapter cannot draw a face from an unavailable bag pile" $ isLeft $ Scenario.prepare (snapshotGame bagBase) player $ bagCommand "next" (String "PlusOne") Null
  emit "controlled-named-auxiliary-bag" bagBase bagTrace bagAfter
  abyss<-withScenario "83001" base
  let setCount key=object ["operation" .= ("setScenarioCount"::Text),"key" .= (key::Text),"amount" .= (7::Int)]
  (countAfter,countTrace)<-trace abyss [setCount "StrengthOfTheAbyss"]
  assert "scenario adapter original scenario count handler updates actual field" $ field "value" (headEx $ rows $ field "counts" $ schema countAfter)==toJSON (7::Int)
  assert "scenario adapter count cannot be borrowed by another scenario" $ isLeft $ Scenario.prepare (snapshotGame base) player $ setCount "StrengthOfTheAbyss"
  emit "controlled-original-abyss-scenario-on-paused-board" abyss countTrace countAfter
  blob<-withScenario "85001" base
  let special action faces=object ["operation" .= ("scenarioDebugAction"::Text),"action" .= (action::Text),"faces" .= (faces::[Text])]
  (blobAfter,blobTrace)<-trace blob [special "blobRealityAcid" ["Skull","Cultist"],special "clearRealityAcid" []]
  assert "scenario adapter Blob original metadata setter and clear really run" $ field "realityAcidTokens" (schema blobAfter)==toJSON ([]::[Text])
  assert "scenario adapter rejects arbitrary scenario-specific key and payload" $
    isLeft (Scenario.prepare (snapshotGame blob) player $ special "blobSetLightActive" [])
      && isLeft (Scenario.prepare (snapshotGame blob) player $ special "blobRealityAcid" ["Zero"])
  emit "controlled-original-blob-scenario-on-paused-board" blob blobTrace blobAfter
  -- Physical endpoints are generated by the existing core placement path.
  -- Only the scenario/meta checkpoint is controlled; no natural completion is claimed.
  twoLocations<-apply base $ object ["operation" .= ("createCard"::Text),"cardCode" .= ("c01112"::Text)
    ,"placement" .= ("play"::Text),"investigatorId" .= ("c60151"::Text),"deck" .= Null]
  let locationIds=case field "locations" $ field "gameEntities" $ toJSON $ snapshotGame twoLocations of Object o->sort $ map K.toText $ KM.keys o;_->[]
  assert "scenario barrier adapter checkpoint has two real core-generated endpoints" $ length locationIds>=2
  let pair=(headEx locationIds)<>"--"<>(fromJustNote "second endpoint" $ locationIds !!? 1)
      meta=object ["barriers" .= Object (KM.singleton (K.fromText pair) $ toJSON (2::Int))]
  barrierScenario<-withScenario "07123" twoLocations
  let barrierBase=scenarioEdit (put "meta" meta) barrierScenario
      wrongBarrierScenario=scenarioEdit (put "meta" meta) twoLocations
      offered=headEx $ rows $ field "barriers" $ schema barrierBase
      decrement=object ["operation" .= ("removeScenarioBarrier"::Text),"barrier" .= field "id" offered]
  assert "scenario barrier adapter ignores unrelated same-named metadata" $ null $ rows $ field "barriers" $ schema wrongBarrierScenario
  (barrierAfter,barrierTrace)<-trace barrierBase [decrement]
  assert "scenario barrier adapter original consumer decrements only offered connection" $
    field "value" (headEx $ rows $ field "barriers" $ schema barrierAfter)==toJSON (1::Int)
  assert "scenario barrier adapter unknown connection rejects" $ isLeft $ Scenario.prepare (snapshotGame barrierBase) player $ put "barrier" (String "foreign") decrement
  emit "controlled-original-in-too-deep-barrier-on-generated-endpoints" barrierBase barrierTrace barrierAfter
  cthulhu<-withScenario "11688a" base
  assert "scenario adapter Cthulhu draw is offered only by actual original scenario" $
    isRight (Scenario.prepare (snapshotGame cthulhu) player $ special "drawCthulhu" [])
      && isLeft (Scenario.prepare (snapshotGame base) player $ special "drawCthulhu" [])
  assert "scenario adapter strict extra fields and direct doom underflow reject" $
    isLeft (Scenario.prepare (snapshotGame base) player $ put "message" Null $ doom 1)
      && isLeft (Scenario.prepare (snapshotGame base) player $ doom (-1))
  disabled<-applyDebug base player (snapshotRevision base) False $ doom 1
  stale<-applyDebug base player (snapshotRevision base+1) True $ doom 1
  let unsafe=base {snapshotGame=(snapshotGame base) {gameInAction=True}}
  blocked<-applyDebug unsafe player (snapshotRevision unsafe) True $ doom 1
  assert "scenario adapter disabled stale and active-action guards reject" $ all isLeft [disabled,stale,blocked]
  assert "scenario adapter all failed probes preserve original whole pending state" $ ready base && snapshotRevision base<snapshotRevision doomed
  putStrLn "SCENARIO_DEBUG_INTEGRATION_COMPLETED"
