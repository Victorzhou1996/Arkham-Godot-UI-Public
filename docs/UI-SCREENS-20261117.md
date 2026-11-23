# 界面截图清单 · 2026-11-17 第三轮

状态用词遵守 `docs/SUPERVISION.md`：以下截图均为 **已做未验证**，"已验证" 只能由 Yunke 标记。

截图由游戏内 QA 步骤在 1920×1080 下截取，数据目录 `qa-data/ui-review`（测试用，不是 userdata）。按玩家顺序排列：从开始界面进入，直到牌桌上的每一个操作界面。

## 1. 开始与菜单

| # | 界面 | 文件 | 看什么 |
|---|---|---|---|
| 01 | 开始界面（主菜单） | `evidence/ui-review/01-main-menu.png` | 标题、五个入口（开始新的调查、继续调查、牌组工坊、设置、退出） |
| 02 | 设置 | `evidence/ui-review/02-settings.png` | 音量、全屏、减少动效、界面缩放、自动略过、结束回合前确认 |
| 03 | 牌组工坊（空） | `evidence/ui-review/03-workshop-empty.png` | 卡库、我的牌组、校验／保存按钮 |
| 04 | 牌组工坊（新牌组） | `evidence/ui-review/04-workshop-new-deck.png` | 新建牌组后的构筑界面 |
| 05 | 继续调查（无存档） | `evidence/ui-review/05-continue-dialog.png` | 无存档时的空状态提示 |

## 2. 准备与选人

| # | 界面 | 文件 | 看什么 |
|---|---|---|---|
| 06 | 准备调查（空） | `evidence/ui-review/06-prepare-empty.png` | 剧本简介、难度（简单／标准／困难／专家）、"选择调查员"入口 |
| 07 | 选择调查员 | `evidence/ui-review/07-selection-stage.png` | 五位调查员的全景画面、底部名字 |
| 08 | 选择调查员（悬停） | `evidence/ui-review/08-selection-hover.png` | 悬停时的镜头与名字高亮 |
| 09 | 选择调查员（已点选） | `evidence/ui-review/09-selection-clicked.png` | 点选后的调查员资料面板、"选择"按钮 |
| 10 | 准备调查（已选人） | `evidence/ui-review/10-prepare-with-investigator.png` | 已入队的调查员、牌组选择、开始调查按钮 |

## 3. 开场与牌桌

| # | 界面 | 文件 | 看什么 |
|---|---|---|---|
| 11 | 剧情开场 | `evidence/ui-review/11-story-intro.png` | 剧本开场文字与"继续" |
| 12 | 开场后的准备（调查员就位、检定前） | `evidence/ui-review/12-setup-after-story.png` | 牌桌刚进入时的状态 |
| 13 | 牌桌（调查阶段） | `evidence/ui-review/13-table-turn.png` | 左侧资产与调查员、顶部目标与毁灭、手牌扇形、牌库与弃牌、专属区与公共区、结束回合 |
| 14 | 悬停一秒的文字框 | `evidence/ui-review/14-hover-card-textbox.png` | 手牌悬停说明框 |
| 15 | 拖牌（中央出牌区） | `evidence/ui-review/15-drag-zone-active.png` | 拖动时的提示"拖到中央松开即可打出"（判定区不可见） |
| 16 | 撤回菜单 | `evidence/ui-review/16-undo-menu.png` | 右上"撤回"展开：撤回到 行动开始／回合开始／一轮开始／剧本开始 |
| 17 | 调查记录（侧栏） | `evidence/ui-review/17-log-panel.png` | 0712 式记录，每条顶层记录右侧"撤回到此处" |
| 18 | 弃牌堆（空） | `evidence/ui-review/18-discard-empty.png` | 点击弃牌堆：空状态提示 |
| 19 | 宅邸总览 | `evidence/ui-review/19-mansion-overview.png` | 宅邸剖面图（顶部"宅邸"图标打开） |
| 20 | 混沌袋 | `evidence/ui-review/20-chaos-bag.png` | 混沌袋内容与数量 |
| 21 | 卡牌阅读器 | `evidence/ui-review/21-card-reader.png` | 点击/右键卡牌看大图与文字 |
| 22 | 菜单 | `evidence/ui-review/22-menu.png` | 继续游戏、设置、返回主菜单、退出游戏 |
| 23 | 操作说明（?） | `evidence/ui-review/23-help.png` | 打出手牌、就地菜单、出口、自动结算、快捷键 |
| 24 | 技能检定（投入前） | `evidence/ui-review/24-skill-test-setup.png` | 检定面板、投入区、"开始检定" |
| 25 | 技能检定结果 | `evidence/ui-review/25-skill-test-result.png` | 结果数字、"失败·差 N 点"、继续 |
| 26 | 神话阶段开始（横幅阶段） | `evidence/ui-review/30-phase-banner-a.png` / `31-phase-banner-b.png` / `32-after-phase-change.png` | 阶段切换时的暗场（横幅文字在遭遇翻牌时被盖住，见 `REPORT` 已知问题） |
| 27 | 遭遇翻牌 | `evidence/ui-review/28-encounter-reveal.png` | 神话·遭遇：翻开的遭遇牌与效果文字 |
| 28 | 强制效果自动结算提示 | `evidence/ui-review/29-forced-notice.png` | 右上角的小提示框 |
| 29 | 幕推进的过场 | `evidence/final/21-act-interlude.png`（上一轮截图） | 场景推进的段落间距（本轮未能重新截到） |

## 4. 自动通玩（结局）

见 `evidence/ui-review/autoplay-1p/`（18 张，按步骤编号，含各阶段切换和幕推进）。1 人局自动通玩 123 步，走到结局"未达成结局"（失败），无卡死。结局界面：`evidence/ui-review/autoplay-1p/18-end.png`（调查结束、返回主菜单／查看牌桌）。

## 5. 上一轮截图（仍有效的界面，未重截）

`evidence/final/` 中仍保留上一轮的 31 张截图和总览 `00-contact-sheet.jpg`，包括：就地菜单 `12-inplace-menu.png`、拖牌打出 `13-drag-to-play.png`、交战敌人 `25-engaged-enemy.png`、伤害分配 `26-assign-damage.png`、排序 `27-ordering.png`。这些截图是上一轮构建的画面，与本轮改动的界面（撤回、记录、弃牌堆、卡背）不完全一致。

## 6. 未截到或未验证

- 弃牌堆有牌时的展开面板（测试局弃牌为空，无法在 QA 中稳定制造弃牌）。代码路径已实现：有可用操作的弃牌会高亮，点击直接操作。
- 撤回到"剧本开始"的确认框与回退结果。
- 阶段横幅的文字阶段（被遭遇翻牌盖住，需在无翻牌的阶段切换中单独截图）。
- 真实鼠标操作（`tools/real_mouse_check.ps1`）仍未做，原因见 `docs/REPORT-20261117.md` 第 4 节。
