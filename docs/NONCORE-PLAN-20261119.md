# 非核心业务规划：音乐、音效、联机（2026-11-19）

负责范围：音乐、音效、联机。牌桌、规则、牌组工坊由主会话负责，本线不改它们的逻辑。
状态用词：**已做未验证** / **已验证** / **未做**。

---

## 0. 素材盘点（0712 html 版，只读）

| 位置（0712 下） | 内容 | 授权 | 结论 |
|---|---|---|---|
| `game/frontend/dist/audio/impactPunch_heavy_000.ogg` | 重击声 1 个（15KB） | Kenney Impact Sounds，**CC0**（附 LICENSE 文件） | 可直接用 |
| `game/frontend/dist/audio/turnIndicator.ogg` | "轮到你了"提示音（4KB） | 项目自制（ffmpeg 正弦波合成），无第三方素材 | 可直接用 |
| `classic-fast-20261117/frontend/public/audio/rain.ogg` | 循环雨声 45 秒 | Ylmir @ OpenGameArt，**CC0** | 可直接用（氛围层） |
| `classic-fast-20261117/frontend/public/audio/bgm/*.mp3` | 52 首背景音乐（0–5 循环，哈希文件名，206MB），曲名如 Smoking Shadows / Midnight Pursuit / Abyssal Confrontation | **0712 里没有任何来源或授权记录**（0712 的 1.0.29 发布说明写了"补齐 52 首背景音乐"并已随安装包发布，但同样没写来源） | **Yunke 11-19 确认：群友制作的开源音乐，可以使用** → 已放入 `app/assets/audio/music/`（附 CREDITS.md） |
| `green-fast-20261118/music-input/`（= 上游仓库 `frontend/public/audio/bgm`） | 78 首背景音乐（6–10 循环、灰烬兄弟会、Children of Blood、独立剧本、网站通用曲 cassildas-song），323MB | 来自上游 arkhamhorror app 仓库；**曲名与官方剧本名一致，仓库许可不一定覆盖这些音频** | 需 Yunke 确认；本项目目前只支持基础 + 第一循环，暂时用不到 |
| 0712 html 的 bgm 映射（`bgm.json`） | 每个剧本编号 → 曲目；0–1 循环逐剧本都有 | 数据（只有编号和文件名） | 已抄进本项目的音频清单（只抄映射，不抄音频） |

0712 html 版的音效其实只有两处：多人局"轮到你"时放 `turnIndicator.ogg`，其余靠背景音乐。没有点击、抽牌、混沌标记等音效。

**第一轮的做法**（11-19 上午；下午 Yunke 确认后已更新，见第 4.2 节）：
- 3 个 CC0/自制文件复制进 `app/assets/audio/sfx/`，同目录附授权说明。
- 背景音乐**不复制进仓库**。程序按清单到 `<数据目录>/audio/music/` 找文件（例如 `userdata/audio/music/a9b72af1a43b456b.mp3`），找不到就安静。Yunke 确认来源后，可以把文件放进去（自己用），或放进 `app/assets/audio/music/`（随仓库开源，需授权清楚）。
- 缺的音效（点击、抽牌、打牌、翻遭遇卡、混沌标记、成功/失败……）列在第 2 节，等 Yunke 决定来源（见第 5 节）。

---

## 1. 音乐方案

### 1.1 场景与选曲

| 场合 | 选曲规则 | 0712 对照 |
|---|---|---|
| 主菜单、工坊、准备页、战役页（所有非牌桌页面） | `menu` 曲 | html 的 SITE_TRACK |
| 牌桌 | 剧本专属曲 `scenario:<剧本编号>` → 战役主题曲 `campaign:<战役编号>` → 通用牌桌曲 `table` → 无 | html：TRACKS[scenario] → CAMPAIGN_THEMES[campaign] |
| 剧情页（序章、幕间、结局文字，核心的 Read 问题） | **不换曲**，当前曲压低约 8dB，读完恢复 | html 不处理；我们做轻量"压低" |
| 剧本结束 | `ending-won` / `ending-lost`；没有就淡出 | html：游戏结束即暂停 |
| 战役之间（升级牌组、继续战役） | 战役主题曲，没有就沿用当前曲 | html：幕间回退到战役主题曲 |

已在清单里写好的 0–1 循环映射（取自 html 版 `bgm.json`）：
01104 聚集于此 → Smoking Shadows；01120 午夜假面 → Midnight Pursuit；01142 地底噬魔 → Abyssal Confrontation；
02041 课外活动 → Empty Country Road (1)；02062 百赌百胜 → Smoke and Shadow；02118 博物馆之夜 → Exhibits in the Dark；02159 疯狂列车 → Runaway Train；02195 血色祭坛 → Village of Secrets；02236 无人知晓 → Primal Hunting Score；02274 毁灭之地 → Shadowed Stairwell；02311 迷失时空 → Ancient Gates Open。
菜单曲暂定 Black Tide Choir（0 循环里唯一没分到剧本的一首），等 Yunke 定。

### 1.2 切换、淡入淡出

- 两个播放器交叉淡化：旧曲 1.0 秒淡出，新曲 1.5 秒淡入；同一首曲目连续出现时**不重启、不回到开头**（html 的规则）。
- 循环播放；打开设置改音量立即生效（每帧对照设置值，变了就改总线音量，不用改设置对话框）。
- 音量 0 = 静音（总线 mute），不是 -80dB 残留。
- 剧情页压低、恢复各 0.6 秒。
- 以后可加：牌桌按阶段的"紧张度"层（神话阶段加一层低频氛围、毁灭接近阈值时加雨声/心跳），先不做，避免过度——和"效果克制"的原则一致。

### 1.3 设置

已有 `UserSettings.MusicVolume` / `EffectsVolume`（0–1），程序建两条总线 `Music`、`Sfx`，线性值转分贝。以后如需"关闭音乐"开关或"自选曲目"，再加设置项（需改 `SettingsDialog`，到时与主会话协调）。

---

## 2. 音效方案

| 提示名（cue） | 触发点 | 现有素材 | 接入状态 |
|---|---|---|---|
| `click` | 所有 `Ui.Button` 及已调用 `Sfx.Click()` 的按钮 | 无 | 已接（`Sfx.Click` → 音频管理），等素材 |
| `phase` | 阶段横幅出现（`TableScreen.Interact.cs OneBanner`） | 无 | 已接，等素材 |
| `chaos` | 混沌标记翻面（`TableScreen.Panels.cs FlipToken`） | 无 | 已接，等素材 |
| `encounter` | 翻出遭遇卡（`TableScreen.Reveal.cs RevealCard`） | 无 | 已接，等素材 |
| `draw` | 当前座位手牌数增加 | 无 | 已接（状态对比），等素材 |
| `damage` / `horror` | 任一调查员伤害/恐惧增加 | `impactPunch_heavy_000.ogg`（伤害）；恐惧暂无 | 已接；伤害有声 |
| `doom` | 毁灭总数增加 | 无 | 已接（状态对比），等素材 |
| `act` / `agenda` | 场景 / 密谋推进（卡号变化） | 无 | 已接（状态对比），等素材 |
| `turn` | 当前行动的调查员换人（多人局） | `turnIndicator.ogg` | 已接，有声 |
| `test-success` / `test-fail` | 检定成功/失败 | 无 | **未接**：检定结果要从核心问题里稳妥识别，等主会话的检定流程稳定后再接 |
| `play-card` | 打出卡牌 | 无 | **未接**：需在出牌动作里加一行，等素材到位一起加 |
| `shuffle` | 洗牌 | 无 | 未接 |
| `ending-won` / `ending-lost` | 剧本结束（音乐切换） | 无 | 已接（状态对比），等曲目 |

规则：
- 每个提示可配多个文件随机选一个，音高随机 ±3%，避免机械重复。
- 同一提示 60 毫秒内只响一次（批量状态变化不会"机关枪"）。
- 找不到文件就静默，日志只记一次 `AUDIO_MISSING`。
- 用户可以在 `<数据目录>/audio/sfx/` 放同名文件覆盖内置音效。
- 降低动态效果（ReducedMotion）不影响声音；以后如需"减少音效"再加设置。

清单文件：`app/assets/audio/audio.json`（提示名 → 文件、场合 → 曲目），改清单不用改代码。

---

## 3. 联机方案（只出方案，未实现）

### 3.1 现状

- 后端 `engine/PrototypeBackend.cs`：本机进程内跑规则核心（`arkham_core.dll`）+ sqlite 存档；每次回答带 `basis.Sequence`（乐观并发：序号不对就拒绝）。
- 视图按玩家生成：`controller.ViewAsync(gameId, player)`，手牌等私有信息天然按座位隔离。
- 多座位 `ClientSeat(Player, Number, HasQuestion)`，现在是同一台电脑轮流切换座位查看和操作。
- 前端调用后端的方法约 30 个（`Answer`、`Choice`、`Continue`、`Open`、`SelectDeck`、`UpgradeDeck`、`Undo*`、`Logs`、`ForcedSources` 等），都经过 `App.I.S.Backend`。
- 0712 做过 LAN 主机 + 客席（network-seat、房间、端点、断线 Resume、TLS 指纹），只作思路参考，不搬代码。

### 3.2 推荐：主机权威（一人开房，其他人连入）

```
 主机（开房的玩家电脑）                          客人（其他玩家电脑）
 ┌──────────────────────────────┐            ┌────────────────────────┐
 │ PrototypeBackend + 核心 + sqlite│ ←答案──── │ 远程后端（同一接口）     │
 │ 房间服务（WebSocket，端口可设）  │ ──视图──→ │ 牌桌界面（不改）          │
 │ 本机牌桌界面（可同时坐多个座位） │            │ 只拿到自己座位的视图      │
 └──────────────────────────────┘            └────────────────────────┘
```

- **谁做主机**：开房的玩家。存档、随机数、规则全在主机，客人不跑核心（客人电脑也不需要算力，只要同版本客户端）。
- **状态同步**：每次提交后，主机给每个在线座位推送"该座位的完整视图 + 序号"（gzip 压缩）。牌桌现在就是"拿到新状态 → 和上一个状态对比 → 播动画"，客人收到的是同一串状态，动画自然一致。第一版用整份视图，够用后再考虑差量。
- **回答**：客人发 `{座位令牌, 游戏号, 基准序号, 答案}`；主机核对座位归属和序号后调用现有 `Answer`，成功后广播。序号过期（别人刚答过）→ 回最新视图，客人界面刷新即可（与本地乐观并发同一套规则）。
- **撤回**：只有主机能撤回（或全员同意后主机执行），撤回后广播。调试/QA 命令（`Debug`、`ImportCrafted`）对客人关闭。
- **座位分配**：主机建局时定座位数（现有 1–4），大厅页里每个座位可以是"主机本人 / 等待客人 / 已被某客人认领"。客人选座位、选自己牌组库里的牌组（牌组 JSON 发给主机走现有 `SelectDeck`）。一台电脑可坐多个座位（混合热座 + 联机）。
- **断线重连**：认领座位时主机发一个座位令牌，客人本地记 `(主机地址, 游戏号, 座位令牌)`；主机把绑定写在 `setups/<游戏号>-seats.json`。重连凭令牌直接回到原座位，主机补发最新视图。客人掉线期间该座位轮到行动时，主机可"代为操作"（转为本机热座）。
- **版本校验**：握手时比对客户端版本、核心 `sourceId`、卡牌数据哈希，不一致拒绝进入，避免不同规则版本下视图不一致。
- **发现**：局域网 UDP 广播房间 + 手动输入 IP:端口；房间口令（6 位）防止误入。
- **互联网**：
  1. 推荐先用虚拟局域网工具（Tailscale / ZeroTier / Radmin VPN 等），零开发，当局域网用。
  2. 路由器端口转发 / UPnP（可选，开发量小）。
  3. 中继服务器（需要一台公网机器，涉及部署，和"不对外发布"冲突，暂不做）。

### 3.3 需要改哪里（工作量估计）

| 步骤 | 内容 | 估计 |
|---|---|---|
| 1 | 抽出后端接口 `IGameBackend`（牌桌用到的约 30 个方法），本地实现 = 现有 `PrototypeBackend`；牌桌改为依赖接口 | 1–2 天；**要动牌桌调用处（机械替换）**，需与主会话约时间 |
| 2 | 房间服务：WebSocket（.NET `HttpListener` 或 Godot `WebSocketPeer`）、消息格式、座位令牌、广播 | 2–3 天 |
| 3 | 远程后端：客人侧实现 `IGameBackend`，答案上行、视图下行、序号冲突重试 | 2 天 |
| 4 | 大厅页：开房/加入/座位认领/选牌组/准备 | 2–3 天（界面新文件） |
| 5 | 断线重连、主机代操作、版本校验 | 2 天 |
| 6 | QA：同机两进程离屏测（主机 + 客人各自数据目录，127.0.0.1），然后两台电脑局域网测 | 2 天 |

合计约 **2–3 周**（单人）。风险：牌桌里有几处直接读本地文件（剧情已读记录、偏移、经验结转等放在 `setups/`），联机时要分清"主机的"和"每个客人自己的"；自动操作（Autoplay、自动跳过）只能在主机上跑或只对自己座位生效。

### 3.4 用 EOS（Epic Online Services）做联机 —— Yunke 11-19 倾向

EOS 正好补上 3.2 里最难的三块：**互联网穿透（P2P 打洞 + Epic 中继兜底）、房间/邀请、玩家身份**，而且免费，不需要我们自己架服务器。"主机权威"的设计不变，只是把传输层从自建 WebSocket 换成 EOS。

参考：0712 `godot-native-20261019/native/friend-reference-20261021/native-client/`（群友的 Godot 原生客户端）已经跑通一套 EOS：`EosTransport/`（登录、大厅、P2P、分包）、`SessionHost/`（房主侧规则会话）、`EOS配置步骤.md`。思路可以照搬；**代码能否直接复用要问群友授权**（目录里没有许可证文件）。

| 环节 | 建议做法 |
|---|---|
| SDK | 官方 EOS C# SDK（参考实现用 1.19.2.1），从 Epic 开发者后台下载，**不放进开源仓库**（SDK 有自己的许可协议），构建时从本地路径引用；运行时只带 `EOSSDK-Win64-Shipping.dll` 和第三方声明。 |
| 身份 | EOS Connect **设备身份（Device ID）**：玩家不用登录 Epic 账号，每台设备得到稳定的玩家 ID（PUID）。以后想要 Epic 好友/邀请弹窗，再加 Epic 账号登录（可选）。 |
| 房间 | EOS Lobby，**邀请制 / 房间码加入**，不做公开搜索；上限 4 人。大厅属性放：客户端版本、规则核心 sourceId、卡牌数据哈希（不一致直接拒绝）、剧本/战役、游戏号；成员属性放：认领的座位、调查员、牌组是否就绪。 |
| 传输 | EOS P2P，`ReliableOrdered` 单通道，允许中继。单包上限约 1170 字节 → 必须**分包重组**（参考实现的 `EosPacketCodec`：消息 ID + 序号 + 总数，限制缓冲大小和超时）。视图 JSON 先 gzip。 |
| 权威 | 大厅房主 = 主机，跑 `PrototypeBackend`、核心和 sqlite；客人只发答案、收自己座位的视图（同 3.2）。 |
| 座位与重连 | 座位绑定 PUID（设备身份稳定，不需要另发令牌）。掉线后重新进大厅，主机按 PUID 认回原座位并补发最新视图。 |
| 房主掉线 | 存档只在房主电脑上，**不做房主迁移**：房主掉线 = 暂停；房主回来后从存档重开大厅，客人按游戏号重新加入。 |
| 凭据 | 产品/沙盒/部署 ID 和客户端 ID、Secret 放本地配置文件（`--eos-config` 或数据目录），**不进仓库、不进日志**。开源后别人自己编译要用自己的 EOS 产品；Yunke 发布的正式包内置自己的配置。 |

**关键建议：先把传输层抽象出来。** 定义 `IRoomTransport`（发/收消息、成员进出），做两个实现：
1. `LoopbackTcpTransport`：本机 127.0.0.1，**离屏 QA 可以开两个游戏进程（主机 + 客人）自动测**，不依赖 Epic 后台和网络；
2. `EosTransport`：真实联机。
协议、座位、重连都在第一个实现上做稳，再换 EOS，排查时能分清是"我们的协议问题"还是"EOS/网络问题"。

工作量：在 3.3 的基础上加 EOS 层（SDK 接入、设备登录、大厅、P2P 分包、联机大厅界面）约 1–1.5 周，合计约 **3–4 周**。

需要 Yunke 准备（我不能代办，涉及账号和协议）：
1. Epic 开发者账号，创建产品，按 `EOS配置步骤.md` 拿到 Product / Sandbox / Deployment ID，建 **GameClient** 策略的客户端拿 Client ID / Secret（接受 EOS 协议需本人操作）；
2. 从开发者后台下载 EOS C# SDK，放到本机某个目录告诉我路径；
3. 问群友：EOS 那部分代码能否按开源许可复用（能复用可省约一半 EOS 层工作量）。

### 3.5 不推荐的方案

- **每台电脑各跑一份核心、只同步答案（锁步）**：要求核心完全确定性且随机种子一致，撤回、导入、调试都会让各端分叉，排查成本高。
- **Godot 自带高层多人（RPC/MultiplayerSynchronizer）**：适合节点状态同步，我们的状态是核心的大 JSON，用它反而绕；底层用 Godot 的 `WebSocketPeer`/`ENet` 当传输层没问题。

---

## 4. 本轮实现（音频基础设施）

- 新文件 `app/Scripts/Audio/GameAudio.cs`：音频管理节点（总线、音量跟随设置、音效池、两路音乐交叉淡化、剧情压低、清单读取、缺文件静默、QA 命令）。
- 新文件 `app/assets/audio/audio.json`：提示名 → 文件；场合 → 曲目。
- 新素材 `app/assets/audio/sfx/`：`impact-heavy.ogg`（Kenney CC0）、`turn.ogg`（0712 自制）、`rain.ogg`（Ylmir CC0），附 `LICENSES.md`。
- 改动已有文件（各只加一行调用，均已在 `backup/` 备份）：见汇报。
- QA 命令：`audio-status`、`audio-cue:<名>`、`audio-music:<场合>`、`audio-volume:<音乐>,<音效>`、`audio-log:on|off`。

改动的已有文件（各一行，标记 `noncore audio 11-19`，原文件备份在 `backup/*-20261119-before-audio.cs.txt`）：

| 文件:行 | 内容 |
|---|---|
| `app/Scripts/Ui.cs:399-400` | `Sfx.Attach` 末尾加 `GameAudio.Attach(root)`；`Sfx.Click()` 改为 `GameAudio.Cue("click")` |
| `app/Scripts/Table/TableScreen.Render.cs:25` | `GameAudio.Observe(model);` |
| `app/Scripts/Table/TableScreen.Interact.cs:418` | `OneBanner` 里 `GameAudio.Cue("phase");` |
| `app/Scripts/Table/TableScreen.Panels.cs:372` | `FlipToken` 里 `GameAudio.Cue("chaos");` |
| `app/Scripts/Table/TableScreen.Reveal.cs:327` | `RevealCard` 里 `GameAudio.Cue("encounter");` |

### 4.1 QA 结果（2026-11-19，离屏，数据目录 `qa-data/audio1`，日志 `evidence/p0/audio1-menu.log`、`audio1-table.log`）

- 本机 QA 进程里 WASAPI 打不开输出设备，Godot 回落到哑驱动（主会话的日志里同样有，非本次引入）。所以**听感未验证**；播放进度、音量、切换逻辑在哑驱动下照常运行，以下按日志**已验证**：
  - 菜单曲自动播放；同一曲目再次请求不重启（进度继续走）；换曲交叉淡化；无曲目时淡出；离开牌桌回到菜单曲。
  - 音量跟随设置：0.3/0.5 → Music -20.9dB / Sfx -12.0dB；0 → 静音；恢复 0.6/0.8 → -8.9dB / -3.9dB。
  - 牌桌打开即切到剧本曲（01104 → Smoking Shadows）；跑 60 步自动对局：伤害音效随伤害增加触发；阶段、混沌、遭遇、抽牌、毁灭、密谋的触发点都被调用（无素材 → 静默，日志各记一次 `AUDIO_MISSING`）。
  - 无新增报错、无异常；构建 0 错误。
- 未验证：真实扬声器听感、剧情页压低（逻辑已做，日志没打印）、多人局"轮到你"提示（测试局为 1 人）、场景推进音效（测试中场景未推进）。

### 4.2 第二轮（11-19 下午，Yunke 确认音乐可用、同意下载音效包）

- 52 首群友音乐复制进 `app/assets/audio/music/`（206MB，SHA256 与 0712 记录全部一致），`audio.json` 补全 46 个剧本映射 + 6 个战役主题曲（各循环的 "01-" 序章曲 / not in the earth；01 战役主题与菜单曲同为 Black Tide Choir）。
- 从 kenney.nl 官方下载 4 个 CC0 包（Casino / Interface / RPG / Impact，共约 3.4MB），挑 38 个放进 `app/assets/audio/sfx/kenney/`，授权见 `sfx/LICENSES.md`。对应：点击=click、抽牌=card-slide、打牌=card-place、洗牌=card-shuffle、遭遇=木头吱呀 creak、混沌标记=皮袋摸索、标记落桌=chip-lay、成功/失败=confirmation/error、伤害=punch、恐惧=闷响 impactSoft、毁灭=钟声 impactBell、场景推进=翻书、密谋推进=合书、阶段=bong、轮到你=turn。
- 只改清单和资源，代码未改。离屏 QA（`qa-data/audio2`，不放自带音乐，`evidence/p0/audio2.log`）：16 个音效全部从项目资源加载成功；菜单曲、剧本曲（01104 Smoking Shadows）从项目资源播放；20 步自动对局中阶段、遭遇、混沌、抽牌、毁灭、伤害、恐惧、密谋音效都实际触发；无新报错（退出时的贴图泄漏报告主会话日志里同样有）。**听感仍未验证**（本机 QA 进程是哑音频驱动）。
- 待调：多阶段连播时（结束回合→敌人→维持→神话）每个横幅各响一次 bong，Yunke 听后如觉得吵可改为只响第一个；成功/失败、打牌、洗牌还没有触发点。

### 4.3 性能（11-19）

- 播放和解码在 Godot 音频线程，不占画面线程。原来能拖到画面的只有：切曲时在主线程读 4MB 的 MP3（实测约 13ms，一次性），以及每次牌桌刷新时的状态对比（实测平均 1.3ms）。
- 改动：切曲读文件挪到后台线程；音效启动时后台预解码；离屏 QA（`--background`）默认关闭全部音频，避免批量 AI 对局时每个进程都在解码音乐（测音频加 `--audio-qa`；任何场合 `--no-audio` / `ARKHAM_NO_AUDIO=1` 可关）。
- 已验证（`evidence/p0/audio3.log`、`audio3off.log`）：开音频时音乐、音效、切换正常；离屏默认 `AUDIO_READY off`。
- **逐帧实测（11-19 下午，`--frame-probe`，日志 `evidence/p0/probe-*.log`）**：本机离屏时 GPU 被图像生成占用，基线帧约 31–36ms。
  - 整局对比（进牌桌→自动对局 25 步→连续音效→回菜单，约 3 分钟）：>100ms 的帧 不开音频 42 / 改动前 33 / 改动后 45，同一水平；最长帧（1.0–1.5 秒）三组都有，是打开牌桌时牌桌自身的构建，不开音频那组也有 1204ms。
  - 静止主菜单只做音频操作（干净对照）：改动前切曲每次让那一帧多 11–14ms（45/42/42ms，基线 31ms），音效首次播放多最多 7ms；改动后切曲最多多 7ms（38ms，开始播放新 MP3 时 Godot 在主线程打开解码器），音效连播最多多 4ms，首次播放的额外开销消失。
  - 结论：音频不是可感知卡顿的来源（**已验证**，离屏、无真实声卡）；明显的卡顿（>100ms，打开牌桌约 1.2 秒）来自牌桌渲染/动画本身，与音频无关，已告知主会话。

### 4.4 选歌 / 切歌 / 设置即时预览（11-20，Yunke 试玩反馈 2、5）

- 曲目对照：我们的剧本→曲目表与 0712 安装版（C 盘 Programs\ArkhamHorror\dist）一致：聚集于此 01104 = Smoking Shadows，午夜假面 01120 = Midnight Pursuit，地底噬魔 01142 = Abyssal Confrontation，敦威治各章同 html。和 html 不同的只有我自己加的两处：菜单曲、狂热之夜战役主题都用了 html 里没分配的 Black Tide Choir（html 的幕间/战役页没有默认曲目）。
- 新增（照 html MusicControls.vue）：每个场合（主菜单 / 每个剧本 / 战役幕间 / 剧本结束）可单独选曲，默认"跟随剧本"；上一首 / 下一首（按 html 曲目表顺序，选中即记为该场合的曲目）；播放 / 暂停（保留进度）/ 停止；自动播放、循环开关；音乐音量。选择存 <数据目录>/music.json。
- 文件：新 app/Scripts/Audio/GameAudio.Picker.cs（接口）、app/Scripts/Audio/MusicPanel.cs（MusicControls 控件 + MusicPanel.Open() 对话框）；GameAudio.cs 改为"场合 + 覆盖 + 暂停"的播放逻辑；audio.json 加 html 的曲目顺序 / 循环分组 / 曲名。
- 设置对话框（SettingsDialog.cs 加 5 行）：音乐、音效音量拖动即时生效（音效滑块拖动时响一声点击），取消恢复原值；嵌入同一套选歌控件。
- 牌桌入口：主会话在顶栏放一个音乐图标，点击调用 MusicPanel.Open()；如要"一键切歌"可调 GameAudio.Next()；GameAudio.Playing / 事件 GameAudio.Changed 可用于图标状态。
- 已验证（离屏 QA evidence/p0/music1.log、截图 evidence/audio1/music-panel-*.png、settings-music.png、music-dropdown.png）：切歌、选歌、恢复默认、暂停后继续、停止后重播、选择在回菜单再进牌桌后保留、设置里音乐滑块 0.2 → 音乐总线 -28dB 并在取消后回到 -8.9dB。听感未验证（离屏无声卡）。

### 4.5 剧情旁白（11-21，讯飞「江阅尘-解说」，Yunke 提供、确认可商用）

- 来源：「江阅尘独读：基础与敦威治」正式版（桌面 Arkham-Narrator-v2）：575 个触发 key、524 段 WAV（2.35 小时）。524 段 SHA-256 与索引逐一核对一致；压缩包内外两份索引内容相同。
- 入库：转 Ogg（q4，保留原元数据含 AI 标识）放 app/assets/audio/voice/，51MB（原 WAV 411MB）；时长与索引逐一核对，误差 < 60ms。精简索引 voice/narration.json，来源说明 voice/NOTICE.md。原交付包未改动。
- 播放规则：剧情页（TableScreen.StoryDialog）显示时，按核心 Read 问题里的 locale key 依次朗读（#p1…#pN），核心标为不适用（InvalidEntry，页面变灰）的段落不读；只读剧情正文（Yunke 11-21：序章、引言、结局、幕间、尾声），卡牌风味文字和选项文字都不读（索引里的 420 条卡牌风味录音保留在资源里但不播放）。剧情的"条件"只是分支名，核心选哪支页面就只有哪支的 key，所以可直接播。卡面/卡背上需要游戏判定的条件句 44 条（如"调查员处于场景1或场景2""选择撤退"）暂不播。同段连读间隔按索引 extraGapMs，页内不同段之间 420ms。朗读时音乐再压低 7dB；换页立即改读新页，离开剧情页 / 回菜单即停止。
- 代码：新 app/Scripts/Audio/GameAudio.Narration.cs、NarrationControls.cs；GameAudio.cs（Voice 总线、音乐压低叠加、跟随换页）、GameAudio.Picker.cs（MusicPrefs 加 Narration / NarrationVolume 两个字段）、MusicPanel.cs（加"剧情朗读"一节）；牌桌只加一行：TableScreen.Panels.cs StoryDialog() 开头 GameAudio.Narrate(m);（备份 backup/TableScreen.Panels-20261121-before-narration.cs.txt）。
- 设置对话框：原想加一行"剧情朗读 开关 + 音量"，但加上后（连同新加的两行悬停设置）"取消/保存"被挤出 1080 高的屏幕，已撤回；朗读开关、音量、重播、停止在背景音乐面板里。设置对话框需要改成可滚动后再加。
- 已验证（离屏 evidence/p0/narr1~5.log）：序章页 1 个 key → 3 段录音按序读完（第 1 段为大标题，交付包未录）；中途点"继续"立即改读聚集于此引言；读完音乐恢复。修过一个换页后不朗读的问题（旧录音淡出时新录音被跳过）。听感未验证（离屏无声卡）。
- 未做：卡牌风味文字只在剧情页的阅读卡中播放（揭示遭遇卡、查看卡牌时不读，避免过吵，可做成开关）；条件句 44 条；第二循环以后没有录音。

### 4.5 目标 / 密谋页朗读（11-21）

- 卡牌数据里剧情和规则是分开的字段：flavor / back_flavor = 剧情，text / back_text = 规则。录音（讯飞「江阅尘-解说」）只录了剧情：正面 260 段、背面 160 段；规则部分只有 44 句条件句在 guarded 里，不播放。
- "新的目标 / 新的密谋 / 场景推进 / 密谋推进"页（TableScreen.Interlude）现在按页面顺序朗读：先读翻面的旧卡背面剧情，停 0.65 秒，再读新卡正面剧情；规则文字不读；关闭页面即停。新文件 app/Scripts/Audio/GameAudio.CardNarration.cs，牌桌只加一行（TableScreen.Reveal.cs:101）。
- 已验证（evidence/p0/cardvoice1.log、cardvoice2.log）：02045 正面单段；01108→01109 两段连读（41 秒 + 21.6 秒）读完自动结束；01105→01106 中途关闭即停。听感未验证（离屏无声卡）。

### 4.6 第二循环旁白：卡尔克萨之路（11-22，同一讯飞「江阅尘-解说」）

- 来源：「江阅尘独读-卡尔克萨之路」正式版（桌面 Arkham-Narrator-v2 交付目录，压缩包 `江阅尘独读-卡尔克萨之路-3b9c87e21e32.zip`，443 段 WAV，2 小时 34 分 46 秒）。压缩包内 `播放索引.json` 与桌面副本相同（sourceIndexSha256 9a5f62d9…）。
- 核对：443 段 WAV 的 SHA-256、采样率 / 位深 / 帧数、秒数全部与索引一致；合段试听示范的 SHA-256 一致。可商用已确认（11-22，Yunke 确认）：声音由 Yunke 购买讯飞 API 生成，条款明确可商用。交付说明本身未写可商用条款，以 Yunke 的确认为准。
- 入库：443 段转 Ogg（`-q:a 4`，`-map_metadata 0`，AIGC 标识保留，转码后时长与索引误差 0 秒）放进 `app/assets/audio/voice/`，并生成 Godot `.import`（voice/ 现 966 个 ogg）。bodyId `body-f7e0d8702185bf2a` 与第一循环重合，复用同一段。原 WAV 与交付包未改动。
- 索引合并：`voice/narration.json` 保留原 531 条 cue（基础与敦威治）不动，追加 464 条（剧情 111、卡牌风味 353）、15 条连读、秒数表；`guarded` 44 → 70（追加卡牌条件句与卡内叙事 26 条）；新增 `held` 28 条（附身尾声 2、萝拉席位序幕 2、选项 24）。key 冲突 0。备份：`backup/narration-20261122-before-carcosa/`。`voice/NOTICE.md` 已补卡尔克萨之路一节。
- 播放规则与第一循环相同，代码未改：只读剧情正文（序章、幕间、剧本引言与结局）和卡牌 flavor / back_flavor；按核心 Read 问题实际显示的 key 播放；不读规则字段、选项、流程文字（如「否则: 进入冒险 I」「如果萝拉…该调查员朗读」）；附身尾声（索引写明仅私下阅读）、萝拉席位序幕（座位条件）不自动播放。
- 已验证（离屏，`qa-data/ptc1` 存档，`--audio-qa`）：seq3 黄衣之王引用页读 1 段（`yellowSign.body#p1`；`#p2` 为引文署名，索引已排除）；seq4 序章正文 4 段连读（`prologue.body#p1`–`#p4`，`AUDIO_NARRATE keys=2 recordings=4`，唯一未录的 `prologue.otherwise` 为流程文字）；基础与敦威治 `qa-data/narr1` 回归：`keys=1 recordings=3`，与改动前日志一致。日志 `evidence/p0/carcosa-narr1.log`、`carcosa-narr2.log`、`base-narr-regress.log`。
- 未验证：听感（离屏无声卡）；卡牌风味在目标 / 密谋页对卡尔克萨的实际朗读；附身尾声与萝拉序幕的座位规则（暂不播）。

## 5. 需要 Yunke 决定或提供（11-19 下午更新）

已决定：音乐可用（群友开源）；同意下载 Kenney 音效包；联机**定为 EOS**（见 3.4，需准备的东西列在那里；Yunke：0712 网页版用过樱花穿透，普通用户成本太高，所以不走组网/穿透工具路线）。

以下为上午的原问题，留作记录：

1. **背景音乐来源**：0–5 循环那 52 首（哈希文件名）是从哪来的、能否随开源发布？上游 78 首同样需要确认。确认前只从数据目录读取，不进仓库。
2. **缺的音效怎么来**（二选一或组合）：
   - a. 允许我下载 Kenney 的 CC0 音效包（Casino Audio：发牌/洗牌/放牌/筹码；Interface Sounds：点击/确认；RPG Audio：翻书/布料；Impact Sounds 全集），授权清楚，可随仓库开源；
   - b. 按 `docs/AUDIO-PROMPTS-20261119.md` 的提示词由 Yunke 自己生成。
3. 菜单曲、结局曲（胜/负）选哪首。
4. 联机：先做局域网还是直接按"虚拟局域网 + 主机权威"一步到位；是否接受第 3.3 节第 1 步对牌桌调用处的机械替换（需要和主会话排期）。

### 4.6 章节音乐与解说音量（11-22）

- 进章节先放本章音乐（Yunke 11-22）：进入新章节（同一局换到新剧本，或读档进到某章）时，先从头播放这一章自己的曲目（audio.json 里的 `scenario:<编号>`），优先于以前给这一章选过的曲目；单曲循环下也只放一遍；顺序模式放完后的静音状态也会被新章节打破。玩家手动暂停/停止、或关了自动播放时不强制播放。这一章没有自己的曲目就按原规则。
- 放完后按原来的播放设置继续：顺序 / 剧本循环 / 全部循环（含随机）从本章曲目往下接；单曲循环回到进章节前在循环的那首。
- 上一首 / 下一首只在本次运行有效，不再写进 music.json 当作这一场合的选择（以前按“下一首”会被记成该章的选择，下次进这一章就不放本章音乐了——Yunke 的 music.json 里 01120、02041、02159 三条就是这样留下的；它们仍保留，但进章节时不再生效）。下拉框里明确选曲照旧保存。
- 解说音量独立：设置对话框加了“解说音量”滑条（拖动即听到，取消恢复，保存写入 music.json 的 NarrationVolume），和音乐音量、音效音量互不影响；音乐面板里剧情朗读那一行的标签改成“解说音量”。为放下这一行，设置对话框行距 18 → 12。
- 已验证（离屏 --audio-qa）：chapmus1（剧本循环+随机，01104 有旧选择）进《聚集于此》放 Smoking Shadows，放完随机接狂热之夜的其他曲；chapmus2（单曲循环）放完 Smoking Shadows 回到 Black Tide Choir 并继续循环；chapmus3 解说滑条 0.75→0.3 时 Voice 总线 -5.0→-20.9 dB、音乐不变，Esc 取消后回到 -5.0 dB；截图 evidence/noncore/chapmus-settings2.png 对话框完整。“放完”用 music-end 模拟，真实整首播完未等；听感未验证。
