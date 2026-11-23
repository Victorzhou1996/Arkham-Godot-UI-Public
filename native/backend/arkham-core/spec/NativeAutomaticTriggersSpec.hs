module NativeAutomaticTriggersSpec (nativeAutomaticTriggersSpecs, remoteResumeFixture) where

import Arkham.Prelude hiding (assert)
import Arkham.Difficulty
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Message
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Events (eventJSON)
import Arkham.Question
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Either (isLeft)
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS "<>label) else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right = either (\e->putStrLn e>>exitFailure) pure
field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null
typed :: FromJSON a => Value -> a
typed = toResult

-- Fresh test-only preparation for the production TLS adapter. The final
-- ResumeAutomaticTriggersAnswer is deliberately NOT executed here.
remoteResumeFixture :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO Snapshot
remoteResumeFixture player choose = do
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e->P.putStrLn e>>exitFailure) pure
  let deck=headEx decks
      ready s = field "available" (field "workbench" $ debugView (snapshotGame s) player)==Bool True
      untilReady budget s
        | ready s = pure s
        | budget <= (0::Int) = error "Remote resume actual checkpoint not reached"
        | otherwise = do
            answer <- choose player deck s
            (next,_) <- applyAnswer s player (snapshotRevision s) answer >>= right
            untilReady (budget-1) next
  (created,_) <- createSession (CreateRequest Nothing (Just "01104") 201021 [player] Standard False False) >>= right
  actual <- untilReady 120 created
  let iid=fst $ headEx $ Map.toList (gameEntities $ snapshotGame actual).investigators
      tag name=object ["tag" .= (name::Text)]
      ability=typed $ object
        ["source" .= object ["tag" .= ("InvestigatorSource"::Text),"contents" .= iid]
        ,"cardCode" .= ("c01033"::Text),"index" .= (1::Int)
        ,"type" .= object ["tag" .= ("FastAbility'"::Text),"cost" .= tag "Free"]
        ,"limit" .= tag "NoLimit","window" .= tag "AnyWindow","criteria" .= tag "NoRestriction"
        ,"doesNotProvokeAttacksOfOpportunity" .= Null,"canBeCancelled" .= True,"basic" .= False,"additionalCosts" .= ([]::[Value])]
      choices=[Info mempty,AbilityLabel iid ability [] [] [],InvalidLabel "unavailable",SkipTriggersButton iid]
      sentinel=ChooseOne [Label "automatic-sentinel" [Noop]]
      parked s=s {snapshotGame=(snapshotGame s){gameQuestion=Map.singleton player $ WindowChooseOne choices,gameRetainedQuestion=False},snapshotQueue=[Ask player sentinel]}
      pending=parked actual
      preference=typed @Answer $ object ["tag" .= ("SetTriggerModeAnswer"::Text),"contents" .= object
        ["investigatorId" .= iid,"cardCode" .= ("c01033"::Text),"abilityIndex" .= (1::Int),"mode" .= ("AbilityAutoSkip"::Text)]]
  -- Use the original preference operation; it settles the preparation window.
  -- Re-park the explicitly controlled test window to model an imported pause.
  (saved,_) <- applyAnswer pending player (snapshotRevision pending) preference >>= right
  let before=parked saved
  imported <- importSnapshot (exportSnapshot before) >>= right
  view <- boundary (gameCustomCards $ snapshotGame imported) (triggerView (snapshotGame imported) player) >>= right
  assert "remote resume preparation is eligible without import/view advancement" $
    field "canResumeAutomaticTriggers" view == Bool True && exportSnapshot imported == exportSnapshot before
  pure imported

nativeAutomaticTriggersSpecs :: PlayerId -> PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeAutomaticTriggersSpecs player other choose = do
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\e->P.putStrLn e>>exitFailure) pure
  let firstDeck=headEx decks
      ready s = field "available" (field "workbench" $ debugView (snapshotGame s) player)==Bool True
      untilReady budget s
        | ready s = pure s
        | budget<=(0::Int) = error "Automatic integration actual checkpoint not reached"
        | otherwise = do
            answer <- choose player firstDeck s
            (next,_) <- applyAnswer s player (snapshotRevision s) answer>>=right
            untilReady (budget-1) next
  (created,_) <- createSession (CreateRequest Nothing (Just "01104") 201021 [player] Standard False False)>>=right
  actual <- untilReady 120 created
  let iid=fst $ headEx $ Map.toList (gameEntities $ snapshotGame actual).investigators
      tag name=object ["tag" .= (name::Text)]
      ab kind index=typed $ object
        ["source" .= object ["tag" .= ("InvestigatorSource"::Text),"contents" .= iid]
        ,"cardCode" .= ("c01033"::Text),"index" .= (index::Int),"type" .= kind
        ,"limit" .= tag "NoLimit","window" .= tag "AnyWindow","criteria" .= tag "NoRestriction"
        ,"doesNotProvokeAttacksOfOpportunity" .= Null,"canBeCancelled" .= True,"basic" .= False,"additionalCosts" .= ([]::[Value])]
      optional=AbilityLabel iid (ab (object ["tag" .= ("FastAbility'"::Text),"cost" .= tag "Free"]) 1) [] [] []
      optional2=AbilityLabel iid (ab (object ["tag" .= ("ReactionAbility"::Text),"window" .= tag "AnyWindow"]) 2) [] [] []
      forced=AbilityLabel iid (ab (object ["tag" .= ("ForcedAbility"::Text),"window" .= tag "AnyWindow"]) 3) [] [] []
      skip=SkipTriggersButton iid
      choices=[Info mempty,optional,InvalidLabel "unavailable",skip]
      window=WindowChooseOne choices
      sentinel=ChooseOne [Label "automatic-sentinel" [Noop]]
      basis cs tailMessages=actual {snapshotGame=(snapshotGame actual){gameQuestion=Map.singleton player $ WindowChooseOne cs,gameRetainedQuestion=False},snapshotQueue=tailMessages}
      pending=basis choices [Ask player sentinel]
      pref mode=typed @Answer $ object ["tag" .= ("SetTriggerModeAnswer"::Text),"contents" .= object
        ["investigatorId" .= iid,"cardCode" .= ("c01033"::Text),"abilityIndex" .= (1::Int),"mode" .= (mode::Text)]]
      resume=ResumeAutomaticTriggersAnswer AutomaticTriggersResume
      execute s answer=applyAnswer s player (snapshotRevision s) answer>>=right
      projection s=boundary (gameCustomCards $ snapshotGame s) (triggerView (snapshotGame s) player)>>=right
      automaticEvents events=length [()|v<-map eventJSON events,field "text" v==String "根据已保存偏好自动跳过可选触发窗口"]
      assertReplay label before answer=do
        (after,events)<-execute before answer
        imported<-importSnapshot (exportSnapshot before)>>=right
        (again,replayedEvents)<-execute imported answer
        assert (label<>" full snapshot and events exact after import") $ exportSnapshot after==exportSnapshot again && encode (map eventJSON events)==encode (map eventJSON replayedEvents)
        assert (label<>" one external revision") $ snapshotRevision after==snapshotRevision before+1
        pure (after,events)
      emit label before answerValue after events=BL.putStrLn $ "AUTOMATIC_TRIGGER_FIXTURE "<>encode (object ["label" .= (label::Text),"before" .= before,"answer" .= answerValue,"after" .= after,"events" .= map eventJSON events])
      prefWire mode=object ["tag" .= ("SetTriggerModeAnswer"::Text),"contents" .= object ["investigatorId" .= iid,"cardCode" .= ("c01033"::Text),"abilityIndex" .= (1::Int),"mode" .= (mode::Text)]]
      resumeWire=object ["tag" .= ("ResumeAutomaticTriggersAnswer"::Text),"contents" .= object []]
  beforeView<-projection pending
  assert "native trigger schema2 owns execution, default ask" $ field "schemaVersion" beforeView==toJSON (2::Int) && field "executionOwner" beforeView==String "native-rules" && field "canResumeAutomaticTriggers" beforeView==Bool False
  (stopped,events)<-assertReplay "setting stop immediately" pending (pref "AbilityAutoSkip")
  assert "stop consumes real skip in same operation, not optional ability" $ Map.lookup player (gameQuestion $ snapshotGame stopped)==Just sentinel && automaticEvents events==1
  assert "preference and skip leave earlier snapshot immutable" $ gameQuestion (snapshotGame pending)==Map.singleton player window
  emit "immediate" pending (prefWire "AbilityAutoSkip") stopped events
  let consecutive=basis choices [Ask player window,Ask player sentinel]
  (twice,twoEvents)<-assertReplay "two consecutive original skip windows" consecutive (pref "AbilityAutoSkip")
  assert "two skips are one candidate with ordered audit events" $ automaticEvents twoEvents==2 && Map.lookup player (gameQuestion $ snapshotGame twice)==Just sentinel
  emit "consecutive" consecutive (prefWire "AbilityAutoSkip") twice twoEvents
  let parked cs=stopped {snapshotGame=(snapshotGame stopped){gameQuestion=Map.singleton player $ WindowChooseOne cs,gameRetainedQuestion=False},snapshotQueue=[Ask player sentinel]}
  for_ [("no skip",[optional]),("duplicate skip",[optional,skip,skip]),("only skip",[skip]),("forced",[optional,forced,skip]),("unconfigured reaction",[optional,optional2,skip]),("unknown actionable",[optional,Label "must decide" [Noop],skip])] \(label,cs)->do
    let s=parked cs;old=exportSnapshot s
    view<-projection s
    result<-applyAnswer s player (snapshotRevision s) resume
    assert ("native auto blocks "<>label) $ field "canResumeAutomaticTriggers" view==Bool False && isLeft result && exportSnapshot s==old
  let restoredWindow=parked choices
  imported<-importSnapshot (exportSnapshot restoredWindow)>>=right
  void $ projection imported
  assert "import and view preserve eligible restored pause without advancement" $ exportSnapshot imported==exportSnapshot restoredWindow
  (resumed,resumeEvents)<-assertReplay "explicit resume" imported resume
  assert "explicit resume executes core policy not a client index" $ automaticEvents resumeEvents==1 && Map.lookup player (gameQuestion $ snapshotGame resumed)==Just sentinel
  emit "resume" imported resumeWire resumed resumeEvents
  let ownerBasis=parked choices
  (ownerAsk,ownerEvents)<-assertReplay "owner-only own active investigator" ownerBasis (pref "AbilityOwnerOnly")
  assert "owner-only preserves own active manual options" $ gameQuestion (snapshotGame ownerAsk)==gameQuestion (snapshotGame ownerBasis) && automaticEvents ownerEvents==0
  emit "owner-asks" ownerBasis (prefWire "AbilityOwnerOnly") ownerAsk ownerEvents
  (alwaysAsk,askEvents)<-assertReplay "switch to ask" restoredWindow (pref "AbilityAlwaysAsk")
  assert "ask preference neither consumes tail nor hides choices" $ gameQuestion (snapshotGame alwaysAsk)==gameQuestion (snapshotGame restoredWindow) && snapshotQueue alwaysAsk==snapshotQueue restoredWindow && null askEvents
  emit "ask" restoredWindow (prefWire "AbilityAlwaysAsk") alwaysAsk askEvents
  legacy<-applyAnswer restoredWindow player (snapshotRevision restoredWindow) (AutoSkipTriggersAnswer 3)
  foreignSeat<-applyAnswer restoredWindow other (snapshotRevision restoredWindow) resume
  stale<-applyAnswer restoredWindow player (snapshotRevision restoredWindow+1) resume
  assert "legacy frontend index, wrong seat and stale resume rejected" $ all isLeft [legacy,foreignSeat,stale]
  let excessive=restoredWindow {snapshotQueue=replicate 257 (Ask player window)<>[Ask player sentinel]}
      original=exportSnapshot excessive
  limited<-applyAnswer excessive player (snapshotRevision excessive) resume
  assert "automatic bound discards candidate instead of partial commit" $ isLeft limited && exportSnapshot excessive==original
  assert "resume payload exposes no message/index/seat fields" $ isNothing (decode @Answer $ encode $ object ["tag" .= ("ResumeAutomaticTriggersAnswer"::Text),"contents" .= object ["index" .= (3::Int)]])
  let teamReady s=any ((==Bool True) . field "available" . field "workbench" . debugView (snapshotGame s)) [player,other]
      untilTeam budget s
        | teamReady s=pure s
        | budget<=(0::Int)=error "Actual two-seat checkpoint not reached"
        | otherwise=do
            let who=fst $ headEx $ Map.toList $ gameQuestion $ snapshotGame s
                deck=if who==player then headEx decks else decks P.!! 1
            answer<-choose who deck s
            (next,_)<-applyAnswer s who (snapshotRevision s) answer>>=right
            untilTeam (budget-1) next
  (teamCreated,_)<-createSession (CreateRequest Nothing (Just "01104") 201022 [player,other] Standard False False)>>=right
  team<-untilTeam 160 teamCreated
  let actorFor who=fst $ fromJustNote "actual owned teammate" $ find (\(_,actor)->field "playerId" (toJSON actor)==toJSON who) $ Map.toList (gameEntities $ snapshotGame team).investigators
      actor1=actorFor player
      actor2=actorFor other
      abilityFor actor=typed $ object
        ["source" .= object ["tag" .= ("InvestigatorSource"::Text),"contents" .= actor]
        ,"cardCode" .= ("c01033"::Text),"index" .= (1::Int),"type" .= object ["tag" .= ("FastAbility'"::Text),"cost" .= tag "Free"]
        ,"limit" .= tag "NoLimit","window" .= tag "AnyWindow","criteria" .= tag "NoRestriction"
        ,"doesNotProvokeAttacksOfOpportunity" .= Null,"canBeCancelled" .= True,"basic" .= False,"additionalCosts" .= ([]::[Value])]
      choiceFor actor=AbilityLabel actor (abilityFor actor) [] [] []
      teamWindow actor=WindowChooseOne [choiceFor actor,SkipTriggersButton actor]
      -- Controlled retained AskMap contract. An ordinary non-retained Window
      -- has an original regeneration tail, and must NOT be re-parked stale.
      teamNoSkip=team {snapshotGame=(snapshotGame team){gameQuestion=Map.fromList [(player,WindowChooseOne [choiceFor actor1]),(other,WindowChooseOne [choiceFor actor2])],gameRetainedQuestion=True},snapshotQueue=[Ask player sentinel]}
      teamPref actor=typed @Answer $ object ["tag" .= ("SetTriggerModeAnswer"::Text),"contents" .= object ["investigatorId" .= actor,"cardCode" .= ("c01033"::Text),"abilityIndex" .= (1::Int),"mode" .= ("AbilityAutoSkip"::Text)]]
      offeredTeam s=s {snapshotGame=(snapshotGame s){gameQuestion=Map.fromList [(player,teamWindow actor1),(other,teamWindow actor2)],gameRetainedQuestion=True}}
  (oneSaved,_)<-applyAnswer teamNoSkip player (snapshotRevision teamNoSkip) (teamPref actor1)>>=right
  let ordinaryGame=(snapshotGame $ offeredTeam oneSaved){gameRetainedQuestion=False}
  ordinaryRoute<-handleAnswerPure ordinaryGame player $ AutoSkipTriggersAnswer 1
  assert "non-retained regenerated window is not incorrectly appended as stale AskMap" $ case ordinaryRoute of
    Handled messages->not $ any (\case AskMap questions->Map.member other questions;Retain (AskMap questions)->Map.member other questions;_->False) messages
    Unhandled _->False
  (leftOther,leftEvents)<-assertReplay "two seats independent saved preferences" (offeredTeam oneSaved) resume
  assert "other seat default ask is preserved after first seat automatically skips" $ Map.lookup other (gameQuestion $ snapshotGame leftOther)==Just (teamWindow actor2) && automaticEvents leftEvents==1
  assert "retained other-seat pause preserves original continuation tail" $ drop (length (snapshotQueue leftOther)-length (snapshotQueue oneSaved)) (snapshotQueue leftOther)==snapshotQueue oneSaved
  BL.putStrLn $ "AUTOMATIC_TRIGGER_MULTIPLAYER "<>encode (object ["before" .= offeredTeam oneSaved,"after" .= leftOther,"events" .= map eventJSON leftEvents,"preservedPlayer" .= other,"expectedAutomaticSteps" .= (1::Int)])
  (bothSaved,_)<-applyAnswer oneSaved other (snapshotRevision oneSaved) (teamPref actor2)>>=right
  (bothSkipped,bothEvents)<-assertReplay "two seats explicit saved stop" (offeredTeam bothSaved) resume
  assert "both seats independently authorized skips settle before final pause" $ automaticEvents bothEvents==2 && Map.lookup player (gameQuestion $ snapshotGame bothSkipped)==Just sentinel
  BL.putStrLn $ "AUTOMATIC_TRIGGER_MULTIPLAYER "<>encode (object ["before" .= offeredTeam bothSaved,"after" .= bothSkipped,"events" .= map eventJSON bothEvents,"expectedAutomaticSteps" .= (2::Int)])
  putStrLn "AUTOMATIC_TRIGGERS_INTEGRATION_COMPLETED"
