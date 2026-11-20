# Linux Dedicated Server

此目录由 Unity Linux Dedicated Server 构建产生。目标：Linux x86_64，Mono 后端。上传整个目录，不能只上传可执行文件。

首次在服务器终端运行（当前目录为上传后的目录）：

```sh
chmod +x MultiplayerServer.x86_64 run-server.sh
./run-server.sh
```

出现 `[Multiplayer] State=Server` 后，客户端填写服务器可达 IP、端口 `7777`，点击“通过 IP 加入”。终端按 Ctrl+C 停止。

修改端口和人数：

```sh
SERVER_PORT=7788 MAX_PLAYERS=8 ./run-server.sh
```

这是单个房间进程。运行多个房间需使用不同端口。脚本将日志输出到终端；systemd 会收集到 journal。服务进程异常退出会触发 Restart=on-failure。UDP 端口需在服务器防火墙、云安全组或路由器中按实际网络放行。

`han-multiplayer.service` 是 systemd 模板，默认路径 `/opt/han-multiplayer`，运行账号 `han-game`。安装前创建这个专用账号，复制完整构建到该路径，再将 service 文件安装到 `/etc/systemd/system/` 并运行 `systemctl daemon-reload`、`systemctl enable --now han-multiplayer`。服务本身不需要 root 身份。详细安装和排查步骤见源码项目的 `docs/Linux服务器部署.md`。

此 x86_64 二进制不能直接用于 aarch64 / ARM64 服务器。服务器不需要安装 Unity 编辑器、Python 或独立 .NET SDK；Unity 运行时随构建提供，系统库仍须满足运行要求。不要对外宣称尚未实机验证的系统已经兼容。


0.8.0 基础示例使用协议版本 6、默认 60 Hz，并启用可选会话恢复和基础移动预测。双方代码、网络预制体、静态运动世界及 Tick 配置需要一致。服务器按 Tick 处理客户端输入，始终决定真实位置；预测只影响客户端展示。当前支持移动、跳跃、重力、静态箱体碰撞及垂直速度恢复，不包含坡面、移动平台或动态物理回滚。恢复凭证和角色快照只保留在服务器内存；服务器重启后不能恢复旧会话。此构建仍使用基础示例场景，不包含大厅的 Entry/Lobby/Game 场景。

本地编译/打包不等于已经在 Linux 运行验收；当前未上传或部署服务器。


可选账号准入通过 `./run-server.sh --net-auth http` 启用，配置 HAN_ACCOUNT_BACKEND_URL 与服务器密钥环境变量 HAN_ACCOUNT_BACKEND_KEY。真实账号后端需要项目自行提供，开发模拟验证器在正式构建禁止。普通 UDP 不会因账号后端使用 HTTPS 而自动加密，正式网络需配置适当的安全传输。
