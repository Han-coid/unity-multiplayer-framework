# Changelog

## 文档修订（0.8.0 代码不变）— 2026-11-07

- 增加当前能力、模块安装与逐层验收清单，区分源码仓库与独立 UPM 包。
- 修正自动恢复、Legacy/预测确认频率和独立安装请求限流的说明。
- 补充跳跃输入、垂直速度恢复适配、组件顺序、故障排查和跨设备验收；同步包内文档。

## 0.8.0 — 2026-11-06

- 增加可选跳跃、重力、地面检测、静态箱体平台/顶板、离边下落与终端速度。
- 权威确认和输入重放包含位置、垂直速度、落地状态；跳跃位图随输入窗口去重。
- 无输入时服务器继续重力；示例恢复快照保存垂直速度，新增空中恢复验收。
- 增加跳跃独立进程检查和 13 项 EditMode 测试；已有报告共 111 项 Unity 测试通过。
- 示例协议升为 6，业务恢复键升为 demo.player.v2；Linux 只本地构建打包，未部署或运行验证。

## 0.7.0 — 2026-11-06

- 增加独立基础移动预测：输入窗口、服务器校正、未确认输入重放、展示平滑和静态碰撞。
- 增加 On/Off 同协议比较、Legacy 兼容模式、输入代次及弱网重新定基。
- 增加预测专项及软件起步响应对比；示例协议升为 5。

## 0.6.0 — 2026-11-06

- 增加可插拔账号准入、HTTPS 验证适配、账号与恢复绑定、重复账号拒绝及凭证过期撤销。
- 增加统一请求 Gate：权限、参数、阶段、大小、频率和序号校验；开发模拟在正式构建禁用。
- 增加账号/请求测试与开启账号的恢复回归；示例协议升为 4。真实账号后台不在包内。

## 0.5.0 — 2026-11-06

- 增加可选进程内恢复凭证、自动退避重试、取消、服务器快照与离线席位。
- 大厅可重新绑定恢复玩家；主动退出/踢出撤销恢复资格。
- 增加恢复集成测试；示例协议升为 3。不包含主机迁移或进程重启存档恢复。

## 0.4.0 — 2026-11-06

- 增加原版 30、对齐 30、响应 60 时序配置与实际发送/模拟频率测量。
- 增加输入确认、软件起步响应和双进程时序比较；不修改全局 Fixed Timestep。
- 示例协议升为 2；后续 TimingComparison 保留 Legacy 对比路径。

## 0.3.1 — 2026-11-06

- Localized connection, lobby, player controls and network diagnostics into Simplified Chinese.
- Added a shared scoped IMGUI skin with a project-font override and installed CJK font fallback.
- Localized presentation states and known errors while preserving protocol codes and diagnostic logs.

## 0.3.0 — 2026-11-06

- Added optional server-authoritative lobby rosters, names, readiness, leader permissions and room rules.
- Added admission extensions, pending-join start guards, synchronized scene-load barriers and return-to-lobby flow.
- Added kick feedback and dedicated-server lobby leader reassignment (not host migration).
- Added separate Entry/Lobby/Game demo scenes, a lobby client build, process integration checks and a manual acceptance launcher.

## 0.2.0 — 2026-11-06

- Added optional UTP RTT/bandwidth/counter diagnostics, bounded state history and local JSON reports.
- Added Editor/Development Build fault injection using the UTP 2.7 global simulator: delay, jitter, independent TX/RX loss, presets and timed blackout recovery.
- Added a draggable diagnostics panel, demo command-line options and standalone weak-network verification.
- Diagnostics are independent of FPS gameplay; the core session assembly does not depend on the tools assembly.
- Fixed Windows demo builds inheriting the Dedicated Server subtarget after a Linux build.
- Fixed stale standalone-test log markers; automated movement now continues during long recovery checks.

## 0.1.0 — 2026-11-05

- Added LAN host, direct IP client and standalone server session APIs.
- Added Unity Relay provider with DTLS / WSS configuration and cancellation-safe result handling.
- Added connection state events, deadlines, compatibility handshake and capacity reservations.
- Added a server-authoritative movement / ready-state demo and editor scene generation.
- Added admission, cancellation and lifecycle tests plus standalone Windows verification scripts.
- Added Linux x86_64 Dedicated Server builds, default server startup, a Linux launcher and a systemd service template.
- Added local-only Linux packaging with ELF architecture checking and SHA256 output. Deployment remains a separate step.

当前仍是可复用联机基础框架。真实云服务、生产账号后端、非 Windows 运行目标及容量需要项目自己的验证。
