// 示例启动入口：将命令行参数和 IMGUI 按钮统一映射到 MultiplayerSession。
using System;
using System.Threading.Tasks;
using Han.Multiplayer.Relay;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Lobby;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Security;
using Han.Multiplayer.Prediction;
using UnityEngine.SceneManagement;
using UnityEngine;
using Han.Multiplayer.UI;

namespace Han.Multiplayer.Demo
{
    /// <summary>提供本地演示界面、无图形服务端启动和自动化测试进程的参数入口。</summary>
    [RequireComponent(typeof(MultiplayerSession))]
    public sealed class DemoLauncher : MonoBehaviour
    {
        MultiplayerSession session; // 连接生命周期入口。
        UnityRelayProvider relay;   // 可选的云服务配置组件，直连时不调用云接口。
        string address = "127.0.0.1"; // 默认连接同一台电脑上的服务端。
        string portText = "7777";    // UI 保留文本输入，发起连接时再转换为端口数值。
        string code = "";           // 玩家输入的 Relay 加入码。
        string error = "";          // UI 最近捕获的操作异常。
        bool busy;                  // 控制启动按钮是否可点击。
        bool exitOnServerFailure;   // 专用服务器失败时退出，供 systemd 等进程管理器感知。
        Vector2 connectionScroll;   // 小窗口中连接面板的滚动位置。
        string accountMode = "none", accountName = "guest";

        /// <summary>初始化演示配置，读取命令行，并在指定模式时自动启动连接。</summary>
        async void Start()
        {
            // 同机多个客户端测试时，即使窗口不在前台也继续处理网络消息。
            Application.runInBackground = true;
            session = GetComponent<MultiplayerSession>();
            relay = GetComponent<UnityRelayProvider>();
            session.StateChanged += LogState;
            var args = Environment.GetCommandLineArgs();
#if UNITY_SERVER
            // 专用服务器没有可操作窗口，默认直接启动；限制主循环目标帧率减少空转。
            Application.targetFrameRate = 60;
            string mode = Argument(args, "--net-mode", "server");
            exitOnServerFailure = true;
#else
            // 普通客户端不带 --net-mode 时只显示界面，等待玩家选择连接方式。
            string mode = Argument(args, "--net-mode", "");
            exitOnServerFailure = Application.isBatchMode && mode == "server";
#endif
            // 示例自动安装可选诊断组件；核心会话不引用诊断程序集。
            // 在网络启动前配置模拟层，已有示例场景也无需手动重新生成。
            try
            {
                if (GetComponent<ServerRequestGate>() == null) gameObject.AddComponent<ServerRequestGate>();
                var configuredAccounts = GetComponent<AccountAdmissionService>();
                accountMode = Argument(args, "--net-auth", configuredAccounts != null && configuredAccounts.enabled ? "configured" : "none").ToLowerInvariant();
                accountName = Argument(args, "--net-account", mode == "host" || mode == "relay-host" ? "host" : "guest");
                ConfigureAccounts(accountMode, args);
                if (!bool.TryParse(Argument(args, "--net-recovery", "true"), out bool recoveryEnabled))
                    throw new ArgumentException("--net-recovery must be true or false.");
                if (recoveryEnabled && GetComponent<RecoveryService>() == null) gameObject.AddComponent<RecoveryService>();
                var timing = GetComponent<NetworkTimingController>() ?? gameObject.AddComponent<NetworkTimingController>();
                timing.ConfigureFromArguments(args);
                var prediction = GetComponent<MovementPredictionSession>();
                if (prediction == null)
                {
                    prediction = gameObject.AddComponent<MovementPredictionSession>();
                    prediction.Settings.EnableVertical = true;
                    prediction.Settings.Obstacles = new[] {
                        new MovementObstacle(Vector2.zero, new Vector2(.5f, 2)),
                        new MovementObstacle(new Vector2(4, -4), Vector2.one, 0, .6f), // 低平台：需跳上，离开边缘会落下。
                        new MovementObstacle(new Vector2(-4, 4), Vector2.one, 2.7f, 3) // 顶板：上跳时阻挡头顶。
                    };
                }
                if (timing.Selected == NetworkTimingPreset.Legacy30 && Array.IndexOf(args, "--net-prediction") < 0)
                    prediction.Select(MovementPredictionMode.Legacy);
                prediction.ConfigureArguments(args);
                SceneManager.sceneLoaded += OnDemoSceneLoaded;
                EnsureDemoObstacles();
                if (!int.TryParse(Argument(args, "--net-target-fps", "60"), out int fps) || fps < 30 || fps > 240)
                    throw new ArgumentException("--net-target-fps must be 30 to 240.");
                Application.targetFrameRate = fps;
                if (GetComponent<NetworkDiagnostics>() == null) gameObject.AddComponent<NetworkDiagnostics>();
                if (!bool.TryParse(Argument(args, "--net-response-metrics", "true"), out bool responses))
                    throw new ArgumentException("--net-response-metrics must be true or false.");
                GetComponent<NetworkResponseMetrics>().CollectResponses = responses;
                var simulator = GetComponent<WeakNetworkSimulator>() ?? gameObject.AddComponent<WeakNetworkSimulator>();
                simulator.ConfigureFromArguments(args);
                if (!Application.isBatchMode && GetComponent<NetworkDiagnosticsPanel>() == null)
                    gameObject.AddComponent<NetworkDiagnosticsPanel>();
                var panel = GetComponent<NetworkDiagnosticsPanel>();
                if (panel != null && Array.IndexOf(args, "--net-no-panel") >= 0) panel.Visible = false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogError("[WeakNet] " + error);
                if (Application.isBatchMode) Application.Quit(1);
                return;
            }
            address = Argument(args, "--net-address", address);
            var lobby = GetComponent<LobbyService>();
            if (lobby != null)
            {
                lobby.LocalNickname = Argument(args, "--net-nickname", lobby.LocalNickname);
                GetComponent<DemoLobbyPanel>()?.SetStartupNickname(lobby.LocalNickname);
            }
            var lobbyEntry = GetComponent<DemoLobbyEntry>();
            if (lobbyEntry != null)
            {
                try { await lobbyEntry.OpenLobbyBeforeConnectingAsync(); }
                catch (Exception exception)
                {
                    error = exception.Message;
                    Debug.LogError("[Lobby] Entry scene failed: " + error);
                    if (Application.isBatchMode) Application.Quit(1);
                    return;
                }
            }
            portText = Argument(args, "--net-port", portText);
            code = Argument(args, "--net-code", code);
            if (relay != null)
            {
                relay.AuthenticationProfile = Argument(args, "--net-profile", relay.AuthenticationProfile);
                if (Argument(args, "--net-protocol", "") == "wss") relay.Protocol = RelayProtocol.SecureWebSocket;
            }
            session.Settings.ApplicationId = Argument(args, "--net-app-id", session.Settings.ApplicationId);
            if (int.TryParse(Argument(args, "--net-max-players", ""), out int capacity)) session.Settings.MaxPlayers = capacity;
            // 命令行小数统一用固定区域格式解析，避免不同系统语言影响超时参数。
            if (float.TryParse(Argument(args, "--net-timeout", ""), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float timeout)) session.Settings.ConnectionTimeoutSeconds = timeout;
            // 只有显式提供测试开关才添加自动移动/退出逻辑，正常游戏不会运行冒烟测试。
            if (Array.IndexOf(args, "--net-smoke") >= 0) gameObject.AddComponent<DemoSmokeProbe>();
            if (Array.IndexOf(args, "--lobby-test") >= 0) gameObject.AddComponent<DemoLobbySmokeProbe>();
            if (Array.IndexOf(args, "--net-latency-test") >= 0) gameObject.AddComponent<DemoLatencyProbe>();
            if (Array.IndexOf(args, "--recovery-test") >= 0) gameObject.AddComponent<DemoRecoveryProbe>();
            if (Array.IndexOf(args, "--security-test") >= 0) gameObject.AddComponent<DemoSecurityProbe>();
            if (Array.IndexOf(args, "--prediction-test") >= 0) gameObject.AddComponent<DemoPredictionProbe>();
            if (Array.IndexOf(args, "--jump-test") >= 0) gameObject.AddComponent<DemoJumpProbe>();
            if (mode.Length > 0) await RunAsync(() => ConnectAsync(mode));
        }

        /// <summary>读取形如 --net-port 7777 的参数，采用首次出现的键及其后一个值。</summary>
        /// <param name="args">进程参数数组。</param>
        /// <param name="key">要查找的完整参数名。</param>
        /// <param name="fallback">未找到键或键后没有值时的默认值。</param>
        public static string Argument(string[] args, string key, string fallback)
        {
            int i = Array.IndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        /// <summary>把示例模式名转换为框架 API；实际业务可以完全替换这层 UI。</summary>
        /// <param name="mode">host、server、client、relay-host 或 relay-client。</param>
        Task ConnectAsync(string mode)
        {
#if UNITY_SERVER
            // 专用服务器包不承担玩家角色，防止错误参数启动需要本地玩家的模式。
            if (mode != "server") throw new ArgumentException("Dedicated Server builds only support --net-mode server.");
#endif
            if (!ushort.TryParse(portText, out ushort port) || port == 0) throw new ArgumentException("Port must be 1–65535.");
            switch (mode)
            {
                case "host": return session.StartLanHostAsync(port);
                case "server": return session.StartDedicatedServerAsync(port);
                case "client": return session.JoinDirectAsync(address, port);
                case "relay-host": return session.StartRelayHostAsync();
                case "relay-client": return session.JoinRelayAsync(code);
                default: throw new ArgumentException("Unknown --net-mode: " + mode);
            }
        }

        /// <summary>统一设置界面忙碌状态并捕获操作错误，避免按钮启动的 Task 异常无人处理。</summary>
        /// <param name="action">在 try/catch 内执行的连接或关闭操作，也捕获调用前的同步校验异常。</param>
        async Task RunAsync(Func<Task> action)
        {
            busy = true;
            error = "";
            try { await action(); }
            catch (OperationCanceledException) { error = "Connection cancelled."; }
            catch (Exception exception)
            {
                error = exception.Message;
                Debug.LogWarning("[Multiplayer] " + error);
                if (exitOnServerFailure) Application.Quit(1);
            }
            finally { busy = false; }
        }

        /// <summary>记录状态以便界面外诊断；服务端故障时返回失败退出码。</summary>
        void LogState(SessionState state)
        {
            Debug.Log("[Multiplayer] State=" + state);
            // 让进程管理器看见失败，避免服务虽然还活着但已经停止接受玩家连接。
            if (exitOnServerFailure && state == SessionState.Faulted) Application.Quit(1);
        }
        /// <summary>解除对会话的订阅，避免组件销毁后仍被回调。</summary>
        void OnDestroy() { if (session != null) session.StateChanged -= LogState; SceneManager.sceneLoaded -= OnDemoSceneLoaded; }

        void OnDemoSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureDemoObstacles();
        // 可视墙仅属于教学场景；移动模块只使用显式配置的静态几何，不扫描任意项目的碰撞体。
        void EnsureDemoObstacles()
        {
#if UNITY_SERVER
            return; // 专用服务器只需要相同的静态几何数据。
#else
            var prediction = GetComponent<MovementPredictionSession>();
            if (prediction == null || GameObject.Find("Arena") == null) return;
            var obstacles = prediction.Settings.Obstacles;
            if (obstacles == null) return;
            for (int i = 0; i < obstacles.Length; i++)
            {
                string name = "Prediction demo wall " + i;
                var existing = GameObject.Find(name);
                if (existing != null)
                {
                    existing.GetComponent<Renderer>().enabled = prediction.UsesCommandMovement;
                    existing.GetComponent<Collider>().enabled = prediction.UsesCommandMovement;
                    continue;
                }
                if (!prediction.UsesCommandMovement) continue;
                var box = obstacles[i]; var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = name;
                wall.transform.position = new Vector3(box.Center.x, (box.Bottom + box.Top) * .5f, box.Center.y);
                wall.transform.localScale = new Vector3(box.HalfSize.x * 2, box.Top - box.Bottom, box.HalfSize.y * 2);
            }
#endif
        }

        /// <summary>绘制示例连接面板与玩家状态；不在这里实现身份系统或完整大厅。</summary>
        void OnGUI()
        {
            if (Application.isBatchMode) return;
            using (ChineseGui.Use()) DrawChineseGui();
        }

        // 在同一中文皮肤作用域中绘制控件，结束后恢复外部界面样式。
        void DrawChineseGui()
        {
            if (session == null || Application.isBatchMode) return;
            float scale = Mathf.Max(0.6f, Mathf.Min(Screen.width / 960f, Screen.height / 640f));
            var previous = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            GUILayout.BeginArea(new Rect(20, 20, 340, 455), GUI.skin.box);
            GUILayout.Label("多人联机 · 连接面板");
            GUILayout.Label("状态：" + ChineseGui.State(session.State) + "   方式：" + ChineseGui.Mode(session.Mode));
            // 错误信息和退出按钮可能增加高度，通过滚动保持小窗口中的操作可达。
            connectionScroll = GUILayout.BeginScrollView(connectionScroll);
            var recovery = GetComponent<RecoveryService>();
            if (recovery != null)
            {
                GUILayout.Label("会话恢复：" + RecoveryLabel(recovery.Status));
                if (recovery.IsBusy)
                {
                    GUILayout.Label("重试 " + recovery.Attempts + " 次 | 剩余 " + recovery.RetryRemainingSeconds.ToString("F0") + " 秒 | 下次 " + recovery.NextAttemptSeconds.ToString("F1") + " 秒");
                    if (GUILayout.Button("取消重连 / 离开")) _ = RunAsync(recovery.CancelReconnectAsync);
                }
                if (recovery.Status == RecoveryStatus.Failed) GUILayout.Label(ChineseGui.Message(recovery.LastError));
                if ((Application.isEditor || Debug.isDebugBuild) && session.State == SessionState.Client && !recovery.IsBusy &&
                    GUILayout.Button("模拟连接中断（测试重连）")) recovery.SimulateUnexpectedDisconnect();
            }
            GUI.enabled = !busy && !(recovery != null && recovery.IsBusy) && (session.State == SessionState.Offline || session.State == SessionState.Faulted);
            var accounts = GetComponent<AccountAdmissionService>();
            GUILayout.Label("账号准入：" + (accounts != null && accounts.enabled ? (accountMode == "development" ? "开发模拟（非真实登录）" : "后端验证") : "未启用"));
            if (Application.isEditor || Debug.isDebugBuild)
            {
                if (accountMode == "none" || accountMode == "development")
                {
                    bool development = GUILayout.Toggle(accountMode == "development", "启用开发账号验收（正式构建禁用）");
                    if (development != (accountMode == "development"))
                    {
                        accountMode = development ? "development" : "none";
                        ConfigureAccounts(accountMode, Array.Empty<string>());
                    }
                    if (development)
                    {
                        GUILayout.Label("开发账号 ID（两端使用不同值）");
                        accountName = GUILayout.TextField(accountName, 64);
                        GetComponent<DevelopmentCredentialProvider>().AccountId = accountName;
                    }
                }
            }
            if (accounts != null && accounts.LocalAccountId.Length > 0) GUILayout.Label("已验证账号：" + accounts.LocalAccountId);
            var gate = GetComponent<ServerRequestGate>();
            if (gate != null && session.Network.IsServer) GUILayout.Label("请求检查：允许 " + gate.Allowed + " / 拒绝 " + gate.Rejected);
            GUILayout.Label("服务器 IP（同机填写 127.0.0.1）");
            address = GUILayout.TextField(address, 64);
            GUILayout.Label("UDP 端口");
            portText = GUILayout.TextField(portText, 5);
            var timing = GetComponent<NetworkTimingController>();
            var prediction = GetComponent<MovementPredictionSession>();
            if (prediction != null)
            {
                GUILayout.Label("移动模式：" + (prediction.Mode == MovementPredictionMode.On ? "本地预测 + 服务器校正" : prediction.Mode == MovementPredictionMode.Off ? "关闭本地预测（相同输入协议）" : "原版服务器移动"));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("开启预测")) { prediction.Select(MovementPredictionMode.On); if (timing.Selected == NetworkTimingPreset.Legacy30) timing.Select(NetworkTimingPreset.Aligned30); EnsureDemoObstacles(); }
                if (GUILayout.Button("关闭预测")) { prediction.Select(MovementPredictionMode.Off); EnsureDemoObstacles(); }
                if (GUILayout.Button("旧版移动")) { prediction.Select(MovementPredictionMode.Legacy); EnsureDemoObstacles(); }
                GUILayout.EndHorizontal();
            }
            if (timing != null)
            {
                GUILayout.Label("同步配置：" + NetworkTimingController.DisplayName(timing.Selected));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("原版 30")) { timing.Select(NetworkTimingPreset.Legacy30); prediction?.Select(MovementPredictionMode.Legacy); EnsureDemoObstacles(); }
                if (GUILayout.Button("对齐 30")) timing.Select(NetworkTimingPreset.Aligned30);
                if (GUILayout.Button("响应 60")) timing.Select(NetworkTimingPreset.Responsive60);
                GUILayout.EndHorizontal();
                GUILayout.Label("两端保持一致；连接后锁定，断开后可切换。");
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("创建局域网主机", GUILayout.Height(30))) _ = RunAsync(() => ConnectAsync("host"));
            if (GUILayout.Button("通过 IP 加入", GUILayout.Height(30))) _ = RunAsync(() => ConnectAsync("client"));
            GUILayout.EndHorizontal();
            if (GUILayout.Button("启动独立服务器", GUILayout.Height(25))) _ = RunAsync(() => ConnectAsync("server"));
            GUILayout.Space(8);
            GUILayout.Label("互联网中继（需配置 Unity 云服务）");
            if (relay != null)
            {
                GUILayout.Label("登录身份缓存名（同机多开请使用不同名称）");
                relay.AuthenticationProfile = GUILayout.TextField(relay.AuthenticationProfile, 30);
                bool wss = GUILayout.Toggle(relay.Protocol == RelayProtocol.SecureWebSocket, "WSS 房间（有浏览器玩家时全员开启）");
                relay.Protocol = wss ? RelayProtocol.SecureWebSocket : RelayProtocol.Dtls;
            }
            if (GUILayout.Button("创建中继房间", GUILayout.Height(28))) _ = RunAsync(() => ConnectAsync("relay-host"));
            GUILayout.Label("输入中继房间码");
            code = GUILayout.TextField(code, 32);
            if (GUILayout.Button("通过房间码加入", GUILayout.Height(28))) _ = RunAsync(() => ConnectAsync("relay-client"));
            // 连接进行中仍允许取消或断开；不让启动按钮的禁用状态影响退出入口。
            GUI.enabled = true;
            foreach (var player in FindObjectsByType<PredictedMovement>(FindObjectsSortMode.None))
                if (player.IsSpawned && player.IsOwner)
                {
                    if (player.ConfigurationMismatch) GUILayout.Label("移动配置不一致或出生点无效，请统一双方的静态场景配置。");
                    else if (player.IsActive) GUILayout.Label("预测输入待确认 " + player.PendingInputs + " | 校正 " + player.Corrections + " 次 / " + player.LastCorrectionError.ToString("F3") + " 米 | 垂直速度校正 " + player.LastVelocityCorrection.ToString("F2") + " 米/秒 | 重放 " + player.ReplayedInputs +
                        (player.WaitingForAuthority ? " | 等待服务器重新定基" : ""));
                }
            if (!string.IsNullOrEmpty(session.JoinCode)) GUILayout.Label("房间码：" + session.JoinCode);
            if (session.State != SessionState.Offline && GUILayout.Button(busy ? "取消连接 / 断开" : "断开连接", GUILayout.Height(28)))
                _ = RunAsync(session.StopAsync);
            string message = string.IsNullOrEmpty(error) ? session.LastError : error;
            if (message.Length > 0) GUILayout.Label(ChineseGui.Message(message), GUI.skin.box);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            if (session.IsOnline && GetComponent<LobbyService>() == null)
            {
                GUILayout.BeginArea(new Rect(Screen.width / scale - 245, 20, 225, 280), GUI.skin.box);
                GUILayout.Label("玩家 / 同步状态");
                // 教学示例从已生成对象推导名单；生产大厅应使用独立的同步名单数据。
                foreach (var player in FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None))
                    if (player.IsSpawned) GUILayout.Label("玩家 " + player.OwnerClientId + (player.IsOwner ? "（你）" : "") + " | " + (player.Ready.Value ? "已准备" : "未准备"));
                GUILayout.EndArea();
            }
            GUI.matrix = previous;
        }

        /// <summary>只在连接前配置示例账号来源；真正的后端密钥和票据从进程环境读取，不放在命令行或场景。</summary>
        void ConfigureAccounts(string mode, string[] args)
        {
            var service = GetComponent<AccountAdmissionService>();
            if (mode == "configured")
            {
                if (service == null || !service.enabled) throw new InvalidOperationException("AUTH_INVALID_CONFIGURATION");
                return; // 保留 Inspector 或项目引导代码已配置的插件。
            }
            if (mode == "none") { if (service != null) service.enabled = false; return; }
            service = service ?? gameObject.AddComponent<AccountAdmissionService>();
            service.enabled = true;
            if (mode == "development")
            {
                if (!(Application.isEditor || Debug.isDebugBuild)) throw new InvalidOperationException("AUTH_DEVELOPMENT_DISABLED");
                var credentials = GetComponent<DevelopmentCredentialProvider>() ?? gameObject.AddComponent<DevelopmentCredentialProvider>();
                var validator = GetComponent<DevelopmentAccountValidator>() ?? gameObject.AddComponent<DevelopmentAccountValidator>();
                credentials.AccountId = accountName;
                credentials.TestCredentialKind = Argument(args, "--net-auth-test-ticket", "valid");
                service.SetProviders(credentials, validator);
            }
            else if (mode == "http")
            {
                var credentials = GetComponent<MemoryCredentialProvider>() ?? gameObject.AddComponent<MemoryCredentialProvider>();
                credentials.SetCredential(Environment.GetEnvironmentVariable("HAN_ACCOUNT_TOKEN"));
                var validator = GetComponent<HttpAccountTokenValidator>() ?? gameObject.AddComponent<HttpAccountTokenValidator>();
                validator.Endpoint = Environment.GetEnvironmentVariable("HAN_ACCOUNT_BACKEND_URL") ?? "";
                service.SetProviders(credentials, validator);
            }
            else throw new ArgumentException("--net-auth must be none, development or http.");
        }

        // 状态枚举保持稳定，玩家界面使用中文解释。
        static string RecoveryLabel(RecoveryStatus status)
        {
            switch (status)
            {
                case RecoveryStatus.Online: return "在线";
                case RecoveryStatus.Waiting: return "等待重试";
                case RecoveryStatus.Connecting: return "正在重新连接";
                case RecoveryStatus.Restoring: return "正在恢复服务器状态";
                case RecoveryStatus.Recovered: return "已恢复原会话";
                case RecoveryStatus.Cancelled: return "已取消";
                case RecoveryStatus.Failed: return "恢复失败，请断开后重新加入";
                default: return "离线";
            }
        }
    }
}
