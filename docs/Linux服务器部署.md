# Linux 服务端：构建与部署手册

**当前仅完成本地构建和打包，按用户要求暂不部署到服务器。** 本文中的远程命令是以后部署时的操作说明，本轮没有执行这些命令。

0.8.0 基础示例服务端使用协议 6、默认 60 Hz，含恢复、可选账号模块与移动/跳跃预测。本轮本地构建日志为 `Logs/jump-linux-build.log`，归档校验记录为 `TestResults/linux-package.json`。此包未上传、未在 Linux 执行，也不包含大厅示例场景；双方需使用同版基础示例及相同静态运动配置。

## 当前已知环境与产物

- 本地开发环境：Windows，Unity 6000.3.26f1，已安装同版本 Linux Dedicated Server Build Support。
- 先前 SSH 只读检查记录的目标：`192.168.1.111`，Ubuntu 26.04.1 LTS，`x86_64`，SSH 端口 22；本次文档修订没有重新连接服务器。
- Linux 构建类型：Dedicated Server，Mono 后端，x86_64。没有游戏窗口，不占用一个玩家名额。
- 本地构建目录：`Builds/LinuxServer`。
- 可上传的完整包：`Builds/HanMultiplayer-Linux-x64.tar.gz`。
- 校验文件：`Builds/HanMultiplayer-Linux-x64.tar.gz.sha256`。
- 0.8.0 已有构建日志：`Logs/jump-linux-build.log`；以后运行 Build-LinuxServer.ps1 的日志按脚本输出路径查看。

Ubuntu 26.04.1 来自先前环境记录，不是本次运行验收结论。部署前应重新确认实际发行版/架构，并按所用 Unity 版本核对 [Unity 6.3 系统要求](https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/getting-started-installing-unity/system-requirements)。构建成功、系统库齐全和客户端互通是三项不同检查，不能只凭本地打包宣称目标系统已兼容。

### 选择配套客户端

当前归档构建的是 `Assets/MultiplayerDemo/MultiplayerDemo.unity` 基础场景。以后服务启动后，使用同版 `Builds/Windows/MultiplayerDemo.exe` 加入，不用大厅 `LobbyDemo.exe` 代替：大厅还需要自己的场景列表、LobbyRoom 和网络配置。要在 Linux 上运行大厅，应按自己的大厅构建流程生成包含 Entry/Lobby/Game 的专用服务器，而不是只复制场景文件到已有归档。

`192.168.1.111` 是私网地址。客户端必须与它处于可路由的网络（如同一局域网或已有 VPN），不能据此认为跨地域公网可达；跨地域方案另按 Relay 或公网服务器的网络条件验证。当前仍不执行部署步骤。

## 一、在 Windows 上构建

在 Unity 菜单选择：

**Tools → Han Multiplayer → Build Linux Dedicated Server (x64)**

也可以关闭本项目的 Unity 编辑器后，在 PowerShell 中运行：

```powershell
& 'D:\Unity 通用联机框架\scripts\Build-LinuxServer.ps1'
```

该脚本先调用 Unity 构建，再生成压缩包和 SHA256 文件。它不会 SSH、上传或启动远程服务。

如果只更新了说明或启动脚本，无需重新编译 C#，可以重新打包已有构建：

```powershell
& 'D:\Unity 通用联机框架\scripts\Package-LinuxServer.ps1'
```

打包时会验证可执行文件是 Linux x86_64 ELF，将启动脚本转换为 UTF-8 无 BOM / LF，并排除 Unity 标记为 DoNotShip 的调试目录。

Unity 编辑器生成的 Linux 构建包含自己的运行时；目标服务器不需要安装 Unity 编辑器、Python 或独立 .NET SDK。Linux 系统库仍需满足该构建的运行要求。

## 二、以后部署时，先上传并前台验证

以下步骤尚未执行。准备部署时，在 Windows PowerShell 上传完整包和校验文件：

```powershell
scp 'D:\Unity 通用联机框架\Builds\HanMultiplayer-Linux-x64.tar.gz' 'D:\Unity 通用联机框架\Builds\HanMultiplayer-Linux-x64.tar.gz.sha256' root@192.168.1.111:/tmp/
ssh root@192.168.1.111
```

在 Linux 上校验文件，并使用新的暂存目录解压，不覆盖现有程序：

```sh
cd /tmp
sha256sum -c HanMultiplayer-Linux-x64.tar.gz.sha256
server_stage=$(mktemp -d /tmp/han-multiplayer.XXXXXX)
tar -xzf /tmp/HanMultiplayer-Linux-x64.tar.gz -C "$server_stage"
cd "$server_stage"
chmod +x MultiplayerServer.x86_64 run-server.sh
file MultiplayerServer.x86_64
ldd MultiplayerServer.x86_64
ldd UnityPlayer.so
./run-server.sh
```

若 `ldd` 出现 `not found`，先根据具体缺失库安装发行版提供的依赖，再启动。不要直接套用其他系统版本的包名。

日志出现 `[Multiplayer] State=Server` 说明会话已经启动。前台测试完成后按 Ctrl+C 停止。

可通过环境变量调整参数：

```sh
SERVER_PORT=7788 MAX_PLAYERS=8 ./run-server.sh
```

默认端口为 UDP 7777、最多 8 个玩家。参数允许 2–128 人只是输入范围，没有完成 128 人压测。一个进程运行一个房间；需要多个房间时使用不同端口。

## 三、以后需要常驻运行时安装 systemd 服务

先完成前台启动和客户端验证，再做常驻安装。以下命令以 root 执行，游戏进程会使用专用账号 `han-game`。

在解压后的服务端目录中执行。若目标安装目录已存在，先检查和备份，不要直接覆盖：

```sh
getent passwd han-game || useradd --system --user-group --home-dir /var/lib/han-multiplayer --shell /usr/sbin/nologin han-game
install -d -o root -g root -m 0755 /opt/han-multiplayer
cp -a . /opt/han-multiplayer/
chown -R root:root /opt/han-multiplayer
chmod 0755 /opt/han-multiplayer/MultiplayerServer.x86_64 /opt/han-multiplayer/run-server.sh
install -m 0644 /opt/han-multiplayer/han-multiplayer.service /etc/systemd/system/han-multiplayer.service
systemctl daemon-reload
systemctl enable --now han-multiplayer
systemctl status han-multiplayer --no-pager
```

服务通过 `StateDirectory` 为专用账号准备 `/var/lib/han-multiplayer`，日志交给 journal，启动失败或传输层故障会以失败退出码结束，便于 systemd 重启。服务模板和命令尚未在目标服务器执行验证。

常用管理命令：

```sh
journalctl -u han-multiplayer -f
systemctl stop han-multiplayer
systemctl start han-multiplayer
systemctl restart han-multiplayer
ss -lunp 'sport = :7777'
```

日志跟踪命令会持续运行，按 Ctrl+C 退出日志查看。停止服务会使当前房间中的玩家断线；本版没有自动保存并恢复房间的能力。

修改端口和人数时使用 `systemctl edit han-multiplayer`，填写：

```ini
[Service]
Environment=SERVER_PORT=7788
Environment=MAX_PLAYERS=8
```

保存后执行 `systemctl restart han-multiplayer`，并让客户端连接新端口。

## 四、Windows 客户端如何连接

在 Windows 上打开相同游戏版本的客户端，填入：

- Server IP：`192.168.1.111`
- UDP port：`7777`
- 点击 **Join IP**。

独立服务器不使用 Relay 房间码，不需要选择 Create Relay room。客户端和服务端的游戏 ID、协议版本、SDK 与网络预制体需要保持一致。Linux 服务端构建使用同一份示例场景和网络行为，已经移除角色着色的服务端执行分支，但真实 Windows ↔ Linux 互通仍待部署后实测。

以后启动服务器后，可在 Windows 上执行本地客户端验证：

```powershell
& 'D:\Unity 通用联机框架\Builds\Windows\MultiplayerDemo.exe' --net-mode client --net-address 192.168.1.111 --net-port 7777
```

至少运行两个客户端，验证远端玩家生成、移动、Ready 状态、主动离开和重新加入。不要把只看到 `State=Server` 当作已通过完整联机测试。

## 五、局域网与外网

`192.168.1.111` 是局域网地址，其他地区的玩家不能直接通过它访问。

- 局域网：客户端能够到达服务器，服务器防火墙允许 UDP 7777。
- 有公网入口：配置路由器的 UDP 端口转发及对应防火墙规则，再使用公网地址连接。
- 没有可入站公网入口：需另行配置支持 UDP 的隧道、组网，或扩展 Relay 接入方式。

只读检查时目标服务器的 UFW 状态为 inactive；这不能证明其他网络层一定放行。当前没有修改 UFW、路由器、云安全组或其他防火墙规则。

## 六、尚未完成的验证

- 目标 Ubuntu 26.04.1 上的可执行文件启动和依赖加载。
- systemd 安装、自启动、异常重启和长时间运行。
- Windows 客户端与 Linux 服务端的真实网络同步。
- 跨地域连接、弱网和容量测试。

官方参考：[Dedicated Server 构建](https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/dedicated-server/build)。


## 可选账号后端（0.6.0）

默认仍为无账号的基础示例。正式服务器可在配置可信账号后端后，通过 `./run-server.sh --net-auth http` 启用验证；后端 URL 与密钥从环境注入，不要把密钥写进源码或命令行。开发模拟账号在此正式服务器构建中强制禁止。协议与客户端保持 6，并注意普通直连 UDP 未自动加密。详见 [账号准入与请求校验](账号准入与请求校验.md)。这些服务器运行命令本轮未执行。
