module NativeHistoryFoundationSpec (nativeHistoryFoundationSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.CampaignLog
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Question
import Arkham.Native.Answer
import Arkham.Native.Session
import Arkham.Native.Settings.Engine qualified as Engine
import Arkham.Native.History.Types
import Arkham.Native.History.Profile
import Arkham.Native.History.Log
import Arkham.Native.History.Inheritance
import Arkham.Native.Settings.Types
import Data.Map.Strict qualified as Map
import Data.Set qualified as Set
import Data.Aeson.Types (parseEither)
import Data.ByteString.Lazy.Char8 qualified as BL
import Data.Either (isLeft,isRight)
import System.Exit (exitFailure)
import SettingsEngineSpec (complete)

assert :: Text -> Bool -> IO ()
assert label ok=if ok then putStrLn ("PASS "<>label) else putStrLn ("FAIL "<>label)>>exitFailure
right :: Either Text a -> IO a
right=either (\reason->putStrLn reason>>exitFailure) pure
typed :: FromJSON a => Value -> a
typed=toResult

emptyDraft :: HistoryLogDraft
emptyDraft=HistoryLogDraft mempty mempty mempty [] mempty mempty

policyFor :: Text -> IO HistoryLogPolicy
policyFor actual=do
  profile<-right $ profileFor actual
  fields<-right $ resolveHistoryFields profile [typed $ String "c01001",typed $ String "c02001"]
  pure $ HistoryLogPolicy fields mempty mempty

nativeHistoryFoundationSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
nativeHistoryFoundationSpecs player chooseActual=do
  for_ ["01","02","05","50","51","52","53","54"] \actual->do
    policy<-policyFor actual
    assert ("history finite profile resolves "<>actual) $ not $ Map.null $ resolvedHistoryFields policy
    assert ("history finite profile unique field identities "<>actual) $
      all (\(ident,field)->ident==historyFieldId (resolvedHistoryField field)) $ Map.toList $ resolvedHistoryFields policy
  assert "history unsupported campaign fails explicitly" $ isLeft $ profileFor "unknown"
  notz<-policyFor "01"
  returnNotz<-policyFor "50"
  let cultistCount policy=length $ resolvedHistoryEntries $ fromJustNote "actual cultist domain" $ Map.lookup "log:CultistsWeInterrogated" $ resolvedHistoryFields policy
  assert "history NOTZ missing old-form domain resolved from six original cultists" $ cultistCount notz==6
  assert "history ReturnTo NOTZ adds actual three cultists and Narogath" $ cultistCount returnNotz==10
  let expectedCultists codes=map (\code->object ["recordType" .= ("RecordableCardCode"::Text)
        ,"recordVal" .= object ["tag" .= ("Recorded"::Text),"contents" .= ("c"<>code)]]) codes
      baseCultists=["01137","01138","01139","01140","01141","01121b"]
      entriesFor policy ident=map (withoutCircle . snd) $ resolvedHistoryEntries $ fromJustNote "offered cultists" $ Map.lookup ident $ resolvedHistoryFields policy
  for_ [(notz,baseCultists),(returnNotz,baseCultists<>["50044","50045","50046","50026b"])] \(policy,codes)->
    for_ ["log:CultistsWeInterrogated","log:CultistsWhoGotAway"] \ident->assert ("history exact enemy-face inventory no Ghoul/ordinarycultist "<>ident) $
      let actual=entriesFor policy ident;expected=expectedCultists codes
      in length actual==length expected && all (`elem` actual) expected
  carcosa<-policyFor "52"
  let get p ident=fromJustNote "test offered field" $ Map.lookup ident $ resolvedHistoryFields p
      vipInterview=get carcosa "log:VIPsInterviewed"
      vipSlain=get carcosa "log:VIPsSlain"
      codes=map (withoutCircle . snd) . resolvedHistoryEntries
  assert "history Carcosa interview and slain exact disjoint domains" $
    length (codes vipInterview)==5 && length (codes vipSlain)==5
    && not (any (`elem` codes vipSlain) $ codes vipInterview)
  assert "history Carcosa readActII uses offered investigator identities" $
    length (resolvedHistoryEntries $ get carcosa "log:ReadActII")==2
  let flagFields=[(ident,historyFieldKey $ resolvedHistoryField field)|(ident,field)<-Map.toAscList $ resolvedHistoryFields carcosa
        ,FlagStorage `elem` historyFieldStorage (resolvedHistoryField field)]
      (flagId,flagKey)=headEx flagFields
      countId="log:ChasingTheStranger"
      countKey=historyFieldKey $ resolvedHistoryField $ get carcosa countId
      flagDraft=emptyDraft {historyFlags=Map.singleton flagId $ HistoryFlags True True
        ,historyCounts=Map.singleton countId 9007199254740993,historyOrderedKeys=[flagId,countId]}
  flagLog<-right $ compileHistoricalLog carcosa mempty mempty flagDraft
  assert "history flags recorded and crossed independent plus exact Int64 count" $
    flagKey `member` campaignLogRecorded flagLog && flagKey `member` campaignLogCrossedOut flagLog
    && Map.lookup countKey (campaignLogRecordedCounts flagLog)==Just 9007199254740993
  assert "history negative count rejected at compilation" $ isLeft $ compileHistoricalLog carcosa mempty mempty $
    flagDraft {historyCounts=Map.singleton countId (-1)}
  let forced=flagLog
  assert "history forced chapter flags and counts cannot be erased" $ isLeft $ compileHistoricalLog carcosa flagLog forced $
    flagDraft {historyFlags=Map.singleton flagId $ HistoryFlags False False}
  assert "history chapter display chronology cannot be cleared" $ isLeft $ compileHistoricalLog carcosa flagLog mempty $
    flagDraft {historyOrderedKeys=[]}
  assert "history chapter display chronology cannot be reversed" $ isLeft $ compileHistoricalLog carcosa flagLog mempty $
    flagDraft {historyOrderedKeys=[countId,flagId]}
  assert "history phantom display key rejected" $ isLeft $ compileHistoricalLog carcosa mempty mempty $
    emptyDraft {historyOrderedKeys=[flagId]}
  tfa<-policyFor "53"
  let jungle=get tfa "log:TheJungleWatches"
      (vengeanceId,vengeanceRecord)=headEx $ resolvedHistoryEntries jungle
      jungleKey=historyFieldKey $ resolvedHistoryField jungle
      ordinary=HistoryRecord vengeanceId HistoricalRecorded False
      circled=ordinary {historyRecordCircled=True}
      crossed=ordinary {historyRecordState=HistoricalCrossedOut}
      jungleDraft=emptyDraft {historySets=Map.singleton "log:TheJungleWatches" [ordinary,circled,crossed]}
  jungleLog<-right $ compileHistoricalLog tfa mempty mempty jungleDraft
  assert "history vengeance duplicates order state and circle retained" $
    map toJSON (Map.findWithDefault [] jungleKey $ campaignLogRecordedSets jungleLog)
      ==map (toJSON . (`changeRecord` vengeanceRecord)) [ordinary,circled,crossed]
  let twoForced=mempty {campaignLogRecordedSets=Map.singleton jungleKey [changeRecord ordinary vengeanceRecord,changeRecord ordinary vengeanceRecord]}
      one=mempty {campaignLogRecordedSets=Map.singleton jungleKey [changeRecord ordinary vengeanceRecord]}
      forcedCircle=mempty {campaignLogRecordedSets=Map.singleton jungleKey [changeRecord circled vengeanceRecord]}
  assert "history forced duplicate multiplicity cannot shrink" $ isLeft $ checkForcedLog twoForced one
  assert "history forced circle cannot be erased" $ isLeft $ checkForcedLog forcedCircle one
  assert "history optional extra circle preserves compulsory identity" $ isRight $ checkForcedLog one forcedCircle
  assert "history Harbinger deliberately dual storage" $
    sort (historyFieldStorage $ resolvedHistoryField $ get tfa "log:TheHarbingerIsStillAlive")==sort [FlagStorage,CountStorage]
  assert "history PathsAreKnownToYou only count" $
    historyFieldStorage (resolvedHistoryField $ get tfa "log:PathsAreKnownToYou")==[CountStorage]
  tcu<-policyFor "54"
  let missingField=get tcu "log:MissingPersons"
      fateA=get tcu "log:WasTakenByTheWatcher"
      fateB=get tcu "log:WasClaimedBySpecters"
      personId=fst $ headEx $ resolvedHistoryEntries missingField
      person=HistoryRecord personId HistoricalRecorded False
      allPersons=[HistoryRecord ident HistoricalRecorded False|(ident,_)<-resolvedHistoryEntries missingField]
      fates=emptyDraft {historySets=Map.fromList [("log:MissingPersons",allPersons),("log:WasTakenByTheWatcher",[person])]}
  assert "history TCU fate entry shares exact native identity" $
    personId `elem` map fst (resolvedHistoryEntries fateA) && personId `elem` map fst (resolvedHistoryEntries fateB)
  assert "history TCU one active fate accepted" $ isRight $ compileHistoricalLog tcu mempty mempty fates
  assert "history TCU two active fates rejected" $ isLeft $ compileHistoricalLog tcu mempty mempty $
    fates {historySets=Map.insert "log:WasClaimedBySpecters" [person] $ historySets fates}
  assert "history TCU fate for crossed missing person rejected" $ isLeft $ compileHistoricalLog tcu mempty mempty $
    fates {historySets=Map.insert "log:MissingPersons" (person {historyRecordState=HistoricalCrossedOut}:tailEx allPersons) $ historySets fates}
  assert "history TCU cannot delete a missing-person identity" $ isLeft $ compileHistoricalLog tcu mempty mempty $
    fates {historySets=Map.insert "log:MissingPersons" (tailEx allPersons) $ historySets fates}
  assert "history TCU cannot duplicate a missing-person identity" $ isLeft $ compileHistoricalLog tcu mempty mempty $
    fates {historySets=Map.insert "log:MissingPersons" (person:allPersons) $ historySets fates}
  assert "history TCU ten exact memento values" $
    length (resolvedHistoryEntries $ get tcu "log:MementosDiscovered")==10
  let partnerPolicy=HistoryPartnerPolicy (typed $ String "c08504") 3 4 [typed $ String "Mia"]
      option=typed $ object ["tag" .= ("Cheated"::Text)]
      withRemainingStores=carcosa {resolvedHistoryOptions=Map.singleton "option:cheated" option
        ,resolvedHistoryPartners=Map.singleton "partner:qa" partnerPolicy}
      remainingDraft=emptyDraft {historyOptions=Map.singleton "option:cheated" True
        ,historyPartners=Map.singleton "partner:qa" $ HistoryPartner 2 3 $ typed $ String "Mia"}
  remainingLog<-right $ compileHistoricalLog withRemainingStores mempty mempty remainingDraft
  assert "history options and all-state typed partner stores preserved" $
    option `member` campaignLogOptions remainingLog && Map.size (campaignLogPartners remainingLog)==1
  assert "history partner policy rejects unsupported status" $ isLeft $ compileHistoricalLog withRemainingStores mempty mempty $
    remainingDraft {historyPartners=Map.singleton "partner:qa" $ HistoryPartner 2 3 $ typed $ String "Safe"}
  let duplicateWire=object ["flags" .= [object ["id" .= flagId,"value" .= HistoryFlags True False],object ["id" .= flagId,"value" .= HistoryFlags False True]]
        ,"counts" .= ([]::[Value]),"sets" .= ([]::[Value]),"orderedKeys" .= ([]::[Text]),"options" .= ([]::[Value]),"partners" .= ([]::[Value])]
  assert "history duplicate named fields rejected before Map construction" $ isLeft $ parseEither (parseJSON @HistoryLogDraft) duplicateWire
  inheritanceSpecs
  chapterSpecs player chooseActual
  BL.putStrLn $ "HISTORY_FOUNDATION_FIXTURE "<>encode (object
    ["flagsCountsOrder" .= flagLog,"vengeance" .= jungleLog,"partnerOptions" .= remainingLog
    ,"inputInteger" .= (9007199254740993::Int),"pureLogFixtureContainsNoGameOrQueue" .= True
    ,"noHistoricalBootstrapInstalled" .= True])
  putStrLn "HISTORY_FOUNDATION_INTEGRATION_COMPLETED"

inheritanceSpecs :: IO ()
inheritanceSpecs=do
  let iid=typed $ String "c01001"
      retiredIid=typed $ String "c02001"
      step=typed $ object ["tag" .= ("PrologueStep"::Text)]
      scenarioStep=typed $ object ["tag" .= ("ScenarioStep"::Text),"contents" .= ("c01104"::Text)]
      policy=InheritancePolicy
        {inheritanceFamily="04",inheritanceCurrentActors=Map.singleton "seat:1" iid
        ,inheritanceHistoricalActors=Map.singleton "retired:02001" retiredIid
        ,inheritanceLeaderActors=Map.singleton "seat:1" iid
        ,inheritanceStepChoices=Map.fromList [("history:prologue",HistoryStepPolicy step mempty [])
          ,("history:later",HistoryStepPolicy scenarioStep mempty ["history:prologue"])]
        ,inheritanceFaceChoices=Map.singleton "face:zero" $ typed $ String "Zero"
        ,inheritanceDestinyScopes=Map.singleton "scope:qa" "campaign.qa"
        ,inheritanceDestinyGroups=[["scope:qa"]],inheritanceFixedDestiny=mempty
        ,inheritanceDestinyRequired=True,inheritanceEffectChoices=mempty}
      actor=HistoryInvestigator 12 2 1 $ Map.singleton "Medicine" 2
      meta=Map.fromList [("tfa:supply-points:seat:1",FormInteger 3),("tfa:yithian:seat:1",FormBoolean True)
        ,("tfa:bonus-xp:seat:1",FormInteger 4),("tfa:expedition-leader",FormChoice $ Just "seat:1")]
      draft=HistoricalDraft "test" (SettingsFormRequest "test" mempty) emptyDraft (Map.singleton "seat:1" actor) [] meta
        (Just $ Map.singleton "face:zero" 3) [("history:prologue",Nothing),("history:later",Nothing)]
        (Map.singleton "scope:qa" $ HistoryTarot "JusticeXI" "reversed") [] ["retired:02001"] []
  compiled<-right $ compileInheritance policy draft
  assert "history target XP trauma are absolute and supplies retain multiplicity" $
    case Map.lookup iid $ inheritedInvestigators compiled of
      Just target->targetExperience target==12 && targetPhysicalTrauma target==2 && targetMentalTrauma target==1 && length (targetSupplies target)==2
      _->False
  assert "history nonempty TFA typed metadata bag steps destiny retired compiled" $
    isJust (inheritedMetadata compiled) && fmap length (inheritedBag compiled)==Just 3
    && length (inheritedSteps compiled)==2 && Map.size (inheritedDestiny compiled)==1 && Set.size (inheritedRetired compiled)==1
  assert "history completed step storage is explicitly newest-first" $
    inheritedSteps compiled==[historyNativeStep $ fromJustNote "later step" $ Map.lookup "history:later" $ inheritanceStepChoices policy
      ,historyNativeStep $ fromJustNote "prologue step" $ Map.lookup "history:prologue" $ inheritanceStepChoices policy]
      && Map.null (inheritedResolutions compiled)
  historical<-right $ compileInheritance policy draft {historyMetadata=Map.insert "tfa:bonus-xp:retired:02001" (FormInteger 7) meta}
  assert "history retired TFA metadata is preserved without inventing absent fields" $
    toJSON (inheritedMetadata historical)/=toJSON (inheritedMetadata compiled)
  assert "history unselected retired TFA metadata rejected" $ isLeft $ compileInheritance policy
    draft {historyMetadata=Map.insert "tfa:bonus-xp:retired:02001" (FormInteger 7) meta,historyRetiredIdentities=[]}
  assert "history retired leader rejected at start requiring current player" $ isLeft $ compileInheritance policy
    draft {historyMetadata=Map.insert "tfa:expedition-leader" (FormChoice $ Just "retired:02001") meta}
  assert "history TFA metadata cannot silently omit a field" $ isLeft $ compileInheritance policy $
    draft {historyMetadata=Map.delete "tfa:yithian:seat:1" meta}
  assert "history TFA metadata unknown owner rejected" $ isLeft $ compileInheritance policy $
    draft {historyMetadata=Map.insert "tfa:expedition-leader" (FormChoice $ Just "foreign-seat") meta}
  assert "history supplies unknown definition rejected" $ isLeft $ compileInheritance policy $
    draft {historyInvestigators=Map.singleton "seat:1" $ actor {historySupplies=Map.singleton "InventedSupply" 1}}
  assert "history bag unknown face rejected" $ isLeft $ compileInheritance policy $
    draft {historyBag=Just $ Map.singleton "face:invented" 1}
  assert "history completed-step chronology dependencies checked" $ isLeft $ compileInheritance policy $
    draft {historySteps=[("history:later",Nothing),("history:prologue",Nothing)]}
  assert "history raw resolution not in policy rejected" $ isLeft $ compileInheritance policy $
    draft {historySteps=[("history:prologue",Just "invented-resolution")]}
  assert "history arbitrary permanent effect rejected" $ isLeft $ compileInheritance policy $
    draft {historyPermanentEffects=["invented-modifier"]}
  assert "history current actor cannot also be retired" $ isLeft $ compileInheritance
    policy {inheritanceHistoricalActors=Map.singleton "retired:current" iid} draft {historyRetiredIdentities=["retired:current"]}
  assert "history non-TFA campaign cannot smuggle metadata or supplies" $ isLeft $ compileInheritance policy {inheritanceFamily="03"} draft
  assert "history required destiny cannot be silently absent" $ isLeft $ compileInheritance policy draft {historyDestiny=mempty}
  let twoScopes=policy {inheritanceDestinyScopes=Map.fromList [("scope:qa","campaign.qa"),("scope:qb","campaign.qb")]
        ,inheritanceDestinyGroups=[["scope:qa","scope:qb"]]}
  assert "history destiny cannot supply half a scope set" $ isLeft $ compileInheritance twoScopes draft
  assert "history destiny arcana cannot duplicate within its original deck" $ isLeft $ compileInheritance twoScopes
    draft {historyDestiny=Map.fromList [("scope:qa",HistoryTarot "JusticeXI" "upright"),("scope:qb",HistoryTarot "JusticeXI" "reversed")]}
  assert "history later rotations need not keep original half-reversed ratio" $ isRight $ compileInheritance twoScopes
    draft {historyDestiny=Map.fromList [("scope:qa",HistoryTarot "JusticeXI" "upright"),("scope:qb",HistoryTarot "TheFool0" "upright")]}
  let fixed=policy {inheritanceFixedDestiny=Map.singleton "scope:qa" $ typed $ String "JusticeXI"}
  assert "history fixed campaign arcana can retain reversed facing" $ isRight $ compileInheritance fixed draft
  assert "history fixed campaign arcana cannot be reassigned" $ isLeft $ compileInheritance fixed
    draft {historyDestiny=Map.singleton "scope:qa" $ HistoryTarot "TheFool0" "upright"}
  let resolution=typed $ object ["tag" .= ("Resolution"::Text),"contents" .= (2::Int)]
      withResolution=policy {inheritanceStepChoices=Map.adjust (\p->p {historyResolutions=Map.singleton "r:2" resolution}) "history:later" $ inheritanceStepChoices policy}
  resolved<-right $ compileInheritance withResolution draft {historySteps=[("history:prologue",Nothing),("history:later",Just "r:2")]}
  assert "history resolutions compile separately by actual scenario identity" $
    map toJSON (Map.elems $ inheritedResolutions resolved)==[object ["tag" .= ("Resolution"::Text),"contents" .= (2::Int)]]
  let badResolution=policy {inheritanceStepChoices=Map.adjust (\p->p {historyResolutions=Map.singleton "r:2" resolution}) "history:prologue" $ inheritanceStepChoices policy}
  assert "history non-scenario step cannot carry resolution despite named policy" $ isLeft $ compileInheritance badResolution
    draft {historySteps=[("history:prologue",Just "r:2")]}
  assert "history compatible later start can retain selected historical leader" $ isRight $ compileInheritance
    policy {inheritanceLeaderActors=Map.insert "retired:02001" retiredIid $ inheritanceLeaderActors policy}
    draft {historyMetadata=Map.insert "tfa:expedition-leader" (FormChoice $ Just "retired:02001") meta}

chapterSpecs :: PlayerId -> (PlayerId -> ArkhamDBDecklist -> Snapshot -> IO Answer) -> IO ()
chapterSpecs player chooseActual=do
  decks<-eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json" >>= either (\reason->putStrLn (fromString reason)>>exitFailure) pure
  let deck=headEx decks
      untilSettings budget snapshot
        | any ((==PickCampaignSettings) . unwrapQuestion) (Map.elems $ gameQuestion $ snapshotGame snapshot)=pure snapshot
        | budget<=(0::Int)=assert "history representative original settings anchor reached" False>>pure snapshot
        | otherwise=do
            answer<-chooseActual player deck snapshot
            (next,_)<-applyAnswer snapshot player (snapshotRevision snapshot) answer >>= right
            untilSettings (budget-1) next
      request=typed $ object ["campaign" .= ("02"::Text),"scenario" .= ("02236"::Text),"seed" .= (171020::Int),"players" .= [player]]
  (initial,_)<-createSession request >>= right
  snapshot<-untilSettings 12 initial
  let untouched=exportSnapshot snapshot
  ordinary<-right $ Engine.resolveSchema (snapshotGame snapshot) player
  historical<-right $ Engine.resolveHistoricalSchema (snapshotGame snapshot) player
  form<-complete ordinary
  let historyForm=form {formSchemaId=Engine.schemaIdentity historical}
  (compiled,forced)<-right $ Engine.compileFormWithForced ordinary form
  (historyCompiled,historyForced)<-right $ Engine.compileFormWithForced historical historyForm
  assert "history actual original chapter results identical under historical projection" $
    toJSON compiled==toJSON historyCompiled && toJSON forced==toJSON historyForced
  assert "history ordinary and historical schema identities cannot cross-submit" $
    Engine.schemaIdentity ordinary/=Engine.schemaIdentity historical
      && isLeft (Engine.compileForm historical form) && isLeft (Engine.compileForm ordinary historyForm)
  assert "history compulsory provenance is not the entire editable chapter" $
    campaignLogRecorded forced `Set.isSubsetOf` campaignLogRecorded compiled
    && not (Set.null $ campaignLogRecorded forced)
    && campaignLogRecorded forced/=campaignLogRecorded compiled
  assert "history read-only chapter compilation preserves whole actual Game queue and RNG" $ exportSnapshot snapshot==untouched
  let decodeSnapshot bytes=either (error . fromString) id $ eitherDecode @Value bytes
  BL.putStrLn $ "HISTORY_CHAPTER_FIXTURE "<>encode (object
    ["before" .= decodeSnapshot untouched,"after" .= decodeSnapshot (exportSnapshot snapshot)
    ,"ordinaryLog" .= compiled,"historicalLog" .= historyCompiled
    ,"ordinaryForced" .= forced,"historicalForced" .= historyForced])
