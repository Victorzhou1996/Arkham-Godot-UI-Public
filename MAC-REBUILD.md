# Mac 重建说明（2026-11-23 更新）

本仓库是 Arkham UI 新版的代码：Godot 4.5.1 .NET 前端、规则核心 b25aded6 的源码，以及 FFI 和宿主。现在 Windows 版和安卓版都已跑通；Mac 版还没有在 Mac 上编过，下面是按已跑通的两条路线整理的步骤。

## 1. 仓库里有什么

| 路径 | 内容 |
|---|---|
| `app/` | Godot 工程：`project.godot`、`ArkhamApp.csproj`、C# 脚本、场景、样式、小体积素材，以及 `lib/` 下的 Jint 和 Acornima |
| `engine/` | 引擎层 C#：`PrototypeBackend.cs` 等，被 `ArkhamApp.csproj` 引用 |
| `docs/` | 设计和计划文档。Mac 相关的重点见第 6 节 |
| `tools/` | 工具脚本：热更新发布工具 `tools/release/`、规则探针、AI 等 |
| `native/backend/` | **规则核心 Haskell 源码，sourceId b25aded6**（classic-fast-1.0.6，和 Windows / 安卓正在用的一致） |
| `native/ffi/` | FFI 层：`ArkhamFFI.hs`、`arkham_core.h`、`exports.def`，以及 Windows 版的 `lifecycle.c`（用 SRWLOCK，Mac 不能用） |
| `native/ffi-posix/` | **POSIX 版的生命周期**（安卓核心就用它编译）：`lifecycle-posix.c`（pthread 读写锁，RTS 不接管信号）、`exports.map`（GNU 链接器）、`exports-macos.txt`（Mac 链接器的导出表）、`core-cli.c`（回放测试程序） |
| `native/host/` | **移植后的 Arkham.NativeHost**（C#）：放宽版宿主，加上平台分支。Windows 上照旧用 LoadLibraryExW 和 win32-longpath VFS；其他平台用 `NativeLibrary.Load` 和 SQLite 默认 VFS。也支持 `ARKHAM_CORE_RECORD=<文件>`，把核心请求逐行录下来 |
| `native/core.json` | Windows 核心的描述文件（sourceId b25aded6、dllSha256、sourceRelease） |
| `native/tools/` | `core-identity.mjs`（原版身份算法）、`check-core-info.py`（加载编好的库并调用 `info`，核对 sourceId） |
| `native/parity/` | 一致性核对用：`requests.jsonl`（93 条录下来的核心请求）、`win-b25aded6.jsonl`（Windows b25aded6 DLL 的输出，安卓核心的输出与它逐字节相同）、`replay-win.py`（Windows 上的回放脚本） |
| `native/README-core-b25aded6.md` | 核心交接说明：相对 5bb054d7 改了什么、编译环境、身份核对 |
| `scripts/build-mac.sh` | Mac 编译脚本（**草稿，没有在 Mac 上跑过**），已按安卓路线改成 b25aded6、POSIX 生命周期、移植后的宿主 |

## 2. 不在仓库里，需要单独拷过去

- **大体积素材**（`.gitignore` 排除，合计约 1.7 GB）：
  - `app/assets/audio`（含 6376 段剧情语音，约 880 MB）
  - `app/assets/bg`、`cards`、`ref`、`art-portrait-wide`、`art-fallback`

  11-21 的 Release 附件是旧的，缺少后来加的语音，请从 Windows 的 `G:\Python Project\Arkham UI\app\assets\` 直接拷。没有这些素材，游戏能编译，但画面和声音会缺。
- **`artstore/`**（卡图压缩库，ArtStore.cs 按路径读）：从 Windows 的 `G:\Python Project\Arkham UI\artstore\` 拷到 Mac 游戏根目录的 `artstore/`。
- **EOS 联机配置** `app/online/eos.dat`（加密封装，含密钥）：不上传，需要的话从 Windows 手动拷。
- **签名钥匙**：热更新签名私钥和安卓签名钥匙都只在 Windows 的 `C:\Users\victo\.arkham\`，Mac 编译用不到。

## 3. 编规则核心（libarkham_core.dylib）

按已跑通的安卓路线（`G:\AHMobile-20261122\scripts\build-core-deps.sh` / `link-core.sh`）：

1. **工具**：GHC **9.14.1**，stack 3.11.1 或 cabal 3.16，resolver nightly-2026-08-09（以 `native/backend/stack.yaml.lock` 为准）。
2. **先核对源码身份**：在 `native/` 下运行 `node tools/core-identity.mjs verify`（需要按脚本说明指到 `backend`；交接包里是 `engine/rules`），结果必须是 `passed: true`，sourceId 是 `arkham-core-sha256:b25aded6…a5cb2`，共 9529 个输入。如果因为换行符（CRLF/LF）对不上，用 `git config core.autocrlf input` 重新检出。
3. **环境变量**（必须）：`GIT_SHA1=f6ed09bec346ebc505ade739d34a7aea3305fa5c`。另外建议 `LANG=C.UTF-8`、`LC_ALL=C.UTF-8`。
4. **只编库**：`stack build arkham-core:lib`（cabal：`arkham-core:lib:arkham-core`），`$locals` 用 `-O0`。不要编整个 `arkham-core`：包里的 `core-spec` 测试程序本来就编不过。安卓上也是 `allow-newer: all`，与核心自己的 stack.yaml 一样。
5. **FFI 和链接**（`scripts/build-mac.sh` 第 2、3 步）：
   - `ghc --make -no-link -fPIC -threaded -O0 -XGHC2021`，包参数 `-package base -package deepseq -package arkham-core -package aeson -package bytestring -package text -package containers -package uuid -package mtl -package transformers`，编 `native/ffi/ArkhamFFI.hs`。
   - `cc -c native/ffi-posix/lifecycle-posix.c`。
   - `ghc -shared -dynamic -threaded -no-hs-main -O0 … -optl-Wl,-exported_symbols_list,native/ffi-posix/exports-macos.txt`，只导出 6 个 `ah_core_*`。
   - 安卓上用的是 `-flink-rts`，把 RTS 链进库里；Mac 上如果找不到 RTS，就加 `-flink-rts`。
   - 安卓链接时遇到过一个问题：用 cabal 写包环境文件时，它要求整套依赖重编。最后改成自己写环境文件，只列全局库、store、in-place 库三个数据库。
6. **核对**：
   - `python3 native/tools/check-core-info.py <dylib>`，coreSourceId 应为 b25aded6，snapshotVersion 为 2。
   - 用 `native/parity/requests.jsonl` 在 Mac 核心上回放：可以照 `native/ffi-posix/core-cli.c` 编一个本机版 core-cli，或者改 `replay-win.py` 去加载 dylib。输出要和 `native/parity/win-b25aded6.jsonl` **逐字节相同**，安卓已经做到了。
7. **SQLite**：宿主固定 SQLite 3.54.1，安卓上也是单独编了 3.54.1。Mac 系统自带的 libsqlite3 版本不同，宿主可能会拒绝；最好用 3.54.1 源码编一个 dylib。

## 4. 宿主和前端

- `native/host` 用 `dotnet build Arkham.NativeHost.csproj -c Release` 编出 `Arkham.NativeHost.dll`，放到游戏根目录的 `runtime/`。`app/ArkhamApp.csproj` 通过 `../runtime/Arkham.NativeHost.dll` 引用它。
- 宿主是"放宽版"：存档不比对核心版本，由核心按 `compatibleImportSourceIds` 判断，所以 5bb054d7 的旧存档也能读。启动握手仍然要求核心的 sourceId 和 `runtime/core.json` 一致。
- `engine/PrototypeBackend.cs` 写死了 `runtime/arkham_core.dll` 和 `runtime/sqlite3.dll`。在 Mac 上最省事的做法：
  - 把编好的 dylib 直接命名为 `runtime/arkham_core.dll` 和 `runtime/sqlite3.dll`。`NativeLibrary.Load` 用的是 dlopen，不看扩展名。
  - 或者像安卓那样改成按平台选文件名（安卓用的是 `overlay/engine/MobileNative.cs`）。
- `runtime/core.json` 用 `native/core.json`，sourceId 必须是 b25aded6。dllSha256 那一项是 Windows DLL 的值，安卓上没有核对它。
- **前端**：Godot 4.5.1 .NET 版加 .NET 8/9 SDK，打开 `app/project.godot`，第一次会重新导入资源。导出时新建一个 macOS 预设，`export_presets.cfg` 里现在只有 Windows 和安卓的设置。打成 dmg 时，没签名的 .app 第一次要右键打开。

## 5. 现状和注意

- **热更新**：`Updater.cs` 现在只支持 win64 和 android 两个平台。Mac 上会显示"开发版"，不检查更新。要支持 Mac，还得加 platform=macos 的清单和更新流程。
- **联机**：房间和成员都带版本号（`VER = 版本号|核心 sourceId`）。Mac 版要和 Windows、安卓用同一个版本号和同一个核心，才能一起联机。
- **当前版本**：Windows 和安卓都是 2026.11.23.6，核心 b25aded6。

## 6. 相关文档

- `native/README-core-b25aded6.md`：核心交接说明（修复列表、编译环境、身份核对）
- `docs/MOBILE-BUILD-PLAN-20261122.md`：安卓的完整移植记录，包括交叉编译 GHC、链接、宿主移植、逐字节核对、正式签名、热更新
- `docs/MOBILE-TOUCH-20261122.md`：触屏和手机排版
- `docs/HOT-UPDATE-DESIGN.md`：热更新设计和部署记录
