-- A display protocol, NOT the original Question/UI JSON. No Message value is
-- serialized, including nested payment, dropdown, wizard and ability payloads.
module Arkham.Native.Network.Question (questionView, choiceView, flavorView, costView) where

import Arkham.Prelude
import Arkham.Ability.Types (Ability (..))
import Arkham.Cost
import Arkham.GameValue (GameValue (..))
import Arkham.Question
import Arkham.Target (Target (..))
import Arkham.Source (Source (..))
import Arkham.Text
import Data.Aeson.Types (Pair)
import Data.Map.Strict qualified as Map
import Data.List (scanl)

tagged :: Text -> [Pair] -> Value
tagged tag fields = object $ ("tag" .= tag):fields

-- Display only, never an affordability evaluator or a serialized Cost. Sources,
-- matchers, arbitrary labels and calculations do not cross the network. The
-- original resolver still receives only the original offered answer index.
costView :: (Target -> Value) -> Cost -> Value
costView publicTarget cost
  | length (take 257 $ nodes cost) > 256 = fst unsupported
  | otherwise = fst $ go (0::Int) cost
 where
  nodes c = c : concatMap nodes (case c of
    Costs xs -> xs; OrCost xs -> xs; LabeledCost _ x -> [x]; SourcedCost _ x -> [x]
    _ -> [])
  leaf kind label fields supported =
    (object $ ["kind" .= (kind::Text),"label" .= (label::Text),"supported" .= supported] <> fields,supported)
  unsupported = leaf "Unsupported" "特殊费用：请依卡牌文字及宿主提示结算" [] False
  amount label n = leaf "Amount" label ["amount" .= (n::Int)] True
  variable label = leaf "Variable" label [] True
  target label n t = case publicTarget t of
    Null -> unsupported
    value -> leaf "Target" label (["target" .= value] <> maybe [] (\v -> ["amount" .= (v::Int)]) n) True
  value label = \case
    Static n -> amount label n
    PerPlayer n -> variable $ label <> "（每位调查员 " <> tshow n <> "）"
    StaticWithPerPlayer n m -> variable $ label <> "（" <> tshow n <> " + 每位调查员 " <> tshow m <> "）"
    ByPlayerCount a b c d -> variable $ label <> "（1/2/3/4 人：" <> tshow a <> "/" <> tshow b <> "/" <> tshow c <> "/" <> tshow d <> "）"
    ValueX -> variable $ label <> " X"
    ValueStar -> variable $ label <> " *"
    ValueUnknown -> unsupported
  group depth kind label xs =
    let children=map (go $ depth+1) xs
    in leaf kind label ["items" .= map fst children] (all snd children)
  go depth _ | depth >= 16 = unsupported
  go depth c = case c of
    Free -> leaf "Free" "无费用" [] True
    Costs xs -> group depth "All" "支付全部费用" xs
    OrCost xs -> group depth "Any" "选择一种费用" xs
    -- Authored text/source can embed hidden card names or protocol payloads.
    -- Retain the actual inner cost rather than forwarding those fields.
    LabeledCost _ inner -> go (depth+1) inner
    SourcedCost _ inner -> go (depth+1) inner
    ActionCost n -> amount "行动" n
    UnlessFastActionCost n -> amount "行动（非快速时）" n
    AdditionalActionCost -> amount "额外行动" 1
    AdditionalActionsCost -> variable "额外行动"
    ResourceCost n -> amount "资源" n
    ScenarioResourceCost n -> amount "场景资源" n
    ClueCost gv -> value "线索" gv
    ClueCostX -> variable "线索 X"
    PerPlayerClueCostX -> variable "线索（每位调查员 X）"
    DiscoveredCluesCost -> variable "本次发现的线索"
    PlaceClueOnLocationCost gv -> value "在地点放置线索" gv
    ExhaustCost t -> target "横置" Nothing t
    RemoveCost t -> target "移出游戏" Nothing t
    ExileCost t -> target "放逐" Nothing t
    DamageCost _ t n -> target "伤害" (Just n) t
    HorrorCost _ t n -> target "恐惧" (Just n) t
    DiscardTopOfDeckCost n -> amount "弃掉牌库顶卡牌" n
    DiscardRandomCardCost -> amount "随机弃牌" 1
    DiscardDrawnCardCost -> amount "弃掉刚抽取的卡牌" 1
    DiscardHandCost -> variable "弃掉全部手牌"
    DrawEncounterCardsCost n -> amount "抽取遭遇卡" n
    AddFrostTokenCost n -> amount "加入寒霜标记" n
    AddCurseTokenCost n -> amount "加入诅咒标记" n
    -- Matchers/conditions/optional costs must not be flattened into an
    -- unconditional amount; unsupported children remain explicit in AND/OR.
    _ -> unsupported

-- I18n variables are authored Values, not necessarily scalar. Permit only the
-- display scalar domain; never serialize a nested source/game/message object.
flavorView :: FlavorText -> Value
flavorView (FlavorText title body) = object ["title" .= title,"body" .= map entry body]
 where
  scalar = \case String _ -> True; Number _ -> True; Bool _ -> True; Null -> True; _ -> False
  item (ListItemEntry e nested) = object ["entry" .= entry e,"nested" .= map item nested]
  entry = \case
    BasicEntry text -> tagged "BasicEntry" ["text" .= text]
    I18nEntry key variables -> tagged "I18nEntry"
      ["key" .= key,"variables" .= Map.filter scalar variables]
    HeaderEntry level key -> tagged "HeaderEntry" ["level" .= level,"key" .= key]
    ModifyEntry modifiers e -> tagged "ModifyEntry" ["modifiers" .= modifiers,"entry" .= entry e]
    CompositeEntry es -> tagged "CompositeEntry" ["entries" .= map entry es]
    ColumnEntry es -> tagged "ColumnEntry" ["entries" .= map entry es]
    ListEntry xs -> tagged "ListEntry" ["list" .= map item xs]
    CardEntry code modifiers -> tagged "CardEntry" ["cardCode" .= code,"imageModifiers" .= modifiers]
    TarotEntry tarot -> tagged "TarotEntry" ["tarot" .= tarot]
    ChaosTokenEntry face -> tagged "ChaosTokenEntry" ["face" .= face]
    ChaosTokenMorphEntry a b -> tagged "ChaosTokenMorphEntry" ["from" .= a,"to" .= b]
    EntrySplit -> tagged "EntrySplit" []

-- Targets are resolved only against the already-authorized visible entity set.
-- A question's explicit CardLabel is an offered card reference, not a lookup
-- into the game's global card registry.
choiceView :: (Target -> Value) -> Int -> UI msg -> Value
choiceView targetRef index choice = object $
  ["index" .= index,"selectable" .= selectable] <> fields
 where
  selectable = case choice of Info {} -> False; InvalidLabel {} -> False; _ -> True
  named tag xs = ("tag" .= (tag::Text)):xs
  fields = case choice of
    Label label _ -> named "Label" ["label" .= label]
    InvalidLabel label -> named "InvalidLabel" ["label" .= label]
    TooltipLabel label tooltip _ -> named "TooltipLabel" ["label" .= label,"tooltip" .= tooltip]
    CostLabel cost _ -> named "CostLabel"
      ["caption" .= ("支付费用"::Text),"cost" .= costView targetRef cost]
    CardLabel code flippable _ -> named "CardLabel" ["cardCode" .= code,"flippable" .= flippable]
    ChaosTokenLabel face _ -> named "ChaosTokenLabel" ["face" .= face]
    KeyLabel key _ -> named "KeyLabel" ["key" .= key]
    PortraitLabel iid _ -> named "PortraitLabel" ["investigatorId" .= iid]
    TargetLabel target _ -> named "TargetLabel" ["target" .= targetRef target]
    SkillLabel skill _ -> named "SkillLabel" ["skill" .= skill]
    SkillLabelWithLabel label skill _ -> named "SkillLabelWithLabel" ["label" .= label,"skill" .= skill]
    EvadeLabel eid _ -> named "EvadeLabel" ["enemyId" .= eid]
    EvadeLabelWithSkill eid skill _ -> named "EvadeLabelWithSkill" ["enemyId" .= eid,"skill" .= skill]
    FightLabel eid _ -> named "FightLabel" ["enemyId" .= eid]
    FightLabelWithSkill eid skill _ -> named "FightLabelWithSkill" ["enemyId" .= eid,"skill" .= skill]
    EngageLabel eid _ -> named "EngageLabel" ["enemyId" .= eid]
    GridLabel label _ -> named "GridLabel" ["label" .= label]
    ConnectionLabel connection _ -> named "ConnectionLabel" ["connection" .= connection]
    TarotLabel card _ -> named "TarotLabel" ["tarot" .= card]
    AbilityLabel iid ability _ _ _ -> named "AbilityLabel"
      ["investigatorId" .= iid,"cardCode" .= abilityCardCode ability,"abilityIndex" .= abilityIndex ability
      ,"tooltip" .= abilityTooltip ability,"resultLabel" .= abilityResultLabel ability]
    ComponentLabel component _ -> named "ComponentLabel" ["component" .= component]
    AuxiliaryComponentLabel component _ -> named "AuxiliaryComponentLabel" ["component" .= component]
    EndTurnButton iid _ -> named "EndTurnButton" ["investigatorId" .= iid]
    StartSkillTestButton iid -> named "StartSkillTestButton" ["investigatorId" .= iid]
    SkillTestApplyResultsButton -> named "SkillTestApplyResultsButton" []
    ChaosTokenGroupChoice _ iid _ -> named "ChaosTokenGroupChoice"
      ["investigatorId" .= iid,"caption" .= ("选择混乱标记组"::Text),"detailsSupported" .= False]
    EffectActionButton tooltip _ _ -> named "EffectActionButton" ["tooltip" .= tooltip]
    Done label -> named "Done" ["label" .= label]
    SkipTriggersButton iid -> named "SkipTriggersButton" ["investigatorId" .= iid]
    CardPile pile _ -> named "CardPile" ["count" .= length pile]
    Info flavor -> named "Info" ["flavor" .= flavorView flavor]
    ScenarioLabel label sid _ -> named "ScenarioLabel" ["label" .= label,"scenarioId" .= sid]

questionView :: (Target -> Value) -> Question msg -> Value
questionView ref = go
 where
  choices xs = zipWith (choiceView ref) [0..] xs
  list tag xs = tagged tag ["choices" .= choices xs]
  amount tag n xs = tagged tag ["amount" .= n,"choices" .= choices xs]
  go = \case
    ChooseFromCollection _ _ codes -> list "ChooseFromCollection" [CardLabel code False [] | code <- codes]
    ChooseOne xs -> list "ChooseOne" xs
    PlayerWindowChooseOne xs -> list "PlayerWindowChooseOne" xs
    WindowChooseOne xs -> list "WindowChooseOne" xs
    ChooseOneFromEach groups -> tagged "ChooseOneFromEach" ["groups" .=
      zipWith (\offset xs -> zipWith (choiceView ref) [offset..] xs)
        (scanl (+) 0 $ map length groups) groups]
    ChooseN n xs -> amount "ChooseN" n xs
    ChooseSome xs -> list "ChooseSome" xs
    ChooseSome1 label xs -> tagged "ChooseSome1" ["label" .= label,"choices" .= choices xs]
    ChooseUpToN n xs -> amount "ChooseUpToN" n xs
    ChooseOneAtATime xs -> list "ChooseOneAtATime" xs
    ChooseOneAtATimeWithAuto label xs -> tagged "ChooseOneAtATimeWithAuto"
      ["label" .= label,"choices" .= choices (Label label []:xs)
      ,"automaticAllowed" .= all (\case Info {} -> False; InvalidLabel {} -> False; _ -> True) xs]
    ChoosePaymentAmounts label target xs -> tagged "ChoosePaymentAmounts"
      ["label" .= label,"target" .= target,"choices" .=
        [object ["choiceId" .= ident,"investigatorId" .= iid,"min" .= lo,"max" .= hi,"title" .= title]
        | PaymentAmountChoice ident iid lo hi title _ <- xs]]
    ChooseAmounts label target xs _ -> tagged "ChooseAmounts"
      ["label" .= label,"target" .= target,"choices" .=
        [object ["choiceId" .= ident,"label" .= text,"min" .= lo,"max" .= hi]
        | AmountChoice ident text lo hi <- xs]]
    ChooseDeck -> tagged "ChooseDeck" []
    ChooseUpgradeDeck -> tagged "ChooseUpgradeDeck" []
    ChooseJoinDeck used -> tagged "ChooseJoinDeck" ["usedInvestigators" .= used]
    QuestionLabel label card q -> tagged "QuestionLabel" ["label" .= label,"cardCode" .= card,"question" .= go q]
    PayCostQuestion cost q -> tagged "PayCostQuestion"
      ["caption" .= ("支付费用"::Text),"cost" .= costView ref cost,"question" .= go q]
    QuestionWithSource _ tooltip q -> tagged "QuestionWithSource" ["tooltip" .= tooltip,"question" .= go q]
    Read flavor readChoices cards -> tagged "Read"
      ["flavor" .= flavorView flavor,"readChoices" .= readView readChoices,"readCards" .= cards]
    ChooseOneWizard flavor xs confirm back -> tagged "ChooseOneWizard"
      ["flavor" .= flavorView flavor,"confirmLabel" .= confirm,"backLabel" .= back
      ,"choices" .= [object ["index" .= i,"label" .= label,"flavor" .= flavorView text]
        | (i,WizardChoice label text _) <- zip [0::Int ..] xs]]
    PickSupplies points supplies xs resupply -> tagged "PickSupplies"
      ["pointsRemaining" .= points,"chosenSupplies" .= supplies,"resupply" .= resupply,"choices" .= choices xs]
    PickDestiny drawings -> tagged "PickDestiny" ["drawings" .= drawings]
    DropDown xs -> tagged "DropDown"
      ["options" .= [object ["index" .= i,"label" .= label] | (i,(label,_)) <- zip [0::Int ..] xs]]
    PickScenarioSettings -> tagged "PickScenarioSettings" ["requiresSchema" .= ("settings"::Text)]
    PickCampaignSettings -> tagged "PickCampaignSettings" ["requiresSchema" .= ("settings"::Text)]
    PickCampaignSpecific key _ -> tagged "PickCampaignSpecific" ["key" .= key,"requiresSchema" .= ("interaction"::Text)]
    PickScenarioSpecific key _ -> tagged "PickScenarioSpecific" ["key" .= key,"requiresSchema" .= ("interaction"::Text)]
    ChooseExchangeAmounts source a av b bv token -> tagged "ChooseExchangeAmounts"
      ["investigator1Id" .= a,"investigator1InitialAmount" .= av
      ,"investigator2Id" .= b,"investigator2InitialAmount" .= bv,"token" .= token
      ,"source" .= safeSource source,"sourceSupported" .= isJust (safeSource source)]
    ContinueCampaign -> tagged "ContinueCampaign" ["requiresSchema" .= ("continuation"::Text)]
  -- A Source is a typed identity, not an executable Message. Only these finite
  -- public roots and recursively checked wrappers may roundtrip unchanged.
  -- In particular do not expose matcher contents, private CardIds or metadata.
  safeSource source = toJSON source <$ guard (public source)
  visible target = ref target /= Null
  public = \case
    GameSource -> True
    ScenarioSource -> True
    CampaignSource -> True
    YouSource -> True
    InvestigatorSource ident -> visible $ InvestigatorTarget ident
    ResourceSource ident -> visible $ InvestigatorTarget ident
    AssetSource ident -> visible $ AssetTarget ident
    EnemySource ident -> visible $ EnemyTarget ident
    LocationSource ident -> visible $ LocationTarget ident
    AbilitySource source _ -> public source
    IndexedSource _ source -> public source
    PaymentSource source -> public source
    UseAbilitySource ident source _ -> visible (InvestigatorTarget ident) && public source
    BothSource a b -> public a && public b
    _ -> False
  readView = \case
    BasicReadChoices xs -> list "BasicReadChoices" xs
    BasicReadChoicesN n xs -> amount "BasicReadChoicesN" n xs
    BasicReadChoicesUpToN n xs -> amount "BasicReadChoicesUpToN" n xs
    LeadInvestigatorMustDecide xs -> list "LeadInvestigatorMustDecide" xs
