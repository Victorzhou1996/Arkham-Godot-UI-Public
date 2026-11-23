# 规则核心接口需求：公开"跳过触发器"全局设置（11-20）

状态：需求，未实现。提出方：Godot 前端（本仓库）。实现方：规则核心（native arkham-core）。

## 1. 要解决的问题

调查员设置里有一项全局设置 `settings.globalSettings.ignoreUnrelatedSkillTestTriggers`。html 版文案是
"跳过技能检定期间不影响结果的触发器"（`gameBoard/game_bar.json` 的 `viewSettingSkipTriggers`）。

- 默认是 **开**（参考源码 `Arkham/Card/Settings.hs:258`）。
- 开着时，检定进行中如果一个快速能力没有声明和检定有关（ability 的 `wantsSkillTest` 为空），就不会出现在选项里（`Arkham/Investigator/Runner.hs:517` 的 `applySettingsFilter`）。
- 玩家目前**没有任何公开途径**把它关掉。

实际影响（已用 rules-probe 复现，见 `evidence/rules-probe/REPORT-20261120-wendy.md`）：
雅沃托（04035）第二个能力"[fast]：弃掉你牌库顶的一张卡牌（每阶段限一次）"在检定中不会出现。卡面允许在检定中使用，
温迪"虎口拔牙 + 雅沃托"清空牌库的连招需要在检定中用它。

| 存档 | 设置 | 检定中雅沃托提供的能力 |
|---|---|---|
| `evidence/rules-probe/u3.json` | 开（默认） | 只有能力 1（横置加值） |
| `evidence/rules-probe/x3.json`（只把存档里的设置改成 false） | 关 | 能力 1 和能力 2 都有 |

## 2. 现在为什么做不到

- `Raw` 消息被 `Arkham/Native/Validation.hs:52` 拒绝："Raw messages are not a public operation"（rules-probe 实测，y1）。这是对的，不应该开放 Raw。
- 现有公开的偏好回答只有逐卡的 `SetTriggerModeAnswer`（`Native/Answer.hs:165` 起、`Native/Triggers.hs`）。它只能把一个已经出现过的能力设为"总是触发 / 只在自己回合 / 停止触发"。全局设置隐藏掉的能力根本没出现过，所以它管不到。
- 前端约定是只用规则 DLL，不改规则行为、存档格式和回答权限（`docs/PLAN-20261117.md` 第 8 行），所以不能靠改存档绕过。

## 3. 需要的接口

仿照 `SetTriggerModeAnswer` 加一个**有限、固定字段**的回答，不做通用的设置写入：

```json
{ "tag": "SetSkipTriggersAnswer",
  "contents": { "investigatorId": "c01005", "enabled": false } }
```

- 字段固定为 `investigatorId` 和 `enabled`，多一个或少一个都拒绝（同 `TriggerModePreference` 的 FromJSON 写法）。
- `investigatorId` 必须是规范的线上值（`toJSON` 往返一致），并且属于提交答案的这个座位（同 `ownedInvestigator`）。别的座位的调查员要拒绝。
- `enabled` 等于当前值时拒绝，理由类似 "Skip triggers preference is already selected"，与 `validatePreference` 一致。

### 什么时候可以提交

- 和 `SetTriggerModeAnswer` 一样，**不要求这个座位正在被提问**（`Session.hs` 里 "Player is not being asked" 检查对它豁免）。玩家应该能在任何时候从菜单里切换。
- 历史设置待定（`historyPending` 且问题是 `PickCampaignSettings`）时也允许，同 `SetTriggerModeAnswer`。
- 其余检查照常：revision 不能过期、玩家必须在这局里、Epic 上下文等。

### 生效方式

1. 写入 `globalSettings.ignoreUnrelatedSkillTestTriggers`，效果等同 `updateGlobalSetting (SetIgnoreUnrelatedSkillTestTriggers enabled)`。
2. **当前问题要立刻刷新。** 如果这个玩家当前有打开的快速玩家窗口（`FastPlayerWindow`），要重新计算选项，让刚放出来或刚隐藏的能力马上出现或消失。
   这和 `Investigator/Runner.hs` 里 `UpdateGlobalSetting` 推 `Do (CheckWindows currentWindows)` 的行为一致。
   注意：`Triggers.applyPreference` 只改实体、不重算窗口，这里不能照搬，必须走重算。
3. 之后走和 `SetTriggerModeAnswer` 相同的收尾：`settleAutomaticTriggers`，再 `finishWithUndoMetadata`，revision 加 1。这样它会记入历史，可以撤回。

### 读取

前端读调查员实体里的 `settings.globalSettings.ignoreUnrelatedSkillTestTriggers` 就够了，不需要新的视图字段。

## 4. 验收（前端会用 tools/rules-probe 跑）

1. 用存档 `u2.json`（检定中、设置为开）提交 `enabled:false`，接受后回答列表**立刻**出现两个"发动能力 · 雅沃托"。
2. 在同一个窗口再提交 `enabled:true`，第二个能力立刻消失。
3. 提交与当前值相同的值，被拒绝，状态不变。
4. 用另一个座位的调查员 id 提交，被拒绝。
5. 多给或少给字段被拒绝（例如加一个 `"extra":1`，或缺 `enabled`）。
6. 撤回一步，设置恢复原值。
7. 导出存档再导入，设置保留。

## 5. 前端准备做什么（接口就绪后）

- 牌桌菜单（Esc）加一行开关，文字用 html 官方文案"跳过技能检定期间不影响结果的触发器"。对本座位控制的每个调查员各提交一次。
- 设置开着时，在检定面板显示 html 的提示"已开启'跳过触发器'：与本次检定无关的快速触发效果已被隐藏。"，后面跟"更改"按钮（`gameBoard/skill_test.json`）。
- 同名能力按钮的区分已经先做好了（`engine/QuestionPresenter.cs`）：同一张卡的两个能力同时出现时，按钮后接各自官方能力文本的开头，悬停显示全文。
