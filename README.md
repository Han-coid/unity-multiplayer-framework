# Unity 通用联机框架

这是一个 **C# 编写的 Unity 联机基础包 + 可运行示例项目**。目标是让多个游戏项目复用连接流程，而不是让任意单机游戏在导入后自动变成联机游戏。

## 先从哪里开始

先看 [当前能力、模块安装与验收清单](docs/项目能力与验收清单.md)：区分完整示例与只导入 UPM 包、列出各模块必须手动安装的组件，以及从双窗口到跨平台的验收步骤。

1. 使用 **Unity Hub → Add / 从磁盘添加项目**，选择当前目录。基准编辑器为 **Unity 6000.3.26f1**。
2. 等待 Package Manager 下载依赖。首次导入需要联网。
3. 打开 `Assets/MultiplayerDemo/MultiplayerDemo.unity`。若未生成，使用菜单 **Tools → Han Multiplayer → Create or Open Demo**。
4. 点击 Play，再点击 **创建局域网主机**。这一步不需要 Unity 云项目或服务器。
5. 使用 **Tools → Han Multiplayer → Build Windows Demo** 构建第二个客户端。在客户端里输入 `127.0.0.1`，点击 **通过 IP 加入**。
6. 两端移动角色并切换 准备，观察对方位置、准备状态是否同步。再尝试断开和重新加入。

示例连接、大厅、角色控制和诊断界面现已使用简体中文。Windows 自动选择已安装的中文字体；跨平台发布可提供项目字体，见中文界面说明。键盘控制依赖旧 Input Manager；只有新 Input System 的项目可先使用屏幕方向按钮，再将自己的输入传给玩法层。

## 为什么选 C#，而不是 Python

- Unity 的 `GameObject`、`MonoBehaviour`、场景、物理和 Netcode API 都可以由 C# 直接调用。
- 同一套 C# 游戏逻辑可随 Unity 构建到不同平台。独立游戏服务器也可使用 Unity 的 C# 逻辑。
- Python 适合以后另行开发数据工具、管理后台或部分 Web 服务。第一阶段没有必要引入第二种语言和另一套游戏运行时。

## 已实现的范围

| 功能 | 方式 |
| --- | --- |
| 同一电脑 / 局域网 | IP + UDP 端口，玩家主机 Host |
| 互联网房间码 | Unity Authentication + Relay；默认 DTLS，可选 WSS |
| 独立服务器 | 无本地玩家的 Server 模式；已构建 Linux x86_64 专用服务端，客户端按 IP 加入 |
| 连接生命周期 | 状态事件、超时、取消、停止后重建、错误原因 |
| 入房控制 | 游戏 ID / 协议版本校验、人数上限、并发名额预留 |
| 玩法示例 | 玩家自动生成、服务器计算移动、NetworkTransform 同步、准备 状态同步 |
| 基础移动预测 | 可选本地预测、服务器校正、未确认输入重放、显示平滑、静态墙体碰撞与异常网络重新定基 |
| 跳跃与重力 | 单次跳跃、地面检测、平台落地/离边下落、碰顶、终端速度与完整运动状态重放 |
| 场景切换入口 | 主机通过 NGO 同步加载 Build 列表中的场景 |
| 项目复用 | UPM 包、独立程序集、可替换的 Relay 适配接口 |
| 网络诊断 | 各远端 RTT、Driver 聚合收发流量/计数、状态日志和本地 JSON 报告 |
| 开发弱网测试 | 延迟、抖动、双向独立丢包、预设、自定义及短时断网自动恢复 |
| 时序优化与响应诊断 | 原版/对齐 30 Hz、响应 60 Hz，实际发送频率、应用确认和起步响应；双进程对比脚本 |
| 账号准入与请求校验 | 可选凭证供应器/验证器、HTTPS 后端适配、账号与恢复绑定、重复登录拒绝、权限/参数/阶段/限流/序号检查 |
| 断线恢复 | 可选模块、随机恢复凭证、自动退避/取消、离线席位、服务器角色快照和大厅重绑定 |
| 通用大厅 | 服务器同步名单/昵称/准备、房主权限、人数规则、进行中锁房、加载屏障、返回大厅及踢人 |

**局域网是同一网络中的多设备联机，不是同屏 / 分屏多人。** 本版按 IP 加入，没有自动搜索局域网房间。

跨地域连接需要 Relay，或有公网可达地址的游戏服务器。Relay 负责转发数据，游戏仍由玩家主机运行；主机退出后房间结束。独立服务器需要自行准备服务器和网络端口。

## 分层与复用

```text
你的游戏：角色、战斗、背包、UI、输入
             │
    MultiplayerSession（C# 连接生命周期）
         ├── IP 直连：局域网 / 公网服务器
         └── IRelayProvider → UnityRelayProvider
             │
    Netcode for GameObjects → Unity Transport
```

- `Packages/com.han.multiplayer/Runtime`：会话状态、参数校验、连接与准入策略。
- `Runtime/Relay`：Unity 云服务适配，只有选择 Relay 时才初始化云服务。
- `Security`：可插拔账号准入、服务器请求校验和开发鉴权验收。
- `Recovery`：可选恢复凭证、自动重试、服务器角色快照。
- `Lobby`：可选大厅与场景屏障。
- `Diagnostics`：可选网络诊断、时序测量与弱网注入。
- `Prediction`：可选移动/跳跃预测与服务器校正，独立于 FPS 和其他业务模块。
- `Demo`：示例玩法及界面；自己的游戏无需使用它。
- `Editor`：示例场景、Windows 客户端和 Linux 专用服务端构建工具。
- `Server~`：Linux 启动脚本、systemd 服务模板和随包说明。
- `Tests`：准入策略、取消竞态、会话生命周期测试。

在另一个项目中打开 **Window → Package Manager → + → Install package from disk**，选择本目录下 `Packages/com.han.multiplayer/package.json`。也可将整个包目录复制到新项目的 `Packages` 中，保留所有 `.meta` 文件。

依赖 SDK 仍随包安装；“LAN 不需要云服务”指运行时不需要云账号和云请求，不是完全不安装云 SDK。核心程序集不引用 Unity 云服务程序集，后续可以拆成独立的可选包。

第一次接入请按 [从零接入教程](docs/接入教程.md) 操作：第 2 节先跑两个窗口，第 3～5 节完成包导入、Inspector 配置和 UI 接入，第 6～7 节接入玩法与场景切换，第 8～11 节处理局域网、Relay、Linux 和跨平台构建。教程包含可复制的连接 UI、准备状态和服务端启动脚本，以及逐项验收和错误排查表。

同一份完整教程也保存在 `Packages/com.han.multiplayer/Documentation~/接入教程.md`，单独复制包到其他项目后仍可阅读。平台边界见 [兼容性与架构](docs/兼容性与架构.md)，测试记录见 [验证记录](docs/验证记录.md)。

网络工具的安装、参数口径、命令行和一键验证见 [网络诊断与弱网测试](docs/网络诊断与弱网测试.md)。示例开发构建默认显示可拖动面板，故障注入初始为关闭。工具位于独立的 `Han.Multiplayer.Diagnostics` 程序集，通用连接层不依赖它。

可选大厅模块见 [通用大厅接入与人工验收](docs/通用大厅接入与验收.md)。菜单 **Create or Open Lobby Demo** 打开独立入口场景；**Build Windows Lobby Demo** 生成 `Builds/LobbyWindows/LobbyDemo.exe`。运行 `scripts/Start-LobbyAcceptance.ps1` 可打开 Host / Guest 两个交互窗口；`scripts/LobbyTest.ps1` 运行自动化大厅检查。

Linux 服务器使用方法见 [Linux 服务端构建与部署手册](docs/Linux服务器部署.md)。当前 Linux 包已经在本地构建完成；按用户要求，尚未上传、安装或启动远端服务。构建脚本和重新打包脚本都只操作本地文件。

新的 [同步时序与延迟优化](docs/同步时序与延迟优化.md) 说明两窗口操作、指标口径、配置切换和自动对比。示例默认使用 60 Hz；核心连接层仍默认 30 Hz。示例协议版本已升为 6，请重建两端程序。Linux 基础示例服务端仅在本地构建打包，尚未部署或执行 Linux 运行验收。

[断线重连与状态恢复](docs/断线重连与状态恢复.md) 提供两个窗口的体验步骤、Inspector 配置、业务快照接口和自动测试。示例默认启用恢复，自己的项目可独立安装。仅覆盖同一服务器和客户端进程仍存活的会话；主动退出会撤销恢复资格。

[账号准入与请求校验](docs/账号准入与请求校验.md) 包含开发验收、自己的登录 SDK/后端适配和请求入口接入步骤。示例默认 LAN 无账号模式；`scripts/Start-LobbyAcceptance.ps1 -DevelopmentAccounts` 打开开发账号验收。正式账号服务需要配置自己的验证器或 HTTPS 后端，开发模拟在正式构建强制禁用。

## 当前边界

这是 `0.8.0` 基础版本。[基础移动预测与服务器校正](docs/基础移动预测与服务器校正.md) 已接入示例，支持独立关闭和同协议 A/B 对比。[跳跃、重力与地面检测](docs/跳跃重力与地面检测.md) 说明空格/触屏跳跃、静态平台和顶板、速度校正及空中恢复。仍不包含坡面、自动台阶、动态物理回滚或射击命中回溯。

未实现房间列表、匹配、主机迁移、账号注册/登录后台、聊天和进程重启后的存档恢复。主动退出再加入会创建新玩家，意外断线可在保留期内恢复；示例尚不具备完整竞技游戏移动和反作弊系统。

它可以作为多人游戏的连接与同步起点。把已有游戏改为联机仍需要明确哪些对象联网、哪些状态同步，以及哪些操作由服务器判定。建议先用本例完成两人小场景，再接入你的玩法。

## 官方参考

- [Netcode for GameObjects](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/index.html)
- [Relay 与 NGO 手动接入](https://docs.unity.com/en-us/mps-sdk/tutorials/relay-and-ngo)
- [Relay 连接流程](https://docs.unity.com/en-us/mps-sdk/advanced-config/connection-flow)

具体的计费、地区可用性、平台账号要求以服务提供方当前信息为准。本项目没有关联你的 Unity 云项目，也没有创建或购买云资源。

中文字体、显示层错误翻译与跨平台字体资源配置见 [中文界面与字体](docs/中文界面与字体.md)。
