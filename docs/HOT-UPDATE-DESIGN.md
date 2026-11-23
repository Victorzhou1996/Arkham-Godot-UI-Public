# 游戏内热更新 · 设计文档

2026-11-23 · 状态：方案已定（§12 是 Yunke 的决定）；本机部分（阶段 1–4）已实现并在本机验证，实施记录见 §14

---

## 0. 结论

- **更新来源**：继续用我们自己的两台服务器，两台都上传完整内容。
  - 国内线路：`<DOMESTIC_SERVER_IP>`
  - 海外线路：`<OVERSEAS_SERVER_HOST>`
  - 从哪条线路下载由玩家选：第一次检查更新时弹窗选"国内 / 海外"，之后在设置里可以改（§5.1）。
- **安全**：沿用 0712 旧版的"签名清单"做法。
  - 清单必须用我们的私钥签过，游戏只认签过名的。
  - 每个文件都按 SHA-256 校验。
  - 就算服务器被人改了，也推不了假更新。
- **只下载改动的部分**：先把现在"整块"的包拆开，按文件比对，只下载变化的文件。
  - 界面的小改动用"补丁资源包"发，通常只有几 MB 到几十 MB。
- **不在运行中替换文件**：所有更新先下载、校验、暂存。
  - 下次启动时（或点"重启并更新"）再切换。
  - 切换前留一份旧文件，出问题能退回。
- **存档永远不动**：更新只写安装目录。改了规则核心的更新，切换前先自动备份存档。
- **安卓**：
  - 数据、图片、文本、剧本这类内容可以热更新。
  - 程序代码和规则核心封在 APK 里，只能下载新 APK，再交给系统安装。
- **联机**：房间带上版本号。版本不一致时不能联机，先更新；单机不受影响，不更新也能玩。

---

## 1. 旧版（0712 网页前端本地 exe）是怎么做的

参考代码（只读，不改）：
- `ArkhamHorrorLocal-WinDeploy-20260712/vue-dll-green-20261027/ClientChannel.cs`
- `.../server-r4-direct-fix1/Program.ClientUpdate.cs`
- `acceptance/build-storage-20261120/stable/*/manifest.json`

| 项 | 旧版做法 | 新版是否沿用 |
|---|---|---|
| 两条线路 | `ClientChannel`：海外 / 国内，各有一个清单地址，玩家选一条记在 `server-line.json` | ✅ 沿用：弹窗让玩家选，记住选择，设置里可改 |
| 签名清单 | `{schemaVersion, keyId, payload(base64 JSON), signature}`。RSA 公钥写在包里的 `update-policy.json` | ✅ 沿用格式；新版单独用一把新钥匙 |
| 文件列表 | payload 里的 `files: [{path, bytes, sha256}]`，约 2 万个文件 | ✅ 沿用 |
| 版本门槛 | `acceptedBaseReleases`：哪些旧版本可以直接升上来 | ✅ 沿用 |
| 核心兼容 | `coreSourceId` + `acceptedPreviousCoreSourceIds` + `saveCompatibility` | ✅ 沿用，用来判断旧存档能不能继续玩 |
| 更新说明 | `releaseNotes` | ✅ 沿用，在更新窗口里显示 |
| 流程 | 检查 → 准备（下载、暂存）→ 正常关闭后切换；`updates/recovery/` 留旧文件 | ✅ 沿用 |
| 更新程序 | 单独的 `Arkham.ReleaseUpdater`（要系统里装 .NET） | ⚠️ 改成新版自带的小程序，不依赖系统 .NET |

---

## 2. 现在的 Win 包结构，以及问题出在哪

以 `exports/win-20261122/ArkhamUI-New-win64/` 为例：

| 部分 | 大小 | 内容 | 多久变一次 |
|---|---|---|---|
| `诡镇奇谈.exe` | 874 MB | Godot 引擎 + **整个 res:// 资源包（嵌在 exe 里，约 777 MB）** | 几乎每次 |
| `data_ArkhamApp_windows_x86_64/` | 103 MB | .NET 运行时 + 我们的代码（`ArkhamApp.dll`、`Gathering.Engine.dll`），以及 EOS SDK | 改代码时 |
| `game/runtime/` | 349 MB | 规则核心 `arkham_core.dll` + `core.json`（sourceId） | 很少（只在重编核心时） |
| `game/artstore/` | 532 MB | 压缩卡图库，7109 个文件 | 很少 |
| `game/assets/` | 117 MB | 宽幅立绘、背景、牌组数据 | 偶尔 |
| `game/ai/` | 11 MB | AI 估值和策略 | 训练后 |

**问题**：资源包嵌在 exe 里，改一个字、换一张图，exe 就整个变了。按文件比对的话，每次都要重新下载 874 MB。

---

## 3. 新版包结构

```
诡镇奇谈/
├─ 诡镇奇谈.exe              Godot 引擎本体（不再嵌资源包）：只在升级 Godot 版本时变
├─ 诡镇奇谈.pck              基础资源包：大版本时才换
├─ data_ArkhamApp_windows_x86_64/   .NET 运行时 + 我们的代码 DLL
└─ game/
   ├─ version.json           本机当前版本 + 每个文件的哈希缓存（启动时不用重新算 2 万个哈希）
   ├─ updater/apply-update.ps1   更新助手：只负责切换 exe / 代码 / 核心（见 §5.3）
   ├─ patches/               补丁资源包，按顺序叠加在基础包上
   │   ├─ 0001.pck
   │   └─ 0002.pck
   ├─ runtime/  artstore/  assets/  ai/   （同现在）
   └─ updates/               更新工作目录：staging（暂存）/ recovery（旧文件备份）/ log
```

**要做的改动：**
1. `export_presets.cfg` 里设 `binary_format/embed_pck=false`，让资源包成为独立文件。
2. 补丁资源包：
   - 每次发版只把改过的 res:// 资源导出成一个小 `.pck`。用 Godot 的补丁导出，4.3 起支持只导出与基础包不同的资源（4.5.1 上需要实测）。
   - 游戏启动时，在加载任何场景之前，按编号依次调用 `ProjectSettings.LoadResourcePack(patch, replaceFiles: true)`。
   - 补丁攒多了（比如超过 10 个或 200 MB），下一个大版本就合并成新的基础包。
3. **启动入口要挪到最前面**：
   - 现在 `App` 一启动就加载主菜单。
   - 改成先跑一个极小的启动脚本：应用暂存的更新 → 挂补丁包 → 再进主菜单。
   - 启动脚本本身也可能被补丁改，所以它必须只用基础包里的东西。

**为什么不把大资源全部搬出 res://？** 那要改 59 处读图、读字体的代码，牵连太大。补丁包能达到同样效果，改动集中在启动这一处。

---

## 4. 更新清单（服务器上的 `manifest.json`）

沿用旧版外层格式：

```json
{ "schemaVersion": 1, "keyId": "arkham-ui-2026xxxx-xxxxxxxx", "payload": "<base64 JSON>", "signature": "<base64 RSA-PSS-SHA256>" }
```

payload 内容：

```json
{
  "schemaVersion": 1,
  "product": "arkham-ui",
  "platform": "win64",                       // win64 / android
  "release": "2026.11.30.1",                 // 版本号：日期 + 当天序号
  "acceptedBaseReleases": ["2026.11.23.1", "..."],
  "minimumRelease": "2026.11.23.1",          // 低于它的版本只能下完整包（比如基础 pck 换了）
  "mandatory": false,                         // true：不更新就不能联机（核心变了的时候）
  "coreSourceId": "arkham-core-sha256:…",
  "acceptedPreviousCoreSourceIds": ["…"],    // 旧存档还能接着玩的核心版本
  "saveCompatibility": "same-core | reviewed-import-v1 | breaks",
  "files": [
    { "path": "game/patches/0002.pck", "bytes": 4194304, "sha256": "…", "layer": "data" },
    { "path": "data_ArkhamApp_windows_x86_64/ArkhamApp.dll", "bytes": …, "sha256": "…", "layer": "code" },
    { "path": "game/runtime/arkham_core.dll", "bytes": …, "sha256": "…", "layer": "core" }
  ],
  "removed": ["game/patches/0001.pck"],      // 这一版要删掉的旧文件（先移到 recovery，不直接删）
  "apk": null,                                // 安卓版专用，见 §6
  "releaseNotes": ["修复：……", "新增：……"]
}
```

**几点说明：**
- **`layer`** 决定怎么切换：
  - `data`：游戏下次启动时自己切换。
  - `code` / `core` / `engine`：交给更新助手，在游戏退出后切换。
- **服务器上的文件按哈希存放**，路径是 `objects/<sha256 前两位>/<sha256>`。
  - 同一个文件在不同版本之间只存一份。
  - 下载时也只下载本机没有的。
- **`removed`** 里的旧文件不直接删，先移进 `updates/recovery/`。这和项目"不删除，备份"的规矩一致。

---

## 5. 客户端流程

### 5.1 检查

- **什么时候检查**：每次启动，在主菜单出现后、在后台检查，不挡玩家操作。另外"设置"里有手动的"检查更新"。
- **选择线路**（Yunke 11-23：两台都上传，从哪台下载由玩家选）：
  - 第一次检查更新前弹窗："选择更新线路：国内（中国大陆推荐）/ 海外"。
  - 选择记在 `%APPDATA%\ArkhamUI-New\userdata\update-line.json`，以后不再问；设置页可以改。
  - 只向选中的线路请求 `manifest.json`，超时 8 秒。
  - 失败时只提示一行"无法连接国内线路"，并给一个"改用海外线路"按钮（反之亦然），不自动切换。
- **验签**：用包里 `update-policy.json` 的公钥验证。不通过就当作没有更新，并记日志。
- **比对**：用 `version.json` 的哈希缓存和清单比较，得出要下载的文件和总大小。

### 5.2 下载与暂存

- 主菜单右上角显示一个小提示"有新版本 · 约 23 MB"，点开看更新说明，再点"下载"。
- 下载到 `game/updates/staging/<release>/`：
  - 每个文件下完立刻校验 SHA-256，不对就重下一次；还不对就停下，提示可以换另一条线路再试。
  - 支持断点续传，已经下好的文件不重下。
- 全部下完、全部校验通过后，写一个 `ready.json`，提示"更新已准备好 · 重启后生效"，并给出"现在重启"按钮。
- 玩家正在对局中时不打扰，只在主菜单显示。

### 5.3 切换

- **只有 data 层**（最常见：补丁包、卡图、AI、剧本数据）：
  1. 下次启动时，启动脚本先把要替换的旧文件移到 `updates/recovery/<release>/`。
  2. 再把暂存的新文件移过去，写新的 `version.json`。
  3. 然后才挂补丁包、进主菜单。
  4. 这一步在任何资源被打开之前做，所以不存在"文件被占用"的问题。
- **含 code / core / engine 层**：
  1. 游戏启动更新助手（`game/updater/apply-update.ps1`，先复制到 `game/updates/helper-run.ps1` 再运行），然后自己退出。
  2. 更新助手等游戏进程结束，再像上面一样"旧文件移进 recovery → 新文件移入"。
  3. 完成后重新启动游戏。
  - 更新助手是一个 PowerShell 脚本（Windows 10/11 都自带 PowerShell 5.1），不需要另外编译、不依赖系统 .NET。脚本只用 ASCII 字符，避免 PowerShell 5.1 按系统编码读错。
  - 更新助手自己需要更新时照常随 data 层更新；运行的是复制出来的那份，所以不会"自己改自己"。
- **失败与回退**：
  - 任何一步失败，都把 recovery 里的文件移回原位，恢复原样。
  - 新版本连续两次启动失败（没走到主菜单），下次启动自动回退到上一版，并提示"更新后启动失败，已退回 X 版本"。
  - recovery 只保留最近 2 个版本，更早的删除（否则安装目录会越来越大）。

### 5.4 存档安全

- 打包版的存档和设置在 `%APPDATA%\ArkhamUI-New\userdata`（`App.cs` 里的 DataRoot），不在安装目录里，更新程序**不读不写**那里。
- 新版本的 `coreSourceId` 变了时，切换前先把存档目录整个复制到 `userdata/backup/before-<release>/`。
- 新核心认不出的旧存档（`saveCompatibility = breaks`）：
  - 在"继续调查"里标成"旧版本存档"，不能继续，但可以导出。
  - 更新窗口里会提前写明这一点，让玩家自己决定要不要更新。

---

## 6. 安卓

现在的全量 APK 结构（见 `docs/MOBILE-BUILD-PLAN-20261122.md`）：
- **APK 里面**：Godot 引擎、.NET 代码，以及规则核心 `libarkham_core.so`。核心直接从 APK 里加载，不会解压成文件。
- **可写目录**：游戏数据放在 `Android/data/com.yunke.arkhamui/files/root`。

| 内容 | 能否热更新 | 做法 |
|---|---|---|
| `root/` 下的数据：artstore、assets、ai、剧本数据 | ✅ | 和 Win 的 data 层完全一样 |
| res:// 资源（界面图片、文本、场景） | ✅ | 补丁 pck 放进 `root/patches/`，启动时挂上（`LoadResourcePack` 在安卓上可用） |
| .NET 代码、规则核心、Godot 引擎 | ❌ 必须换 APK | 下载新 APK，再交给系统安装 |

**安卓上换 APK 的流程：**
1. 清单的 `apk` 字段给出 `{versionCode, bytes, sha256, path}`。
2. 游戏下载到私有目录，校验 SHA-256，再通过系统安装界面安装（需要 `REQUEST_INSTALL_PACKAGES` 权限和一个 FileProvider，要在安卓插件里加几行 Java）。
3. 第一次安装时，系统会让玩家允许"安装未知应用"，这一步只能玩家自己点。

⚠️ **签名钥匙必须固定**：新 APK 必须和已安装的 APK 用同一把签名钥匙，否则系统拒绝覆盖安装，玩家只能卸载重装，存档也会跟着丢。现在的 APK 文件名是 `arkham-debug.apk`，可能用的是本机的调试钥匙。正式开放热更新前，要生成一把正式钥匙并妥善备份。下一次打 APK 时一起处理（按约定，等 Yunke 要求再打）。

---

## 7. 联机版本检查

Yunke 11-23：不更新就不能联机，单机可以继续玩。

- **有新版本没装**：主菜单的"联机"按钮变灰，提示"有新版本，更新后才能联机"，点一下直接打开更新窗口。"继续调查""开始新的调查""牌组工坊"都照常可用。
- **检查失败**（两条线路都连不上）：不因此禁止联机，交给下面的房间版本比对。
- **房间版本比对**：建房时，房间属性里加一项 `VER = <release>|<coreSourceId>`。加入时完整比对，任何不同都不能加入，提示"你的版本（X）和房主（Y）不同，请先更新"。版本较旧的一方会看到"更新"按钮。
- 继续联机存档时同样检查。
- 这样可以避免"规则核心不一致 → 结算对不上"这种最难查的问题。

---

## 8. 安全

- **签名私钥**：
  - 放在 `C:\Users\victo\.arkham\update-signing-key.pem`，和 EOS 配置一样不进项目目录、不打进任何包、不打印、不进日志。
  - 另外由 Yunke 自己离线备份一份（丢了以后就发不了更新）。
- **公钥**：编译在程序里（`app/Scripts/Update/UpdatePolicy.cs`，钥匙编号 `arkham-ui-f58eb0ae28b1`），另存一份在 `tools/release/update-public-key.pem`。换钥匙只能发完整新包。
- **新钥匙**（Yunke 11-23 确认）：不复用旧版 `arkham-win-20261104-…` 那把，新版单独生成一把，互不影响。
- **传输**：
  - 国内线路如果只能用 IP 加自签证书，HTTPS 证书校验可以放宽，因为安全靠签名和哈希保证，不靠证书。
  - 海外线路正常校验证书。
- **路径检查**：清单里的路径必须是安装目录内的相对路径，不允许 `..`、绝对路径或盘符，不允许指向 `userdata`。沿用旧版 `ReleaseRuntime.Resolve` 的检查方式。
- **EOS 密封配置** `app/online/eos.dat`：照常随包更新，不受影响。

---

## 9. 服务器端

```
/srv/arkham-ui-updates/
├─ win64/manifest.json
├─ android/manifest.json
├─ objects/ab/ab12…        所有版本共用，按哈希存放
└─ full/                   完整安装包（新玩家、或版本太旧时下载）
```

- 两台服务器都上传，内容完全一样（Yunke 11-23）。用 nginx 静态目录对外提供，不碰现有 Arkham 服务和数据库（与旧版发布页同样的隔离方式）。
- **发布工具** `tools/release/`（新写）：
  1. `make_manifest.py`：扫描导出好的包目录，生成 payload，计算哈希，和上一版比对。
  2. `sign_manifest.py`：用私钥签名。只在本机运行，私钥不离开这台电脑。
  3. `publish.ps1`：先上传缺少的 objects，**最后**才上传 manifest，所以玩家不会拿到"清单有、文件还没传完"的版本。两台服务器依次发布，发布后用公网地址各下载一次清单并验签。
- **每次发布前都需要 Yunke 明确同意**（发布对外可见）。

**带宽估计**：
- 完整包约 2 GB，新玩家走 `full/`。
- 日常更新以补丁 pck 为主，几 MB 到几十 MB。
- 国内服务器如果带宽小、玩家又多，可以把 `objects/` 和 `full/` 放到对象存储（比如腾讯云 COS）+ CDN，服务器上只放 `manifest.json`。清单里的地址不用改，只是服务器把请求转过去。这一步等真的慢了再做。

---

## 10. 界面（中文文案草稿）

| 位置 | 文案 |
|---|---|
| 主菜单右上角小标签 | 有新版本 · 约 23 MB |
| 更新窗口标题 | 新版本 2026.11.30.1 |
| 更新窗口按钮 | 下载更新 / 以后再说 |
| 选线弹窗 | 选择更新线路　[国内（中国大陆推荐）]　[海外]　·　以后可以在设置里更改 |
| 下载中 | 正在下载 12 / 23 MB（国内线路） |
| 下载完成 | 更新已准备好，重启后生效　[现在重启]　[下次启动时] |
| 联机按钮（未更新） | 有新版本，更新后才能联机 |
| 核心变化提示 | 这次更新包含规则核心变化。更新前会自动备份存档。 |
| 不兼容存档 | 以下存档来自旧版本规则，更新后不能继续，但可以先导出保存 |
| 回退提示 | 更新后启动失败，已退回 2026.11.23.1 |
| 检查失败 | 无法连接国内线路　[改用海外线路] |
| 设置页 | 当前版本 2026.11.23.1　更新线路：国内 ▾　[检查更新] |

风格沿用现有的金色悬停、黑色渐变，不加新特效。

---

## 11. 分阶段实施与验证

| 阶段 | 内容 | 怎么验证（都在本机离屏完成） |
|---|---|---|
| 1 | 包拆分：`embed_pck=false`、`version.json`、启动脚本挂补丁 pck | 导出拆分版，用一个改过一张图的补丁 pck 启动，截图确认换上了；不带补丁时和原来一样 |
| 2 | 清单生成和签名工具；客户端检查、下载、校验、暂存、data 层切换和回退 | 在本机 127.0.0.1 起一个假的更新服务器：正常更新、哈希错误、签名错误、下载中断后续传、第一条线路失败换第二条、启动失败自动回退 |
| 3 | 更新助手（code / core / engine 层） | 假服务器发一个只改 `ArkhamApp.dll` 的版本，确认游戏退出、替换、重启，版本号正确；中途强杀更新助手，再启动能回退 |
| 4 | 联机版本检查 | 两个实例，用本地连接 `--online-loop` 测：版本相同能进，核心不同被拒并提示 |
| 5 | 服务器部署 | **需要 Yunke 同意**。两台服务器建目录和 nginx 路由，发一个测试版本，用打包好的 exe 走一次真实更新 |
| 6 | 安卓 | 数据层和补丁 pck 同 Win；APK 下载和系统安装需要正式签名钥匙。**等 Yunke 要求打 APK 时再做**，真机验证 |

阶段 1 到 4 完全在本机，不碰服务器，可以先做。阶段 5、6 各自需要 Yunke 点头。

---

## 12. Yunke 的决定（2026-11-23）

| 问题 | 决定 |
|---|---|
| 服务器 | 两台都上传；玩家在弹窗里选从国内还是海外下载（§5.1） |
| 签名钥匙 | 新生成一把 |
| 强制更新 | 不更新就不能联机，单机照常（§7） |
| 检查时机 | 每次启动时检查 |
| 安卓正式签名 | 下次打 APK 时换成正式钥匙 + 正式版导出（见 §13）。换之前提醒玩家先导出存档 |

还没定的：服务器上传权限（阶段 5 时再问）。

---

## 13. 安卓签名现状（2026-11-23 查证）

- **签名钥匙**：调试钥匙。
  - 文件是 `G:\AHMobile-20261122\tools\debug.keystore`，别名 `androiddebugkey`，证书 `CN=Android Debug`。
  - 2026-11-22 在这台电脑上生成，有效到 2054 年。
  - 密码是安卓通用的公开默认密码。
- **导出方式**：`scripts/export-apk.sh` 用 `--export-debug`，导出的是"调试版"。
- **签名方案**：只有 v2。证书 SHA-256 是 `5f0501ab…2d00f07`。

**换成正式签名的区别：**

| | 现在（调试钥匙 + 调试版） | 正式钥匙 + 正式版 |
|---|---|---|
| 别人能不能冒充 | 能。密码是公开的，谁拿到这个钥匙文件，就能签一个能覆盖安装我们游戏的 APK | 不能。钥匙有自己的密码，只在这台电脑上，另做离线备份 |
| 钥匙丢了会怎样 | 这个文件夹被清理或换电脑后，新 APK 装不上去，玩家只能卸载重装 | 一样，所以必须备份。正式钥匙会被当成重要文件专门保管 |
| 运行 | 调试版带调试功能，略慢、略大；手机上能用 adb 直接读出应用的私有数据 | 正式版更快一些，不能被 adb 随意读取 |
| 热更新 | 能用，但上面两个风险一直在 | 推荐 |

**换钥匙的代价**：只有一次。旧 APK 和新 APK 签名不同，系统不让覆盖安装，必须先卸载。卸载会清掉手机上的存档（在 `Android/data/...` 里），所以换之前要先用"导出"把存档存出来。

**结论**：现在装了这个 APK 的人还很少，现在换代价最小，越晚换要卸载重装的人越多。建议下次打 APK 时一起换。

---

## 14. 实施记录（2026-11-23）

### 已实现

| 部分 | 文件 |
|---|---|
| 发布工具：生成钥匙、扫描包、复制对象、生成并签名清单、写包内 `version.json`、校验 | `tools/release/update_release.py`（`keygen` / `build` / `verify`） |
| 签名钥匙 | 私钥 `C:\Users\victo\.arkham\update-signing-key.pem`（只在这台电脑上，**请 Yunke 另做离线备份**）；公钥见 §8 |
| 客户端：检查、比对、下载、校验、暂存、启动时切换、回退、补丁 pck 挂载 | `app/Scripts/Update/Updater.cs` |
| 更新助手 | `tools/release/apply-update.ps1`（打包时放进 `game/updater/`） |
| 界面：选线弹窗、主菜单右上角标签、更新窗口、设置页"游戏版本"一行 | `app/Scripts/Update/UpdateUi.cs`、`MainMenu.cs`、`SettingsDialog.cs` |
| 联机：有新版本未装时联机入口变灰；房间和成员带 `VER`（版本号 \| 核心 sourceId），加入时比对，房主开局前也比对 | `Online.cs`、`EosOnlineService.cs`、`OnlineGame.cs` |
| 导出设置 | `app/export_presets.cfg`：`embed_pck=false`（资源包不再嵌进 exe） |

和设计稿不同的地方：
- 联机入口在有新版本时是变灰并提示"有新版本，更新后才能联机（主菜单右上角）"，点击不会直接打开更新窗口；更新入口在主菜单右上角。
- 开发版（编辑器运行）不检查更新，设置页显示"开发版（不检查更新）"；联机比对时开发版只比核心。
- 安卓暂不启用（等打 APK 时做阶段 6）。

### 已验证（本机，假安装目录 `qa-update/u1/` + 本机测试服务器）

| 场景 | 结果 |
|---|---|
| 检查：验签、逐文件比对，得出 2 个文件、1 个删除 | ✅ 截图 `evidence/update/u1-offer.png` |
| 有新版本时"加入联机游戏"变灰；更新后恢复 | ✅ 日志 `ONLINE_GATE_CLOSED`，更新后 `online=True` |
| 下载 → 暂存 → 下次启动自己切换，旧文件进 recovery，staging 清掉 | ✅ 截图 `u1-ready.png`；`已更新到 2026.11.23.2` |
| 两次启动失败 → 第三次自动退回，坏版本不再提供 | ✅ 文件回到上一版，提示"新版本 … 启动失败，已退回原版本" |
| 代码层更新：更新助手在游戏退出后替换文件 → 自动重启游戏 → 第一次启动通过 | ✅ `helper apply ok=True` → `boot ok 2026.11.23.4` |
| 清单被篡改 | ✅ "清单签名校验失败"，不提供更新 |
| 服务器上的文件被篡改 | ✅ 重试一次后"下载失败：文件校验失败"，安装目录不变 |
| 补丁 pck：启动时挂载，res:// 里读到补丁内容 | ✅ `patch 0001.pck loaded` |

### 还没做 / 没验证

- **打包流程**（下次打包时做）：
  1. 导出后把 `ArkhamUI.exe` / `ArkhamUI.pck` 改名为 `诡镇奇谈.exe` / `诡镇奇谈.pck`（Godot 按 exe 同名找 pck）。
  2. 把 `tools/release/apply-update.ps1` 放进 `game/updater/`。
  3. 运行 `update_release.py build`，写出包内 `game/version.json` 和服务器文件夹。
- 用 Godot `--export-patch` 生成真正的补丁 pck（目前只用测试补丁验证了挂载）。
- 用真正导出的 exe 走一遍完整更新（含更新助手替换 exe 和 pck）。
- 阶段 5：两台服务器部署（需要 Yunke 同意和上传权限）；阶段 6：安卓。

## 15. 第一次真实部署（2026-11-23 晚，Yunke："放更新包到kaho，我等下要用新版实验热更新功能"）
- **服务器（海外线路）**：kaho <OVERSEAS_SERVER_IP> / <OVERSEAS_SERVER_HOST>。
  - 在 `/etc/nginx/sites-enabled/kaho-sites` 的 arkham 那个 server 块加了 `location ^~ /ui-updates/ { alias /srv/arkham-ui-updates/; … }`。改前备份到 `/home/ubuntu/kaho-sites.bak-20261123-ui-updates`，`nginx -t` 通过后 reload；网页版首页仍然返回 200。
  - 目录 `/srv/arkham-ui-updates/` 归 ubuntu 用户。
  - 国内服务器 <DOMESTIC_SERVER_IP> 还没部署，测试时线路选"海外"。
- **两个版本**（只差这次的梦境结晶器修复，开关 `TableScreen.CommitFromElsewhere`）：
  - 底版 2026.11.23.1：`exports/win-20261123/r1/ArkhamUI-New-win64`，修复关闭，本地生成，没有上传。
  - 更新版 2026.11.23.2：`exports/win-20261123/r2/ArkhamUI-New-win64`。服务器上放了签名清单 `win64/manifest.json`、`win64/releases/2026.11.23.2.json`，以及与底版不同的唯一一个文件的对象（`data_ArkhamApp_windows_x86_64/ArkhamApp.dll`，3,081,216 字节，属于程序代码层）。
  - **注意**：服务器上只有这个差异对象，所以只有底版能更新到 2026.11.23.2。正式发布前要上传完整对象集（本地 `exports/win-20261123/server-kaho/objects`，约 3GB），或者只覆盖玩家可能的底版。
- **包的组装**：脚本在 scratchpad 的 `assemble_hu.py`，布局如下（`embed_pck=false`）：
  - 导出的 exe 和 pck 改名为 `诡镇奇谈.exe` / `诡镇奇谈.pck`，加 `data_*` 目录；
  - `game/assets` 放按路径读取的 8 个目录；
  - `game/runtime` 放已部署的核心 b25aded6；
  - `game/artstore`、`game/ai`；
  - `game/updater/apply-update.ps1`。
- **已验证**（用底版的副本 `exports/win-20261123/r1-test` 离屏跑）：
  1. 线路选海外，检查到更新：1 个文件，需要更新助手。
  2. 下载 3MB。
  3. 助手替换 dll，游戏重启（`helper apply ok=True`）。
  4. `boot ok 2026.11.23.2`，状态 UpToDate，联机解锁。
  5. 公网下载的清单和对象与本地逐字节一致，签名有效。

## 16. 第二次发布 2026.11.23.3（2026-11-23 晚，Yunke："把存档导出也带上侧卡组和升级记录，这个功能写成服务器上的热更新包"）
- 内容：存档导出 / 发送到手机时，把 `setups/<存档id>*` 的旁文件（侧卡组、局间升级记录、升级元数据、经验结余、剧情、创伤）放进导出文件的 `arkhamuiFiles` 字段；导入时拿掉字段再交给核心，按新 id 写回（`SaveTransfer.cs`）。
- 包：`exports/win-20261123/r3b/ArkhamUI-New-win64`（导出 `r3b-build`，日志 `r3b-export.log`；第一次导出 `r3-build` 因没设 .NET 环境变量失败，留着没删）。导出要用和 `tools/dev/build.ps1` 一样的 DOTNET_ROOT 等环境变量，脚本见本次会话 scratchpad `export_r3.ps1`。AI 模型沿用 .2 包里的。
- 与 .2 逐文件比对：只有 `data_ArkhamApp_windows_x86_64/ArkhamApp.dll` 不同（3,085,312 字节，sha256 12d330f6…）。服务器上只新增这一个对象，所以 .1 和 .2 都能直接更新到 .3。
- 这个 dll 还包含 .2 之后其他会话改的 `TableScreen.Panels.cs` / `.Board.cs` / `.Layout.cs`；**不含**安卓热更新会话 19:17 起对 `Updater.cs` / `UpdateUi.cs` 的改动（dll 里没有 NeedsApk 等字符串）。
- 已验证：打包后的程序离屏启动正常、导出带 8 个旁文件；公网下载的清单和对象与本地逐字节一致，签名有效。**没有**用装好的 .2 实际走一遍更新（Yunke 稍后验证）。
- 服务器上旧清单备份在 `/home/ubuntu/ui-manifest-2026.11.23.2.bak.json`；本地 `server-kaho/win64` 改前备份在 `backup/sidedeck-20261123/server-kaho-win64-before-r3/`。

## 17. 安卓热更新（2026-11-23 晚，Yunke："把安卓的热更新也做出来"）
- **代码**：`Updater.cs`、`UpdateUi.cs`、`tools/release/update_release.py`，改前备份在 `backup/android-update-20261123/`。
  - 安卓下 platform = android。安装根目录是解包后的 files/root，`version.json`、`updates/`、`patches/` 都直接放在这个根目录下。
  - **数据层**：增量下载；点"退出游戏"后，下次打开时切换。
  - **程序或核心变了**：清单里有 `apk {release, path, bytes, sha256}`，并且比本机 APK 新（本机 APK 的 release 来自 `res://ahm-bundle/bundle.json` 的 "release"）时，提示"下载安装包"，用浏览器打开 `<线路>/full/android/arkham-ui-<release>.apk`，由系统覆盖安装。这要求每次都用同一把正式签名钥匙。
  - 安卓上大小一致就信任 version.json 里的哈希，因为解包后文件修改时间会变。
  - 服务器上没有对应平台的清单（404）时，视为已是最新。
  - 发布工具：`--platform android --apk FILE [--apk-release R]`。
- **kaho nginx**：`/ui-updates/` 加了 apk 的 MIME（application/vnd.android.package-archive），改前备份在 `/home/ubuntu/kaho-sites.bak-20261123-apkmime`。
- **已验证**（电脑上用 `--update-platform=android` 模拟，本机测试服务器 `qa-update/and/`）：
  - 数据更新：2026.11.23.10 → .11，下载 → 退出 → 重开时切换，`boot ok`（`evidence/p0/and-c.log`、`and-d.log`）。
  - 新 APK 提示和打开下载地址（`and-e.log`、`evidence/update/and-e1.png`）。
- **没验证**：真机上的浏览器下载和系统安装；服务器上还没有发布安卓清单。
- **版本号**：2026.11.23.3 已被侧卡组会话发布（第 16 节）。这次的安装包用 **2026.11.23.4**：电脑版是 `exports/win-20261123/r4/` 和自解压 exe，APK 由手机会话用同号打包。电脑版和手机版要同号，联机版本才对得上。

## 18. 第三次发布 2026.11.23.5（2026-11-23 晚，Yunke："全部做完…发更新包到两个服务器，我要用更新功能来测试"）
- **内容**：新手教程 v2（发光/涟漪引导、固定换牌、检定结果停留到点击、点头像/地图小卡切视角）、卡尔克萨之路去掉 BETA、手机排版会话的改动。
- **pck 问题和补丁包**：这次 res:// 只变了 3 个文件（`locale-zh.json`、新脚本 `TutorialRipple.cs`、`.godot/uid_cache.bin`），但整份导出的 `诡镇奇谈.pck`（1.5GB）哈希全变，一次更新要下 1.5GB；国内服务器上行只有约 25KB/s，根本传不上去。
  - 改用 Godot 4.5 的补丁导出：`--export-patch "Windows Desktop" 0001.pck --patches <底版 pck>`（脚本见本次会话 scratchpad `export-patch.ps1`），得到 2.9MB 的补丁，正好是那 3 个文件。
  - 包 `exports/win-20261123/r5b/ArkhamUI-New-win64`：底版 pck 直接用 r4 的（逐字节相同），补丁放 `game/patches/0001.pck`，Updater 启动时加载（日志 `UPDATE patch 0001.pck loaded`）。
  - 相对 .1–.4 任何一个底版都只差 4 个对象（ArkhamApp.dll、Gathering.Engine.dll、locale-zh.json、0001.pck），约 8.7MB。
  - 以后每次发布都要这样：底版 pck 不动，补丁按 0002、0003… 递增（或者合并成一个相对底版的补丁）。发布工具还没有自动化这一步。
  - 先发过一次整包版 .5（含 1.5GB pck），随后同号换成补丁版；整包版清单备份在 `backup/r5-fullpck-manifest/` 和 kaho `/tmp/win64-manifest-r5-fullpck.json`。kaho 上那个 1.5GB 对象 `objects/13/13d0a907…` 已不被引用，可以清理（kaho 磁盘只剩约 2GB）。
- **海外 kaho**：win64 清单 + 对象；安卓清单 `android/manifest.json` + `full/android/arkham-ui-2026.11.23.5.apk`（2.6GB，手机会话打包，正式签名）。安卓数据对象（708MB）没传，这次 .4→.5 走整包 APK，用不到。
- **国内 <DOMESTIC_SERVER_IP>**：root + `~/.ssh/codex_ed25519`。nginx `sites-available/arkham-client-cn-https` 加了 `location ^~ /ui-updates/`（备份 `/root/nginx-backup-20261123/`），reload 后网页版首页、旧客户端更新清单都仍是 200。对象由国内服务器直接从 kaho 拉取并校验 sha256（约 325KB/s，比本机上传快）。只发布了 win64；**安卓没发**：磁盘只剩约 5GB，放不下 2.6GB 的 APK，所以国内线路的安卓清单是 404，客户端视为已是最新。
- **已验证**：
  - 两台服务器上的 win64 清单与本地逐字节一致，签名有效；kaho 公网对象哈希一致。
  - r5b 包离屏启动：补丁加载、新手教程能进到选领队（`r5b-smoke2.log`）。
  - 整包版用 .4 副本走了一遍真实更新：下载 1.5GB → 助手替换 → version.json 变成 .5（`r5-up.log`）。
  - 安卓模拟（`--update-platform=android --qa-apk-release=2026.11.23.4`）连 kaho：提示新 APK 2.6GB，地址正确（`evidence/p0/and5-b.log`）；kaho 返回 APK 的 MIME 正确。
- **没验证**：补丁版用真实助手从 .4 更新的全过程（Yunke 测）；真机下载安装 APK；国内线路从本机下载很慢，只在服务器本机核对。

## 19. 2026.11.23.6（2026-11-23 夜，Yunke："这两个改动应该不用全量更新包？直接做成增量更新包放到kaho上"）
- 内容：设置里"界面缩放"改名"显示比例"；新增"缩放"（50%–150%，整个画面绕屏幕中心缩小或放大，实现方式是根视口的 `CanvasTransform`，所以布局、点击和 GetGlobalRect 都不受影响；换屏时那张定格截图放到 CanvasLayer 上，避免缩两次）；04 及以后的战役选人直接进该战役的群像（`PrepareScreen.OpenPicker`）。改前备份在 `backup/zoom-lineup-20261123/`。
- 只改了已有的 .cs 文件，所以重新导出后 pck 的条目和 .5 完全相同，exe 也相同，只有 `ArkhamApp.dll` 变了。包 `exports/win-20261123/r6/` = r5b + 新的 dll。从 .5 升级只要 1 个对象（3MB），从 .1–.4 升级约 8.3MB。
- 已发布到 kaho，公网清单和本地一致，签名有效。国内线路仍停在 .5。
- **已验证**：用 .4 安装的副本（`r6-uptest`）在海外线路检查更新，提示 4 个文件、8.7MB；下载后助手替换文件并重启。再启动时 `patch 0001.pck loaded`，版本显示 2026.11.23.6，检查更新返回已是最新，设置里有"显示比例"和"缩放"。离屏测试：缩放 70% 时点"设置"、130% 时点"开始新的调查"，都打开了对应页面（`evidence/zoom/`）。
- 助手重启后的那一轮没有把日志写到 `--log-file` 指定的位置，所以重启后的结果是另起一次程序核对的。
- **安卓 .6**（手机会话打包）：`G:\AHMobile-20261122\out\arkham-mobile-layout-2026.11.23.6.apk`，软件名"诡镇奇谈"。只发到 kaho（`full/android/arkham-ui-2026.11.23.6.apk` + 安卓清单），公网核对一致，MIME 正确。用模拟的 .5 安卓连 kaho 检查更新，提示新 APK .6。旧 .5 APK 已从 kaho 删除（没有清单再引用它）。国内线路按 Yunke 的意思不放安卓，等安卓能做增量更新时再放。
- 安卓只要改了代码就必须装新 APK（程序打包在 APK 里），做不了增量；以后可以给 APK 瘦身（资源改为下载）来缩小下载量。
