# Han Multiplayer Framework

**面向 Unity 的模块化 C# 联机基础框架，提供可复用 UPM 包与可运行示例。**

基于 Netcode for GameObjects 和 Unity Transport，支持局域网、IP 直连与 Relay 房间码，并提供可选大厅、断线恢复、账号准入、网络诊断及基础移动预测模块。

适合希望从可运行示例开始，为多个 Unity 项目复用连接与同步基础设施的开发者。接入自己的游戏仍需要定义网络对象、同步状态和服务器判定逻辑。

**当前版本：`0.8.0` · 验证基准：Unity `6000.3.26f1` · 状态：早期开发**

[快速开始](#快速开始) · [安装与接入](#安装与接入) · [文档](#文档) · [贡献](#贡献) · [更新记录](Packages/com.han.multiplayer/CHANGELOG.md) · [Issues](https://github.com/Han-coid/unity-multiplayer-framework/issues)

**开源许可证** · [MIT](LICENSE)

## 功能概览

| 模块 | 已实现能力 |
| --- | --- |
| 连接与会话 | Host / Client / Dedicated Server、超时、取消、状态事件、游戏 ID / 协议版本 / 人数校验 |
| 网络接入 | 局域网与公网 IP + UDP；Unity Authentication + Relay 房间码，支持 DTLS / WSS |
| 通用大厅 | 名单、昵称、准备、房主权限、人数规则、进行中锁房、场景加载屏障、返回大厅与踢人 |
| 断线恢复 | 恢复凭证、自动退避重试、离线席位保留、角色快照与大厅身份重绑定 |
| 账号与请求校验 | 可插拔凭证供应器和账号验证器、HTTPS 后端适配、权限 / 参数 / 阶段 / 大小 / 限流 / 序号检查 |
| 移动与跳跃预测 | 客户端预测、服务器校正、输入重放、显示平滑、静态箱体碰撞、重力、跳跃、平台落地与碰顶 |
| 网络诊断 | RTT、传输层聚合收发统计、JSON 报告与响应时序指标 |
| 开发弱网测试 | 延迟、抖动、双向丢包、预设与短时断网注入 |
| 示例与工具 | 中文界面、基础联机 / 大厅示例、Windows 与 Linux 服务端构建入口、多进程验收脚本 |

大厅、恢复、安全、诊断和预测模块按需接入，通用连接层不要求使用 FPS 玩法层或示例角色。LAN 运行不需要云账号；Relay 接入需配置自己的 Unity 云项目。云服务 SDK 仍作为包依赖安装。

## 环境与平台

| 项目 | 基准版本或要求 |
| --- | --- |
| Unity Editor | `6000.3.26f1`；包声明最低 `6000.0`，不代表所有 Unity 6 版本均已验证 |
| Netcode for GameObjects | `2.13.3` |
| Unity Transport | `2.7.4` |
| Multiplayer Services | `2.3.3` |
| 首次导入 | 联网下载 Package Manager 依赖 |
| 命令行验收 | Windows、PowerShell、可正常运行的 Unity 编辑器与对应构建模块 |

完整依赖见 [package.json](Packages/com.han.multiplayer/package.json)，示例工程依赖见 [manifest.json](Packages/manifest.json)。

| 平台 / 接入方式 | 当前验证状态 |
| --- | --- |
| Windows 本地联机 | 已完成编辑器、构建与多进程测试，详见[验证记录](docs/验证记录.md) |
| Linux x86_64 Dedicated Server | 已完成本地构建与打包检查；尚未完成 Linux 运行验收 |
| macOS / Linux 客户端 | 尚未构建与实机验证 |
| Android / iOS | 尚未完成实机验证；需适配触控、字体、网络权限与后台切换 |
| WebGL | 设计入口为 Relay WSS；拒绝 UDP 直连，尚未完成浏览器构建与联调 |
| Relay 跨地域联机 | 已实现适配，尚未完成真实云项目端到端验收 |

跨平台互通需要游戏 ID、协议版本、网络预制体与玩法配置匹配。当前示例协议为 **6**，更新后应重新构建所有参与端。更多说明见[兼容性与架构](docs/兼容性与架构.md)。

## 快速开始

### 1. 获取项目

```bash
git clone https://github.com/Han-coid/unity-multiplayer-framework.git
cd unity-multiplayer-framework
```

在 Unity Hub 中选择 **Add / 从磁盘添加项目**，打开仓库根目录，使用 Unity `6000.3.26f1`，等待依赖导入和脚本编译完成。

仓库不包含 `Library`、`Builds` 或测试日志；客户端与服务端程序需要在本地构建。

### 2. 在同一台电脑上运行两个玩家

1. 打开 `Assets/MultiplayerDemo/MultiplayerDemo.unity`；也可使用 **Tools → Han Multiplayer → Create or Open Demo**。
2. 使用 **Tools → Han Multiplayer → Build Windows Demo**，生成 `Builds/Windows/MultiplayerDemo.exe`。
3. 在编辑器中点击 Play，保持端口 `7777`，点击 **创建局域网主机**。
4. 启动构建出的程序，服务器 IP 填 `127.0.0.1`，端口填 `7777`，点击 **通过 IP 加入**。
5. 分别聚焦两个窗口，用 WASD / 方向键移动、空格跳跃，确认两端能看到对方的移动和准备状态。

本步骤不需要部署服务器或配置 Unity 云服务。示例键盘输入依赖旧 Input Manager；使用其他输入系统时，可先使用屏幕按钮，或将输入接入预测模块。

### 3. 测试局域网或大厅

- **不同设备**：连接同一局域网，客户端填写主机的局域网 IPv4 地址与 UDP 端口 `7777`；确认主机防火墙允许游戏通信。`127.0.0.1` 仅用于连接本机。
- **大厅示例**：使用 **Tools → Han Multiplayer → Create or Open Lobby Demo**，从 `Entry` 场景启动；通过 **Build Windows Lobby Demo** 构建对应客户端。所有玩家准备后，由房主开始游戏。
- **跨地域**：按教程配置 Relay，或连接具有公网可达地址的独立服务器。Relay 由玩家主机运行游戏逻辑，主机退出后房间结束。

完整操作与故障排查见[从零接入教程](docs/接入教程.md)和[通用大厅接入与验收](docs/通用大厅接入与验收.md)。

## 安装与接入

只复用框架时，无需将整个示例工程复制到自己的项目。

1. 克隆本仓库后，在目标项目打开 **Window → Package Manager**。
2. 点击 **+ → Install package from disk**，选择本仓库的 `Packages/com.han.multiplayer/package.json`。
3. 按[模块安装与验收清单](docs/项目能力与验收清单.md)配置连接组件、网络预制体及所需的可选模块。

也可以将完整的 `Packages/com.han.multiplayer` 目录复制到目标项目的 `Packages` 下，保留所有 `.meta` 文件。通过磁盘引用时，请保留原包目录，避免移动后引用失效。

**安装 UPM 包不会自动创建业务场景、接入账号后台或同步任意 GameObject。** 仓库根目录中的 `Assets` 示例场景和 `scripts` 验收脚本也不会随单独的 UPM 包导入；包内提供编辑器示例生成入口与 `Documentation~` 文档。

## 项目结构

```text
Assets/                         # 基础联机与大厅示例场景、预制体
Packages/com.han.multiplayer/
├── Runtime/                    # 会话核心；Relay/ 为云服务适配
├── Lobby/                      # 大厅与场景加载屏障
├── Recovery/                   # 重连与状态恢复
├── Security/                   # 账号准入与请求校验
├── Prediction/                 # 移动、跳跃预测与服务器校正
├── Diagnostics/                # 网络诊断与开发弱网测试
├── Demo/                       # 示例玩法与界面
├── UI/                         # 中文界面辅助
├── Editor/                     # 示例生成与构建工具
├── Tests/                      # EditMode / PlayMode 测试
├── Server~/                    # Linux 启动与 systemd 模板
└── Documentation~/             # 可随包携带的文档
ProjectSettings/                # Unity 工程设置
docs/                           # 文档主副本
scripts/                        # 本地构建与验收脚本
```

连接层通过 NGO / Unity Transport 处理网络通信；玩法层负责输入与状态语义，服务器负责权威判定。预测模块可独立启用，不绑定特定游戏类型。

## 文档

| 主题 | 文档 |
| --- | --- |
| 能力总览与模块配置 | [项目能力与验收清单](docs/项目能力与验收清单.md) |
| 首次接入与故障排查 | [接入教程](docs/接入教程.md) |
| 平台与依赖边界 | [兼容性与架构](docs/兼容性与架构.md) |
| 大厅与场景流程 | [通用大厅接入与验收](docs/通用大厅接入与验收.md) |
| 意外断线与业务快照 | [断线重连与状态恢复](docs/断线重连与状态恢复.md) |
| 登录后端与服务器校验 | [账号准入与请求校验](docs/账号准入与请求校验.md) |
| 移动预测与输入重放 | [基础移动预测与服务器校正](docs/基础移动预测与服务器校正.md) |
| 垂直运动与静态碰撞 | [跳跃重力与地面检测](docs/跳跃重力与地面检测.md) |
| 网络指标与故障注入 | [网络诊断与弱网测试](docs/网络诊断与弱网测试.md) |
| Tick 与延迟口径 | [同步时序与延迟优化](docs/同步时序与延迟优化.md) |
| 服务端构建与部署 | [Linux 服务器部署](docs/Linux服务器部署.md) |
| 中文字体与界面 | [中文界面与字体](docs/中文界面与字体.md) |
| 历史验证结果 | [验证记录](docs/验证记录.md) |
| 版本变更 | [CHANGELOG](Packages/com.han.multiplayer/CHANGELOG.md) |

## 构建与测试

Windows 客户端使用快速开始中的构建菜单。Linux 服务端使用 **Tools → Han Multiplayer → Build Linux Dedicated Server (x64)**，需安装对应构建模块；当前入口构建基础示例，不包含大厅场景。其他平台或自己的场景请在 **File → Build Profiles** 中配置。

在 Windows 上，先关闭占用本项目的 Unity 编辑器，再从仓库根目录运行：

```powershell
# 运行 EditMode / PlayMode 测试、构建 Windows 基础示例并执行联机冒烟检查。
.\scripts\Verify.ps1 -UnityPath 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe'

# 基础示例构建成功后，按改动范围运行专项验收。
.\scripts\PredictionTest.ps1
.\scripts\JumpTest.ps1
.\scripts\SecurityTest.ps1
```

`Verify.ps1` 不会自动执行所有专项测试，也不构建大厅示例。大厅、恢复、弱网和时序测试的前置条件见对应文档。日志与报告分别写入 `Logs/` 和 `TestResults/`，不会提交到 Git。

`0.8.0` 的历史本地记录为 **87 项 EditMode + 24 项 PlayMode 测试通过**，另有多进程专项验收。这是特定环境下的结果，不代表持续集成状态或所有平台均已通过；原始生成报告未随仓库发布。复现条件和范围见[验证记录](docs/验证记录.md)。

## 已知限制与后续方向

- 当前为基础框架，尚未实现房间搜索、自动匹配、主机迁移、聊天与账号注册 / 登录后台。
- 恢复依赖仍存活的服务器和客户端进程及有效席位保留期；不支持进程重启后的存档恢复。主动退出会撤销恢复资格。
- 预测使用显式静态箱体模型，不包含坡面、自动台阶、移动平台、玩家间物理碰撞、动态物理回滚或射击命中回溯。
- 请求校验需在对应入口接入，不等同于完整反作弊系统。开发模拟账号验证器不能作为正式账号后端。
- 手机示例按钮尚未替代正式触控摇杆、多点触控与安全区适配；中文字体需按目标平台打包。

后续可围绕跨平台实机验收、Relay 联调、移动端输入、兴趣管理与玩法扩展逐步完善。以上为待完善方向，不代表已实现能力或发布日期承诺。

## 贡献

欢迎通过 [Issues](https://github.com/Han-coid/unity-multiplayer-framework/issues) 报告问题、讨论功能或提出文档改进。

报告问题时，请提供 Unity / 框架版本、操作系统、连接方式（LAN / 直连 / Relay）、最小复现步骤、预期与实际结果，以及经过脱敏的相关日志。请勿在公开 Issue 或 PR 中提交密码、访问令牌、恢复凭证或私钥。

提交 Pull Request 前，请：

1. 对较大的接口或架构变更先创建 Issue，说明使用场景与方案。
2. 将改动保持在明确范围内，沿用现有代码风格，为新增或修改的代码补充中文注释。
3. 根据改动运行相关测试，在 PR 中说明验证环境、结果及未验证范围；纯文档修改可只检查链接和内容一致性。
4. 修改接入方式或行为时同步更新文档；`docs/` 中的文档与 `Documentation~/` 对应副本应保持一致。
5. 保留 Unity `.meta` 文件，不提交缓存、构建产物、日志或本地凭证。

## 许可证

本项目以 MIT 许可证发布。可以使用、修改和分发框架，包括将其用于闭源或商业项目。分发框架的副本或实质性部分时，请保留版权声明和许可声明。具体条款见 [LICENSE](LICENSE)。

单独分发 UPM 包时，请随包保留 [LICENSE.md](Packages/com.han.multiplayer/LICENSE.md)。Unity、依赖 SDK 及另行引入的第三方字体和素材各自适用其原有许可。
