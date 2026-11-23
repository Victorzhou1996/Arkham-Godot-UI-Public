module NativeLabyrinthMiniSpec (labyrinthMiniSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Message (Message (ClearUI))
import Arkham.Question
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Validation (indexedChoices)
import Arkham.Native.Settings.Engine qualified as Engine
import Arkham.Native.Settings.Types (SettingsFormRequest (..))
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.Either (isLeft)
import Data.ByteString.Lazy qualified as BL
import Data.ByteString.Lazy.Char8 qualified as BLC
import Prelude qualified as P
import System.Exit (exitFailure)
import System.Directory qualified as Directory

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label)
  else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure

typed :: FromJSON a => Value -> IO a
typed = either (\e -> P.putStrLn e >> exitFailure) pure . Aeson.parseEither parseJSON

field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null

-- Adapter coverage only. R2 is explicitly injected into a genuinely prepared
-- scenario; the next-group route is selected from the ORIGINAL offered Read.
-- This does not claim that the first group was naturally won.
labyrinthMiniSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
labyrinthMiniSpecs player choose = do
  defaultRequest <- typed @CreateRequest $ object
    ["scenario" .= ("70001" :: Text),"players" .= [player],"seed" .= (2170001 :: Int)]
  assert "labyrinth mini request defaults false" $ not $ createLabyrinthMiniCampaign defaultRequest
  let miniRequest = defaultRequest {createLabyrinthMiniCampaign=True}
  foreignResult <- createSession $ miniRequest {createScenario=Just "01104"}
  campaign <- createSession $ miniRequest {createCampaign=Just "01"}
  historical <- createHistoricalSession miniRequest
  assert "labyrinth mini rejects foreign campaign and historical creation" $
    isLeft foreignResult && isLeft campaign && isLeft historical
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json"
    >>= either (\e -> P.putStrLn e >> exitFailure) pure
  let meta s = field "meta" $ field "That" $ toJSON $ gameMode $ snapshotGame s
      questions s = map unwrapQuestion $ Map.elems $ gameQuestion $ snapshotGame s
      choices s = concatMap (fromMaybe [] . indexedChoices) $ questions s
      key label = fromMaybe "" $ headMay $ reverse $ T.splitOn "." $ T.takeWhile (/=' ') label
      another s = [i | (i,Label label _) <- zip [0..] (choices s), key label=="playAnotherGroup"]
      respond s = case questions s of
        [PickScenarioSettings] -> do
          schema <- right $ Engine.resolveSchema (snapshotGame s) player
          pure $ SettingsFormAnswer $ SettingsFormRequest (Engine.schemaIdentity schema) mempty
        _ -> choose player (headEx decks) s
      answer s a = fst <$> (applyAnswer s player (snapshotRevision s) a >>= right)
      ready budget s
        | not (gameInSetup $ snapshotGame s)
        , any (\case PlayerWindowChooseOne {} -> True; _ -> False) (questions s) = pure s
        | budget <= (0 :: Int) = error "Labyrinth actual setup did not reach PlayerWindow"
        | otherwise = respond s >>= answer s >>= ready (budget-1)
      controlledR2 s = do
        r2 <- typed @Message $ object
          ["tag" .= ("ScenarioResolution" :: Text)
          ,"contents" .= object ["tag" .= ("Resolution" :: Text),"contents" .= (2 :: Int)]]
        env <- makeEnv (snapshotGame s) [ClearUI,r2]
        advance env
        fst <$> finish (snapshotHistoricalBootstrap s) (snapshotRevision s) env
      nextPrompt budget s
        | notNull (another s) = pure s
        | budget <= (0 :: Int) = error "Original R2 did not offer playAnotherGroup"
        | otherwise = respond s >>= answer s >>= nextPrompt (budget-1)
      ordinaryEnd budget s
        | notNull (another s) = pure False
        | Map.null (gameQuestion $ snapshotGame s) = pure True
        | budget <= (0 :: Int) = error "Ordinary Labyrinth resolution did not finish"
        | otherwise = respond s >>= answer s >>= ordinaryEnd (budget-1)
  mini <- fst <$> (createSession miniRequest >>= right)
  firstReady <- ready 40 mini
  assert "labyrinth mini original metadata survives empty settings and StartScenario" $
    field "miniCampaign" (meta mini)==Bool True && field "miniCampaign" (meta firstReady)==Bool True
  prompt <- controlledR2 firstReady >>= nextPrompt 10
  let firstGroup=field "currentGroup" $ meta firstReady
  assert "labyrinth controlled R2 records real current group and offers next group" $
    field "playedGroups" (meta prompt)==toJSON [firstGroup]
      && field "survivedGroups" (meta prompt)==toJSON [firstGroup]
      && length (another prompt)==1
  let continue s = case another s of
        [i] -> answer s $ Answer $ QuestionResponse i (Just player) Nothing
        _ -> error "Expected unique original playAnotherGroup"
  choosing <- continue prompt
  assert "labyrinth actual playAnotherGroup reaches original deck barrier with metadata retained" $
    ChooseDeck `elem` questions choosing && meta choosing==meta prompt
  secondReady <- ready 40 choosing
  assert "labyrinth next original setup selects an unplayed group and retains outcomes" $
    field "currentGroup" (meta secondReady)/=firstGroup
      && field "playedGroups" (meta secondReady)==toJSON [firstGroup]
      && field "survivedGroups" (meta secondReady)==toJSON [firstGroup]
      && field "miniCampaign" (meta secondReady)==Bool True
  restored <- importSnapshot (exportSnapshot prompt) >>= right
  replayChoosing <- continue restored
  replay <- ready 40 replayChoosing
  reopened <- importSnapshot (exportSnapshot secondReady) >>= right
  -- Diagnostic artifacts precede the strict assertion. Encode directly from
  -- Haskell; never round-trip RNG Int64 values through JavaScript numbers.
  -- These filenames belong to this targeted diagnostic run, not accepted QA.
  Directory.createDirectoryIfMissing True "spec-outputs"
  for_ [("prompt",prompt),("second",secondReady),("replay",replay),("reopened",reopened)
       ,("prompt-restored",restored),("choosing",choosing),("choosing-replay",replayChoosing)] $
      \(name,snapshot) -> BL.writeFile ("spec-outputs/labyrinth-mini-r4-" <> name <> ".json") $
      exportSnapshot snapshot
  BLC.putStrLn $ "LABYRINTH_MINI_REPLAY_DIAGNOSTIC " <> encode (object
    ["promptRoundtripEqual" .= (exportSnapshot prompt==exportSnapshot restored)
    ,"nextGroupDeckBarrierEqual" .= (exportSnapshot choosing==exportSnapshot replayChoosing)
    ,"replayEqual" .= (exportSnapshot replay==exportSnapshot secondReady)
    ,"reopenedEqual" .= (exportSnapshot reopened==exportSnapshot secondReady)
    ,"expectedSeed" .= gameSeed (snapshotGame secondReady)
    ,"replaySeed" .= gameSeed (snapshotGame replay)
    ,"expectedRevision" .= snapshotRevision secondReady
    ,"replayRevision" .= snapshotRevision replay])
  assert "labyrinth actual next-group answer replays and reopens exact game queue RNG" $
    exportSnapshot replay==exportSnapshot secondReady && exportSnapshot reopened==exportSnapshot secondReady
  ordinary <- fst <$> (createSession defaultRequest >>= right)
  ordinaryReady <- ready 40 ordinary
  noMini <- controlledR2 ordinaryReady >>= ordinaryEnd 10
  assert "labyrinth default single-group R2 never offers mini continuation" $
    field "miniCampaign" (meta ordinaryReady)==Bool False && noMini
