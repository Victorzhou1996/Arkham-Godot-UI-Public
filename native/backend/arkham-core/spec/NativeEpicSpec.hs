module NativeEpicSpec (epicSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Game (Game (..))
import Arkham.Id (PlayerId (..))
import Arkham.Native.Epic.Engine qualified as Epic
import Arkham.Native.Epic.Types
import Arkham.Native.Answer (unwrapQuestion)
import Arkham.Native.Settings.Engine qualified as Settings
import Arkham.Native.Settings.Types (SettingsFormRequest (..))
import Arkham.Question
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types qualified as Aeson
import Data.ByteString.Lazy qualified as BL
import Data.Map.Strict qualified as Map
import Data.Text qualified as T
import Data.UUID qualified as UUID
import Data.Either (isLeft)
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label)
  else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure

typed :: FromJSON a => Value -> IO a
typed = either (\e -> P.putStrLn e >> exitFailure) pure . Aeson.parseEither parseJSON

-- New adapter only: two original fresh groups and ONE original offered answer.
-- No fabricated completed scenario or organizer threshold is used.
epicSpecs :: IO ()
epicSpecs = do
  let uid s = fromJustNote "Epic fixture UUID" $ UUID.fromString s
      eid=uid "00000000-0000-4000-8000-000000008500"
      gid1=uid "00000000-0000-4000-8000-000000008501"
      gid2=uid "00000000-0000-4000-8000-000000008502"
      player1=PlayerId $ uid "00000000-0000-4000-8000-000000008511"
      player2=PlayerId $ uid "00000000-0000-4000-8000-000000008512"
      action=uid "00000000-0000-4000-8000-000000008521"
      undoId=uid "00000000-0000-4000-8000-000000008522"
  request <- typed @EpicCreate $ object
    ["eventId" .= eid
    ,"definition" .= object ["scenarioId" .= ("85001"::Text),"difficulty" .= ("Standard"::Text)
      ,"tarot" .= False,"blobElse" .= False,"timeLimitMinutes" .= (180::Int)]
    ,"groups" .=
      [object ["gameId" .= gid1,"ordinal" .= (0::Int),"name" .= ("First"::Text),"players" .= [player1],"seed" .= (85001::Int)]
      ,object ["gameId" .= gid2,"ordinal" .= (1::Int),"name" .= ("Second"::Text),"players" .= [player2],"seed" .= (85002::Int)]]]
  (initial,_) <- Epic.createEvent request >>= right
  firstGroup <- right $ Epic.selectedGroup initial gid1
  secondGroup <- right $ Epic.selectedGroup initial gid2
  let firstGame=groupGame $ groupSnapshot firstGroup
      secondGame=groupGame $ groupSnapshot secondGroup
      originalBytes=encode initial
  assert "epic creates two bound original groups" $
    eventRevision initial==0 && map groupMembers (eventGroups initial)==[[player1],[player2]]
  assert "epic original setup asks each own seat" $
    Map.member player1 (gameQuestion firstGame) && Map.member player2 (gameQuestion secondGame)
  decoded <- typed @EventSnapshot $ toJSON initial
  assert "epic whole event JSON roundtrip exact" $ encode decoded==originalBytes
  let wrongSource=case toJSON initial of
        Object o -> Object $ KM.insert "coreSourceId" (String "foreign-source") o
        _ -> Null
  assert "epic rejects foreign event source" $
    isLeft $ Aeson.parseEither (parseJSON @EventSnapshot) wrongSource
  rawAnswer <- case unwrapQuestion <$> Map.lookup player1 (gameQuestion firstGame) of
    Just PickScenarioSettings -> do
      schema <- right $ Settings.resolveSchema firstGame player1
      pure $ object ["tag" .= ("SettingsFormAnswer"::Text)
        ,"contents" .= SettingsFormRequest (Settings.schemaIdentity schema) mempty]
    Just ChooseDeck -> do
      presets <- eitherDecodeFileStrict @[Value] "arkham-core/data/community-starter-decks.json"
        >>= either (\e -> P.putStrLn e >> exitFailure) pure
      pure $ object ["tag" .= ("DeckListAnswer"::Text),"playerId" .= player1,"deckList" .= headEx presets]
    _ -> putStrLn "FAIL unexpected original Epic initial question" >> exitFailure
  let operation=EpicApply initial 0 action 1700000000
        (GroupAnswer gid1 (groupSequence firstGroup) player1 rawAnswer) Nothing
  (after,_) <- Epic.applyEvent operation >>= right
  changed <- right $ Epic.selectedGroup after gid1
  other <- right $ Epic.selectedGroup after gid2
  assert "epic one answer advances event and selected group" $
    eventRevision after==1 && groupCoreRevision (groupSnapshot changed)>groupCoreRevision (groupSnapshot firstGroup)
  -- Original server sync legitimately updates mirrors/seed/revision in other
  -- groups. It MUST NOT consume their Ask or old continuation as an answer.
  assert "epic other group retains pending ask entities and queue" $
    toJSON (gameQuestion $ groupGame $ groupSnapshot other)==toJSON (gameQuestion secondGame)
    && toJSON (gameEntities $ groupGame $ groupSnapshot other)==toJSON (gameEntities secondGame)
    && toJSON (groupQueue $ groupSnapshot other)==toJSON (groupQueue $ groupSnapshot secondGroup)
  assert "epic old event candidate remains unchanged" $ encode initial==originalBytes
  assert "epic history is thin and binds original revision" $ case groupHistory changed of
    [h] -> beforeEventRevision h==0 && beforeCoreRevision h==groupCoreRevision (groupSnapshot firstGroup)
      && case toJSON h of Object o -> not (KM.member "beforeSnapshot" o); _ -> False
    _ -> False
  base <- right (Epic.extractUndoBase initial gid1) >>= typed @UndoBase
  assert "epic extracted undo base verifies" $ not $ isLeft $ Epic.verifyUndoBase after gid1 action base
  assert "epic altered undo digest rejected" $ isLeft $
    Epic.verifyUndoBase after gid1 action $ base {undoSha256=T.replicate 64 "0"}
  let undoRequest=EpicApply after 1 undoId 1700000001
        (UndoGroup gid1 (groupSequence changed) action) (Just base)
  (undone,_) <- Epic.applyEvent undoRequest >>= right
  restored <- right $ Epic.selectedGroup undone gid1
  assert "epic undo restores original ask without rolling back event revision" $
    eventRevision undone==2 && null (groupHistory restored)
    && toJSON (gameQuestion $ groupGame $ groupSnapshot restored)==toJSON (gameQuestion firstGame)
  importedAfter <- typed @EventSnapshot $ toJSON after
  (replayed,_) <- Epic.applyEvent (undoRequest {applySnapshot=importedAfter}) >>= right
  assert "epic imported thin undo has exact whole event replay" $ encode replayed==encode undone
  -- Force all fixture bytes before returning, matching the actual wire boundary.
  BL.length (encode (initial,after,undone)) `seq` pure ()
