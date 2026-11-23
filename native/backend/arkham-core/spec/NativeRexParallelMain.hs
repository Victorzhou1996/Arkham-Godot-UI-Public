{-# LANGUAGE BlockArguments, OverloadedRecordDot, OverloadedStrings, NoImplicitPrelude, TypeApplications #-}
module NativeRexParallelMain (main) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Native.Env (runCore)
import Arkham.Native.Session
import Arkham.Native.Validation (indexedChoices)
import Arkham.Helpers.Playable (getIsPlayableAfterInitiation)
import Arkham.Question
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
field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null
setField :: Text -> Value -> Value -> Value
setField key value (Object o) = Object $ KM.insert (K.fromText key) value o
setField _ _ _ = error "Expected a test object"
values :: Value -> [Value]
values (Array xs) = toList xs
values _ = []
tag :: Text -> Value
tag name = object ["tag" .= name]
actorFor :: PlayerId -> Snapshot -> InvestigatorId
actorFor player s = fst $ fromJustNote "actual owned actor" $ find ((== toJSON player) . field "playerId" . toJSON . snd) $ Map.toList (gameEntities $ snapshotGame s).investigators
actorValue :: InvestigatorId -> Snapshot -> Value
actorValue iid s = toJSON $ fromJustNote "actual actor" $ Map.lookup iid (gameEntities $ snapshotGame s).investigators
tokenCount :: Text -> InvestigatorId -> Snapshot -> Int
tokenCount name iid s = fromMaybe 0 $ lookup name (toResult $ field "tokens" $ actorValue iid s :: [(Text,Int)])
bagCurses :: Snapshot -> [Value]
bagCurses s = filter ((== String "CurseToken") . field "chaosTokenFace") $ values $ field "bagTokens" $ field "workbench" $ debugView (snapshotGame s) player
 where player = headEx $ gamePlayers $ snapshotGame s
sealedCurses :: Snapshot -> [Value]
sealedCurses s = filter ((== String "CurseToken") . field "chaosTokenFace") $ concatMap (values . field "sealedChaosTokens" . toJSON) $ Map.elems (gameEntities $ snapshotGame s).assets
resources :: Snapshot -> Int
resources s = sum [tokenCount "Resource" iid s | iid <- Map.keys (gameEntities $ snapshotGame s).investigators]
sentinel :: Question Message
sentinel = ChooseOne [Label "rex-cost-finished" [Noop]]

-- The same bounded ordinary-answer policy used by the existing integration
-- driver, kept here so this diagnostic can link an old installed library
-- without rebuilding it or compiling unrelated spec suites.
choose :: PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer
choose player deck s = case unwrapQuestion $ fromJustNote "actual recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s of
 ChooseDeck -> pure $ DeckListAnswer deck player
 ChooseUpgradeDeck -> pure $ DeckListAnswer deck player
 ChooseJoinDeck {} -> pure $ DeckListAnswer deck player
 PickScenarioSettings -> pure $ StandaloneSettingsAnswer []
 PickCampaignSettings -> pure $ CampaignSettingsAnswer $ CampaignSettings [] mempty mempty []
 ChooseAmounts _ (TotalAmountTarget total) rows _ -> do
   let ranges=[(key,lo,hi) | AmountChoice key _ lo hi <- rows]
       combinations=product [toInteger hi-toInteger lo+1 | (_,lo,hi)<-ranges]
   assert "bounded legal allocation ranges" $ combinations<=10000 && all (\(_,lo,hi)->lo<=hi) ranges
   let selected=fromJustNote "legal total allocation" $ listToMaybe [xs | xs<-sequence [[(key,n) | n<-[lo..hi]] | (key,lo,hi)<-ranges],sum (map (toInteger . snd) xs)==toInteger total]
   pure $ AmountsAnswer $ AmountsResponse (Map.fromList selected) (Just $ gameScenarioSteps $ snapshotGame s) (Just player)
 q -> case indexedChoices q >>= listToMaybe . map fst . filter (\(_,c)->case c of InvalidLabel {}->False;Info {}->False;_->True) . zip [0..] of
   Just n -> pure $ Answer $ QuestionResponse n (Just player) Nothing
   Nothing -> error $ "Add an ordinary answer for this actual question: "<>show q

main :: IO ()
main = do
 args <- getArgs
 assert "explicit test mode" $ args == [] || args == ["--expect-old-bug"]
 let old = args == ["--expect-old-bug"]
     player = PlayerId $ fromJustNote "test player" $ UUID.fromString "00000000-0000-4000-8000-000000000001"
     other = PlayerId $ fromJustNote "test teammate" $ UUID.fromString "00000000-0000-4000-8000-000000000002"
 decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (error . fromString) pure
 -- Controlled test deck. The original setup/selection/initial hand handlers
 -- still run; deck-building legality is not the behavior under test here.
 let rexDeck = toResult @ArkhamDBDecklist $ setField "meta" Null $ setField "investigator_code" (String "90078") $ toJSON $ headEx $ drop 1 decks
     deckFor who = if who == player then rexDeck else headEx decks
     ready s = field "available" (field "workbench" $ debugView (snapshotGame s) player) == Bool True
     answer s who response = fst <$> (applyAnswer s who (snapshotRevision s) response >>= right)
     step s = do
       let who = fst $ headEx $ Map.toList $ gameQuestion $ snapshotGame s
       response <- choose who (deckFor who) s
       answer s who response
     untilReady budget s
       | ready s = pure s
       | budget <= (0::Int) = error "Actual initial player window not reached"
       | otherwise = step s >>= untilReady (budget-1)
     debug s command = fst <$> (applyDebug s player (snapshotRevision s) True command >>= right)
     create s iid code placement = debug s $ object ["operation" .= ("createCard"::Text),"cardCode" .= (code::Text),"placement" .= (placement::Text),"investigatorId" .= iid,"deck" .= Null]
     adjust s iid name n = debug s $ object ["operation" .= ("adjustTokens"::Text),"target" .= object ["tag" .= ("InvestigatorTarget"::Text),"contents" .= iid],"token" .= (name::Text),"amount" .= (n::Int)]
     run s messages = do
       let game = (snapshotGame s) {gameQuestion=Map.empty,gameRetainedQuestion=False}
       env <- makeEnv game messages
       advance env
       fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s+1) env
     finishEvent budget s
       | Map.lookup player (gameQuestion $ snapshotGame s) == Just sentinel = pure s
       | budget <= (0::Int) = error "Original event did not reach the queued continuation"
       | otherwise = step s >>= finishEvent (budget-1)
 (created,_) <- createSession (CreateRequest Nothing (Just "01104") 201114 [player,other] Standard False False) >>= right
 base <- untilReady 180 created
 let iid=actorFor player base; teammate=actorFor other base
 assert "actual parallel Rex is selected" $ field "cardCode" (actorValue iid base) == String "c90078"
 withClue <- adjust base iid "Clue" 1
 nine <- foldM (\s _ -> debug s $ object ["operation" .= ("editChaosBag"::Text),"action" .= ("add"::Text),"face" .= ("CurseToken"::Text)]) withClue [1..9::Int]
 moonStarted <- create nine teammate "c07271" "play"
 moon <- untilReady 50 moonStarted
 let aid=fst $ fromJustNote "actual teammate Moon" $ find ((== String "c07271") . field "cardCode" . toJSON . snd) $ Map.toList (gameEntities $ snapshotGame moon).assets
 -- Seal using the original debug Seal/Sealed messages, not by changing bag
 -- or asset JSON. Another investigator owns the three reserved tokens.
 assert "Moon setup has not sealed more than the fixture's three curses" $ length (sealedCurses moon)<=3
 sealed <- foldM (\s _ -> do
   let token=headEx $ bagCurses s
   debug s $ object ["operation" .= ("sealToken"::Text),"tokenId" .= field "chaosTokenId" token,"target" .= object ["tag" .= ("AssetTarget"::Text),"contents" .= aid]]) moon [1..(3-length (sealedCurses moon))]
 assert "six in bag plus three sealed on teammate leaves one in supply" $ length (bagCurses sealed)==6 && length (sealedCurses sealed)==3
 let nineBag=nine
 for_ [("six-bag-three-sealed",sealed),("nine-bag",nineBag)] \(supplyLabel,supply) ->
  for_ ["c07028","c07023"] \code -> do
   handed <- create supply iid code "hand"
   let newIds=Map.keys (gameCards $ snapshotGame handed) \\ Map.keys (gameCards $ snapshotGame supply)
       cid=headEx newIds
       card=fromJustNote "registered event" $ Map.lookup cid $ gameCards $ snapshotGame handed
       beforeClues=tokenCount "Clue" iid handed
       beforeResources=resources handed
   env <- makeEnv (snapshotGame handed) []
   playable <- runCore env $ getIsPlayableAfterInitiation iid iid (toResult $ object ["tag" .= ("UnpaidCost"::Text),"contents" .= ("NoAction"::Text)]) [] card
   assert (supplyLabel<>" "<>code<>" is payable using Rex's clue replacement") playable
   zero <- adjust handed iid "Clue" (-beforeClues)
   noClueEnv <- makeEnv (snapshotGame zero) []
   impossible <- runCore noClueEnv $ getIsPlayableAfterInitiation iid iid (toResult $ object ["tag" .= ("UnpaidCost"::Text),"contents" .= ("NoAction"::Text)]) [] card
   assert "same insufficient supply with zero clues cannot pay" $ not impossible
   pending <- run handed [PayCardCost iid card [],Ask player sentinel]
   let question=unwrapQuestion $ fromJustNote "Rex cost prompt" $ Map.lookup player $ gameQuestion $ snapshotGame pending
       choices=fromJustNote "indexed original cost prompt" $ indexedChoices question
       offered=headEx choices
       ability=field "ability" $ toJSON offered
       expectedIndex=if old then 1 else 2
   assert "cost offers only the replacement, never a free skip" $ length choices==1 && field "tag" (toJSON offered)==String "AbilityLabel"
   assert "actual original artificial label index" $ field "index" ability==toJSON (expectedIndex::Int)
   let response=Answer $ QuestionResponse 0 (Just player) Nothing
       before=exportSnapshot pending
   attempted <- applyAnswer pending player (snapshotRevision pending) response
   if old then do
     assert "baseline truly reproduces no-more-curse rejection" $ case attempted of Left why -> "no more curse tokens" `T.isInfixOf` why;Right _ -> False
     assert "baseline rejected candidate does not mutate the old saved pause" $ exportSnapshot pending==before
   else do
     (resolved,_) <- right attempted
     completed <- finishEvent 50 resolved
     assert "Rex actually places one clue instead of both unavailable curse tokens" $ tokenCount "Clue" iid completed==beforeClues-1 && length (bagCurses completed)==length (bagCurses handed) && length (sealedCurses completed)==length (sealedCurses handed)
     -- Investigator.discard is [PlayerCard]; its real ToJSON implementation
     -- writes pcId as the direct "id" field (not a Card/contents wrapper).
     let discarded=any ((==toJSON cid) . field "id") $ values $ field "discard" $ actorValue iid completed
     assert "the original physical event resolves and enters discard after its real cost is paid" $ Map.null (gameActiveCost $ snapshotGame completed) && discarded && (code/="c07028" || resources completed==beforeResources+5)
     let rewrite (Object o) =
           let changed=KM.map rewrite o
            in Object $ if KM.lookup "tag" changed==Just (String "AbilityLabel")
                 then case KM.lookup "ability" changed of
                   Just ab -> KM.insert "ability" (setField "index" (toJSON (1::Int)) ab) changed
                   Nothing -> changed
                 else changed
         rewrite (Array xs)=Array $ fmap rewrite xs
         rewrite x=x
         legacy=pending {snapshotGame=(snapshotGame pending){gameQuestion=fmap (toResult . rewrite . toJSON) $ gameQuestion $ snapshotGame pending}}
         oldWire=setField "coreSourceId" (String "arkham-core-sha256:e98793b07a293bea0febf803fd642eb066fe946aa9ebf4d3ee9eb812db9022a0") $ toJSON legacy
     imported <- importSnapshot (encode oldWire) >>= right
     (continued,_) <- applyAnswer imported player (snapshotRevision imported) response >>= right
     legacyDone <- finishEvent 50 continued
     assert "old saved index1 prompt imports and resolves without editing the save" $ tokenCount "Clue" iid legacyDone==beforeClues-1 && length (bagCurses legacyDone)==length (bagCurses handed) && Map.null (gameActiveCost $ snapshotGame legacyDone)
     importedAgain <- importSnapshot (encode oldWire) >>= right
     (continuedAgain,_) <- applyAnswer importedAgain player (snapshotRevision importedAgain) response >>= right
     replayDone <- finishEvent 50 continuedAgain
     assert "old saved pause full-snapshot replay remains deterministic" $ exportSnapshot legacyDone==exportSnapshot replayDone
     -- A genuine clue-placement window must continue using ability 1.
     let placement=field "contents" $ field "placement" $ actorValue iid withClue
         source=field "source" ability
         window=object ["windowTiming" .= ("When"::Text),"windowType" .= object ["tag" .= ("WouldPlaceClueOnLocation"::Text),"contents" .= [toJSON iid,placement,source,toJSON (1::Int)]],"windowBatchId" .= ("00000000-0000-4000-8000-000000000099"::Text),"windowConditionTick" .= Null]
     normal <- run withClue [UseCardAbility iid (toResult source) 1 [toResult window] (toResult $ tag "NoPayment"),Ask player sentinel]
     normalDone <- finishEvent 15 normal
     assert "normal ability1 remains clue-to-two-curses, not the opposite replacement" $ length (bagCurses normalDone)==2 && tokenCount "Clue" iid normalDone==tokenCount "Clue" iid withClue
   BL.putStrLn $ "REX_CURSE_RESULT " <> encode (object ["mode" .= (if old then "baseline" else "fixed"::Text),"coreSourceId" .= coreSourceId,"supply" .= (supplyLabel::Text),"event" .= (code::Text),"passed" .= True])
 putStrLn $ if old then "REX_OLD_BUG_REPRODUCED" else "REX_CURSE_REPLACEMENT_INTEGRATION_COMPLETED"
