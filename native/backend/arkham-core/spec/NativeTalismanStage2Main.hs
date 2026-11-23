{-# LANGUAGE BlockArguments, OverloadedRecordDot, OverloadedStrings, NoImplicitPrelude, TypeApplications #-}
module NativeTalismanStage2Main (main) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation (indexedChoices)
import Arkham.Question
import Control.Exception qualified as E
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.UUID qualified as UUID
import Data.Text qualified as T
import Data.ByteString.Lazy.Char8 qualified as BL
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label) else putStrLn ("FAIL " <> label) >> exitFailure
right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure
jsonField :: Text -> Value -> Value
jsonField key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
jsonField _ _ = Null
setField :: Text -> Value -> Value -> Value
setField key value (Object o) = Object $ KM.insert (K.fromText key) value o
setField _ _ _ = error "Expected an isolated fixture object"
setExisting :: Text -> Value -> Value -> Value
setExisting key value original@(Object o)
 | KM.member (K.fromText key) o = setField key value original
 | otherwise = error $ "Actual source JSON has no fixture field: "<>T.unpack key
setExisting _ _ _ = error "Expected an existing fixture object"
values :: Value -> [Value]
values (Array xs) = toList xs
values (Object o) = KM.elems o
values _ = []
mapObject :: (Text -> Value -> Value) -> Value -> Value
mapObject f (Object o) = Object $ KM.mapWithKey (f . K.toText) o
mapObject _ _ = error "Expected a fixture entity map"
entities :: Snapshot -> Value
entities = jsonField "gameEntities" . toJSON . snapshotGame
entityRows :: Text -> Snapshot -> [Value]
entityRows name = values . jsonField name . entities
entityId :: Value -> Value
entityId = jsonField "id"
contains :: Value -> Value -> Bool
contains needle value = needle==value || any (contains needle) (values value)
atLocation :: Value -> Value
atLocation location = object ["tag" .= ("AtLocation"::Text),"contents" .= location]
tokens :: Int -> Value -> Value
tokens clues value = setExisting "tokens" (toJSON $ ("Clue" :: Text, clues) : filter ((/= "Clue") . fst) existing) value
 where existing = toResult @[(Text, Int)] $ jsonField "tokens" value
backSequence :: Value -> Value
backSequence act = case values $ jsonField "sequence" act of
 [stage,String "A"] | stage==toJSON (2::Int) -> toJSON [stage,String "B"]
 _ -> error "Actual source Act2 sequence is not the exported [2,A] shape"
ariExposureChoice :: Value -> UI Message -> Bool
ariExposureChoice expectedId (AbilityLabel _ ability windows _ _) =
 let encoded=toJSON ability
     actualSource=jsonField "source" encoded
     source=object ["tag" .= ("AssetSource"::Text),"contents" .= expectedId]
     indexedSource=object ["tag" .= ("AbilitySource"::Text),"contents" .= [source,toJSON (1::Int)]]
     exposed window=jsonField "tag" (jsonField "windowType" window)==String "CampaignEvent"
       && case values $ jsonField "contents" $ jsonField "windowType" window of
         String name:_ -> name `elem` ["exposed[enemy]","exposed[location]"]
         _ -> False
  in expectedId/=Null && (actualSource==source || actualSource==indexedSource)
    && jsonField "index" encoded==toJSON (1::Int) && any (exposed . toJSON) windows
ariExposureChoice _ _ = False
sentinel :: Question Message
sentinel = ChooseOne [Label "talisman-stage2-finished" [Noop]]

choose :: PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer
choose player deck s = case unwrapQuestion $ fromJustNote "actual recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s of
 ChooseDeck -> pure $ DeckListAnswer deck player
 ChooseUpgradeDeck -> pure $ DeckListAnswer deck player
 ChooseJoinDeck {} -> pure $ DeckListAnswer deck player
 ContinueCampaign -> do
   let campaign=jsonField "This" $ toJSON $ gameMode $ snapshotGame s
       continuation=jsonField "contents" $ jsonField "step" campaign
       next=jsonField "nextStep" continuation
   assert "actual campaign continuation provides its next step" $ next/=Null
   pure $ CampaignStepAnswer $ toResult next
 PickScenarioSettings -> defaultSettings
 PickCampaignSettings -> defaultSettings
 ChooseAmounts _ (TotalAmountTarget total) rows _ -> do
   let ranges=[(key,lo,hi) | AmountChoice key _ lo hi <- rows]
       combinations=product [toInteger hi-toInteger lo+1 | (_,lo,hi)<-ranges]
   assert "bounded legal allocation ranges" $ combinations<=10000 && all (\(_,lo,hi)->lo<=hi) ranges
   let selected=fromJustNote "legal allocation" $ listToMaybe [xs | xs<-sequence [[(key,n) | n<-[lo..hi]] | (key,lo,hi)<-ranges],sum (map (toInteger . snd) xs)==toInteger total]
   pure $ AmountsAnswer $ AmountsResponse (Map.fromList selected) (Just $ gameScenarioSteps $ snapshotGame s) (Just player)
 q -> case indexedChoices q >>= listToMaybe . map fst . filter (\(_,c)->case c of InvalidLabel {}->False;Info {}->False;_->True) . zip [0..] of
   Just n -> pure $ Answer $ QuestionResponse n (Just player) Nothing
   Nothing -> error $ "Unsupported actual setup question: "<>show q
 where
 defaultSettings = do
   let form=settingsFormView (snapshotGame s) player
   assert "actual offered settings form is supported" $ jsonField "supported" form==Bool True
   pure $ toResult @Answer $ object ["tag" .= ("SettingsFormAnswer"::Text),"contents" .= object
     ["schemaId" .= jsonField "schemaId" form,"valuesByRowId" .= ([]::[Value])]]

main :: IO ()
main = do
 args <- getArgs
 assert "explicit isolated mode" $ length args==length (nub args) && all (`elem` ["--expect-old-bug","--probe-empty-cultists"]) args
 let expectOld="--expect-old-bug" `elem` args
     probeEmpty="--probe-empty-cultists" `elem` args
     player=PlayerId $ fromJustNote "fixture player" $ UUID.fromString "00000000-0000-4000-8000-000000000101"
 decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (error . fromString) pure
 exposureWindows<-newIORef (0::Int)
 ariId<-newIORef Null
 tracedQuestions<-newIORef (0::Int)
 tracePhase<-newIORef ("setup"::Text)
 let deck=headEx decks
     ready s=jsonField "available" (jsonField "workbench" $ debugView (snapshotGame s) player)==Bool True
     actCode code s=toResult @ActId . entityId <$> find ((==String ("c"<>code)) . entityId) (entityRows "acts" s)
     step s=do
       let who=fst $ headEx $ Map.toList $ gameQuestion $ snapshotGame s
           choices=concatMap (fromMaybe [] . indexedChoices . unwrapQuestion) $ Map.elems $ gameQuestion $ snapshotGame s
       expectedId<-readIORef ariId
       let ariOffered=any (ariExposureChoice expectedId) choices
       when ariOffered $ modifyIORef' exposureWindows (+1)
       response<-choose who deck s
       traced<-readIORef tracedQuestions
       when (traced<80) do
         phase<-readIORef tracePhase
         let question=unwrapQuestion $ fromJustNote "actual trace recipient" $ Map.lookup who $ gameQuestion $ snapshotGame s
             chosen=case response of Answer r->Just $ qrChoice r;_->Nothing
         BL.putStrLn $ "TALISMAN_QUESTION "<>encode (object ["phase" .= phase,"step" .= gameScenarioSteps (snapshotGame s),"tag" .= jsonField "tag" (toJSON question),"choiceCount" .= length choices,"firstChoiceTags" .= map (jsonField "tag" . toJSON) (take 6 choices),"chosenIndex" .= chosen,"ariExposureOffered" .= ariOffered,"talismanKeys" .= length (filter ((==String "c09590") . entityId) $ entityRows "scarletKeys" s)])
         modifyIORef' tracedQuestions (+1)
       fst <$> (applyAnswer s who (snapshotRevision s) response >>= right)
     untilDone label budget done s
       | done s=pure s
       | budget<=(0::Int)=error $ "Actual rule flow did not finish: "<>T.unpack label
       | otherwise=step s >>= untilDone label (budget-1) done
     run s messages=do
       -- The public Game JSON round-trip drops its runtime-only action-start
       -- snapshot. It never opens or alters a save file or a player's profile.
       let game=(toResult @Game $ toJSON $ snapshotGame s){gameQuestion=Map.empty,gameRetainedQuestion=False}
       env<-makeEnv game messages
       advance env
       fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s+1) env
     createCard s code=do
       let iid=entityId $ headEx $ entityRows "investigators" s
       fst <$> (applyDebug s player (snapshotRevision s) True (object ["operation" .= ("createCard"::Text),"cardCode" .= (code::Text),"placement" .= ("play"::Text),"investigatorId" .= iid,"deck" .= Null]) >>= right)
 -- Use the original campaign prologue to install TheScarletKeysMeta. A
 -- standalone 09566 has no campaign and its original keysFor helper cannot be
 -- used to seed this test. Stop at the real continuation before chapter one;
 -- no campaign metadata or completed chapter is fabricated here.
 (created,_)<-createSession (CreateRequest (Just "09") Nothing 201116 [player] Standard False False) >>= right
 let atContinuation s=any ((==ContinueCampaign) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame s)
       && (case jsonField "meta" $ jsonField "This" $ toJSON $ gameMode $ snapshotGame s of Object o->not $ KM.null o;_->False)
 campaignReady<-untilDone "real TSC prologue and deck setup" 100 atContinuation created
 let campaign=jsonField "This" $ toJSON $ gameMode $ snapshotGame campaignReady
 assert "real TSC prologue supplied campaign metadata and one investigator" $
   jsonField "id" campaign==String "09" && (case jsonField "meta" campaign of Object o->not $ KM.null o;_->False)
     && length (entityRows "investigators" campaignReady)==1
 -- The trusted isolated driver selects the original chapter transition. Its
 -- native Campaign.Runner performs all resets and StartScenario itself.
 entering<-run campaignReady [NextCampaignStep $ Just $ toResult $ object ["tag" .= ("ScenarioStep"::Text),"contents" .= ("09566"::Text)]]
 stage1<-untilDone "real campaign 09566 setup" 300 ready entering
 assert "real chapter transition preserves the initialized TSC campaign" $
   jsonField "id" (jsonField "This" $ toJSON $ gameMode $ snapshotGame stage1)==String "09"
     && jsonField "id" (jsonField "That" $ toJSON $ gameMode $ snapshotGame stage1)==String "c09566"
 withAri<-createCard stage1 "c09763" >>= untilDone "real Ari Quinn creation" 100 ready
 let oldAssetIds=map entityId $ entityRows "assets" stage1
     ariAssets=filter ((`notElem` oldAssetIds) . entityId) $ entityRows "assets" withAri
 assert "actual debug creation yielded one ready Ari Quinn" $ length ariAssets==1 && jsonField "cardCode" (headEx ariAssets)==String "c09763" && jsonField "exhausted" (headEx ariAssets)==Bool False
 writeIORef ariId $ entityId $ headEx ariAssets
 twoCultists<-foldM (\s code->createCard s code >>= untilDone "real enemy creation" 100 ready) withAri ["c09586a","c09586b"]
 let firstAct=fromJustNote "actual Act1" $ actCode "09568" twoCultists
 stage1Advancing<-run twoCultists [AdvanceAct firstAct (toResult $ object ["tag" .= ("GameSource"::Text)]) AdvancedWithOther]
 stage2<-untilDone "actual Act1 to Act2" 250 (\s->isJust (actCode "09569" s)&&ready s) stage1Advancing
 -- The two enemies were materialized by the original debug create-card path.
 -- Identify them from their real exported IDs, not a hidden matcher import.
 let originalEnemyIds=map entityId $ entityRows "enemies" withAri
     cultists=map entityId $ filter (\enemy->entityId enemy `notElem` originalEnemyIds
       && jsonField "cardCode" enemy `elem` [String "c09586a",String "c09586b"]) $ entityRows "enemies" stage2
     eastMarker=object ["tag" .= ("ScenarioModifier"::Text),"contents" .= ("eastGate"::Text)]
     eastTargets=[jsonField "contents" target | row<-values $ jsonField "gameModifiers" $ toJSON $ snapshotGame stage2,[target,mods]<-[values row],jsonField "tag" target==String "LocationTarget",contains eastMarker mods]
     entranceRows=filter ((==toJSON [1::Int,0]) . jsonField "position") $ entityRows "locations" stage2
     eastGate=headEx eastTargets
     entrance=entityId $ headEx entranceRows
 assert "actual exported grid has one east marker and one entrance" $ length eastTargets==1 && length entranceRows==1
 assert "actual setup provides exactly two fixture cultists and distinct east gate" $ length cultists==2 && eastGate/=entrance
 assert "actual Unveiling story exists" $ length (filter ((==String "c09571a") . entityId) $ entityRows "stories" stage2)==1
 let aid=fromJustNote "actual Act2" $ actCode "09569" stage2
     -- These are ephemeral fixture copies of a fully set-up original game.
     -- No player save/profile/database is read, patched, imported or rewritten.
     fixture cultWins shadow keepMini noCultist playerClues =
       let original=snapshotGame stage2
           originalEntities=entities stage2
           actorLocation=if cultWins then entrance else eastGate
           investigators=mapObject (\_ ->setExisting "placement" (atLocation actorLocation) . tokens playerClues) $ jsonField "investigators" originalEntities
           enemies=if noCultist then Object KM.empty else mapObject (\_ enemy->
             setExisting "placement" (if shadow then object ["tag" .= ("InTheShadows"::Text)] else atLocation entrance)
               $ tokens (if entityId enemy `elem` cultists then 3 else 0) enemy) $ jsonField "enemies" originalEntities
           stories=mapObject (\_ story->tokens (if entityId story==String "c09571a" && cultWins then if noCultist then 8 else 2 else 0) story) $ jsonField "stories" originalEntities
           acts=mapObject (\_ act->if entityId act==toJSON aid then setExisting "sequence" (backSequence act) $ setExisting "flipped" (Bool True) act else act) $ jsonField "acts" originalEntities
           changed=foldl' (\e (name,value)->setExisting name value e) originalEntities
             [("investigators",investigators),("enemies",enemies),("stories",stories),("acts",acts),("concealed",if keepMini then jsonField "concealed" originalEntities else Object KM.empty)]
           seeded=setExisting "gameQuestion" (Object KM.empty) $ setExisting "gameRetainedQuestion" (Bool False) $ setExisting "gameEntities" changed $ toJSON original
           game=toResult @Game seeded
        in if toJSON game/=seeded then error "Actual Game parser did not preserve the complete fixture JSON" else stage2{snapshotGame=game,snapshotQueue=[]}
     completed s=Map.lookup player (gameQuestion $ snapshotGame s)==Just sentinel
     play s=run s [AdvanceAct aid (toResult $ object ["tag" .= ("GameSource"::Text)]) AdvancedWithOther,Ask player sentinel] >>= untilDone "Act2b continuation" 250 completed
 for_ [("cult-shadow-tie-x1",True,True,True,False,7),("cult-in-play-no-mini-x1",True,False,False,False,7),("cult-shadow-tie-x0",True,True,True,False,8),("investigator-first",False,False,False,False,8)] \(name,cultWins,shadow,mini,noCultist,playerClues)->do
   let input=fixture cultWins shadow mini noCultist playerClues
       before=exportSnapshot input
   writeIORef exposureWindows 0
   writeIORef tracedQuestions 0
   writeIORef tracePhase name
   attempt<-E.try @E.SomeException $ play input
   result<-case attempt of Left why->putStrLn ("FAIL "<>name<>" "<>T.pack (show why))>>exitFailure;Right s->pure s
   assert (name<>" original fixture remains immutable") $ exportSnapshot input==before
   let keys=filter ((==String "c09590") . entityId) $ entityRows "scarletKeys" result
   assert (name<>" creates exactly one real key") $ length keys==1
   let placement=jsonField "placement" $ headEx keys
   assert (name<>" assigns the key to the winning side") $ if cultWins then jsonField "tag" placement==String "AttachedToEnemy" && jsonField "contents" placement `elem` cultists else jsonField "tag" placement==String "AttachedToInvestigator"
   assert (name<>" advances original agenda to The Chase") $ any ((==String "c09570") . entityId) $ entityRows "agendas" result
   assert (name<>" removes all mini-cards") $ null $ entityRows "concealed" result
   offered<-readIORef exposureWindows
   assert (name<>" reveal is not an expose reaction window") $ if mini&&shadow then if expectOld then offered>0 else offered==0 else offered==0
   BL.putStrLn $ "TALISMAN_STAGE2_RESULT "<>encode (object ["case" .= (name::Text),"coreSourceId" .= coreSourceId,"keyCount" .= length keys,"exposeWindows" .= offered,"passed" .= True])
 -- Optional synthetic diagnostic, NOT a claimed reproduction of the player's
 -- unavailable failure save and NOT a rule-authorized fallback assignment.
 when probeEmpty do
   let orphan=fixture True False False True 7;frozen=exportSnapshot orphan
   missing<-E.try @E.SomeException $ play orphan
   assert "failed synthetic candidate never changes its input fixture" $ exportSnapshot orphan==frozen
   assert "synthetic empty-cultist state exposes missing ScarletKeyIs 09590" $ case missing of Left why->"ScarletKeyIs" `T.isInfixOf` T.pack (show why) && "09590" `T.isInfixOf` T.pack (show why);Right _->False
   putStrLn "SYNTHETIC_EMPTY_CULTISTS_MISSING_KEY_DIAGNOSTIC_ONLY"
 putStrLn $ if expectOld then "TALISMAN_OLD_REVEAL_WINDOW_REPRODUCED" else "TALISMAN_NORMAL_STAGE2_BRANCHES_COMPLETED"
