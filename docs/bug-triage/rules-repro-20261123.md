# 规则类反馈复现 · 2026-11-23

范围：QQ 群反馈 #8 #11 #19 #20 #28。只调查，不改后端、不改前端。
规则核心：`runtime/arkham_core.dll`，sourceId `5bb054d7…`（probe 每次启动都打印了完整 sourceId，见各 probe 输出第一行）。
源码对照：`handoff/mac-core-repo/engine/rules`（只读）。

## 结论一览

| 编号 | 反馈 | 结论 | 谁的问题 |
|---|---|---|---|
| #8 | 黛安娜 + 倒吊人正位，每次换牌多一张黑暗洞察 | **复现成功** | 规则核心 |
| #11 | 阿加莎神话阶段取消标记后，本轮回合结束不能发动 | **复现成功** | 规则核心 |
| #19 | 三循环"令人震惊的一幕"写着不能被取消，实际能取消 | **复现成功** | 规则核心 |
| #20 | 拆迷雾高塔用不了幽灵利刃 | **复现成功** | 规则核心 |
| #28 | 温蒂的技能在判定前出现，揭示标记之后反而用不了 | **核心正常；是前端显示问题** | 前端 |

已有 patch（`evidence/rules-fix-20261123/rules-fix.patch`）：#11 #19 #20 的改法与复现结果一致；**#8 的改法只修了一半**，见下文。#28 不需要改核心。

## 复现方法

- 工具：`tools/rules-probe` 的副本 Probe2（源码 `evidence/bugs-server/rules-probe2/`）。只链接已编好的 `tools/rules-probe/bin/RulesProbe.dll`，不重编 `app/Scripts`（其他会话在改）。比原版多了：`start … [stopAt]` 停在指定问题、`dump` 打印核心原始问题 JSON、`vg:<字段>` 打印前端读到的 game 视图字段。
- 定点场景靠改存档做出来（脚本同目录 `patch_*.py`）：设塔罗、把混沌袋全换成旧印、把某张牌移到弃牌/手牌、把遭遇牌库顶换成指定卡、把地点换成迷雾高塔。
- 改卡时踩过一次坑：核心按卡牌 id 在 `gameCards` 表里查卡，只改遭遇牌库那一份不算数（第一次 #19 测试因此无效，已挪到 `_invalid-first-try/` 并写了说明）。之后的脚本把同一 id 的所有副本一起改。
- 牌组都带一张随机基础弱点（`01000`）。

---

## #8 黛安娜：每次换牌多一张黑暗洞察 —— 规则核心问题

**复现**（`evidence/bugs-server/8/`）：黛安娜牌组开局，在换牌之前把塔罗设成"倒吊人·正位"（`s1-hangedman-upright.json`）。

`probe-hangedman-replace-x3.txt`（脚本准备了三轮，实际第二轮后核心就不再给换牌，原因见下）：
1. 第一次换牌：换掉 1 张致命打击 → 结束选牌。手牌 6 张 = 5 张 + 1 张黑暗洞察（正确）。
2. 核心再给一次换牌，**黑暗洞察出现在"换掉"选项里**（`[6] 换掉 · 黑暗洞察`）。换掉探知术 → 结束选牌。
3. 结果手牌 6 张：致命打击 证据！ 皱缩术 神秘研究 **黑暗洞察 黑暗洞察** —— 多了一张黑暗洞察，普通牌只剩 4 张（应为 5 张 + 1 张黑暗洞察）。

`probe-hangedman-done-x3.txt`：不换任何牌直接"结束选牌"，核心就不再给后续换牌（因为手牌已满），也不会重复加牌。所以玩家必须真的换过牌才会遇到。

**原因**（`Investigator/Runner.hs` 的 `FinishedWithMulligan`，约 996–1043 行）：
- 每次结束换牌都 `genCard investigatorStartsWithInHand` 再加一张黑暗洞察；
- 上一轮加进来的黑暗洞察不在 `excludeFromMulligan` 里，所以下一轮它算进"起手 5 张"（少抽一张），还能被选去换掉。
- 是否再给一轮换牌看的是"手牌是否少于 5 张"；第二轮换掉一张后手里仍有 4 张 + 黑暗洞察 = 5 张，于是第三轮也被吞掉了（倒吊人本该给满三次）。
- 只有塔罗"倒吊人"正位（`Scenario.hs:224`，`Mulligans 2`）会让换牌超过一次，规则核心里没有其他 `Mulligans` 来源；其他"开局在手"的调查员（加布丽埃拉、杰罗姆、潘妮、瓦伦蒂诺）都重写了换牌流程，不受影响，所以只有黛安娜会出这个问题。

**现有 patch 不够**：patch 是"手里已有同名卡就不再加"。这样第二轮不会多一张黑暗洞察，但：
- 黑暗洞察仍算进起手 5 张，第二轮之后普通牌还是少一张；
- 第二轮仍能把黑暗洞察换掉，它会被洗回牌库，第三轮又会生成一张新的 → 牌库里多一张。

**建议改法**（需 Yunke 同意后再重编 DLL，未编译验证）：只在第一次结束换牌时加"开局在手"的卡，把它们记进 `excludeFromMulligan`，"是否再换一轮"的判断也扣掉这些卡：
```haskell
startsWithInHandCards <-
  if a ^. mulligansTakenL == 0 then traverse genCard investigatorStartsWithInHand else pure []
...
hand' <- field InvestigatorHand iid
let excludedNow = length $ filter ((`elem` investigatorExcludeFromMulligan) . toCardId) hand'
let moreMulligans = (a ^. mulligansTakenL + 1) < allowedMulligans
                    && startingHandAmount - (length hand' - excludedNow) > 0
...
  & (excludeFromMulliganL <>~ map toCardId startsWithInHandCards)
```
这样之后几轮黑暗洞察既不能被换掉，也不占起手 5 张的名额（抽牌处的 `excludedCount` 会把它扣掉），倒吊人的三次换牌也能用满。

---

## #11 阿加莎：神话阶段取消标记后回合结束不能发动 —— 规则核心问题

卡面（官方中文）：「[reaction]在你的回合结束时，如果你**本轮**封印、取消或忽略了混乱标记：从你的弃牌堆打出一张[[法术]]或[[洞察]]事件……」

**复现**（`evidence/bugs-server/11/`）：阿加莎（潜修者版 11008），改存档：混沌袋全换成旧印、一张飞蛾扑火（洞察事件）放进弃牌堆、遭遇牌库顶是掠夺之手（敏捷检定）。

`probe-mythos-cancel.txt`：
- 第 2 轮神话阶段，掠夺之手检定揭示旧印 → 选"取消此标记并抽取另一个" → `meta: {"agathaTrigger":true}`；
- 进入第 2 轮调查阶段，阿加莎回合一开始 `agathaTrigger` 变回 `false`。

`probe-endturn-after-mythos-cancel.txt`：第 2 轮回合结束，**没有出现阿加莎的响应**，直接进入第 3 轮神话阶段。

对照组 `probe-control-cancel-in-own-turn.txt`：同一局，改成在自己回合调查时取消标记 → 回合结束出现 `响应 · 阿加莎·克雷恩`。

**原因**：`Investigator/Cards/AgathaCrane.hs` 在 `BeginTurn` 清掉 `agathaTrigger`。一轮里神话阶段在调查阶段之前，所以神话阶段做的取消/忽略/封印永远被清掉。卡面是"本轮"，应该在新一轮开始时清。

**patch 核对**：patch 改为 `Do BeginRound` 时清。查过时序：`EndRound` 之后依次是 `BeginRoundWindow`、`BeginRound`（剧本立即推 `Do BeginRound`，每个调查员都会收到）、`Begin MythosPhase`，所以清零发生在神话阶段之前，改法正确。只有一个边角：如果有人在"每轮开始时"窗口里取消标记，会被随后的 `Do BeginRound` 清掉；目前没见到这样的卡，可以不管。

---

## #19 令人震惊的一幕能被取消 —— 规则核心问题

卡面（官方中文，52027）：「<b>显现</b> - 结算冒险参考卡牌底下的随机一张令人作呕的现实卡牌。不能被取消。」

**复现**（`evidence/bugs-server/19/`）：阿加莎手里放一张守护结界（"在你抽取一张非弱点诡计时：取消其显现效果"），遭遇牌库顶换成令人震惊的一幕，结束回合进神话阶段。

`probe-shocking-display.txt`：
- 抽到令人震惊的一幕时，核心给出 `打出 · 守护结界`；`probe-shocking-ward-played.txt`：打出后被取消，守护结界进弃牌堆，阿加莎受 1 点恐惧。
- 对照组：遭遇牌库顶换成核心已标"不能取消"的究极混沌（05342），**不给守护结界**，直接进意志检定。说明"不能取消"的机制本身正常，只是这张卡缺标记。

**原因**：`Treachery/CardDefs/ReturnToThePathToCarcosa/ReturnToTheLastKing.hs` 的 `shockingDisplay` 没有 `cdRevelation = CannotBeCanceledRevelation`。patch 正是补这一行，按对照组的结果，补上即可。

**顺带核对**（`audit-cannot-be-canceled.txt`，按卡面文字对照核心定义，只读源码、未逐张复现）：卡面写"显现…不能取消"而核心缺标记的还有沉没之城"宏伟秘库"的三张**古代秘库**（11608 / 11609 / 11610）。`AncientVaultO.hs` 的注释写着"由卡牌定义的 CannotBeCanceledRevelation 处理"，但 `TheGrandVault.hs` 里的定义并没有这一项，属于同一类问题。表里其他"缺标记"的（黑暗未来、愚人的命运、凶兆、万界终章）说的是"别的牌/标记的效果不能取消"，不是同一个机制，不算。

---

## #20 拆迷雾高塔用不了幽灵利刃 —— 规则核心问题

说明：迷雾高塔（Mist-Pylon，08674–08678）属于《大地边缘》"疯狂之心"，不在七循环印斯茅斯；按反馈描述（"拆高塔"+"幽灵利刃"）对应的就是这张，循环编号可能是玩家记错了。

**复现**（`evidence/bugs-server/20/`）：疯狂之心单人开局的高级设置前端还没接入（`ClientSettings` 报"本场景的高级设置尚未接入"，存档 `s0-heartofmadness-stuck-at-settings.json`），只能注入：把阿加莎所在的书房换成迷雾高塔（已揭示），手里放一张幽灵利刃（06201），资源 6。

`probe-mistpylon-actions.txt`：可执行行动里有 `攻击 · 迷雾高塔`，但**没有 `打出 · 幽灵利刃`**。
`raw-question-mistpylon.txt`：核心原始问题里没有 `c06201`，不是前端漏显示。
对照组 `probe-control-razor-with-enemy.txt`：同一手牌，地点上有一个食尸鬼爪牙时，出现 `打出 · 幽灵利刃`。

**原因**：幽灵利刃的打出条件是 `canFightSomething`（`Criteria.hs`），只认"能攻击的敌人"和"可揭露的隐藏小卡"，没有"可以如同敌人一样被攻击的地点"（`LocationWithModifier CanBeAttackedAsIfEnemy`）。迷雾高塔自带的"攻击"行动是地点自己的能力，资产的攻击能力走的是 `Helpers/Ability.hs`（约 320–345 行）的另一套判断（含这类地点），所以基础攻击能用、攻击事件不能用。patch 在 `canFightSomething` 里补上这一项，与复现一致。补上后所有用 `canFightSomething` 的攻击事件都会受益。

未验证：patch 后真正打出幽灵利刃攻击迷雾高塔的结算（`hasFightTargets` 已包含这类地点，按源码应能选到；幽灵利刃的"+2 伤害"效果只对敌人目标生效，对迷雾高塔只有基础伤害，这是卡面"敌人"措辞的自然结果）。需要重编 DLL 后再跑一遍。

---

## #28 温蒂的技能 —— 前端问题（核心正常）

卡面（官方中文）：「[reaction]在你揭示一个混乱标记时，选择并弃掉你1张手牌：取消该混乱标记并将其放回混乱袋。揭示一个新的混乱标记。(每次检定/能力仅限一次)」

**核心复现**（`evidence/bugs-server/28/`）：温蒂调查书房 → 开始检定。
- `probe-wendy-trigger.txt`：揭示 +1 后核心给出 `响应 · 温蒂·亚当斯`；选它 → 选弃一张手牌（野猫）→ 新标记揭示为旧印 → 检定成功。存档 `s2b-after-wendy.json` 里 `resolvedChaosTokens` 只有旧印，+1 已被取消放回。**核心结算完全正确。**

**前端问题**：`probe-wendy-view-at-cancel-window.txt` 是温蒂响应窗口打开时、前端拿到的 game 视图：
```
skillTestChaosTokens        = []
skillTest.revealedChaosTokens = []
focusedChaosTokens          = [ {chaosTokenFace: "PlusOne", chaosTokenRevealedBy: "c01005", …} ]
skillTest.step              = "SkillTestFastWindow2"
```
也就是说，此时刚揭示的标记**只在 `focusedChaosTokens` 里**，检定还没把它记进 `revealedChaosTokens`。而前端：
- 检定面板的标记行只读 `TableModel.RevealedTokens`（`app/Scripts/Table/TableModel.cs:244`，即 `skillTestChaosTokens` + `skillTest.revealedChaosTokens`）→ 空；
- 响应提示框和桌面中央的"揭示了"横幅都用 `FocusedTokens`（`app/Scripts/Table/TableScreen.Tokens.cs:16`），但它第一行 `if (m.InSkillTest) return list;` 检定中直接返回空（`TableScreen.Interact.cs:237`、`TableScreen.Layout.cs:983` 调用）。

结果：玩家看到"响应 · 温蒂"时画面上**还没有任何标记**，看起来像"判定前就能用"；如果点了跳过，标记才显示出来，而这时响应窗口已经过去，看起来像"揭示之后反而用不了"。和反馈描述完全吻合。

**要改的位置**（不在本会话改，由主会话安排）：检定中也要显示 `focusedChaosTokens` 里带 `chaosTokenRevealedBy` 的标记。两种做法任选其一：
1. `TableModel.RevealedTokens`（`TableModel.cs:244`）在 `InSkillTest` 时追加 `focusedChaosTokens` 里有揭示者、且不在 `revealedChaosTokens` 里的标记 —— 检定面板直接就显示了；
2. 或 `TableScreen.Tokens.cs:19` 的 `FocusedTokens` 在检定中不直接返回空，而是返回"有揭示者且不在本次检定 revealedChaosTokens 里"的标记，让响应提示框（Interact.cs:237）把它画出来。

注意保留现有的过滤：歪打正着等效果会把整袋标记都放进 focused，只有带揭示者的才是刚揭示的那枚。

未做：实机截图（需要把 probe 存档导入游戏数据库再用 `rungame.ps1` 打开；视图数据和代码路径已足够定位）。修完前端后建议实机用这份 `28/s1.json` 场景（温蒂调查书房）验一次。

---

## 证据目录

```
evidence/bugs-server/
  rules-probe2/   Probe2 源码 + 改存档脚本 patch_tarot.py / patch_agatha.py / patch_encounter_top.py / patch_rename_cards.py
  8/   deck-diana.json, s0-before-setup.json, s1-hangedman-upright.json, probe-hangedman-*.txt, s2/s3 存档
  11/  deck-agatha.json, s1-patched.json, probe-mythos-cancel.txt, probe-endturn-after-mythos-cancel.txt, probe-control-cancel-in-own-turn.txt
  19/  probe-shocking-display.txt（含对照组）, probe-shocking-ward-played.txt, audit-cannot-be-canceled.txt
  20/  probe-mistpylon-actions.txt, raw-question-mistpylon.txt, probe-control-razor-with-enemy.txt, raw-question-control.txt
  28/  probe-wendy-trigger.txt, probe-wendy-view-at-cancel-window.txt, s2b-after-wendy.json
  各目录下 _invalid-first-try/：做法有误的首次尝试，附说明，结论不采用
```
重放示例（在对应编号目录下）：
```
Probe2.exe play s1-hangedman-upright.json out.json s:继续 s:换掉 s:结束选牌 s:换掉 s:结束选牌
```

## 待 Yunke 决定

- #8 #11 #19 #20 都要改规则核心并重编 DLL，会改变 sourceId（Win 版、手机核心、联机必须同步）。#8 建议用上面的改法替换现有 patch；#19 可一并补古代秘库三张。
- #28 只改前端，不动核心。

---

## 新 DLL 验证（2026-11-23，sourceId `ce629ddb…ba39`）

新 DLL：`rules-work/probe-root/runtime/arkham_core.dll`（`classic-fast-1.0.6-ce629ddb`）。用同一个 Probe2（`rules-work/probe2-bin/Probe2.exe`），设 `PROBE_ROOT=rules-work/probe-root`，存档和步骤与上面相同。每份输出第一行都是 `core sourceId arkham-core-sha256:ce629ddb…`。证据在 `evidence/bugs-server/<编号>/new-dll/`。

### 结论

| 编号 | 结果 | 说明 |
|---|---|---|
| 旧存档兼容 | **不兼容** | 旧 DLL（5bb054d7）的存档直接导入被拒：`Core source mismatch; migration required` |
| #8 | 修好 | 倒吊人三次换牌都能用满，每次都是 5 张普通牌 + 1 张黑暗洞察 |
| #11 | 修好 | 神话阶段取消后，回合结束出现阿加莎的响应；下一轮开始标记清零；对照组照旧 |
| #19 | 修好 | 不再给守护结界；对照组照旧 |
| #20 | 修好 | 能打出幽灵利刃、选迷雾高塔为目标、正常结算；对照组照旧。前端选目标的标题显示的是 id，见下 |

### 旧存档兼容（第 5 项）

- `8/new-dll/import-old-save-rejected.txt`：`s1-hangedman-upright.json`（旧 DLL 存的）直接导入，核心回 `{"code":"rejected","message":"Error in $: Core source mismatch; migration required"}`。
- 原因：`rules-work/engine/.../Arkham/Native/Session.hs:414`。导入时只接受本 DLL 的 sourceId 和 `compatibleImportSourceIds`（第 91–101 行）里列出的 sourceId，**列表里没有现在正式版的 `5bb054d7…`**。
- 影响：照这样发布，玩家现有存档（5bb054d7 存的）在新版里都导不进去，包括战役中途的存档、联机，以及手机核心如果也换的话。
- 存档结构本身没问题：只把 `snapshot.coreSourceId` 改成新 ID 的副本（脚本 `rules-probe2/retag_source.py`，文件名带 `-retag`），8 份旧存档全部能导入并正常往下打（下面的验证都是用这些副本跑的）。四处改动都没有加新字段。所以把 `5bb054d7…` 加进 `compatibleImportSourceIds` 应该就够了，但这要再改源码、再编一次 DLL。

### #8（`8/new-dll/probe-hangedman-replace-x3.txt`）

同样的步骤：换掉 → 结束选牌，连续三轮。

| 时点 | 手牌 | "换掉"选项 | 牌库 |
|---|---|---|---|
| 开局 | 5 张，没有黑暗洞察 | 5 张普通牌 | 33 |
| 第 1 次换牌后 | 5 张普通牌 + 黑暗洞察 | 5 张普通牌，**没有黑暗洞察** | 33 |
| 第 2 次换牌后 | 5 张普通牌 + 黑暗洞察 | 5 张普通牌，**没有黑暗洞察** | 33 |
| 第 3 次换牌后（已换牌 3，进入剧情） | 5 张普通牌 + 黑暗洞察 | — | 33，**没有黑暗洞察** |

以前第二轮后就没有换牌了，现在第三轮也给。全程只有一张黑暗洞察（在手里）。

### #11（`11/new-dll/`）

- `probe-mythos-cancel.txt`：第 2 轮神话阶段取消旧印 → `agathaTrigger: true`。进入调查阶段、她回合开始后**仍是 true**（旧 DLL 这里会变回 false）。
- `probe-endturn-after-mythos-cancel.txt`：回合结束出现 `[0] 响应 · 阿加莎·克雷恩`。
- `probe-agatha-react-then-round3.txt`：发动响应 → 选弃牌堆的飞蛾扑火 → 打出，弃牌堆变空；进入第 3 轮神话阶段时 `agathaTrigger: false`，新一轮开始时清零了。
- 对照组 `probe-control-cancel-in-own-turn.txt`：标记原本是 false，在自己回合调查时取消 → true → 回合结束出现响应。照旧。

### #19（`19/new-dll/`）

- `probe-shocking-display.txt`：令人震惊的一幕在遭遇牌库顶、守护结界在手。结束回合 → 神话阶段抽遭遇卡 → **直接进入第 2 轮调查阶段，没有"打出 · 守护结界"**，守护结界仍在手里。令人震惊的一幕进了胜利区（卡定义里是胜利 0）。测试局的剧本参考卡下面没有"令人作呕的现实"，所以显现本身没有效果，这符合预期。
- 对照组 `probe-control-ultimatechaos.txt`：究极混沌在牌库顶 → 直接进入检定投入（投入 · 绝处逢生 / 投入 · 守护结界 / 开始检定），不给打出守护结界。照旧。
- 未修：沉没之城的古代秘库三张（11608 / 11609 / 11610）在新源码 `TheGrandVault.hs` 里仍然没有 `CannotBeCanceledRevelation`。这次没改，留给下次。

### #20（`20/new-dll/`）

- `probe-mistpylon-actions.txt`：站在迷雾高塔（无敌人）上，可执行行动里有 `[2] 打出 · 幽灵利刃` 和 `[7] 攻击 · 迷雾高塔`。`raw-question-mistpylon.txt` 里有 `c06201`（核心给出了这个选项）。
- `probe-razor-on-mistpylon.txt` → `probe-razor-resolve.txt`：打出幽灵利刃（资源 6→4，行动 2→1）→ 选攻击目标（唯一一个是迷雾高塔）→ 投入 → 开始检定 → 应用结果。迷雾高塔 `tokens` 从 `[Clue 2]` 变成 `[Clue 2, Damage 1]`，幽灵利刃进弃牌堆。只有 1 点伤害，是因为卡效里的"+2 伤害"只对敌人目标生效（`SpectralRazorEffect` 要求 `EnemyTarget`），对迷雾高塔只有基础伤害，与之前的预计一致。
- 对照组 `probe-control-razor-with-enemy.txt`：书房里有食尸鬼爪牙时，`打出 · 幽灵利刃`、`攻击 · 食尸鬼爪牙`、`躲避 · 食尸鬼爪牙` 都在。照旧。
- **前端小问题（新发现，没改）**：选攻击目标时题目显示 `攻击 · b534e166-63b3-…`（地点 id），不是"攻击 · 迷雾高塔"。核心发的是 `FightLabel`，`enemyId` 填的是地点 id（`raw-question-razor-target.txt`），`engine/QuestionPresenter.cs:137`（还有 138 行的躲避、139 行的交战）只去 `enemies` 里找名字，找不到就显示 id。桌面锚点 `TableModel.FightTarget`（`app/Scripts/Table/TableModel.cs:301`）已经会依次查敌人、隐藏小卡、地点、支援，标题这里也照这个顺序查名字就行。迷雾高塔自带的攻击行动、还有其他能攻击地点的攻击事件也会碰到这个问题。

### 文件

```
evidence/bugs-server/
  rules-probe2/retag_source.py           仅测试用：复制存档，把 coreSourceId 换成新 DLL 的
  8/new-dll/   import-old-save-rejected.txt, s1-retag.json, probe-hangedman-replace-x3.txt, s3-after-3-mulligans.json
  11/new-dll/  *-retag.json, probe-mythos-cancel.txt, probe-endturn-after-mythos-cancel.txt,
               probe-agatha-react-then-round3.txt, probe-control-cancel-in-own-turn.txt, s4/s5/s6/s7 存档
  19/new-dll/  *-retag.json, probe-shocking-display.txt, probe-control-ultimatechaos.txt, s2 存档
  20/new-dll/  *-retag.json, probe-mistpylon-actions.txt, raw-question-mistpylon.txt, probe-razor-on-mistpylon.txt,
               probe-razor-resolve.txt, raw-question-razor-target.txt, probe-control-razor-with-enemy.txt, s2–s4 存档
```

---

## b08f62c4 验证（2026-11-23，sourceId `b08f62c4…6427`）

DLL：`rules-work/probe-root/runtime/`（`classic-fast-1.0.6-b08f62c4`）。和 ce629ddb 相比只改了一处：`Session.hs` 的 `compatibleImportSourceIds` 加进了 5bb054d7。这次**一律用原始旧存档**（5bb054d7 存的），不用 `-retag` 副本，步骤与上一节相同。每份输出第一行都是 `core sourceId …b08f62c4…`，所有输出里都没有 CORE_ERROR、NO_CHOICE 或异常。证据在 `evidence/bugs-server/<编号>/new-dll-b08/`。

### 旧存档兼容：通过

`8/new-dll-b08/import-old-saves.txt`：8 份旧存档都直接导入成功，导出后的 `coreSourceId` 都是 `b08f62c4`（导出文件为 `<编号>/new-dll-b08/*-imported.json`）。

| 存档 | 原 ID | 导出后 |
|---|---|---|
| 8/s1-hangedman-upright | 5bb054d7 | b08f62c4 |
| 11/s3、11/s4-round2-investigation、11/s4b-round2-start | 5bb054d7 | b08f62c4 |
| 19/s1-shocking-top-ward-in-hand、19/s1-control-ultimatechaos-top | 5bb054d7 | b08f62c4 |
| 20/s1-study-as-mistpylon-razor-in-hand、20/s1-control-razor-ghoul-top | 5bb054d7 | b08f62c4 |

### 四条规则和对照组：结论与 ce629ddb 相同

- **#8**（`probe-hangedman-replace-x3.txt`）：三次换牌都给了，每次换完都是 5 张普通牌加黑暗洞察，"换掉"选项里没有黑暗洞察，牌库始终 33 张。结束后牌库里黑暗洞察 0 张，手里 1 张。选项和步骤与 ce629ddb 那次逐行一致。
- **#11**：神话阶段取消后 `agathaTrigger` 一直是 true，回合结束出现 `响应 · 阿加莎·克雷恩`（`probe-endturn-after-mythos-cancel.txt`）。发动后打出飞蛾扑火，到第 3 轮神话阶段变回 false（`probe-agatha-react-then-round3.txt`）。对照组：在自己回合取消后出现响应（`probe-control-cancel-in-own-turn.txt`）。
- **#19**：令人震惊的一幕在牌库顶时，全程没有"打出 · 守护结界"（`probe-shocking-display.txt`）。对照组究极混沌直接进入检定（`probe-control-ultimatechaos.txt`）。
- **#20**：站在迷雾高塔上有"打出 · 幽灵利刃"，核心原始问题里有 `c06201`。打出后选迷雾高塔为目标并结算，迷雾高塔从 `[Clue 2]` 变成 `[Clue 2, Damage 1]`，幽灵利刃进弃牌堆。对照组有食尸鬼爪牙时也能打出幽灵利刃、攻击。前端选目标时标题仍显示地点 id（`攻击 · b534e166-…`），和上一节说的前端问题一样，没有改。

上一节提到的两处遗留不变：古代秘库 11608/9/10 仍缺"不能取消"标记；`engine/QuestionPresenter.cs:137-139` 的标题只查敌人。

---

## "显现不能被取消"全库审计 + 古代秘库复现（2026-11-23）

详细内容在 `evidence/bugs-server/local-cannot-cancel/README.md`。

- **卡面**：古代秘库 11608 / 11609 / 11610 的官方中文都是「显现 – 将古代秘库附属到你所在地点。不能取消。」，html 卡牌数据、mac-core-repo 前端卡牌数据和英文原文都一致。
- **全库审计**：共 7419 个卡号，29 张写了"不能取消"。真正"本卡显现不能取消"但核心缺 `CannotBeCanceledRevelation` 的**只有这三张**（`Treachery/CardDefs/TheDrownedCity/TheGrandVault.hs` 的 `ancientVaultO` / `ancientVaultN` / `ancientVaultP`）。另有 10 张同一机制的卡核心已有标记。其余 16 张说的是攻击、移动、别的牌的效果、混乱标记不能取消，属于别的机制，单独列出、不算。
- **复现**（旧 DLL 5bb054d7）：三张古代秘库放到遭遇牌库顶、手里有守护结界时，都会出现"打出 · 守护结界"。打出后显现被取消，秘库没有进场，阿加莎受 1 点恐惧。对照组：究极混沌不给守护结界；跳过守护结界时，古代秘库正常附属到地点。

---

## b25aded6 验证（2026-11-23，sourceId `b25aded6…a5cb2`）

DLL：`rules-work/probe-root/runtime/`（`classic-fast-1.0.6-b25aded6`）。和 b08f62c4 相比改了两处：古代秘库三张补了 `CannotBeCanceledRevelation`；兼容名单加进了 b08f62c4。一律用原始存档：5bb054d7 存的存档，以及古代秘库复现时用的 `local-cannot-cancel/s1-<卡号>-top-ward-in-hand.json`（同样是 5bb054d7）。每份输出第一行都是 `…b25aded6…`，没有 CORE_ERROR、NO_CHOICE 或异常。证据在 `evidence/bugs-server/<编号>/new-dll-b25/` 和 `evidence/bugs-server/local-cannot-cancel/new-dll-b25/`。

### 存档导入：通过（`8/new-dll-b25/import-saves.txt`）

| 存档 | 原 ID | 导出后 |
|---|---|---|
| 8 份旧存档（8/s1、11/s3、11/s4、11/s4b、19/s1×2、20/s1×2） | 5bb054d7 | b25aded6 |
| `8/new-dll-b08/s1-hangedman-upright-imported.json` | b08f62c4 | b25aded6 |
| `20/new-dll-b08/s1-study-as-mistpylon-razor-in-hand-imported.json` | b08f62c4 | b25aded6 |

### 古代秘库：修好（三张都验证了）

同一个局面（遭遇牌库顶是古代秘库、手里有守护结界），结束回合进入神话阶段：

| 卡 | 5bb054d7（复现） | b25aded6 |
|---|---|---|
| 11608 | 给"打出 · 守护结界"，能取消 | **不给守护结界**，秘库直接附属到书房（`AttachedToLocation`），守护结界留在手里，恐惧 0 |
| 11609 | 同上 | 同上 |
| 11610 | 同上 | 同上 |
| 对照：究极混沌 | 不给守护结界，直接进检定 | 照旧 |

证据：`probe-<卡号>-drawn.txt`、`s2-<卡号>.json`、`where-is-the-vault.txt`、`probe-control-ultimatechaos.txt`。

### #8 #11 #19 #20 和对照组：结论与 b08f62c4 相同

把选项、步骤、`agathaTrigger` 和阶段行逐行和 `new-dll-b08/` 比对，11 份输出全部一致：
- **#8**：三次换牌都能用满，每次都是 5 张普通牌加黑暗洞察，黑暗洞察不在"换掉"选项里，牌库始终 33 张。
- **#11**：神话阶段取消后回合结束出现阿加莎的响应，第 3 轮清零；对照组照旧。
- **#19**：不给守护结界；对照组照旧。
- **#20**：能打出幽灵利刃，选迷雾高塔为目标，结算后迷雾高塔 `Damage 1`；对照组照旧。

### 前端遗留（没改，不影响规则）

- 选攻击目标时题目显示地点 id（`engine/QuestionPresenter.cs:137-139`），同前。
- 新发现：古代秘库的能力按钮显示成 `发动能力 · 古代秘库[tdcrunen]`，卡名里的符文图标代码 `[tdc_rune_n]` 下划线被吞了，没有画成图标。只是显示问题，原因没有查。
