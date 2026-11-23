module NativeHistoryBootstrapSpec (nativeHistoryBootstrapSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.CampaignLog
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Question
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Events (eventJSON)
import Arkham.Native.History.Types
import Arkham.Native.History.Profile qualified as Profile
import Arkham.Native.History.Story qualified as Story
import Arkham.Native.History.Manifest qualified as Manifest
import Arkham.Native.History.Inheritance (HistoryStepPolicy (..))
import Arkham.Native.Settings.Engine qualified as Chapter
import Arkham.Native.Settings.Types
import SettingsEngineSpec (complete)
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Aeson.Key qualified as K
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Types (parseEither,Parser)
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Either (isLeft,isRight)
import System.Exit (exitFailure)
import Control.Monad.Fail (fail)

assert :: Text -> Bool -> IO ()
assert label ok=if ok then putStrLn ("PASS "<>label) else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right=either (\reason->putStrLn reason>>exitFailure) pure
typed :: FromJSON a => Value -> a
typed=toResult
campaignOf :: Snapshot -> Value
campaignOf snapshot=case gameMode $ snapshotGame snapshot of This c->toJSON c;These c _->toJSON c;_->error "Expected campaign"
field :: Text -> Value -> Value
field name (Object o)=fromMaybe Null $ KM.lookup (K.fromText name) o
field _ _=Null
items :: Value -> [Value]
items (Array values)=toList values
items (Object values)=Map.elems $ Map.fromList [(K.toText key,value)|(key,value)<-KM.toList values]
items _=[]

nativeHistoryBootstrapSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeHistoryBootstrapSpecs player chooseActual=do
  storySpecs
  manifestSpecs
  decks<-eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason->putStrLn (fromString reason)>>exitFailure) pure
  let deck=headEx decks
      untilSettings budget snapshot
        | any ((==PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot)=pure snapshot
        | budget<=(0::Int)=assert "history bootstrap reached original settings barrier" False>>pure snapshot
        | otherwise=do
            assert "history bootstrap survives every ordinary deck or trauma pause" $ historyPending snapshot
            answer<-chooseActual player deck snapshot
            (next,_)<-applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            untilSettings (budget-1) next
      request campaign scenario=typed $ object ["campaign" .= (campaign::Text),"scenario" .= (scenario::Text)
        ,"seed" .= (20102026::Int),"players" .= [player]]
  (initial,_)<-createHistoricalSession (request "52" "52048") >>= right
  parked<-untilSettings 16 initial
  assert "history bootstrap new entry retains real pending initial intention" $ historyPending parked
  let old=exportSnapshot parked
  imported<-importSnapshot old >>= right
  assert "history bootstrap pending export import preserves whole snapshot" $ exportSnapshot imported==old
  chapter<-right $ Chapter.resolveHistoricalSchema (snapshotGame parked) player
  form<-complete chapter
  prepared<-right $ historicalPrepare parked player (snapshotRevision parked) form
  draft0<-right $ first fromString $ parseEither (withObject "prepared" (.: "draft")) prepared
  let draft=draft0
        {historyStoryAssignments=[HistoryStoryAssignment "story:03059" "seat:1" 1]
        ,historySteps=[("scenario:52014",Just "resolution:1")]
        ,historyInvestigators=Map.map (\a->a {historyExperience=11,historyPhysicalTrauma=1}) $ historyInvestigators draft0}
  _<-right $ historicalPreview parked player (snapshotRevision parked) draft
  assert "history bootstrap prepare and preview consume no whole state or RNG" $ exportSnapshot parked==old
  assert "history bootstrap ordinary settings preview cannot downgrade history" $ isLeft $ settingsPreview parked player (snapshotRevision parked) form
  rejected<-applyAnswer parked player (snapshotRevision parked) (SettingsFormAnswer form)
  assert "history bootstrap ordinary form submission cannot bypass historical state" $ isLeft rejected
  debugRejected<-applyDebug parked player (snapshotRevision parked) True $ object ["operation" .= ("setExperience"::Text),"investigatorId" .= ("c01001"::Text),"amount" .= (3::Int)]
  assert "history bootstrap pending debug mutation rejected" $ isLeft debugRejected
  assert "history bootstrap missing unique Stranger rejected before mutation" $ isLeft $ historicalPreview parked player (snapshotRevision parked) draft {historyStoryAssignments=[]}
  assert "history bootstrap duplicate unique Stranger rejected" $ isLeft $ historicalPreview parked player (snapshotRevision parked)
    draft {historyStoryAssignments=replicate 2 $ HistoryStoryAssignment "story:03059" "seat:1" 1}
  assert "history bootstrap foreign owner rejected" $ isLeft $ historicalPreview parked player (snapshotRevision parked)
    draft {historyStoryAssignments=[HistoryStoryAssignment "story:03059" "seat:9" 1]}
  assert "history bootstrap foreign schema and stale preview rejected" $
    isLeft (historicalPreview parked player (snapshotRevision parked) draft {historySchemaId="foreign"})
    && isLeft (historicalPreview parked player (snapshotRevision parked+1) draft)
  assert "history Lodge reward cannot be installed in a different campaign" $ isLeft $
    historicalPreview parked player (snapshotRevision parked) draft {historyLodgeSetupBeneficiaries=["seat:1"]}
  assert "history bootstrap all rejected previews leave exact original bytes" $ exportSnapshot parked==old
  (installed,events)<-applyAnswer parked player (snapshotRevision parked) (HistoricalSettingsAnswer draft) >>= right
  let cards=concatMap items $ items $ field "storyCards" $ campaignOf installed
      actors=items $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame installed
      cardId card=field "id" $ field "contents" card
      registered=items $ field "gameCards" $ toJSON $ snapshotGame installed
  assert "history bootstrap original scenario continuation runs after one installation" $
    not (historyPending installed) && maybe False ((==HistoryInstalled) . bootstrapPhase) (snapshotHistoricalBootstrap installed)
    && snapshotRevision installed==snapshotRevision parked+1
  assert "history bootstrap generated actual unique Stranger in original campaign custody" $
    length (filter ((==String "c03059") . field "cardCode" . field "contents") cards)==1
      && all (\card->any ((==cardId card) . cardId) registered) cards
  assert "history bootstrap actual original reload preserves target XP and trauma" $
    any (\actor->field "xp" actor==toJSON (11::Int) && field "physicalTrauma" actor==toJSON (1::Int)) actors
  assert "history bootstrap past actual Return scenario resolution installed separately" $
    field "c52014" (field "resolutions" $ campaignOf installed)==object ["tag" .= ("Resolution"::Text),"contents" .= (1::Int)]
  reopen<-importSnapshot (exportSnapshot installed) >>= right
  assert "history bootstrap installed reopen preserves whole Game queue RNG and provenance" $ exportSnapshot reopen==exportSnapshot installed
  (replayed,replayedEvents)<-applyAnswer imported player (snapshotRevision imported) (HistoricalSettingsAnswer draft) >>= right
  assert "history bootstrap restored old snapshot retries exact full candidate and events" $
    exportSnapshot replayed==exportSnapshot installed && encode (map eventJSON replayedEvents)==encode (map eventJSON events)
  twice<-applyAnswer reopen player (snapshotRevision reopen) (HistoricalSettingsAnswer draft)
  assert "history bootstrap installed history cannot be submitted a second time" $ isLeft twice
  let rawOld=toJSON installed
  assert "history bootstrap snapshot format 2 explicitly carries provenance" $ isRight $ parseEither
    (withObject "snapshot" \o->do
      version<-o .: "snapshotFormat"
      unless (version==(2::Int)) $ fail "format"
      void (o .: "historicalBootstrap" :: Parser (Maybe HistoricalBootstrap))) rawOld
  BL.putStrLn $ "HISTORY_BOOTSTRAP_FIXTURE "<>encode (object
    ["pending" .= parked,"draft" .= draft,"installed" .= installed,"reopened" .= reopen,"replayed" .= replayed
    ,"previewPure" .= (old==exportSnapshot parked),"naturalCampaignCompletionNotTested" .= True])
  inheritedBoundarySpecs player chooseActual decks
  putStrLn "HISTORY_BOOTSTRAP_INTEGRATION_COMPLETED"

-- Two extra adapter cases, not a campaign correctness matrix: one actual
-- nonempty chapter form and one nonempty TFA metadata/two-seat installation.
inheritedBoundarySpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> [ArkhamDBDecklist] -> IO ()
inheritedBoundarySpecs player chooseActual decks=do
  let other=typed $ String "00000000-0000-4000-8000-000000000002"
      untilSettings budget actors snapshot
        | any ((==PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot)=pure snapshot
        | budget<=(0::Int)=assert "history additional original settings barrier reached" False>>pure snapshot
        | otherwise=do
            let asked=filter (\(pid,_)->Map.member pid $ gameQuestion $ snapshotGame snapshot) actors
            (pid,deck)<-case asked of pair:_->pure pair;_->assert "history additional seat has actual question" False>>pure (headEx actors)
            answer<-chooseActual pid deck snapshot
            (next,_)<-applyAnswer snapshot pid (snapshotRevision snapshot) answer >>= right
            assert "history two-seat deck and trauma replies preserve pending provenance" $ historyPending next
            untilSettings (budget-1) actors next
      request campaign scenario players=typed $ object ["campaign" .= (campaign::Text),"scenario" .= (scenario::Text)
        ,"seed" .= (20102027::Int),"players" .= players]
      preparedDraft snapshot form=do
        prepared<-right $ historicalPrepare snapshot player (snapshotRevision snapshot) form
        draft<-right $ first fromString $ parseEither (withObject "prepared" (.: "draft")) prepared
        pure (prepared,draft)
  (initial,_)<-createHistoricalSession (request "02" "02236" [player]) >>= right
  chapterPause<-untilSettings 16 [(player,headEx decks)] initial
  chapter<-right $ Chapter.resolveHistoricalSchema (snapshotGame chapterPause) player
  form<-complete chapter
  (prepared,draft0)<-preparedDraft chapterPause form
  let logValue=typed $ field "chapterLog" prepared
      selected=campaignLogOptions logValue
      rewards=items $ field "rewards" $ field "story" prepared
      chosen=[HistoryStoryAssignment (typed $ field "id" row) "seat:1" 1|row<-rewards,field "option" row/=Null
        ,(typed $ field "option" row) `member` selected,field "unavailable" row==Null]
      draft=draft0 {historyStoryAssignments=chosen}
      chapterBefore=exportSnapshot chapterPause
  _<-right $ historicalPreview chapterPause player (snapshotRevision chapterPause) draft
  (chapterAfter,_)<-applyAnswer chapterPause player (snapshotRevision chapterPause) (HistoricalSettingsAnswer draft) >>= right
  assert "history actual nonempty Dunwich chapter form passes one atomic native installation" $
    not (historyPending chapterAfter) && notNull (campaignLogRecorded logValue) && snapshotRevision chapterAfter==snapshotRevision chapterPause+1
  assert "history nonempty chapter preview and installation preserve old whole snapshot" $ exportSnapshot chapterPause==chapterBefore
  reopenedChapter<-importSnapshot (exportSnapshot chapterAfter) >>= right
  assert "history nonempty chapter whole state reopens exactly" $ exportSnapshot reopenedChapter==exportSnapshot chapterAfter
  BL.putStrLn $ "HISTORY_NONEMPTY_CHAPTER_FIXTURE "<>encode (object ["pending" .= chapterPause,"draft" .= draft,"installed" .= chapterAfter,"reopened" .= reopenedChapter])

  (twoInitial,_)<-createHistoricalSession (request "53" "53028" [player,other]) >>= right
  twoPause<-untilSettings 24 [(player,headEx decks),(other,fromJustNote "second preset" $ decks !!? 1)] twoInitial
  schema<-right $ Chapter.resolveHistoricalSchema (snapshotGame twoPause) player
  twoForm<-complete schema
  (_,twoDraft0)<-preparedDraft twoPause twoForm
  let target slot value=value {historyExperience=if slot=="seat:1" then 19 else 7
        ,historySupplies=Map.fromList [("Medicine",if slot=="seat:1" then 2 else 1),("Provisions",3)]}
      metadata=Map.fromList [("tfa:supply-points:seat:1",FormInteger 3),("tfa:yithian:seat:1",FormBoolean False)
        ,("tfa:bonus-xp:seat:1",FormInteger 4),("tfa:supply-points:seat:2",FormInteger 2)
        ,("tfa:yithian:seat:2",FormBoolean False),("tfa:bonus-xp:seat:2",FormInteger 1)
        ,("tfa:expedition-leader",FormChoice $ Just "seat:2")]
      twoDraft=twoDraft0 {historyInvestigators=Map.mapWithKey target $ historyInvestigators twoDraft0
        ,historyMetadata=metadata,historyStoryAssignments=[HistoryStoryAssignment "story:04102" slot 1|slot<-["seat:1","seat:2"]]
        ,historyBag=Just $ Map.fromList [("face:Zero",4),("face:AutoFail",1),("face:ElderSign",1)]}
      twoBefore=exportSnapshot twoPause
  assert "history TFA missing current typed metadata rejected" $ isLeft $ historicalPreview twoPause player (snapshotRevision twoPause)
    twoDraft {historyMetadata=Map.delete "tfa:bonus-xp:seat:2" metadata}
  wrongSeat<-applyAnswer twoPause other (snapshotRevision twoPause) (HistoricalSettingsAnswer twoDraft)
  assert "history non-lead seat cannot answer initial settings on teammate behalf" $ isLeft wrongSeat
  _<-right $ historicalPreview twoPause player (snapshotRevision twoPause) twoDraft
  (twoAfter,_)<-applyAnswer twoPause player (snapshotRevision twoPause) (HistoricalSettingsAnswer twoDraft) >>= right
  let campaign=campaignOf twoAfter
      actualMetadata=field "meta" campaign
      actors=items $ field "investigators" $ field "gameEntities" $ toJSON $ snapshotGame twoAfter
      storyDecks=items $ field "storyCards" campaign
  assert "history actual TFA metadata is nonempty and retains both owned maps" $
    field "supplyPoints" actualMetadata==object ["c60151" .= (3::Int),"c60251" .= (2::Int)]
    && field "bonusXp" actualMetadata==object ["c60151" .= (4::Int),"c60251" .= (1::Int)]
    && field "expeditionLeader" actualMetadata==String "c60251"
  assert "history actual two-seat supply multiplicity survives original reset" $
    any (\actor->length (filter (==String "Medicine") $ items $ field "supplies" actor)==2) actors
      && any (\actor->length (filter (==String "Medicine") $ items $ field "supplies" actor)==1) actors
  assert "history actual original two-seat reload preserves separate XP targets" $
    sort (map (field "xp") actors)==sort [toJSON (19::Int),toJSON (7::Int)]
  assert "history two actual Poisoned cards have distinct real owners" $
    length storyDecks==2 && all (\cards->length (filter ((==String "c04102") . field "cardCode" . field "contents") $ items cards)==1) storyDecks
  assert "history TFA rejected previews never alter old whole snapshot" $ exportSnapshot twoPause==twoBefore
  reopenedTwo<-importSnapshot (exportSnapshot twoAfter) >>= right
  assert "history TFA full metadata cards queue RNG reopen exactly" $ exportSnapshot reopenedTwo==exportSnapshot twoAfter
  restoredTwo<-importSnapshot twoBefore >>= right
  (replayedTwo,_)<-applyAnswer restoredTwo player (snapshotRevision restoredTwo) (HistoricalSettingsAnswer twoDraft) >>= right
  assert "history TFA two-seat whole install deterministically replays from old snapshot" $ exportSnapshot replayedTwo==exportSnapshot twoAfter
  BL.putStrLn $ "HISTORY_TFA_BOOTSTRAP_FIXTURE "<>encode (object ["pending" .= twoPause,"draft" .= twoDraft,"installed" .= twoAfter,"reopened" .= reopenedTwo,"replayed" .= replayedTwo])

  -- Test the new identity-bound adapter, not the original campaign rules.
  (lodgeInitial,_)<-createHistoricalSession (request "54" "54056" [player,other]) >>= right
  lodgePause<-untilSettings 24 [(player,headEx decks),(other,fromJustNote "second preset" $ decks !!? 1)] lodgeInitial
  lodgeSchema<-right $ Chapter.resolveHistoricalSchema (snapshotGame lodgePause) player
  lodgeForm<-complete lodgeSchema
  (_,lodgeDraft0)<-preparedDraft lodgePause lodgeForm
  assert "history Lodge beneficiaries are never inferred from current seats" $ null $ historyLodgeSetupBeneficiaries lodgeDraft0
  let flagId="log:TheInvestigatorsAssumedControlOfTheSilverTwilightLodge"
      oldLog=historyLog lodgeDraft0
      lodgeDraft=lodgeDraft0
        {historyLog=oldLog {historyFlags=Map.insert flagId (HistoryFlags True False) $ historyFlags oldLog
          ,historyOrderedKeys=nub $ historyOrderedKeys oldLog<>[flagId]}
        ,historySteps=[("interlude:4",Nothing)],historyRetiredIdentities=["retired:01001"]
        ,historyLodgeSetupBeneficiaries=["seat:1","retired:01001"]}
      preview value=historicalPreview lodgePause player (snapshotRevision lodgePause) value
      oldLodge=exportSnapshot lodgePause
  actualPreview<-right $ preview lodgeDraft
  assert "history Lodge preview binds an old current actor and explicit retired actor not replacement" $
    field "lodgeFutureFinaleBeneficiaries" actualPreview==toJSON (["c60151","c01001"]::[Text])
  assert "history Lodge prior interlude missing cannot preinstall future rewards" $ isLeft $ preview lodgeDraft {historySteps=[]}
  assert "history Lodge prior assumed-control record required" $ isLeft $ preview lodgeDraft
    {historyLog=oldLog {historyFlags=Map.insert flagId (HistoryFlags False False) $ historyFlags oldLog}}
  assert "history Lodge unselected retired identity rejected" $ isLeft $ preview lodgeDraft {historyRetiredIdentities=[]}
  assert "history Lodge unknown current slot and duplicate identity rejected" $
    isLeft (preview lodgeDraft {historyLodgeSetupBeneficiaries=["seat:9"]})
      && isLeft (preview lodgeDraft {historyLodgeSetupBeneficiaries=["seat:1","seat:1"]})
  assert "history Lodge fifth historical beneficiary rejected before installation" $ isLeft $ preview lodgeDraft
    {historyRetiredIdentities=["retired:01001","retired:01002","retired:01003"]
    ,historyLodgeSetupBeneficiaries=["seat:1","seat:2","retired:01001","retired:01002","retired:01003"]}
  _<-right $ preview lodgeDraft {historyLodgeSetupBeneficiaries=[]}
  (emptyLodge,_)<-applyAnswer lodgePause player (snapshotRevision lodgePause)
    (HistoricalSettingsAnswer lodgeDraft {historyLodgeSetupBeneficiaries=[]}) >>= right
  (lodgeAfter,_)<-applyAnswer lodgePause player (snapshotRevision lodgePause) (HistoricalSettingsAnswer lodgeDraft) >>= right
  let effects snapshot=filter ((==String "cwmode") . field "cardCode") $ items $ field "effects" $ field "gameEntities" $ toJSON $ snapshotGame snapshot
      targets=map (field "target") $ effects lodgeAfter
      target iid=object ["tag" .= ("InvestigatorTarget"::Text),"contents" .= (iid::Text)]
  assert "history Lodge empty explicit list generates no inherited effect" $ null $ effects emptyLodge
  assert "history Lodge actual four effects target exactly two identities not new seat 2 actor" $
    length targets==4 && length (filter (==target "c60151") targets)==2
      && length (filter (==target "c01001") targets)==2 && target "c60251" `notElem` targets
  reopenedLodge<-importSnapshot (exportSnapshot lodgeAfter) >>= right
  restoredLodge<-importSnapshot oldLodge >>= right
  (replayedLodge,_)<-applyAnswer restoredLodge player (snapshotRevision restoredLodge) (HistoricalSettingsAnswer lodgeDraft) >>= right
  assert "history Lodge import replay preserve exact effects targets counts whole state queue RNG" $
    exportSnapshot reopenedLodge==exportSnapshot lodgeAfter && exportSnapshot replayedLodge==exportSnapshot lodgeAfter
  assert "history Lodge all rejected and pure previews leave pending snapshot untouched" $ exportSnapshot lodgePause==oldLodge
  BL.putStrLn $ "HISTORY_LODGE_BOOTSTRAP_FIXTURE "<>encode (object
    ["pending" .= lodgePause,"draft" .= lodgeDraft,"preview" .= actualPreview,"installed" .= lodgeAfter
    ,"emptyBeneficiaries" .= emptyLodge,"reopened" .= reopenedLodge,"replayed" .= replayedLodge])

storySpecs :: IO ()
storySpecs=do
  let iid="01001";other="01002"
      actors=Map.fromList [("seat:1",(iid,typed $ String "Guardian")),("seat:2",(other,typed $ String "Guardian"))]
      context campaign family=Story.Context campaign family "52048" mempty actors [] mempty mempty False
      assignment code owner=HistoryStoryAssignment ("story:"<>code) owner 1
  let notz=context "01" "01"
      lita=typed $ object ["tag" .= ("AddLitaChantler"::Text)]
      cheatedOption=typed $ object ["tag" .= ("Cheated"::Text)]
  assert "history story Lita chapter option requires an explicit current owner" $ isLeft $ Story.compile notz (Set.singleton lita) []
  plan<-right $ Story.compile notz (Set.singleton lita) [assignment "01117" "seat:1"]
  assert "history story chapter option and owner become one grant not two" $ length (Story.storyCardsToCreate plan)==1 && null (Story.storyOriginalOptions plan)
  let tfa=context "53" "04"
  assert "history story Poisoned can be inherited by two different seats" $ isRight $ Story.compile tfa mempty [assignment "04102" "seat:1",assignment "04102" "seat:2"]
  assert "history story four Relic variants remain globally exclusive" $ isLeft $ Story.compile tfa mempty [assignment "04061" "seat:1",assignment "04191" "seat:2"]
  assert "history story same-seat Poisoned duplicate rejected" $ isLeft $ Story.compile tfa mempty [assignment "04102" "seat:1",assignment "04102" "seat:1"]
  let dunwich=context "51" "02"
  cheated<-right $ Story.compile dunwich (Set.singleton cheatedOption) []
  assert "history story non-card Cheated option is retained for original handler" $ Story.storyOriginalOptions cheated==[cheatedOption]
  assert "history story pre-existing base-deck reward cannot generate another copy" $ isLeft $ Story.compile
    notz {Story.storyExistingCards=Map.singleton iid ["01117"]} mempty [assignment "01117" "seat:1"]
  assert "history story current Phantom setup cannot inherit a duplicate Lost Soul" $ isLeft $ Story.compile
    (context "52" "03") {Story.storyStart="52040"} mempty [assignment "03227" "seat:1"]
  keys<-right $ Profile.familyKeys "05"
  let anetteKey=fromJustNote "fixed Anette key" $ find ((=="AnetteTaughtYouTheSpellsOfOld") . Profile.keyName) keys
      anette=(context "54" "05") {Story.storyLog=mempty {campaignLogRecorded=Set.singleton anetteKey},Story.storyCompletedScenarios=["interlude:4"]}
  rules<-right $ Story.catalog anette
  assert "history story Return Anette uses finite real eligible weaknesses" $ any (\r->case Story.storyMultiplicity r of Story.PerOwnerFamilyOne "anette-spells"->notNull (Story.storyAllowedOwners r)&&isNothing (Story.storyUnavailable r);_->False) rules
  early<-right $ Story.catalog anette {Story.storyCompletedScenarios=["interlude:3"]}
  assert "history story Anette is not granted one interlude too early" $ all (\r->case Story.storyMultiplicity r of Story.PerOwnerFamilyOne "anette-spells"->isJust $ Story.storyUnavailable r;_->True) early

manifestSpecs :: IO ()
manifestSpecs=do
  actual<-right $ Manifest.scenarios "54"
  assert "history manifest Return Union exposes final stored 2 8 9 only" $
    maybe False ((==[object ["tag" .= ("Resolution"::Text),"contents" .= n]|n<-[2,8,9::Int]]) . map toJSON . Manifest.finalStoredResolutions) $
      find ((=="54046") . Manifest.actualScenario) actual
  assert "history manifest Return skip prologue distinguishes absent resolution" $
    Manifest.validateCompletedResolutions "54" [("scenario:54016",Nothing)]==Right ()
    && isLeft (Manifest.validateCompletedResolutions "05" [("scenario:05043",Nothing)])
  choices<-right $ Manifest.choicesForStart "51" "51012"
  assert "history manifest Dunwich reverse opening permits completed House" $ Map.member "scenario:51015" choices
  tcu<-right $ Manifest.choicesForStart "54" "54056"
  assert "history manifest TCU exact Price keys never invent interlude2 Nothing" $
    Map.notMember "interlude:2" tcu && Map.member "interlude:2:ThePriceOfProgress4" tcu
  tfa<-right $ Manifest.choicesForStart "53" "53059"
  assert "history manifest TFA poison part 51 is retained" $ Map.member "interlude-part:4:none:51" tfa
  assert "history manifest reversed completed chapter chronology rejected" $ isLeft $
    Manifest.validateChronology "52" "52048" [("scenario:52028",Just "resolution:1"),("prologue",Nothing)]
  assert "history manifest Dunwich reversed opening order remains legal" $ isRight $
    Manifest.validateChronology "51" "51020" [("scenario:51015",Just "resolution:1"),("scenario:51012",Just "resolution:1")]
  assert "history manifest mutually exclusive Price variants rejected" $ isLeft $
    Manifest.validateChronology "54" "54056" [("interlude:2:ThePriceOfProgress5",Nothing),("interlude:2:ThePriceOfProgress6",Nothing)]
  later<-right $ Manifest.choicesForStart "52" "52059"
  assert "history manifest terminal Black Stars numeric and NoResolution cannot precede next start" $
    maybe False ((==["resolution:1","resolution:2"]) . Map.keys . historyResolutions) $ Map.lookup "scenario:52054" later
  assert "history manifest terminal Union 2 and 9 cannot precede later TCU start" $
    maybe False ((==["resolution:8"]) . Map.keys . historyResolutions) $ Map.lookup "scenario:54046" tcu
  special<-right $ Manifest.choicesForStart "53" "53066"
  assert "history manifest special TurnBackTime only offers Shattered resolution5" $
    maybe False ((==["resolution:5"]) . Map.keys . historyResolutions) $ Map.lookup "scenario:53061" special
  assert "history manifest inherited TurnBackTime requires resolution5 plus epilogue" $
    isLeft (Manifest.validateHistoricalRoute "53" "53066" [("scenario:53061",Just "resolution:5")])
    && isRight (Manifest.validateHistoricalRoute "53" "53066" [("scenario:53061",Just "resolution:5"),("epilogue",Nothing)])
