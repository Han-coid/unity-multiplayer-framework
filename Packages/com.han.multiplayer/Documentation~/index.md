# Han Multiplayer 0.8.0

面向 Unity 的 C# 会话连接基础包。基准版本：Unity 6000.3.26f1、NGO 2.13.3、UTP 2.7.4、Multiplayer Services 2.3.3。未验证 Unity 2022.3。

本包以 MIT 许可证发布，分发时请保留随包附带的版权与许可声明。具体条款见 [LICENSE.md](../LICENSE.md)。

先查 [当前能力、模块安装与验收清单](项目能力与验收清单.md)，再按需求进入教程。只导入 UPM 包不会带入原仓库根的 scripts、预构建 exe 或 TestResults；不要把只在示例启动器安装的组件当成所有项目自动安装。

**初次接入请先阅读 [从零接入完整教程](接入教程.md)。** 教程按“两个窗口验证 → 导入新项目 → Inspector 与玩家预制体 → 连接 UI → 玩法同步 → 场景切换 → 局域网 / Relay / Linux”展开，包含完整代码、预期结果、验收清单和排错表。下文保留为快速索引。

## 第一次使用

1. 安装包后，选择 **Tools → Han Multiplayer → Create or Open Demo**。
2. 打开生成的示例场景，Play → **创建局域网主机**。
3. 使用菜单 **Build Windows Demo** 构建第二个程序，填写 `127.0.0.1`，点击 **通过 IP 加入**。
4. 局域网其他设备使用主机的局域网 IP；互联网使用 Relay 房间码或公网服务器 IP。

## 接入游戏

在根 GameObject 上添加 NetworkManager、UnityTransport、MultiplayerSession。设置 NetworkManager 的 Transport、Player Prefab 和 Network Prefabs List。玩家预制体需要 NetworkObject；需要位置同步时加 NetworkTransform。

给不同游戏设置独立的 `SessionSettings.ApplicationId`；不兼容的规则更新需要提高 `ProtocolVersion`。所有接口在 Unity 主线程调用，使用 await / try-catch 处理结果：

```csharp
await session.StartLanHostAsync(7777);
// 另一个进程：await session.JoinDirectAsync("192.168.1.10", 7777);
await session.StopAsync();
```

不要在 MultiplayerSession 管理期间从别处直接启动或关闭 NetworkManager，也不要覆盖 ConnectionApproval 回调。连接状态通过 StateChanged、State、LastError 获取。主动退出会清除恢复资格，手动再加入创建新角色；意外断线时，安装并启用恢复模块且租约有效，可以通过新的网络对象恢复原逻辑身份和服务器快照。服务器重启、客户端进程退出后的存档恢复尚未实现。

## Relay

关联自己的 Unity Cloud 项目并配置 Authentication / Relay，添加 UnityRelayProvider 组件，赋给会话的 Relay Provider：

```csharp
await session.StartRelayHostAsync();
string roomCode = session.JoinCode;
// 另一个进程：await session.JoinRelayAsync(roomCode);
```

同机多实例设置不同的 Authentication Profile。已登录后切换 Profile 需要重启。跨地域 Relay 尚需使用自己的云项目完成实机验证。

WebGL 只使用 Relay WSS，本示例中的所有参与者都选择 SecureWebSocket 协议。Windows、macOS、Linux、Android、iOS、WebGL 应分别构建测试；包的源码可移植不等于每个平台都已验证。主机平台未适配。

## Linux 独立服务器

安装对应 Unity 版本的 Linux Dedicated Server Build Support 后，选择 **Tools → Han Multiplayer → Build Linux Dedicated Server (x64)**。构建输出到 `Builds/LinuxServer`，包含可执行文件、运行时、`run-server.sh` 和 systemd 模板。

这是实际 Linux 专用服务器目标，不依赖图形窗口，默认启动 Server 模式。上传完整构建后，在 Linux x86_64 上赋予执行权限并运行 `./run-server.sh`；默认 UDP 7777，可用 `SERVER_PORT` 和 `MAX_PLAYERS` 环境变量调整。构建成功不代表实机启动或跨平台互通已经验证。

## 边界

可选大厅模块见 [通用大厅接入与人工验收](通用大厅接入与验收.md)，包括服务器同步的名单、准备、规则、房主权限、加载完成屏障和返回大厅流程。它不依赖 FPS，也不提供跨服务器房间搜索与匹配。

新增可选开发工具详见 [网络诊断与弱网测试](网络诊断与弱网测试.md)：包含 RTT、聚合流量、JSON 报告、弱网预设、自定义延迟/抖动/丢包和短时断网恢复。弱网仅在 Editor / Development Build 启用。

- 示例使用服务器权威移动，新增可选基础预测与输入重放；没有动态物理回滚、射击命中回溯和完整作弊防护。
- 已有同一服务器进程内的可选恢复重连；没有房间列表、匹配、主机迁移或服务器重启恢复。
- ID / 版本握手不是账号认证；正式服务需要自己的身份验证。
- 直连使用普通 UDP；Relay 默认 DTLS / WSS。
- Host 退出后 Relay 房间结束；独立服务器需要自行构建部署。
- Demo 和核心连接层使用不同程序集，自己的游戏可以完全替换 Demo。

官方说明：[NGO](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/index.html)、[Relay 接入](https://docs.unity.com/en-us/mps-sdk/tutorials/relay-and-ngo)。

中文按钮和字体配置见 [中文界面与字体](中文界面与字体.md)。

- [同步时序与延迟优化](同步时序与延迟优化.md)：30/60 Hz、起步响应及双进程比较。

- [断线重连与状态恢复](断线重连与状态恢复.md)：恢复凭证、自动重试、离线席位、角色快照与验收。

- [账号准入与请求校验](账号准入与请求校验.md)：账号验证插件、HTTPS 后端接口、角色绑定及逐次请求校验。

- [基础移动预测与服务器校正](基础移动预测与服务器校正.md)：可选本地预测、静态碰撞、输入重放、展示适配、弱网重新定基及开关对比。当前示例协议为 6，双方需要重建。

- [跳跃、重力与地面检测](跳跃重力与地面检测.md)：静态平台/顶板、完整运动状态校正与空中恢复。
