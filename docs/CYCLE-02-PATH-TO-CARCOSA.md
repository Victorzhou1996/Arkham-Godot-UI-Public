# 第二循环《卡尔克萨之路》（战役 03）（2026-11-20 起）

状态用词遵守 `docs/SUPERVISION.md`：**已验证** = 本会话用探针或离屏 QA 实际跑到并截图/日志确认；**已做未验证** = 改了但没有实际跑到。真人鼠标一律未测，最终验收由 Yunke 标记。

做法照第一循环（`docs/CYCLE-01-DUNWICH-LEGACY.md`）：规则只问核心 DLL；文字一律用 html 版官方文本；规则源码只读参考；图片只用已有原图（缩放裁切）。

## 1. 规则核心（探针 `probe/CarcosaProbe.cs`）

运行：`Gathering.Probe.dll <root> --carcosa schema` / `--carcosa flow [步数]`，输出在 `evidence/probe-carcosa-*`。

| 项目 | 结果 | 状态 |
|---|---|---|
| 创建战役 `"03"` | 核心直接支持；两个座位先问 `ChooseDeck` | 已验证（`evidence/probe-carcosa-094446`） |
| 序章流程 | `ContinueCampaign`(PrologueStep) → 读《黄衣之印》→ 读序幕正文（"如果萝拉参与"一段由核心标为不适用/适用）→ `ContinueCampaign`(ScenarioStep 03043) → 选领队 → 第一章《谢幕》引言 → 调度 → 设置说明 → 回合正常进行（行动、神话阶段、敌人攻击、分配伤害） | 已验证（同上，40 步） |
| 六名调查员组卡规则（`deckBuildSchema`） | 潘明、赛菲娜、阿喀琦、约里克、萝拉：示例牌组 `valid`；**马克·哈里根：核心拒绝**（`unsupportedReasons: missingEnglishFields 03009: back_text`，即专属卡"索菲"缺英文背面文字数据） | 已验证（`evidence/probe-carcosa-094431`；新数据目录启动时自动生成五套示例牌组并通过核心校验，马克被跳过只给警告：`qa-data/ptc2/decks.json`、`evidence/p0/ptc2.log`） |
| 单章开局 / 从中途开始 | 未试；敦威治时核心对第 2 章起的单章开局一律拒绝（settingsSchema.supported=false），预计相同 | 未测 |

~~马克·哈里根不能用~~ → 11-20 已解决。报错只来自规则核心组卡校验自带的英文缓存（缺 03009 的 `back_text`）；规则核心实际能打索菲。现在有索菲的马克牌组改由 build 判断构筑，示例牌组 `example-03001` 已生成，实际开局验证过。详见 PLAYTEST-ROUND3 的“马克·哈里根的示例牌组”。

## 2. 数据（`tools/import_cycle.py`，只读来源）

| 内容 | 来源 | 结果 | 状态 |
|---|---|---|---|
| 卡牌文字（含遭遇卡、敌人、地点，官方中文名） | html 版 `classic-fast-20261117/frontend/public/cards/cards_zh-cn.json` 的 03xxx | `app/assets/data/ptc-cards.json`，383 张（本体 + 6 个神话包：ptc/eotp/tuo/apot/tpm/bsr/dca） | 已验证（文件生成；QA 牌桌显示中文名见第 5 节） |
| 卡图 | 0712 `cards/03*.avif` | `app/assets/cards/03xxx.webp`，531 张（含背面、a/b 面）；已用 Godot `--headless --import` 导入 | 已验证（牌桌地点、手牌、威胁区截图） |
| 插画裁切 | 卡图按类型裁切（同敦威治） | `app/assets/art-fallback/03xxx-fallback.webp`，384 张 | 已验证（同上） |
| 调查员小卡 | html 版 `img/arkham/portraits/0300[1-6].jpg` | `app/assets/ui/mini/`（6 张，之前已有，内容相同） | 已验证（选择战役截图） |
| 费用、技能图标 | 规则源码 `Asset/Cards/ThePathToCarcosa.hs`、`Event/Cards/ThePathToCarcosa.hs`、`Skill/CardDefs/ThePathToCarcosa.hs` | `app/assets/data/ptc-costs.json`，111/111 | 已做未验证（显示） |
| 调查员数值 | 规则源码 `Investigator/Cards/<名字>.hs` 的 `Stats`（html 卡牌数据没有数值） | `SelectionStage.Stats` 加六人 | 已做未验证 |
| 混沌袋 | 规则源码 `Campaigns/ThePathToCarcosa/ChaosBag.hs`：与狂热之夜数字相同，**三个骷髅、没有异教徒和石板**；简单/标准 15 枚，困难 16，专家 17 | `ChaosBagRules.Bag(难度, "03")` | 已验证（准备界面截图，标准 15 枚） |
| 封面 | html 版 `img/arkham/boxes/03.jpg`（盒面中间插画带）及 03043…03316 | `app/assets/art-cover/03.webp` 等 9 张，只裁切不重绘（`tools/make_covers.py` 加了只处理指定编号的参数，旧封面没动） | 已验证（`evidence/ptc/10-cycle-cover.png`） |
| 名称 | html locale：`campaignNames` 03 = 卡尔克萨之路；`scenarioNames` 03043 谢幕、03061 最后的王者、03120 往事回声、03159 邪秽誓约、03200 真相幻影、03240 苍白面具、03274 黑星升起、03316 卡城幽影；序幕、幕间故事 I: 现实之影、幕间故事 II：失魂落魄、尾声 | `Campaigns.Carcosa` | 已验证（选择战役、准备界面截图） |
| 剧情、结局、标记效果、战役日志 | `locale-zh.json` 的 `thePathToCarcosa.*`；本地副本比 html 版少 35 条（如《邪秽誓约》"你记得…"的 7 个标签、《黑星升起》"调查骑士大厅"、《苍白面具》"读完第二幕"、各难度名），已原样补进 `locale-zh-extra.json`（不覆盖已有词条） | 序幕、前三章引言、第一章无结局、第二章结局 2：已验证（第 5 节）；补的 35 条：已做未验证（那几章没走到） |
| 遭遇卡名 | 敌人、诡计、地点、密谋/场景都在 `ptc-cards.json` 里（官方中文名） | 牌桌、记录直接用 | 已验证（"哈斯塔的傀儡""蟑螂群""卡尔克萨探求者""追猎鬼影""脑海中的低语"等截图与记录） |

卡牌目录合并规则（`engine/CoreCatalog.cs`）：同一编号出现在多个 `*-cards.json` 时，**先读到的文件的官方文字保留**（`ptc-cards.json` 在 `zz-build-cards.json` 之前），它缺的字段（每卡上限、生命理智、技能图标、费用等数值，html 卡牌数据没有）由后面的文件补上。只对 03 及以后的编号这样补，核心盒和敦威治不变。原因：不补的话赛菲娜的"彩绘世界"上限会被当成 1，示例牌组校验失败。

## 3. 界面

| 项目 | 做法 | 状态 |
|---|---|---|
| 选择战役 | 第三栏"第二循环 · 卡尔克萨之路"：封面、八章名、六人小卡；三栏间距收窄 | 已验证（`evidence/ptc/01-cycle.png`、`02-cycle-hover.png`；封面导入后重截） |
| 准备界面 | 战役路线：序幕 › 第一章 · 谢幕 … 第八章 · 卡城幽影 › 尾声；说明两段幕间的位置；混沌袋 15 枚 | 已验证（`evidence/ptc/03-prepare.png`） |
| 选人 | 卡尔克萨六人群像（另一会话 11-20 已做，`SelectionStage.Carcosa`）；准备界面打开时直接进这幅，仍可切到基础游戏/敦威治 | 已验证（`evidence/ptc/04-picker.png`） |
| 混沌标记数值与通过概率 | `TableScreen.Odds.cs` `CarcosaModifier`：八章逐章对照规则源码 `Scenario/Scenarios/ThePathToCarcosa/<关卡>.hs` 的 `getChaosTokenValue`（表见第 4 节）。检定中仍以核心的 `valueBreakdown` 为准。六人的远古印记也补了（马克 +身上伤害数、潘明 +1、赛菲娜 +3、阿喀琦 +1、约里克 +2、萝拉 +2） | 谢幕：规则审计 `AUDIT_RULE token ok` 全部 9 种标记（骷髅 4 次）已验证；远古印记补之前审计报 MISMATCH（牌桌 0、核心 +1），补后未再跑审计；其余七章：已做未验证 |
| 牌桌地图 | 八章都还没有绘制的整张地图：`app/assets/scenarios/03xxx.json` 用第一张冒险卡插画作场景图、核心 `locationLayout` 网格排地点小卡（同敦威治的做法）。要做成"整张画 + 热点"需要每章一幅地图画（按规矩不自己出图，需要时写 prompt） | 谢幕、最后的王者、往事回声：已验证（地点、连线、调查员与敌人位置、中文名）；其余：已做未验证 |
| "隐私"诡计在手牌 | 核心把"脑海中的低语"放在 `HiddenInHand`；原来前端把它画进威胁区。照 html 版 `Player.vue`（inHandTreacheries）改为画在自己的手牌扇面末尾（紫边、不能拖出），点击按它的能力键 `tr:<id>` 提供"弃掉"行动；队友行的手牌数也算上它（`TableModel.HiddenInHand`、`TableScreen.Layout.cs HandFan`、`TableScreen.Team.cs`） | 显示：已验证（`evidence/ptc/20-hidden-in-hand.png`，三张低语在潘明手牌里，威胁区只剩"负债"）；点击弃掉：未测 |
| 标题字体缺字 | 显示字体 ZCOOL XiaoWei 自带的"回"字形坏了（内框被填实，"往事■声"）。逐字渲染检查 locale 和卡牌数据里所有汉字，只有"回""徊"。`Style.Title` 遇到这两个字改用后备的楷体 | 已验证（`evidence/ptc/37-ch3-intro.png` 修前、`37b-ch3-intro-fixed.png` 修后） |
| 剧情里的英文人名 | 核心在《往事回声》引言的"检查战役日志：贵宾会谈下是否记有{name}"里传英文名（Sebastien Moreau）。html 版原样代入（也会显示英文）；这里改为：变量 `name` 是英文且卡牌数据里有同英文名的卡时，用该卡的官方中文名（塞巴斯蒂安·莫罗）（`engine/ClientLocale.cs`） | 已验证（修后 `scan-english` 不再报） |

## 4. 各章混沌标记（规则源码核对，简单/标准 ‖ 困难/专家）

| 章 | 骷髅 | 异教徒 | 石板 | 远古邪物 |
|---|---|---|---|---|
| 谢幕 03043 | −1（身上 ≥3 恐惧时 −3）‖ −你的恐惧数（至少 1） | −4 ‖ −5 | −4 ‖ −5 | −4 ‖ −5 |
| 最后的王者 03061 | 再抽一个（按 −2 估）| −2 ‖ −3 | −4 | −所在地点隐藏值 |
| 往事回声 03120 | −单个敌人身上最多的毁灭 ‖ −所有敌人身上毁灭总数 | −2 ‖ −4 | −2 ‖ −4 | −2 ‖ −4 |
| 邪秽誓约 03159 | −1 ‖ 再抽一个 | −你的恐惧数 | −所在地点基础隐藏值 | 0 |
| 真相幻影 03200 | −场上毁灭总数（最多 5）‖ 不封顶 | −2 | −3 ‖ −4 | −2 ‖ −3 |
| 苍白面具 03240 | −离起始地点的地点数（最多 5）‖ 不封顶 | −2 ‖ −3 | −2 ‖ −3 | −3 ‖ −4 |
| 黑星升起 03274 | −单个密谋上最多的毁灭 ‖ −两个密谋毁灭总数 | 再抽一个（对有毁灭的敌人改为自动失败）| 再抽一个 | −2 ‖ −3 |
| 卡城幽影 03316 | −2（神智耗尽时 −4）‖ −你的恐惧数 | 再抽一个 | −3 ‖ −5 | −3 ‖ −5 |

**官方中文卡面与规则源码不一致之处（数值以核心为准，只记录）**：《往事回声》骷髅的中文文字把两个难度写反了（简单/标准写"所有敌人上的毁灭总数"，困难/专家写"毁灭最多的敌人"）；规则源码是简单/标准取单个最多、困难/专家取总数。

## 5. 离屏 QA（`tools/dev/rungame.ps1`，全部离屏）

| 轮次 | 内容 | 结果 | 证据 |
|---|---|---|---|
| ptc1 | 主菜单 → 选择战役 → 卡尔克萨准备界面 → 群像选人（悬停、点选潘明）→ 加入潘明、阿喀琦 → 开局 | 全部正常；当时新图未导入，封面空白（已导入后重截） | `evidence/ptc/01–08`、`run1/` |
| ptc2 | 同上从头开局，自动游玩（带截图、`lossundo`） | 序幕 → 谢幕第一幕"惊醒"→ 第二幕"陌生人"（密谋第二幕"加演曲目"）；规则审计数值一致（见第 3 节）。之后自动游玩工具的"失败撤回"反复报"当前没有可以撤回到这里的节点"并卡在同一回合，手动停止 | `evidence/ptc/run2/`（36 张）、`evidence/p0/ptc2.log` |
| ptc4 | 约里克 + 赛菲娜，从头开局，自动游玩（`fast`，不截图、不撤回） | **连续推进七章**：序幕 → 谢幕（无结局）→ 升级 → 最后的王者（结局 2）→ 升级 → 往事回声（无结局）→ 邪秽誓约（结束时两人都已发疯，核心要求换人，自动换成敦威治的佐伊、雷克斯示例牌组）→ 真相幻影 → 苍白面具 → 黑星升起（无结局 → 结局 3，核心按规则结束战役，`AUTOPLAY_END Lost`）。全程 1646 步，没有游戏报错 | `evidence/p0/ptc4.log`；存档 `qa-data/ptc4`（结束）、副本 `qa-data/ptc4s`（截图用） |
| ptc4s | 用 ptc4 存档副本 `open-at` 回到各关键步骤截图 + `scan-english` | 第一章无结局、第二章引言/设置/牌桌/结局 2、第三章引言/牌桌/调查记录：全是官方中文 | `evidence/ptc/30–39`、`37b` |
| ptc2h | 用 ptc2 存档副本 `open-at` 看"隐私"诡计 | 见第 3 节 | `evidence/ptc/20–21` |

注意：
- **幕间故事 I 没出现是对的**：规则源码 `TheLastKing.hs:269` 只有结局 1 才进入幕间故事 I（现实之影）；这局两人都被击败走结局 2，核心直接进第三章。所以幕间 I 的三选一界面还没走到。
- 第一章开局截图的文件名里写着 01104（序幕时没有当前关卡，QA 截图命名回退到默认值），只影响截图文件名。
- 自动游玩的"失败撤回"（`lossundo`）问题属于 AI 会话负责的 `TableScreen.Autoplay.cs`，已在共享笔记转告，本会话没改。

## 6. 卡尔克萨特有机制清单（逐个确认显示）

| 机制 | 出现在 | 前端要做到的 | 状态 |
|---|---|---|---|
| 序幕的萝拉分支（"如果萝拉参与"） | 序幕 | 核心标为不适用的段落显示为灰色 | 没有萝拉时的显示：已验证（序幕页）；有萝拉：未测 |
| **隐私**（Hidden）诡计：秘密放进手牌 | "脑海中的低语"（03084a–d） | 画在手牌里，可点击发动"弃掉" | 显示已验证；点击未测 |
| 信念 / 怀疑（Conviction / Doubt）计数 | 战役日志，全战役 | 战役日志页显示计数 | 未测（没走到） |
| 被附身的宾客（"附身"敌人）、贵宾会谈记录 | 最后的王者 | 剧情支援变敌人、战役日志"贵宾会谈" | 宾客与"哈斯塔的傀儡"等敌人在牌桌上显示：已验证（`33-ch2-board.png`）；会谈记录与日志页：未专门检查 |
| 幕间故事 I 三选一（现实之影 1/2/3） | 第二章后 | 选项按官方文字显示 | 已验证（第 9.2 节） |
| 历史协会地点（多张同名）、"卡尔克萨探求者"身上放线索/毁灭 | 往事回声 | 同名地点能区分、敌人身上的标记 | 地点网格与记录已验证（`39-ch3-log.png`）；六张"历史协会"同名、只靠小字副标题区分（同基础游戏阿卡姆密林的问题） |
| 丹尼尔·切斯特菲尔德、"你记得…"标签 | 邪秽誓约 | 记得的事件按官方标签显示（已补词条） | 幕间 II 的丹尼尔段落已验证；"你记得…"标签未专门检查 |
| 管风琴手、拜亚基移动 | 真相幻影 | — | 未测（没走到） |
| 地下墓穴：按方向（上下左右）放置新地点，离起始地点的距离 | 苍白面具 | 网格地图随地点增加扩展 | 未测（没走到） |
| **两个密谋牌堆同时进行**（毁灭分别放） | 黑星升起 | 顶栏/牌桌同时显示两张密谋 | 已验证（第 9.3 节，`52c-ch7-two-agendas.png`） |
| 哈斯塔、神智耗尽、"可能的结局" | 卡城幽影 | — | 第八章已走到（第 9.2 节）；胜利结局未走到 |
| 调查员：萝拉的"角色"（换职阶）、阿喀琦的法术充能、赛菲娜开局选事件放在下面、约里克从弃牌堆打出、潘明的技能卡加成 | 全战役 | 能力按钮、标记、"下面的卡"显示 | 赛菲娜的"选择事件"（ChooseUpToN）每章开局出现：已验证（日志）；其余未专门检查 |

## 7. 没做 / 没测到（第二轮之后，见第 9 节）

- 第八章只在自制存档里打过一次（全员被击败结束），胜利结局（结局 1–5）没走到；尾声只验证了"有被附身调查员"的那段文字。
- 马克·哈里根：核心缺"索菲"（03009）数据，整名调查员不能用。
- 八章都没有绘制的整张地图（同敦威治）。
- 真人鼠标未测；"隐私"诡计的点击弃掉未测。

## 8. 改动的文件（原件都在 `backup/`）

`probe/CarcosaProbe.cs`（新）、`probe/Program.cs`；`tools/import_cycle.py`、`tools/make_covers.py`；`engine/CoreCatalog.cs`、`engine/ClientLocale.cs`、`engine/PrototypeBackend.cs`（新战役标题）；`app/Scripts/Campaigns.cs`、`ChaosBagRules.cs`、`Style.cs`、`Screens/CycleScreen.cs`、`Screens/PrepareScreen.cs`、`Screens/SelectionStage.cs`、`Screens/MainMenu.cs`（QA `menu:carcosa`）、`Table/TableModel.cs`、`Table/TableScreen.Odds.cs`、`Table/TableScreen.Layout.cs`、`Table/TableScreen.Team.cs`；数据 `app/assets/data/ptc-cards.json`、`ptc-costs.json`、`locale-zh-extra.json`、`app/assets/scenarios/030xx.json`（8 个）、`app/assets/cards/03*`、`art-fallback/03*`、`art-cover/03*`。没改 `AI/*`、`Build/*`、`tools/build-engine`。

## 9. 第二轮（11-20 晚，主会话转 Yunke 要求：补第八章、两个幕间、尾声、第五到七章、换人、战役结束）

### 9.1 怎么走到的（以规则核心为准）

先查规则源码各步的条件：
- 幕间故事 I：只在《最后的王者》**结局 1**（无结局且至少一人撤退）之后（`TheLastKing.hs:269`）。
- 幕间故事 II：只在《邪秽誓约》**结局 2 或 3**（"告别疯人院"目标推进）之后；无结局走结局 1，不进幕间（`TheUnspeakableOath.hs:332/338`）。
- 第八章：《黑星升起》要靠两条目标路线推进（"浪破路现"→结局 1、"天开路现"→结局 2）；无结局转结局 3，核心直接**判负结束战役**（`BlackStarsRise.hs`，`gameOverLost`）。上一轮就是这样结束的。
- 尾声：只给战役日志里"被附身"的调查员读一段，然后以胜利结束；没有人被附身时不读任何文字（`Campaign/Campaigns/ThePathToCarcosa.hs:139`）。

这些条件自动游玩打不出来（要到指定地点清空线索、达成 4 个条件后撤退等），所以照敦威治第八章的先例（`qa-data/crafted/dunwich-ch8.json`）做**自制存档**：取 ptc4 真实战役里"继续战役"那一步的存档，只把"下一步"改掉，用核心自带的导入接口（`qa-import` → `ImportCrafted`）载入，后面的流程全由核心按规则跑：

| 存档 | 来源 | 改动 |
|---|---|---|
| `qa-data/crafted/carcosa-interlude1.json` | ptc4 第 655 步（第二章后） | 下一步 → 幕间故事 I |
| `carcosa-interlude2.json` | 第 1253 步（第四章后） | 下一步 → 幕间故事 II（丹尼尔存活） |
| `carcosa-ch8.json` | 第 1670 步（第六章后） | 下一步 → 第八章卡城幽影 |
| `carcosa-epilogue.json` | 第 1670 步 | 下一步 → 尾声 |
| `carcosa-epilogue-possessed.json` | 同上 | 另在战役日志加一条"被附身：雷克斯·墨菲" |

### 9.2 结果

| 内容 | 结果 | 状态 | 证据 |
|---|---|---|---|
| 幕间故事 I：现实之影 | 官方标题和三选一（进入现实之影 1/2/3），选 1 后读对应文字，继续进入第三章 | 已验证 | `evidence/ptc/int1/04-read-*.png`、`05-*` |
| 幕间故事 II：失魂落魄 | "丹尼尔存活"段落 + 二选一（无视警告 / 听从警告），之后进入第五章 | 已验证 | `evidence/ptc/int2/04-read-*.png` |
| 第八章卡城幽影 | 引言、设置、牌桌、检定、第一到第三幕（"褴褛之王"）；全员被击败后核心结束战役 | 已验证 | `evidence/ptc/ch8/`（32 张）、`ch8-board-fixed2.png`、`ch8-campaign-end.png` |
| 第八章混沌标记 | 规则审计：两人共 22 次全部一致（骷髅 −2 / 神智耗尽 −4 都对上） | 已验证 | `evidence/p0/ptc-ch8.log` |
| 尾声 | 没有被附身的人：核心直接以胜利结束，不读文字（与规则源码一致）；加了"被附身"记录后：显示官方"尾声"标题和正文 | 已验证 | `evidence/ptc/epi-99-after.png`、`epi2/04-read-*.png` |
| 第五到第七章 | 引言、牌桌（前后两个时间点）、结局页 | 已验证 | `evidence/ptc/42–54` |
| 换人对话框 | 第四章后赛菲娜、约里克发疯："赛菲娜·卢梭 已发疯"、创伤、"暂不替换 / 选择新调查员" | 已验证 | `evidence/ptc/41-replace-dialog.png` |
| 战役结束画面 | 黑星升起结局 3（判负）、卡城幽影失败两种：八章结果、战役日志、两人状态与经验 | 已验证 | `evidence/ptc/55-campaign-end.png`、`ch8-campaign-end.png`、`epi-99-after.png` |

### 9.3 这一轮发现并修好的

| 问题 | 原因 | 改法 | 状态 |
|---|---|---|---|
| 第七章顶栏只显示一张密谋（黑星升起同时有两张） | 顶栏只画 `m.Agenda`（第一张） | `TableScreen.Plates.cs`：有几张画几张；多张时每张的毁灭按核心的算法（`Agenda/Runner.hs`：场上全部毁灭 − 其他密谋上的毁灭）对自己的阈值；密谋能力按钮、悬停说明仍在第一张 | 已验证（`52c-ch7-two-agendas.png`：3/6 与 2/5） |
| 第五章目标文字露出 `[per_investigator]`（"风琴手上有3[per_investigator]个线索"） | 顶栏目标句子没处理每位调查员图标 | `TableScreen.Render.cs`：换成"6（每位调查员 3）"（按人数算） | 已验证（`44b-ch5-objective-fixed.png`） |
| 第八章一张地点显示成编号"03325a" | 核心用 03325a，卡牌数据只有 03325；卡图查找会去掉字母后缀，名字查找不会 | `engine/CoreCatalog.cs` 名字查找加同样的回退；并给 03325a 补了插画裁切 | 已验证（`ch8-board-fixed2.png`："哈利湖滨"） |

### 9.4 发现但没改（记录）

- 战役结束画面里第二、四章写"未达成结局"，其他章写"未达成任何结局"：这两章的官方文字没有"无结局"标题（核心转去了结局 2 / 结局 1），前端用的是敦威治时定下的后备写法（`TableScreen.Campaign.cs:126`，两个循环共用），这次没动。
- 幕间故事 I 的三选一说明在官方文字里是嵌套列表，前端显示成平铺的几行（加粗的选项 + "进入…"），能读，但没有 html 版的项目符号。
- 第八章里雷克斯的剩余神智在队友栏显示为 −3（卡城幽影神智耗尽的调查员不被击败，核心给的就是负数），没核对 html 版怎么显示。
- 自制存档跳过了中间章节，所以战役结束画面里有的章节是"未进行"，尾声存档的结束标题写的是第六章：属于存档本身，不是显示问题。

## 10. 真相幻影"其他毁灭改为减去"（11-20 晚，Yunke 要求）

**规则（以核心为准）**：真相幻影的三张"夜"密谋在战役日志"确信"多于"怀疑"时带 `OtherDoomSubtracts` 修正。核心判断密谋是否推进时（`Game.hs` 的密谋阈值检查、`Helpers/Doom.hs`）：
- 阈值 = 卡面阈值（按人数）+ 各个 `DoomThresholdModifier`；
- 带 `OtherDoomSubtracts`：计数 = 本密谋毁灭 −（场上全部毁灭 − 本密谋毁灭），全部毁灭按 `getSubtractDoomCount` 加总（支援扣掉 `IgnoreDoomOnThis`、敌人、事件、地点、诡计、密谋、未出局的调查员）；
- 否则：计数 = 场上毁灭（视图的 `totalDoom`）− 其他密谋上的毁灭。

html 版没有这套计算（只显示总毁灭和牌上的毁灭标记），所以照核心的公式做。

**做法**：`TableModel.AgendaThreshold / AgendaDoomCount / OtherDoomSubtracts / SubtractDoomCount` 读核心视图里每张密谋自带的 `modifiers`；`DoomThreshold` 也改为带上阈值修正。密谋牌（`TableScreen.Plates.cs`）和悬停说明（`TableScreen.Layout.cs`）都用这个计数；减去规则生效时，第三行写"其他卡牌上的毁灭从本密谋中减去"，悬停说明写出场上共有多少毁灭。QA 命令 `dump-agenda` 打印每张密谋的修正、阈值和计数。

**核对**（全部已验证）：
| 情形 | 结果 | 证据 |
|---|---|---|
| ptc4 第五章 6 个时间点（怀疑不少于确信，规则不生效） | 前端加总的全部毁灭与核心 `totalDoom` 每次都相等；计数 = 场上毁灭 | `evidence/p0/ptc5ag.log` |
| 自制存档"确信 3"（`qa-data/crafted/carcosa-ch5-conviction.json`） | 核心给三张"夜"都带上 `OtherDoomSubtracts`；第六章的密谋没有 | `evidence/p0/ptc-conv.log` |
| 调试命令在佐伊身上放 6 点毁灭，再过一次神话阶段：**有确信** | 场上 7 点毁灭，核心**没有**推进密谋；前端计数 −5 / 6 | `evidence/p0/ptc-dm2-conviction.log`、`evidence/ptc/59b-conviction-plate.png` |
| 同样操作：**无确信**（对照组） | 核心推进到第二夜（普通算法 6 ≥ 6） | `evidence/p0/ptc-dm2-noconviction.log` |

计数为负时毁灭点不填色，数字照实显示（例如 −5 / 6）。

## 11. 毁灭标记动效规范（`docs/DOOM-ANIMATION-RULES.md` 第 5 节）逐项检查（11-20 晚）

用新代码重跑三段（自制存档导入后自动游玩，`qa-data/ptc-doom-{ech,lk,bsr}`），再用存档副本 `open-at` 回到关键步骤截图。

### 11.1 这一循环把毁灭放在卡上的效果

| 章 | 效果 | 本次是否出现 |
|---|---|---|
| 往事回声 | "卡尔克萨探求者"每个神话阶段结束时放毁灭（拿不到线索时）；三张密谋把放到敌人上的线索翻成毁灭；异教徒标记"在最近的敌人上放 1 个毁灭" | 出现：探求者身上 2→5，异教徒（遭遇组里的侍僧、密会巫师）、狂徒身上也有 |
| 黑星升起 | 遭遇组"黑暗教团"的侍僧、密会巫师生成时带毁灭；石板"在每个密谋上放 1 个毁灭"；两个密谋 | 出现：两名异教徒各带毁灭；两个密谋同时计数 |
| 最后的王者 | 骷髅"检定失败时在一个附身敌人上放 1 个毁灭" | 本次 320 步没有出现（日志里没有放到敌人上的毁灭） |
| 真相幻影 | 特殊计算"其他毁灭改为减去"（第 10 节） | 用自制存档 + 调试毁灭验证过计数 |

### 11.2 检查结果

| 规范条目 | 结果 | 状态 | 证据 |
|---|---|---|---|
| 地图敌人小卡左下角的毁灭角标 | 探求者"5"、异教徒"1" | 已验证 | `66-ech-doom-on-seeker.png`、`60-bsr-doom-on-enemies.png` |
| 右上概况区交战敌人卡的毁灭角标 | 密会巫师"1"、狂徒"1" | 已验证 | `67-ech-doom-engaged.png` |
| 顶上轨道：毁灭全在卡上时只画橙圈 | 往事回声"埋藏的真相"4 个橙圈（7/4）、"洗劫宅邸"3 个橙圈（3/4） | 已验证 | `66`、`67` |
| 两个密谋（黑星升起）红点 + 橙圈 | "风暴肆虐" 2 红 + 4 橙 = 6/6，"仪式开始" 0 红 + 4 橙 = 4/5，与核心算法一致（每张 = 场上全部毁灭 − 另一张密谋上的毁灭） | 已验证 | `60-bsr-doom-on-enemies.png` |
| 遭遇卡带来的毁灭：先揭示再放毁灭 | 中途帧是"侍僧在晋升之门现身"的揭示画面，日志里 `QA_DOOM … fromCards=enemy:…` 在揭示之后 | 已验证（顺序） | `61-bsr-doom-flight-mid.png`、`evidence/p0/ptc-doom-bsr2.log` |
| 密谋被推满：轨道写"毁灭已满 · 密谋推进"后再翻牌 | 黑星升起"风暴肆虐"→"水漫金山" | 已验证 | `63-bsr-doom-gone-mid.png`、`64-bsr-doom-gone-after.png` |
| 带毁灭的卡离场：橙圈减少 | 往事回声狂徒（1 毁灭）被击败：7/4 → 6/4，概况区的狂徒消失 | 已验证（结果） | `69-ech-gone-step1.png`、`69-ech-gone-after.png`，日志 `QA_DOOM_GONE 7->6 from=enemy:…` |
| 毁灭从来源飞到卡上 / 离场时飞向目标牌子 / 橙圈光晕 | 都触发了（日志有 `QA_DOOM … fromCards=enemy`、`QA_DOOM_GONE`），但 QA 的中途截图每次都先被同一步里的揭示、检定翻标记等动画用掉，没有拍到毁灭飞行的那一帧 | 已做未验证（动画本身未截到） | — |
| 真相幻影减去规则下的轨道 | 计数为负（如 −5 / 6）时不画红点和橙圈，数字照实写，第三行说明"其他卡牌上的毁灭从本密谋中减去" | 已验证 | `59b-conviction-plate.png` |

### 11.3 需要主会话知道的

- 我在 `TableScreen.Plates.cs` 里改过两处（都在收到"改前先说"之前，已备份在 `backup/`）：
  1. 第 175 行附近 `own` 加了 `Math.Max(0, …)`，因为减去规则下计数可能为负；
  2. 第三行说明：减去规则生效时写"其他卡牌上的毁灭从本密谋中减去"。

  另外密谋计数改用 `TableModel.AgendaDoomCount / AgendaThreshold`，见第 10 节。`TableScreen.Doom.cs` 没动。
- 想截到毁灭飞行的中途帧，需要给毁灭动画单独一个 QA 中途截图开关（现在和揭示、翻标记共用一个 `midshot`，总被先触发的动画用掉），要改 `TableScreen.Doom.cs`，我没改。
