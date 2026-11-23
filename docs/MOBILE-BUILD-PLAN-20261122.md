# 手机版构建计划（2026-11-22，只计划未动手）

Yunke 11-22：手机版走"核心编译到手机"（方案 A），用上游开源仓库 halogenandtoast/ArkhamHorror 的 Haskell 源码交叉编译；**构建必须和我们 Win 版的 DLL 对应**，方便以后版本一致。联机功能保留。

## 1. 现状（已核对，只读）

| 部件 | Win 版现状 | 手机要做什么 |
|---|---|---|
| 规则核心 `runtime/arkham_core.dll` | GHC 9.14.1 编译的原生库（约 361MB），导出 `ah_core_init / call / result_size / result_copy / result_release / shutdown` | 用 GHC 交叉编译成 android-arm64 `.so`、iOS arm64 静态库 |
| 宿主 `runtime/Arkham.NativeHost.dll` | .NET 托管程序集；`NativeCoreWorker` 用 kernel32 的 LoadLibrary/GetProcAddress 加载核心 | 改成按平台加载（安卓 dlopen；iOS 静态链接，用 `__Internal`） |
| `runtime/sqlite3.dll` | Windows 原生库 | 换成安卓/iOS 对应的 sqlite |
| EOS SDK 原生库 | `EOSSDK-Win64-Shipping.dll`；csproj 里排除了 Android/IOS 绑定目录 | 换成安卓 `.so` / iOS framework，打开对应绑定 |
| C# 游戏代码（app、engine） | Godot .NET 一起打包 | 安卓需 .NET 9 导出模板（Godot 4.5 起）；C# 移动端导出仍标实验性 |

## 2. 怎样保证和 Win 版一致

- Win 版核心的版本身份是 `sourceId`，写在 `runtime/core.json`：`arkham-core-sha256:5bb054d7…`，`sourceRelease` 为 `classic-fast-1.0.5`。当前文件哈希 `21e09b84…` 与 0712 构建输出 `build-output/native-windows-classic-fast-105-views/arkham_core.dll` 完全一致。
- `sourceId` 的算法在 0712 `acceptance/arkhamhorror-app-repo-20261113/staging/tools/core-identity.mjs`：对 `engine/rules` 下全部 `.hs`、`data`、`native-data`、stack 配置文件逐个取 sha256，再加上编译器 `ghc-9.14.1` 和上游常量 `f6ed09bec346…`（上游 2026-10-08 的提交），整体再取 sha256。
- **判据**：手机核心用同一份源码算出的 `sourceId` 必须等于 Win 版的值；核心启动握手也会校验这个值（`NativeCoreWorker` 不一致就拒绝）。
- **注意**：我们的核心不是纯上游，而是上游 `f6ed09b` 加上我们自己的仓库 `Victorzhou1996/Arkhamhorror-App` 里的规则补丁和 FFI 层（`engine/ffi/ArkhamFFI.hs`、`lifecycle.c`、`exports.def`）。只拿纯上游源码编，`sourceId` 对不上。
- **源码已找到（11-22）**：GitHub `Victorzhou1996/Arkhamhorror-App` 的分支 `mac-core/classic-fast-1.0.5-5bb054d7`（提交 `547b948c`，由 mac-core-handoff 会话整理推送，目录 `mac-handoff/` 有出处说明）。
  - 规则部分 = 提交 `39284bc5`（rules 与 `2ea7f4d8` 相同）+ 3 个文件：`Investigator/Runner.hs`、`Native/Session.hs`、`Native/Triggers.hs`；FFI 用 1.0.5 版 `ArkhamFFI.hs`（`ea81c740…`）。
  - 我独立复核（只读）：对 git 各提交逐个复算，`2ea7f4d8`/HEAD 的 9529 个身份文件中 9526 个与回执 `phase2-identity-r104004.json` 逐字节一致；分支上那 3 个文件的 sha256 与回执完全一致。因此该分支的身份文件集合 = 回执 = `sourceId 5bb054d7…`。**已验证**（文件级）。
  - 注意：`Session.hs` 这一版不在任何 git 提交或工作区里，是由两份 0712 留档拼回的（详见分支 `mac-handoff/README.md`），哈希与回执一致。
  - 手机核心就从这个分支编；以后升级时 Win 与手机从同一提交一起重编，双方 `sourceId` 相同即可。

## 2b. 编译环境（11-22 开始搭，Yunke 同意下载并同意接受 Android SDK 许可协议）

全部放在 `G:\AHMobile-20261122`（不动 0712，不动 AppData 里的 Godot 设置）：

| 东西 | 位置 | 状态 |
|---|---|---|
| 源码 | `src/core-1.0.5`（GitHub 分支 `mac-core/classic-fast-1.0.5-5bb054d7`，`core.autocrlf=false` 原样 LF 检出） | `node tools/core-identity.mjs verify` → passed，sourceId `5bb054d7…c79ba8`，9529 个输入。**已验证** |
| JDK 17 | `tools/jdk-17.0.21.1+2`（Temurin） | 已装 |
| .NET 9 SDK | `tools/dotnet9`（9.0.321） | 已装 |
| Android SDK | `tools/android-sdk`：platform-tools、build-tools 35.0.1、android-35、NDK 28.1.13356709 | 已装 |
| Godot 4.5.1 .NET | `tools/godot`（从 0712 复制，`_sc_` 自包含模式），安卓/iOS 导出模板从 0712 已下载的 `.tpz` 解出 | 已装 |
| WSL 分发 `AHMobile-GHC` | Ubuntu 24.04，磁盘在 `wsl/`；用户 builder；引导 GHC 9.14.1 + cabal 3.16.1 + alex/happy（ghcup）；NDK r28b Linux 版在 `~/ndk` | 已装 |
| GHC 9.14.1 交叉编译器（x86_64 → aarch64-linux-android，API 28） | 脚本 `scripts/build-ghc-cross.sh`，日志 `logs/ghc-cross-build.log` | 编译中 |

- Win 版原来的编译环境其实还在：`0712/godot-native-20261019/tools/ghc/ghc-9.14.1-x86_64-unknown-mingw32` 和 `tools/downloads/stack-3.11.1.exe`（只是不在 1.0.5 构建脚本写的 `G:/AHNativeTools-20261019` 路径）。以后 Win 重编可以从这里复制。
- 交叉编译器按 termux/ghc-cross-tools 的配方：打 `hadrian-enable-iserv`（9.14.1 上 Default.hs 那段手工改）、`elf-types`，`-no-pie` 检测强制为否；`rts-heap-reservation` 不用打，9.14.1 已自带同类修复。
- 选择：安卓最低 API 28（Android 9，bionic 自带 iconv）；bignum 先用 `native_bignum`（不依赖 GMP，结果与 GMP 相同，只是大整数慢一些）。
- 规则源码有 222 个文件用 Template Haskell，交叉编译时 TH 要靠 iserv 在 qemu-aarch64 下执行，这是下一步的主要风险。
- GHC 自带的 libffi 交叉编译会把目标当成 aarch64-linux-gnu、链接失败；改为 `scripts/build-libffi.sh` 单独编静态 libffi 3.5.2（API 28），configure 用 `--with-system-libffi`。
- EOS 安卓 SDK：Yunke 11-22 从 Epic 开发者后台下载 `EOS-SDK-Android-58105819-Release-v1.19.2.1.zip`（sha256 `4af5f8ee…1f3e`），解压在 `tools/eos-android-1.19.2.1`（含 `eossdk-StaticSTDC-release.aar`）。版本与 Win 版 C# SDK（`~/.arkham/eos-sdk-1.19.2.1`）一致；C# SDK 里已有 `Generated/Android`（`AndroidInitializeOptions`）。安卓上还要在 Java 层先调用 `EOSSDK.init(activity)`，需要一个小的 Godot 安卓插件 + gradle 导出。

## 2c. 快速路线：手机只当客人（11-22 发现）

宿主 `LanRemoteSession.cs` 注明客人端"没有 NativeCoreWorker，也没有游戏存储，只接收座位视图"，握手只报 sourceId 字符串。所以只当客人的 APK 不需要规则核心，可以先用它测跨端联机（Win 开房，手机加入）。需要改：`LanSecrets`（Windows DPAPI）、`NativeSqlite`（换安卓 sqlite）、`NativeCoreWorker` 在客人模式下不加载。**未验证**：界面在客人模式下是否还在别处调用本地核心。

## 2d. APK 构建管线（11-22 搭建，镜像方式，不改游戏目录）

- `scripts/sync-app.cmd`：把 `Arkham UI/app`、`engine`、`runtime`（只取 NativeHost.dll、core.json）镜像到 `G:\AHMobile-20261122\work`，再覆盖 `overlay/`（安卓专用 csproj、`export_presets.cfg`、`ArkhamApp.sln`）。镜像里的 `android/`、`.sln`、`export_presets.cfg` 不会被同步删掉。
- `scripts/patch-mirror.sh`：每次同步后给镜像打安卓补丁，每处都校验，对不上就报错停下：
  1. `project.godot` 开 `import_etc2_astc`（不开的话 Godot 会不给任何提示就拒绝导出安卓）；
  2. `App.cs`：安卓上 Root 指向 `/storage/emulated/0/Android/data/com.yunke.arkhamui/files/root`。游戏有 59 处按文件路径读数据、图片和字体，第一版用 `scripts/push-assets.sh` 经 adb 推过去；
  3. `EosConfig.cs`：安卓上的开发配置放在同一目录的 `eos-config.local.json`（含密钥，不打进 APK）；
  4. `EosOnlineService.cs`：安卓用 `AndroidInitializeOptions` 和通用 `Options`，按库名加载 `libEOSSDK.so`；
  5. 装 gradle 模板，放入 EOS AAR，`GodotApp.java` 加 `System.loadLibrary("EOSSDK")` 和 `EOSSDK.init(this)`。
- `scripts/export-apk.sh`：同步、打补丁，然后用自带的 Godot 无头导出 `out/arkham-debug.apk`（arm64，最低 API 28，调试签名 `tools/debug.keystore`）。
- 第一次导出（不用 gradle）得到 2.3GB 的包，但 C# 没打进去（缺 .sln，已补上）。体积以后再压。
- 已知限制：规则核心还没有安卓版，所以启动时会显示"启动失败"（菜单还在）；EOS 登录、建房、按房间码加入不依赖核心。**对局同步在 Windows 上也还没实现**（见 `ONLINE-SYNC-DESIGN.md`），所以目前能跨端测的只有登录和房间。

## 2e. 客人版 APK 现状（11-22 17:30）

- `G:\AHMobile-20261122\out\arkham-debug.apk`（约 838MB，arm64，调试签名）：含触屏层（`docs/MOBILE-TOUCH-20261122.md`）、客人入口、EOS 安卓库。
- 客人入口：核心启动失败时（安卓上必然失败），主菜单仍给出"加入联机游戏"和"退出游戏"，EOS 登录后按钮可点。**在 Windows 上用镜像模拟验证过**（EOS 登录成功，显示"联机已就绪：玩家1281"）；**真机未验证**。
- 带进去的内容以导出时游戏目录的状态为准，包括主程序的压缩贴图和 `artstore`（压缩卡图库，通过 adb 推到手机 `root/artstore`）。主程序发"全内容更新"后，重新跑 `scripts/export-apk.sh` 即可。
- 核心：GHC 交叉编译器已编好；静态 iserv 能编，但跑 Template Haskell 时需要 `dlopen`，要改用动态 iserv，在 qemu 下配真实的安卓系统库（从 SDK 系统镜像或手机里取）。

## 2f. 全量 APK（11-22 晚，Yunke："我需要一个全量包"）

装上就能用，不用 adb 推资源。做法：

- 游戏读文件有两种方式。**通过 res:// 读的**（卡图、音频、大部分图片）已经以 Godot 导入后的形式在 APK 里，直接用，不再重复打包。**按文件路径读的**才需要额外打包，共 7482 个文件、约 723MB：
  - `build-data`、`build-engine`、`data`、`scenarios`、`fonts`、`art-portrait-wide`、`ui/icons-user`、`bg/home`；
  - `bg` 里的地图区域蒙版、发光层、选人蒙版、工坊桌面；
  - `artstore`（约 533MB）；
  - `runtime/core.json`。
- 这部分由 `scripts/make-bundle.py` 打成 4 个 zip，放在 `res://ahm-bundle/`。zip 里图片不压缩，json 压缩；APK 里这些 zip 设为"不压缩存放"。输入没变时不会重打。
- 首次启动由 `Scripts/Mobile/MobileBundle.cs` 解包到 `files/root`，显示黑底进度条（"首次启动：正在解包游戏资源，只需要一次"）。解包完成后写入 `root/.bundle-id`，里面记有耗时；以后启动直接跳过。APK 更新后如果资源有变，会再解包一次。手机空间不足时会提示需要多少 MB。
- `App._Ready` 在读字体之前拆开：解包完成后才继续启动。
- **没打进去的**：
  - `assets/ref`（参考图，游戏不用）；
  - `evidence/ai/model`（AI 训练模型，1.8GB）。AI 只在本机开局时用，要有规则核心才行，手机现在没有核心。
- Windows 离线测试（`--bundle-test=<空目录>`），**已验证**：
  - 从空目录解包 7482 个文件，用时约 115 秒（这台电脑，含杀毒扫描）；
  - 解包后主菜单、牌组工坊正常，卡图（res://）和 artstore 条形卡图（按路径读）都能显示；
  - 第二次启动不再解包。
- **真机还没验证**：在 APK 里读 zip、手机上的解包耗时。
- EOS 配置（Yunke 11-22 "用1+2"）：用主工程加密后的 `app/online/eos.dat`（288 字节），已打进包里。`EosConfig.cs` 原样使用，不再用 adb 推配置。
  - 安卓导出预设的 include_filter 加了 `online/eos.dat`。
  - 镜像在 Windows 上用 `--eos-sealed-only --online-selftest=host` 跑过，**已验证**：EOS 登录成功并建了房间（房间码 2KFZW4）。
  - EOS P2P 没有改中继设置，EOS 默认允许中继，所以手机用流量网络也能走中继。**真机未验证**。
- 当前包：`out/arkham-debug.apk`，1,546,852,379 字节（约 1.55GB，低于 2GB），11-22 21:55 导出。`scripts/verify-apk.py` 核对过，**已验证**：
  - 4 个资源 zip 在包里都是不压缩存放；
  - `assets/online/eos.dat` 在包里；
  - 语音 966 个、音乐 52 个、音效 41 个，导入后的数据都在包里；
  - 图库 7109 个文件在资源 zip 里；
  - 解压包里每个文件（包括 zip 里的）搜索 clientSecret 明文，结果是没有。

## 2g. 安卓规则核心（11-22 晚，Yunke："能在手机单独玩的那种"）

源码用交接包 `src/core-1.0.5`（sourceId 5bb054d7）。规则源码没改一个字，FFI 层的 `ArkhamFFI.hs` 也原样使用。Yunke 同意了两项下载：Hackage 依赖包（约 127 个），以及 SQLite 3.54.1 官方源码（sha3 已核对）。

做法：
- **Template Haskell**：在 qemu 下跑一个完全静态的 bionic iserv。bionic 的静态 libdl 是空壳，所以用 `--wrap=dlopen/dlsym` 换成内置符号表，表里是 libc/libm/libz/libffi 的全局符号，约 2800 个。另外补了一个按 64 字节对齐的 TLS 变量，满足 bionic 的要求。这样不用安卓系统库，也不用模拟器。（`scripts/th-wrap.sh`）
- **构建中要跑的安卓小程序**（Setup 脚本、cards-discover 预处理器）：clang 包装器在 `AHM_STATIC_EXE=1` 时把可执行程序静态链接，再通过 binfmt 交给 qemu 运行。
- **依赖和核心**：用 cabal 加交叉 GHC 9.14.1 编译。设置和 Windows 版一致：`allow-newer`（和核心自己的 stack.yaml 一样）、`GIT_SHA1=f6ed09be…`、核心 `-O0`。只编库本身，不编测试程序。（`scripts/build-core-deps.sh`、`build-core.sh`）
- **链接**：`libarkham_core.so` 只导出 6 个 `ah_core_*` 接口，去掉调试信息后 383MB，没有 TEXTREL。`lifecycle.c` 的安卓版 `core-ffi/lifecycle-posix.c` 逻辑不变，SRWLOCK 换成 pthread 读写锁，并且不让 RTS 接管信号。（`scripts/link-core.sh`）
- **宿主**：从源码移植 `Arkham.NativeHost`（`scripts/port-native-host.py`）。Windows 上仍走原来的调用；安卓上用 NativeLibrary 加载，SQLite 用默认 VFS。
- **路径**：引擎镜像里，`PrototypeBackend` 在安卓上从 APK 的原生库目录加载核心和 SQLite（`overlay/engine/MobileNative.cs`）。
- **SQLite**：3.54.1 编成 `libarkham_sqlite3.so`，版本号和 Windows 固定的一致。

验证：
- **已验证**：
  - 在 Windows 上用移植后的宿主加 Windows 原版 DLL 开一局，自动打几步，录下 92 条核心调用。
  - 加一条 info，共 93 条，在 Windows DLL 上回放两次，结果逐字节一致。
  - 同样 93 条在 qemu 下的安卓核心上回放，结果和 Windows **逐字节一致**，其中 coreSourceId 是 5bb054d7…c79ba8，snapshotVersion 是 2。（`parity/`）
- **已做未验证**：在安卓真机或模拟器上，Mono 宿主加载 `.so` 并启动 RTS。
- **真机结果（11-22 晚，Yunke 的手机）**：
  - 第一次启动报"Android native library folder not found"。原因是这台手机直接从 APK 里加载原生库，库不会解压成文件。改为按库名加载后，主菜单出现"继续调查 / 开始新的调查 / 牌组工坊"，说明**核心在真机上启动成功（已验证）**。
  - 全屏：安卓上始终全屏，导出时加了 `screen/edge_to_edge`。
  - 手机和电脑之间的 EOS P2P 在真机上**已连通（已验证，见主持人日志里的 ONLINE_PONG）**。

### 需要 Yunke 动手的

1. 一台安卓手机（Android 9 及以上、64 位）。打开"开发者选项"→"USB 调试"，用数据线连这台电脑，手机上弹出"允许 USB 调试"时点允许。
2. 告诉我连好了。我来用 adb 装 APK（全量包，不用推资源，也不用推配置；也可以直接把 APK 拷到手机上安装）。
3. 测试：这台电脑开新版建房，拿到房间码；手机上点"加入联机游戏"，输入房间码。能测的是登录、加入、成员列表。**对局同步还没实现**（Windows 也一样）。

## 3. 步骤

1. **定源码**：找到或约定与 Win 版一致的源码提交，在自己的目录里复制一份（0712 只读，不改）。
2. **安卓可行性验证**（约 1–2 天）：GHC 9.14.1 交叉编译到 aarch64-linux-android（NDK），编出 `libarkham_core.so`，算 `sourceId` 与 Win 版比对；Godot .NET 导出一个最小 APK，加载核心调用一次 `ah_core_init`，真机测一次计算耗时。
3. **宿主适配**：`NativeCoreWorker` 的加载方式改为按平台；sqlite 换平台库。
4. **EOS 安卓原型**：设备登录 + 一次 P2P 收发（联机方案见 `docs/NONCORE-PLAN-20261119.md` 第 3 节）。
5. **手机交互**：悬停改长按/点击、右键改长按菜单、Esc 改系统返回键、布局适配手机屏幕。
6. **iOS**：需要 Mac + Xcode；GHC 交叉编译到 aarch64-apple-ios 并静态链接。
7. **以后每次升级**：Win 与手机从同一提交一起编译，发布前比对两边 `sourceId`。

## 4. 风险与需要 Yunke 决定的事

- GHC 对安卓/iOS 的交叉编译支持不如桌面成熟，第 2 步失败的话要重新评估。
- 核心约 361MB，安装包会很大。
- 上游仓库在 GitHub 上没有识别出许可证文件（API 返回 license 为空）；自己用没问题，公开发布 APK 前要再确认。
- 需要下载的东西（GHC 交叉工具链、Android NDK、.NET 9、Godot 安卓导出模板、EOS 安卓 SDK）都要先经 Yunke 同意。

## 11-23 核心身份文件固定（手机）

- **问题**：电脑版 11-23 14:44 换成了 b25aded6 核心，同步时 runtime/core.json 也跟着变成 b25aded6。手机核心还是 5bb054d7，宿主握手对不上（"handshake mismatch"），手机只能加入联机。17:24 导出的手机排版 APK 就带着这个问题；Windows 上用旧核心加新 core.json 能复现。
- **处理**：`scripts/export-apk.sh` 在同步之后，用 `core-out/core.json`（5bb054d7 的身份文件，取自 rules-work/runtime-backup-5bb054d7）覆盖 runtime/core.json。
- **代价**：手机上的规则仍是旧核心，没有 b25aded6 修的那些规则问题；电脑上用新核心开的存档，传到手机后能不能读，没有验证。
- **正式解决**：用 rules-work/engine 重新编 Android 核心，用 rules-work/host 的放宽版宿主重新移植。完成后删掉这一行覆盖。

## 11-23 晚：安卓核心升级到 b25aded6

- **源码**：`handoff/mac-core-b25aded6` 复制到 `src/core-b25aded6`，`node tools/core-identity.mjs verify` 通过（b25aded6，9529 个输入）。用 `scripts/update-core-src.sh` 同步进 WSL 的构建目录，变化的正好是交接说明列出的 7 个文件，FFI 与上一版相同。
- **编译**：`build-core.sh` 增量编译约 6 分钟，依赖不变。
- **链接中的问题**：`link-core.sh` 原来用 cabal `--write-ghc-environment-files` 生成包环境。这次 cabal 给全部依赖算出了新的哈希，要求整套重编，`character-ps` 编译失败。原因未查明，编译器、cabal 版本和配置文件都没变。现在改为脚本自己写包环境文件：全局库、cabal store、核心的 in-place 库，`-package` 里补了 base 和 deepseq。
- **宿主**：`scripts/port-native-host.py core-b25aded6`，从放宽版宿主源码移植，同样的平台补丁，dotnet 编译通过，放到 `overlay/runtime/Arkham.NativeHost.dll`。旧移植版在 `backup/Arkham.NativeHost.5bb054d7-port.dll`。
- **核对（已验证）**：
  - `parity/requests.jsonl` 共 93 条，在电脑 b25aded6 DLL 上回放，结果和 `rules-work/regression/b25-1.jsonl` 相同；
  - 在 qemu 下的安卓核心上回放，结果和电脑**逐字节一致**，coreSourceId 是 b25aded6，snapshotVersion 是 2；
  - 瘦身后 383MB，只导出 6 个 `ah_core_*`，没有 TEXTREL。
- **导出脚本**：去掉了临时固定 core.json 的那一行，直接用主项目的 b25aded6 core.json；导出前先跑 `--import`，让 `.import` 设置（size_limit）生效。
- 旧的 5bb054d7 核心备份在 `backup/core-out-5bb054d7/`。

## 11-23 晚：正式签名 + 热更新版 APK 2026.11.23.4（手机排版分支）

- **文件**：`out/arkham-mobile-layout-2026.11.23.4.apk`，2,623,256,664 字节。
  - 包名 com.yunke.arkhamui.layout，versionName 2026.11.23.4，versionCode 2026112304，正式版导出，不可调试。
- **签名**：正式钥匙 `C:\Users\victo\.arkham\arkham-android-release.keystore`（PKCS12）。别名和密码在同目录的 `.alias` / `.pass` 里，不打印、不提交。
  - 证书 CN=Arkham UI，SHA-256 `40:14:36:8E:74:A0:8B:71:F7:91:58:8D:14:4A:FA:8B:80:64:C7:C7:33:7E:14:4B:0A:E3:D1:80:AB:03:E0:E6`，apksigner 校验通过。
  - 从这一版起，所有 APK 都要用 `SIGN=release`。第一次换签名时，要先卸载旧版（卸载前先导出存档）。
- **导出脚本**：
  - `RELEASE=<r>`：把版本写进 APK；make-bundle 先把文件按手机根目录的结构硬链接到 `bundle-root/`，跑 `update_release.py build --platform android`，写出 `version.json`（随 bundle 解包到根目录），`bundle.json` 加上 "release"。
  - `SIGN=release`：从 ~/.arkham 读出签名参数，放进 Godot 的 GODOT_ANDROID_KEYSTORE_RELEASE_* 环境变量，用 `--export-release` 导出。
- **包内核对（已验证）**：
  - bundle.json 的 release 是 2026.11.23.4，version.json 有 7483 个文件；
  - 核心 b25aded6，两个原生库与编好的一致；
  - 撤回、导出存档、手指滑动、安全边距、安卓热更新的代码都在包里；
  - 没有密钥明文。
- **电脑上模拟手机热更新（已验证）**，用 `--bundle-test --update-platform=android --update-root=.`，加上本机测试服务器：
  1. 解包后第一次检查，本机 .4 对服务器 .4，结果是"已是最新"，没有重算全部文件。
  2. 本机测试服务器放一个只多一个数据文件的 .5：
     - 检查得出"1 个文件，30 字节，不需要助手"；
     - 下载暂存后，下次启动切换（3 moves）；
     - 状态"已更新到 2026.11.23.5"，新文件在根目录里。
  - 测试版 .5 只在本机（`qa-upd-server/`），没有上传。
- **未验证**：真机安装、真机检查和下载，以及要换 APK 时的提示与浏览器下载。服务器发布由主对话负责，要等 Yunke 同意。
