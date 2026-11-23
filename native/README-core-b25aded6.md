# 规则核心源码交接包：classic-fast-1.0.6 / sourceId b25aded6

目标规则身份：

```
arkham-core-sha256:b25aded6595c947497890ea4d3d63ea46792a465022a0e189cda5e0a40ea5cb2
```

在 Windows 上已用原版身份算法核对（**已验证**）：

```
node tools/core-identity.mjs verify
{"passed":true,"sourceId":"arkham-core-sha256:b25aded6…a5cb2","inputs":9529}
```

本包 = 上一版交接包 `handoff/mac-core-5bb054d7`（classic-fast-1.0.5，sourceId 5bb054d7）+ QQ 群反馈的 4 处规则核心修复 + 沉没之城古代秘库三张"不能被取消" + 旧存档兼容条目，以及宿主（`services/native-host`）放宽存档版本检查。编译环境、FFI、snapshot 格式都没变。

**宿主也要一起换（2026-11-23 部署时发现）**：旧宿主读档、写入、读日志、存档包导入导出时，都要求存档的 sourceId 与当前核心完全相同，所以换核心后旧存档会被拒（"Saved core is incompatible; explicit migration required"），核心里的兼容名单根本用不上。本包的宿主去掉了这些比对，改由核心导入时判断（认得的旧版继续，其他照样拒绝）；核心刚生成的结果、联机双方核心一致的检查保留。改动见 `reference/host-relax-source-check.patch`（28 行，5 个文件）。改前核对过：原宿主源码编出的 DLL 与 Win 版正在用的 `Arkham.NativeHost.dll` 3089 个方法 IL 逐个相同。Mac / 手机用同一份宿主代码，也要换成本包这份。

（中间版本 ce629ddb（没有兼容条目，会拒绝 5bb054d7 的存档）和 b08f62c4（没有古代秘库）都没发布，已作废，移到 `Arkham UI/rules-work/superseded/`，不要使用。）

## 1. 与 5bb054d7 的差别

完整 diff：`reference/rules-vs-5bb054d7.patch`（以 `handoff/mac-core-repo`，即 git 提交 547b948c、sourceId 5bb054d7 为基准）。只改了 6 个规则文件，外加重新生成的 `SourceIdentity.hs`：

| 反馈 | 文件 | 改动 |
|---|---|---|
| #8 黛安娜 + 倒吊人正位，每次换牌多一张黑暗洞察 | `Arkham/Investigator/Runner.hs`（`FinishedWithMulligan`） | 开局在手的卡只在第一次换牌时生成，并记进 `excludeFromMulligan`；"是否再换一轮"的判断扣掉这些卡 |
| #11 阿加莎神话阶段取消标记后，本轮回合结束不能发动 | `Arkham/Investigator/Cards/AgathaCrane.hs` | `agathaTrigger` 改在 `Do BeginRound`（每轮开始、神话阶段之前）清零，原来在 `BeginTurn` |
| #19 令人震惊的一幕（52027）能被取消 | `Arkham/Treachery/CardDefs/ReturnToThePathToCarcosa/ReturnToTheLastKing.hs` | 补 `cdRevelation = CannotBeCanceledRevelation` |
| #20 迷雾高塔所在地打不出幽灵利刃 | `Arkham/Criteria.hs`（`canFightSomething`） | 加入"可以如同敌人一样被攻击的地点"：`exists (YourLocation <> LocationWithModifier CanBeAttackedAsIfEnemy)` |
| 古代秘库（11608/11609/11610，沉没之城"宏伟秘库"）能被取消 | `Arkham/Treachery/CardDefs/TheDrownedCity/TheGrandVault.hs` | 三张都补 `cdRevelation = CannotBeCanceledRevelation`（卡面"不能取消"）；全库审计只有这三张同类漏标记 |
| 旧存档兼容 | `Arkham/Native/Session.hs`（`compatibleImportSourceIds`） | 加入 5bb054d7（当前正式版）和 b08f62c4（未发布的中间版，只有 QA 存档），它们的存档可以直接导入；上面的改动都没有新增存档字段 |

每条都先用旧 DLL（5bb054d7）复现确认是规则核心问题，再在新 DLL 上验证修好，见 `Arkham UI/docs/bug-triage/rules-repro-20261123.md`（古代秘库审计见 `evidence/bugs-server/local-cannot-cancel/`）。

## 2. 目录内容

| 路径 | 内容 | 是否计入身份 |
|---|---|---|
| `engine/rules/` | 规则源码（同上一版的 9529 个身份输入，其中 6 个改过） | 是 |
| `engine/rules/arkham-core/generated/Arkham/Native/SourceIdentity.hs` | 生成文件，内容为 b25aded6；`verify` 会核对 | 否 |
| `engine/ffi/` | `ArkhamFFI.hs`、`lifecycle.c`、`arkham_core.h`、`exports.def`，与上一版逐字节相同 | 否 |
| `services/native-host/` | 宿主源码：上一版加上放宽存档版本检查（`reference/host-relax-source-check.patch`）；Win 版编出的 `Arkham.NativeHost.dll` sha256 `f425d436…8543` | 否 |
| `core.json` | 新的 Windows 核心描述：sourceId、dllSha256、sourceRelease | — |
| `tools/core-identity.mjs` | 原版身份算法，原样 | — |
| `tools/check-core-info.py` | 编好以后加载库、调用 `info` 核对 sourceId；目标已改为 b25aded6 | — |
| `reference/phase2-identity-r106003.json` | b25aded6 的完整身份回执（9529 个文件的 path/bytes/sha256） | — |
| `reference/phase2-identity-r104004.json` | 上一版 5bb054d7 的回执，供逐文件比对 | — |
| `reference/rules-vs-5bb054d7.patch` | 相对 5bb054d7 的全部差异 | — |
| `reference/windows-build/build-dll.ps1` | Windows 上编这个 DLL 的脚本 | — |
| `SHA256SUMS` | 本目录每个文件的 sha256（不含自身） | — |

## 3. 编译环境（和上一版相同）

- GHC **9.14.1**，stack **3.11.1**，resolver **nightly-2026-08-09**（以 `stack.yaml.lock` 为准），`$locals: -O0`
- 环境变量：`GIT_SHA1=f6ed09bec346ebc505ade739d34a7aea3305fa5c`（必须），`GHC_CHARENC=UTF-8`、`LANG=C.UTF-8`、`LC_ALL=C.UTF-8`、`GHC_NO_UNICODE=1`
- 只编库：`stack build arkham-core:lib`。不要用 `stack build arkham-core`：包里的 `core-spec` 测试程序在 5bb054d7 上就编不过（`NativeNetworkSpec`、`SettingsEngineSpec` 还在用旧的 `QuestionLabel` 写法），和本次改动无关。
- Windows 上终端要用 UTF-8 代码页（`chcp 65001`），否则 stack 输出 "»" 时会因 GBK 编码报错中断。
- 之后 FFI 编译与链接步骤同上一版 README 第 4 节（Mac 用 `-exported_symbols_list` 导出 6 个 `ah_core_*`）。

## 4. Windows 版结果（已验证）

- DLL：`Arkham UI/rules-work/build-output/classic-fast-b25aded6/arkham_core.dll`，sha256 `bf6b96dd3ef8ef4cb20a900e55fede5a861e354a65ad7bb01aa1c4c9b846a5df`。**2026-11-23 已部署到 `Arkham UI/runtime/`**（连同新宿主），旧存档实机验证能读、继续、撤销、看日志、局间升级、导入旧 json（`Arkham UI/evidence/deploy-b25aded6/`）。
- `info`：`coreSourceId` = b25aded6…，`snapshotVersion` = 2，`abiVersion` 1（`check-core-info.py` PASS）。
- 回归：手机移植时录的 93 条核心请求（`G:\AHMobile-20261122\parity\requests.jsonl`，狂热之夜开局、构筑、视图、导出等），新 DLL 输出与旧 DLL 输出比对：71 条逐字节相同，22 条只差 sourceId 字符串，其他差异 0。旧 DLL 自身两次输出 93/93 相同。
- 四条修复与旧存档导入在新 DLL 上的验证：见 `docs/bug-triage/rules-repro-20261123.md` 的"b25aded6 验证"一节。

## 5. 在 Mac 上

```sh
tar -xzf mac-core-b25aded6.tar.gz && cd mac-core-b25aded6
shasum -a 256 -c SHA256SUMS
node tools/core-identity.mjs verify          # 应输出 passed:true、b25aded6…a5cb2、inputs 9529
export GIT_SHA1=f6ed09bec346ebc505ade739d34a7aea3305fa5c
# 按第 3 节编库、编 FFI、链接 dylib
python3 tools/check-core-info.py /path/to/libarkham_core.dylib   # 应输出 PASS
```

跨平台要对上的只有 **sourceId**（b25aded6…）和 **snapshotVersion**（2）。如果 `verify` 对不上，不要改文件凑哈希，用 `reference/phase2-identity-r106003.json` 逐个比对后报告。
