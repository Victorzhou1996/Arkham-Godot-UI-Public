-- Fixed-source identities for the advanced historical-state editor. This is
-- not a replay/solver of past games: the user describes an already completed
-- history. Only reviewed actual steps and final stored resolution values are
-- offered; retry/intermediate dispatch numbers are not completion identities.
module Arkham.Native.History.Manifest where

import Arkham.Prelude
import Arkham.CampaignStep (CampaignStep (..),InterludeKey (..))
import Arkham.Id
import Arkham.Resolution (Resolution (..))
import Arkham.Tarot
import Arkham.Native.History.Inheritance (HistoryStepPolicy (..))
import Arkham.Native.History.Profile (historyFamily)
import Data.Map.Strict qualified as Map
import Data.List (findIndex)
import Data.Text qualified as T

data HistoricalScenario = HistoricalScenario
  { actualScenario :: Text
  , definitionScenario :: Text
  , finalStoredResolutions :: [Resolution]
  , permitsAbsentResolution :: Bool
  }
  deriving stock Show

scenarios :: Text -> Either Text [HistoricalScenario]
scenarios actual=do
  family<-historyFamily actual
  let returned=actual `elem` ["50","51","52","53","54"]
      row base target noResolution values=HistoricalScenario (if returned then target else base) base
        ([NoResolution|noResolution]<>map Resolution values) (actual=="54" && target=="54016")
  pure $ case family of
    "01"->[row "01104" "50011" True [1,2,3],row "01120" "50025" False [1,2],row "01142" "50032" True [1,2,3]]
    "02"->[row "02041" "51012" True [1,2,3,4],row "02062" "51015" False [1,2,3,4]
      ,row "02118" "51020" True [1,2],row "02159" "51025" True [1,2],row "02195" "51032" True [1,2,3]
      ,row "02236" "51041" True [1,2],row "02274" "51047" True [1,2],row "02311" "51053" True [1,2,3,4]]
    "03"->[row "03043" "52014" True [1,2],row "03061" "52021" True [1,2,3]
      ,row "03120" "52028" True [1,2,3,4],row "03159" "52034" True [1,2,3]
      ,row "03200" "52040" True [1,2,3],row "03240" "52048" True [1,2]
      ,row "03274" "52054" True [1,2,3],row "03316" "52059" True [1,2,3,4,5]]
    "04"->[row "04043" "53016" True [1,2],row "04054" "53017" True [1,2,3,5]
      ,row "04113" "53028" True [1],row "04161" "53038" True [1,2]
      ,row "04205a" "53045" False (if returned then [1,2] else [1])
      ,row "04205b" "53048" False [1,2],row "04237" "53053" True [1]
      ,row "04277" "53059" False [1,2],row "04314" "53061" False [1,2,3,4,5]
      ,row "04344" "53066" True [1]]
    "05"->[row "05043" "54016" True [],row "05050" "54017" False (if returned then [1..5] else [1..4])
      ,row "05065" "54024" False [1,2,3],row "05120" "54029" False [1,2]
      ,row "05161" "54034" False (if returned then [1,2,3] else [1,2])
      ,row "05197" "54042" True [1,2,3],row "05238" "54046" False (if returned then [2,8,9] else [2,8])
      ,row "05284" "54049" False [1,2,3,4]
      ,row "05325" "54056" False (if returned then [1,2,3,5,6,11] else [1,2,3,5,11])]
    _->[]

scenarioId :: Text -> ScenarioId
scenarioId=ScenarioId . fromString . T.unpack

resolutionIdentity :: Resolution -> Text
resolutionIdentity= \case NoResolution->"resolution:none";Resolution number->"resolution:"<>tshow number

-- A history outcome is not merely an open numeric Resolution. Explicit
-- GameOver paths cannot precede a new playable chapter. Conditional paths use
-- named continuing outcomes: selecting one describes that actual completed,
-- nonterminal branch, rather than claiming the number alone proves survival.
continuingOutcomes :: HistoricalScenario -> [(Text,Resolution)]
continuingOutcomes row=concatMap offered $ finalStoredResolutions row
 where
  base=definitionScenario row
  terminal value=(base=="02274" && value `elem` [NoResolution,Resolution 2])
    || (base=="03274" && value `elem` [NoResolution,Resolution 3])
    || (base=="04237" && value==NoResolution)
    || (base=="05238" && value `elem` [Resolution 2,Resolution 9])
    || (base=="04314" && value/=Resolution 5)
  offered value
    | terminal value=[]
    | base=="04054"=[("outcome:eztli-"<>branch<>"-finished-not-retry:"<>resolutionIdentity value,value)
        |branch<-["no-defeated","historical-fury-below-four","at-least-one-not-defeated"]]
    | base=="03159"=[("outcome:oath-"<>branch<>":"<>resolutionIdentity value,value)
        |branch<-["survival-disabled","at-least-one-not-defeated"]]
    | base=="04277" && value==Resolution 1=
        [("outcome:yoth-depth-two-or-three-survivors-remain:"<>resolutionIdentity value,value)
        ,("outcome:yoth-depth-at-least-four:"<>resolutionIdentity value,value)]
    | otherwise=[(resolutionIdentity value,value)]

-- The two Dunwich opening scenarios may be played in either order. Other
-- scenarios must precede the requested start in the fixed campaign sequence.
-- Optional interludes are not invented as mandatory dependencies.
choicesForStart :: Text -> Text -> Either Text (Map Text HistoryStepPolicy)
choicesForStart actual start=Map.map fst <$> rankedChoicesForStart actual start

rankedChoicesForStart :: Text -> Text -> Either Text (Map Text (HistoryStepPolicy,Int))
rankedChoicesForStart actual start=do
  family<-historyFamily actual
  rows<-scenarios actual
  position<-maybe (Left "Historical starting scenario is not in this campaign's finite manifest") Right $
    findIndex ((==start) . actualScenario) rows
  let earlier=[row|(index,row)<-zip [0::Int ..] rows,index<position
               || (family=="02" && position<2 && index<2 && index/=position)]
      scenarioChoices=[("scenario:"<>actualScenario row,(HistoryStepPolicy (ScenarioStep $ scenarioId $ actualScenario row)
          (Map.fromList $ continuingOutcomes row) [],scenarioRank row))|row<-earlier]
      scenarioRank row=let p=fromJustNote "manifest scenario position" $ findIndex ((==actualScenario row) . actualScenario) rows
        in if family=="02" && p<2 then 50 else p*100+50
      interlude number before=("interlude:"<>tshow number,InterludeStep number Nothing,before)
      keyed number key before=("interlude:"<>tshow number<>":"<>tshow key,InterludeStep number (Just key),before)
      part number key value before=("interlude-part:"<>tshow number<>":"<>maybe "none" tshow key<>":"<>tshow value
        ,InterludeStepPart number key value,before)
      extras=case family of
        "01"->[]
        "02"->[interlude 1 2,interlude 2 5]
        "03"->[interlude 1 2]<>[keyed 2 key 4|key<-[DanielSurvived,DanielWasPossessed,DanielDidNotSurvive]]
        "04"->[interlude 1 1,interlude 2 2,("resupply",ResupplyPoint,3),interlude 3 4
          ,interlude 4 7,keyed 4 TheCustodianWasUnderControl 7,interlude 5 8,("epilogue",EpilogueStep,9)]
          <>[part 1 Nothing 2 1]<>[part 2 Nothing value 2|value<-[1..5]]<>[part 3 Nothing 2 4]
          <>[part 4 key value 7|key<-[Nothing,Just TheCustodianWasUnderControl],value<-[1,2,3,4,5,51,6]]
          <>[part 5 Nothing value 8|value<-[1..4]]
        "05"->[keyed 2 key 3|key<-[ThePriceOfProgress4,ThePriceOfProgress5,ThePriceOfProgress6]]
          <>[part 2 (Just key) value 3|key<-[ThePriceOfProgress5,ThePriceOfProgress6],value<-[7..9]]
          <>[interlude 3 6,interlude 4 8]<>[part 3 Nothing value 6|value<-[2..6]]
        _->[]
      partRank step=case step of
        InterludeStepPart 4 _ partNumber->fromMaybe partNumber $ lookup partNumber $ zip [1,2,3,4,5,51,6] [1..]
        InterludeStepPart _ _ partNumber->partNumber
        _->0
  pure $ Map.fromList $ scenarioChoices<>[(ident,(HistoryStepPolicy step mempty [],before*100+partRank step))|(ident,step,before)<-extras,position>=before]
    <>[("prologue",(HistoryStepPolicy PrologueStep mempty [],0))|position>0 || family=="02"]

validateChronology :: Text -> Text -> [(Text,Maybe Text)] -> Either Text ()
validateChronology actual start completed=do
  family<-historyFamily actual
  offered<-rankedChoicesForStart actual start
  selected<-for completed \(ident,_)->maybe (Left "Historical step is not earlier than this start") Right $ Map.lookup ident offered
  let ranks=map snd selected
      steps=map (historyNativeStep . fst) selected
  unless (and $ zipWith (\a b->a<b || (family=="02" && a==50 && b==50)) ranks (drop 1 ranks)) $
    Left "Historical completed steps must be chronological; only the two Dunwich opening scenarios may swap order"
  let keyOf= \case InterludeStep n key->Just (n,key);InterludeStepPart n key _->Just (n,key);_->Nothing
      keys=mapMaybe keyOf steps
  for_ (nub $ map fst keys) \n->unless (length (nub [key|(number,key)<-keys,number==n])<=1) $
    Left "Historical interlude contains mutually exclusive key variants"
  let parts n=[p|InterludeStepPart number _ p<-steps,number==n]
      exclusive values=length (filter (`elem` values) $ if family=="04" then parts 2 else parts 3)<=1
  when (family=="04") do
    unless (exclusive [2,3] && (5 `notElem` parts 2 || not (any (`elem` [1,2,3,4]) $ parts 2))) $
      Left "Historical TFA interlude two combines exclusive routes"
  when (family=="05") do
    unless (length (filter (`elem` [7,8,9]) $ parts 2)<=1 && exclusive [2,3]
      && length (filter (`elem` [5,6]) $ parts 3)<=1 && length (filter (`elem` [4,5]) $ parts 3)<=1) $
      Left "Historical TCU interlude combines exclusive answers"
  -- Do not require an invented full linear prefix or replay old rules. The
  -- finite order/variant checks preserve explicitly supplied semantic nodes.
  pure ()

validateHistoricalRoute :: Text -> Text -> [(Text,Maybe Text)] -> Either Text ()
validateHistoricalRoute actual start completed=do
  validateChronology actual start completed
  when (actual=="52" && start/="52014") $ unless (any ((=="scenario:52014") . fst) completed) $
    Left "A later inherited Carcosa start needs its completed Curtain Call history and real Stranger ownership"
  when (actual=="53" && start=="53066") $ unless
    (lookup "scenario:53061" completed==Just (Just "resolution:5") && any ((=="epilogue") . fst) completed) $
      Left "Inherited Turn Back Time requires completed Return Shattered Aeons resolution 5 followed by its epilogue"

outcomeLabel :: Text -> Text
outcomeLabel ident
  | "outcome:eztli-no-defeated-" `T.isPrefixOf` ident="已完成且不重玩；当时无人被击败 · "<>ending
  | "outcome:eztli-historical-fury-below-four-" `T.isPrefixOf` ident="已完成且不重玩；当时检查前伊格的愤怒低于4 · "<>ending
  | "outcome:eztli-at-least-one-not-defeated-" `T.isPrefixOf` ident="已完成且不重玩；当时至少一人未被击败 · "<>ending
  | "outcome:oath-survival-disabled:" `T.isPrefixOf` ident="当时未启用生存通牒，可继续／更换人物 · "<>ending
  | "outcome:oath-at-least-one-not-defeated:" `T.isPrefixOf` ident="当时至少一人未被击败，战役继续 · "<>ending
  | "outcome:yoth-depth-two-or-three-survivors-remain:" `T.isPrefixOf` ident="当时深度2或3，结算创伤后仍有人存活 · "<>ending
  | "outcome:yoth-depth-at-least-four:" `T.isPrefixOf` ident="当时深度至少4 · "<>ending
  | otherwise=ending
 where
  ending=case reverse $ T.splitOn ":" ident of
    "none":_->"无编号结局（NoResolution，非空白）"
    number:_->"存储结局 "<>number
    _->ident

-- Exact original Return-To Witching Hour drawings. A historical editor may
-- retain a later rotation, not arbitrarily pair a different arcana to a scope.
fixedDestiny :: Text -> Map Text TarotCardArcana
fixedDestiny actual=if actual/="54" then mempty else Map.fromList
  [("theWitchingHour",TemperanceXIV),("atDeath'sDoorstep",JusticeXI),("theSecretName",TheHermitIX)
  ,("theWagesOfSin",TheHangedManXII),("forTheGreaterGood",TheHierophantV)
  ,("unionAndDisillusion",TheLoversVI),("inTheClutchesOfChaos",TheChariotVII),("beforeTheBlackThrone",WheelOfFortuneX)]

validateCompletedResolutions :: Text -> [(Text,Maybe Text)] -> Either Text ()
validateCompletedResolutions actual completed=do
  rows<-scenarios actual
  for_ completed \(ident,resolution)->for_ (T.stripPrefix "scenario:" ident) \sid->do
    row<-maybe (Left "Unknown completed historical scenario") Right $ find ((==sid) . actualScenario) rows
    unless (isJust resolution || permitsAbsentResolution row) $
      Left "This completed scenario needs an explicit final stored resolution"
