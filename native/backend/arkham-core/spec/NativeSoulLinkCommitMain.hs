{-# LANGUAGE BlockArguments, OverloadedRecordDot, OverloadedStrings, NoImplicitPrelude, TypeApplications #-}
module NativeSoulLinkCommitMain (main) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Helpers.Modifiers (getModifiers)
import Arkham.Id
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Native.Env (runCore)
import Arkham.Native.Session
import Arkham.Native.Validation (indexedChoices)
import Arkham.Question
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.List (findIndex)
import Data.UUID qualified as UUID
import Data.Text qualified as T
import Data.ByteString.Lazy.Char8 qualified as BL
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label) else putStrLn ("FAIL " <> label) >> exitFailure
right :: Either Text a -> IO a
right = either (\reason -> putStrLn reason >> exitFailure) pure
field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null
setField :: Text -> Value -> Value -> Value
setField key value (Object o) = Object $ KM.insert (K.fromText key) value o
setField _ _ _ = error "Expected an isolated fixture object"
values :: Value -> [Value]
values (Array xs) = toList xs
values (Object o) = KM.elems o
values _ = []
contains :: Value -> Value -> Bool
contains needle value = needle == value || any (contains needle) (values value)
actorValue :: InvestigatorId -> Snapshot -> Value
actorValue iid s = toJSON $ fromJustNote "actual fixture investigator" $ Map.lookup iid (gameEntities $ snapshotGame s).investigators
tokenCount :: Text -> InvestigatorId -> Snapshot -> Int
tokenCount token iid s = fromMaybe 0 $ lookup token (toResult $ field "tokens" $ actorValue iid s :: [(Text, Int)])
offered :: PlayerId -> Snapshot -> [UI Message]
offered player s = fromMaybe [] $ Map.lookup player (gameQuestion $ snapshotGame s) >>= indexedChoices . unwrapQuestion
sentinel :: Question Message
sentinel = ChooseOne [Label "soul-link-commit-finished" [Noop]]

-- Original setup answers, as in NativeTalismanStage2Main. This driver reads
-- only the checked-in starter deck and creates fresh, in-memory sessions.
choose :: PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer
choose player deck s = case unwrapQuestion $ fromJustNote "actual recipient" $ Map.lookup player $ gameQuestion $ snapshotGame s of
 ChooseDeck -> pure $ DeckListAnswer deck player
 ChooseUpgradeDeck -> pure $ DeckListAnswer deck player
 ChooseJoinDeck {} -> pure $ DeckListAnswer deck player
 PickScenarioSettings -> defaultSettings
 PickCampaignSettings -> defaultSettings
 ChooseAmounts _ (TotalAmountTarget total) rows _ -> do
   let ranges = [(key, lo, hi) | AmountChoice key _ lo hi <- rows]
       combinations = product [toInteger hi - toInteger lo + 1 | (_, lo, hi) <- ranges]
   assert "bounded ordinary allocation" $ combinations <= 10000 && all (\(_, lo, hi) -> lo <= hi) ranges
   let selected = fromJustNote "legal allocation" $ listToMaybe
         [xs | xs <- sequence [[(key, n) | n <- [lo..hi]] | (key, lo, hi) <- ranges], sum (map (toInteger . snd) xs) == toInteger total]
   pure $ AmountsAnswer $ AmountsResponse (Map.fromList selected) (Just $ gameScenarioSteps $ snapshotGame s) (Just player)
 q -> do
   let choices = fromMaybe [] $ indexedChoices q
       preferred choice = case choice of
         SkipTriggersButton {} -> True
         HorrorLabel {} -> True
         SkillTestApplyResultsButton {} -> True
         _ -> False
       actionable choice = case choice of InvalidLabel {} -> False; Info {} -> False; _ -> True
       index = findIndex preferred choices <|> findIndex actionable choices
   pure $ Answer $ QuestionResponse (fromJustNote ("Unsupported actual question: " <> show q) index) (Just player) Nothing
 where
 defaultSettings = do
   let form = settingsFormView (snapshotGame s) player
   assert "actual settings form is supported" $ field "supported" form == Bool True
   pure $ toResult @Answer $ object ["tag" .= ("SettingsFormAnswer" :: Text), "contents" .= object
     ["schemaId" .= field "schemaId" form, "valuesByRowId" .= ([] :: [Value])]]

-- Reuse an actual ActAttrs shape but decode the requested original Act type.
-- Its HasModifiersFor supplies the protection; no modifier/cost result is
-- injected. These are focused Act fixtures, not claimed full prelude playthroughs.
actFixture :: Maybe Text -> Snapshot -> Snapshot
actFixture selected s = s {snapshotGame = toResult @Game changed, snapshotQueue = []}
 where
 original = toJSON $ snapshotGame s
 entities = field "gameEntities" original
 acts = field "acts" entities
 replaced = case selected of
   Nothing -> acts
   Just code -> let act = setField "advanceCost" Null $ setField "id" (String code) $ headEx $ values acts
                in Object $ KM.singleton (K.fromText code) act
 changed = setField "gameQuestion" (Object KM.empty)
   $ setField "gameRetainedQuestion" (Bool False)
   $ setField "gameEntities" (setField "acts" replaced entities) original

main :: IO ()
main = do
 args <- getArgs
 assert "explicit isolated test mode" $ args == [] || args == ["--expect-old-bug"]
 let expectOld = args == ["--expect-old-bug"]
     player = PlayerId $ fromJustNote "test player" $ UUID.fromString "00000000-0000-4000-8000-000000000167"
 decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (error . fromString) pure
 -- Deck legality is not under test. The original deck selection, setup and
 -- initial-hand handlers still install the actual Amina investigator.
 let deck = toResult @ArkhamDBDecklist $ setField "meta" Null $ setField "investigator_code" (String "09011") $ toJSON $ headEx decks
     ready s = field "available" (field "workbench" $ debugView (snapshotGame s) player) == Bool True
     answer s index = fst <$> (applyAnswer s player (snapshotRevision s) (Answer $ QuestionResponse index (Just player) Nothing) >>= right)
     step s = do
       let who = fst $ headEx $ Map.toList $ gameQuestion $ snapshotGame s
       response <- choose who deck s
       fst <$> (applyAnswer s who (snapshotRevision s) response >>= right)
     untilDone label budget done s
       | done s = pure s
       | budget <= (0 :: Int) = error $ "Original flow did not finish: " <> T.unpack label
       | otherwise = step s >>= untilDone label (budget - 1) done
     run s messages = do
       let game = (toResult @Game $ toJSON $ snapshotGame s) {gameQuestion = Map.empty, gameRetainedQuestion = False}
       env <- makeEnv game messages
       advance env
       fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s + 1) env
     hasStart s = any isStart $ offered player s
     isStart StartSkillTestButton {} = True
     isStart _ = False
     finished s = Map.lookup player (gameQuestion $ snapshotGame s) == Just sentinel
 (created, _) <- createSession (CreateRequest Nothing (Just "01104") 201167 [player] Standard False False) >>= right
 base <- untilDone "real Amina setup" 180 ready created
 let iid = fst $ headEx $ Map.toList (gameEntities $ snapshotGame base).investigators
 assert "actual Amina selected by original setup" $ field "cardCode" (actorValue iid base) == String "c09011"
 (handed, _) <- applyDebug base player (snapshotRevision base) True (object
   ["operation" .= ("createCard" :: Text), "cardCode" .= ("c12067" :: Text), "placement" .= ("hand" :: Text)
   ,"investigatorId" .= iid, "deck" .= Null]) >>= right
 let newIds = Map.keys (gameCards $ snapshotGame handed) \\ Map.keys (gameCards $ snapshotGame base)
     cid = headEx newIds
 assert "one actual Soul Link physical instance created" $ length newIds == 1
   && contains (toJSON cid) (field "hand" $ actorValue iid handed)
 for_ [("ordinary-control", Nothing), ("prelude-act-10682", Just "c10682"), ("prelude-act-10684", Just "c10684")] \(name, actCode) -> do
   let fixture = actFixture actCode handed
       frozen = exportSnapshot fixture
       beforeHorror = tokenCount "Horror" iid fixture
       beforeActions = field "remainingActions" $ actorValue iid fixture
       testSpec = object
         ["investigator" .= iid
         ,"skill" .= object ["tag" .= ("DebugTestOneSkill" :: Text), "contents" .= ("SkillWillpower" :: Text)]
         ,"action" .= Null, "target" .= Null, "sourceCard" .= Null
         ,"difficulty" .= object ["tag" .= ("DebugDifficultyFixed" :: Text), "contents" .= (1 :: Int)]]
   env <- makeEnv (snapshotGame fixture) []
   modifiers <- runCore env $ getModifiers iid
   assert (name <> " original Act supplies expected damage immunity") $ ("CannotBeDamaged" `elem` map tshow modifiers) == isJust actCode
   begun <- run fixture [SetChaosTokens [toResult $ String "Zero"], DebugBeginSkillTest (toResult testSpec), Ask player sentinel]
   commitWindow <- untilDone "actual ST.2 commit window" 40 hasStart begun
   let commitChoice choice = field "tag" (toJSON choice) == String "TargetLabel"
         && contains (toJSON cid) (field "target" $ toJSON choice)
         && contains (String "SkillTestCommitCard_") (field "messages" $ toJSON choice)
       commitIndex = findIndex commitChoice $ offered player commitWindow
   assert (name <> " original question offers the actual Soul Link") $ isJust commitIndex
   committed <- answer commitWindow $ fromJustNote "actual commit index" commitIndex
   assert (name <> " original commit answer stages the physical card") $ contains (toJSON cid)
     (field "committedCards" $ toJSON $ fromJustNote "ongoing skill test" $ gameSkillTest $ snapshotGame committed)
   assert (name <> " staging does not pay horror early") $ tokenCount "Horror" iid committed == beforeHorror
   let startIndex = fromJustNote "original StartSkillTestButton" $ findIndex isStart $ offered player committed
       beforeStart = exportSnapshot committed
   attempted <- applyAnswer committed player (snapshotRevision committed) (Answer $ QuestionResponse startIndex (Just player) Nothing)
   if expectOld && isJust actCode then do
     assert (name <> " baseline reproduces the exact horror-cost failure") $ case attempted of
       Left why -> "Can't afford cost (b)" `T.isInfixOf` why && "HorrorCost" `T.isInfixOf` why
       Right _ -> False
     assert (name <> " rejected candidate preserves original ST.2 pause") $ exportSnapshot committed == beforeStart
   else do
     (started, _) <- right attempted
     completed <- untilDone "real horror allocation and skill-test continuation" 80 finished started
     assert (name <> " pays exactly one real horror") $ tokenCount "Horror" iid completed == beforeHorror + 1
     assert (name <> " zero action cost spends no action") $ field "remainingActions" (actorValue iid completed) == beforeActions
     assert (name <> " original skill test reaches its queued continuation") $ isNothing (gameSkillTest $ snapshotGame completed)
       && Map.null (gameActiveCost $ snapshotGame completed)
     assert (name <> " committed physical card reaches discard") $ any ((== toJSON cid) . field "id")
       $ values $ field "discard" $ actorValue iid completed
   assert (name <> " isolated input fixture stays immutable") $ exportSnapshot fixture == frozen
   BL.putStrLn $ "SOUL_LINK_COMMIT_RESULT " <> encode (object
     ["case" .= (name :: Text), "baseline" .= expectOld, "coreSourceId" .= coreSourceId
     ,"scope" .= ("Fresh Amina session; original 10682/10684 Act entities in focused copied fixtures, not full prelude walkthroughs" :: Text)
     ,"passed" .= True])
 putStrLn $ if expectOld then "SOUL_LINK_OLD_HORROR_COST_BUG_REPRODUCED" else "SOUL_LINK_COMMIT_INTEGRATION_COMPLETED"
