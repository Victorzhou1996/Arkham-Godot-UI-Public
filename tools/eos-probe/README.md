# EOS 探测程序（登录 / 大厅 / P2P）

只用于验证 EOS 设备身份登录、大厅、P2P 收发是否通。不是游戏代码。

## 已验证（同一台机器）
- 设备身份登录（`lobby` 模式的前半段）：通过
- 大厅创建 / 查询成员数 / 销毁：通过（`evidence/eos/probe3-lobby.log`）
- P2P 同机回环：发送 Success，但接收端未收到（同一个 PUID 给自己发包，结果不作数）
- **P2P 同机两个 Windows 账户（2026-11-21）：通过**
  - `victo` 发送，`windowsguest` 接收；两边是不同的 PUID（访客账户首次运行 `CreateDeviceId: Success`，生成了新的设备身份）。
  - 接收端先收到连接请求并接受（`AcceptConnection=Success`），随后收到包：`RECV: Success, channel 0, bytes 45`，退出码 0。从发出到收到约 4 秒。
  - 日志：`evidence/eos/probe6-2user-p2p-send-victo.log`、`evidence/eos/probe6-2user-p2p-recv-guest.log`。
  - 局限：两个账户在同一台电脑、同一个网络出口，没有测到跨路由器打洞和 Epic 中继兜底。这部分要靠下面的两台机器测试。

### 同机两账户怎么测
`guest/` 下的脚本：
1. 在 `victo` 里运行 `guest/stage-to-public.ps1`：把程序和配置复制到 `C:\Users\Public\eos-probe-test`，访客账户能读到。
2. 切到 `windowsguest`，运行 `C:\Users\Public\eos-probe-test\recv-loop.ps1`：接收端最多循环 8 轮（每轮 90 秒），给切换账户留出时间。每轮打印的 `RECEIVER PUID` 都一样，因为设备身份按 Windows 账户固定。
3. 按 Win+L 切到 `victo`，运行 `run-guest.ps1 send <访客PUID>`。
4. 切回 `windowsguest` 看结果，判定方法同下面第 4 步。
5. 测完在 `victo` 里运行 `guest/cleanup-public.ps1`：公共目录里的配置文件含 Client Secret，必须删掉。

## 两台机器（跨网络）P2P 测试（待做）

前提：两台机器必须是两个不同的身份。同一台电脑的两个账户会得到不同的设备 ID；同一账户、同一台电脑不行。

### 1. 准备
- 两台机器都装 .NET 8 运行时（或直接拷贝下面的整个输出目录，它自带 EOSSDK DLL）。
- 把 `bin/Release/net8.0/` 整个文件夹拷到第二台机器。
- 配置文件 `C:\Users\victo\.arkham\eos-config.local.json` 含有 Client Secret，拷贝时注意保密。第二台机器可以放在任意位置，用 `--config` 指定。

### 2. 接收端（先启动，等它打印 PUID）
```bash
"./EosProbe.exe" --config "C:/path/to/eos-config.local.json" p2p-recv
```
输出里 `RECEIVER PUID: ...` 那一行就是对方要用的 PUID。接收端最多等 90 秒。

### 3. 发送端（另一台机器，PUID 从接收端抄过来）
```bash
"./EosProbe.exe" --config "C:/path/to/eos-config.local.json" p2p-send <接收端PUID>
```
### 4. 判定
- 接收端出现 `RECV: Success ... text="hello from probe ..."`，并且退出码为 0：两台机器之间 P2P 收发通过。
- 接收端 `RECV: timeout, no packet`：没通。把两边的日志都发回来。

### 退出码
- 0 成功
- 2 配置不完整
- 3 SDK 初始化失败
- 4 平台创建失败
- 5 登录失败
- 6 大厅测试失败
- 7 收包失败
- 8 等待超时
- 9 缺少对端 PUID
- 10 发送失败

### 日志位置（本机）
`evidence/eos/`：`probe1.log`（登录）、`probe3-lobby.log`（大厅）、`probe4-p2p-recv.log`（同机回环）、`probe5-config-arg.log`（`--config` 参数验证）、`probe6-2user-p2p-*.log`（同机两账户 P2P）。
