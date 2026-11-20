// 框架的会话入口：统一处理直连/Relay、主机/客户端/独立服务器以及启动和关闭竞态。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer
{
    /// <summary>
    /// 每个 NetworkManager 对应一个会话，在 Unity 主线程调用其接口。
    /// 本组件独占网络启动、关闭和准入回调，游戏 UI 应通过它操作连接。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
    public sealed class MultiplayerSession : MonoBehaviour
    {
        // Inspector 中的配置是下一次启动的输入；运行中的连接使用独立快照。
        [SerializeField] SessionSettings settings = new SessionSettings();
        // Unity 不能直接序列化接口字段，因此用 MonoBehaviour 保存实现 IRelayProvider 的组件。
        [SerializeField] MonoBehaviour relayProvider;

        NetworkManager manager;  // NGO：玩家、网络对象和连接事件。
        UnityTransport transport; // UTP：实际端点与底层数据传输。
        AdmissionPolicy admission; // 仅本次会话使用的兼容性和容量策略。
        CancellationTokenSource startupCancellation; // 合并主动取消、外部取消和连接超时。
        Task startupTask; // 关闭流程需要等待正在进行的启动完成清理。
        Task stopTask;    // 多次 StopAsync 共用同一个关闭任务。
        TaskCompletionSource<bool> connected; // 把 NGO 的连接回调桥接为可等待的 Task。
        bool starting;      // 启动占用标记，防止同时开启两个会话。
        bool stopRequested; // 区分主动退出和意外断线。
        bool destroyed;     // 组件销毁后阻止异步结果继续访问 Unity 对象。
        bool intentionalStopPending; // 即使意外关闭已开始，后来的用户退出仍须撤销重连。
        ISessionContinuity continuity; // 每次启动冻结唯一的可选连续性模块。
        IConnectionAuthenticator authenticator;
        IServerRequestValidator requestValidator;
        CancellationTokenSource authenticationLifetime;
        sealed class PendingAuthentication
        {
            public CancellationTokenSource Cancellation;
            public NetworkManager.ConnectionApprovalResponse Response;
        }
        readonly Dictionary<ulong, PendingAuthentication> pendingAuthentications = new Dictionary<ulong, PendingAuthentication>();
        bool hostAuthenticated;
        string hostApprovalError;
        string activeApplicationId;
        public bool AccountAuthenticationEnabled => authenticator != null;
        public string GetAccountId(ulong clientId) => authenticator?.GetAccountId(clientId);
        /// <summary>服务器销毁业务对象时释放其序号作用域；保留该连接的总请求预算。</summary>
        public void ForgetRequestScope(ulong sender, ulong sequenceScope) => requestValidator?.ForgetScope(sender, sequenceScope);
        /// <summary>只传 NGO 提供的 sender 和服务器查到的 owner；业务有效性和阶段条件也在服务器计算。</summary>
        public string ValidateRequest(ServerRequestRule rule, ulong sender, ulong owner, int bytes, bool validPayload = true, bool phaseAllowed = true, uint? sequence = null, ulong sequenceScope = 0)
        {
            if (requestValidator != null) return requestValidator.Validate(rule, sender, owner, bytes, validPayload, phaseAllowed, sequence, sequenceScope);
            if (rule == null || !manager.IsServer || manager.ShutdownInProgress || !manager.ConnectedClients.ContainsKey(sender)) return "REQUEST_NOT_CONNECTED";
            if (bytes < 0 || bytes > rule.MaximumPayloadBytes || !validPayload) return "REQUEST_INVALID";
            if (rule.RequireOwnership && sender != owner) return "REQUEST_NOT_OWNER";
            if (!phaseAllowed) return "REQUEST_PHASE";
            if (rule.RequireAccount || authenticator != null) return "REQUEST_VALIDATOR_REQUIRED";
            return null; // 没有安装可选工具时保留核心权限/参数检查；限流和序号由业务原有代码处理。
        }
        string lastAddress, lastRelayCode;
        ushort lastPort;
        ConnectionMode lastClientMode;
        public bool IsClientSession { get; private set; }
        /// <summary>本机凭证的端点绑定键；不输出到日志，不含玩家凭证。</summary>
        public string ClientTargetKey { get; private set; }
        public bool IsResumingLocalClient => continuity != null && continuity.IsLocalResume;
        public bool TryGetPreviousClientId(ulong id, out ulong previous)
        { previous = ulong.MaxValue; return continuity != null && continuity.TryGetPreviousClientId(id, out previous); }
        public bool IsHeldClient(ulong id) => continuity != null && continuity.IsHeldClient(id);

        /// <summary>保留原来的直连/Relay 目标；只有客户端可重试，主机迁移不属于此入口。</summary>
        public Task RejoinLastClientAsync(CancellationToken token = default)
        {
            if (!IsClientSession) throw new InvalidOperationException("No client connection target is available.");
            return lastClientMode == ConnectionMode.Direct ? JoinDirectAsync(lastAddress, lastPort, token) : JoinRelayAsync(lastRelayCode, token);
        }

        /// <summary>管理端主动移除默认撤销重连资格；测试短时中断可显式允许恢复。</summary>
        public void DisconnectPlayer(ulong id, string reason, bool allowRecovery = false)
        {
            if (!manager.IsServer || id == NetworkManager.ServerClientId) throw new InvalidOperationException("Only the server can disconnect a remote player.");
            if (!allowRecovery) continuity?.RevokeClient(id);
            if (!allowRecovery) authenticator?.RevokeClient(id);
            if (manager.ConnectedClients.ContainsKey(id)) manager.DisconnectClient(id, reason);
        }

        /// <summary>下次启动使用的配置；运行中修改不会影响已经取得的快照。</summary>
        public SessionSettings Settings => settings;
        /// <summary>底层 NGO 实例，供玩法读取信息；不要绕过会话入口启动或关闭它。</summary>
        public NetworkManager Network => manager;
        /// <summary>当前本地会话状态。</summary>
        public SessionState State { get; private set; } = SessionState.Offline;
        /// <summary>最近一次启动选择的传输路径。</summary>
        public ConnectionMode Mode { get; private set; }
        /// <summary>本次 Relay 分配的加入码；非 Relay 模式或清理后为空。</summary>
        public string JoinCode { get; private set; } = "";
        /// <summary>最近一次启动失败或意外断线的原因；新启动会清空该值。</summary>
        public string LastError { get; private set; } = "";
        /// <summary>本地已完成 Host、Client 或 Server 启动；不代表业务资源全部加载完毕。</summary>
        public bool IsOnline => State == SessionState.Host || State == SessionState.Client || State == SessionState.Server;
        /// <summary>本次启动的容量快照；空闲时为 0，运行中修改 Settings 不会改变它。</summary>
        public int ActiveMaxPlayers { get; private set; }
        /// <summary>服务器已批准、但尚未正式连接的请求数，供加载屏障等功能查询。</summary>
        public int PendingApprovalCount => pendingAuthentications.Count + (admission == null ? 0 : admission.PendingCount(manager.ConnectedClientsIds, Time.realtimeSinceStartupAsDouble));
        /// <summary>状态发生实际变化时触发，适合更新连接 UI。</summary>
        public event Action<SessionState> StateChanged;
        /// <summary>服务器收到玩家连接事件，客户端收到本地连接事件；完整名单需另行同步。</summary>
        public event Action<ulong> PlayerConnected;
        /// <summary>转发 NGO 断开事件；不要将该事件直接当成所有客户端共享的大厅名单。</summary>
        public event Action<ulong> PlayerDisconnected;

        /// <summary>缓存同一对象上的网络组件，并注册会话需要的生命周期回调。</summary>
        void Awake()
        {
            manager = GetComponent<NetworkManager>();
            transport = GetComponent<UnityTransport>();
            // 通过代码 AddComponent 创建的 NetworkManager 可能还没有序列化配置。
            if (manager.NetworkConfig == null) manager.NetworkConfig = new NetworkConfig();
            manager.OnClientConnectedCallback += OnConnected;
            manager.OnClientDisconnectCallback += OnDisconnected;
            manager.OnTransportFailure += OnTransportFailure;
            manager.OnClientStopped += OnNetworkStopped;
            manager.OnServerStopped += OnNetworkStopped;
        }

        /// <summary>空闲时更换 Relay 适配组件；传 null 可移除适配器。</summary>
        /// <param name="provider">必须实现 IRelayProvider 的 MonoBehaviour。</param>
        public void SetRelayProvider(MonoBehaviour provider)
        {
            EnsureIdle();
            if (provider != null && !(provider is IRelayProvider))
                throw new ArgumentException("Provider must implement IRelayProvider.", nameof(provider));
            relayProvider = provider;
        }

        /// <summary>启动直连主机：当前进程同时承担服务器和一个玩家。</summary>
        /// <param name="port">UDP 监听端口。</param>
        /// <param name="listenAddress">默认监听所有 IPv4 网卡；客户端应填写实际可达 IP。</param>
        /// <param name="cancellationToken">允许调用方提前取消连接。</param>
        public Task StartLanHostAsync(ushort port = 7777, string listenAddress = "0.0.0.0", CancellationToken cancellationToken = default)
            => BeginStart(ConnectionMode.Direct, true, false, "127.0.0.1", port, listenAddress, null, null, cancellationToken);

        /// <summary>按 IP 加入局域网主机或公网独立服务器，等待本地连接成功。</summary>
        /// <param name="address">目标服务器的 IP 字面量。</param>
        /// <param name="port">目标服务器的 UDP 端口。</param>
        /// <param name="cancellationToken">允许调用方提前取消连接。</param>
        public Task JoinDirectAsync(string address, ushort port = 7777, CancellationToken cancellationToken = default)
            => BeginStart(ConnectionMode.Direct, false, false, address, port, null, null, null, cancellationToken);

        /// <summary>只启动服务器，不生成本地玩家；Windows 和 Linux 服务端共用该入口。</summary>
        /// <param name="port">UDP 监听端口。</param>
        /// <param name="listenAddress">用于接收连接的本地监听地址。</param>
        /// <param name="cancellationToken">允许取消尚未完成的启动。</param>
        public Task StartDedicatedServerAsync(ushort port = 7777, string listenAddress = "0.0.0.0", CancellationToken cancellationToken = default)
            => BeginStart(ConnectionMode.Direct, false, true, "127.0.0.1", port, listenAddress, null, null, cancellationToken);

        /// <summary>创建 Relay 房间并作为 Host 运行；游戏逻辑仍由当前进程计算。</summary>
        /// <param name="region">可选 Relay 地区，null 表示由服务选择。</param>
        /// <param name="cancellationToken">同时控制云请求等待和网络启动的取消。</param>
        public Task StartRelayHostAsync(string region = null, CancellationToken cancellationToken = default)
            => BeginStart(ConnectionMode.Relay, true, false, null, 0, null, null, region, cancellationToken);

        /// <summary>按房间码加入 Relay 主机，输入会先统一大小写并校验。</summary>
        /// <param name="joinCode">主机分享的 Relay 加入码。</param>
        /// <param name="cancellationToken">允许取消云请求等待或连接过程。</param>
        public Task JoinRelayAsync(string joinCode, CancellationToken cancellationToken = default)
            => BeginStart(ConnectionMode.Relay, false, false, null, 0, null, SessionSettings.NormalizeJoinCode(joinCode), null, cancellationToken);

        /// <summary>所有启动入口的同步准备阶段：校验参数、占用启动状态并建立总超时。</summary>
        // host/server 均为 false 时是客户端；joinCode/region 只在 Relay 路径中使用。
        Task BeginStart(ConnectionMode mode, bool host, bool server, string address, ushort port,
            string listenAddress, string joinCode, string region, CancellationToken externalToken)
        {
            EnsureIdle();
            externalToken.ThrowIfCancellationRequested();
            var snapshot = settings.Snapshot();
            if (mode == ConnectionMode.Direct)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                // 浏览器不能走此处的原生 UDP 直连路径，应使用 Relay WSS。
                throw new PlatformNotSupportedException("WebGL cannot use direct UDP. Use a Relay room (WSS).");
#endif
                SessionSettings.ValidateEndpoint(address, port);
                if (listenAddress != null) SessionSettings.ValidateEndpoint(listenAddress, port);
            }
            else if (!(relayProvider is IRelayProvider))
                throw new InvalidOperationException("Assign an IRelayProvider component before using Relay.");
            continuity = null;
            authenticator = null; requestValidator = null;
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component.isActiveAndEnabled)
                {
                    if (component is ISessionContinuity candidate)
                    { if (continuity != null) throw new InvalidOperationException("Only one session continuity module is allowed."); continuity = candidate; }
                    if (component is IConnectionAuthenticator account)
                    { if (authenticator != null) throw new InvalidOperationException("Only one account authenticator is allowed."); authenticator = account; }
                    if (component is IServerRequestValidator validator)
                    { if (requestValidator != null) throw new InvalidOperationException("Only one server request validator is allowed."); requestValidator = validator; }
                }
            IsClientSession = !host && !server;
            if (IsClientSession)
            {
                lastAddress = address; lastPort = port; lastRelayCode = joinCode; lastClientMode = mode;
                ClientTargetKey = mode == ConnectionMode.Direct ? "Direct:" + System.Net.IPAddress.Parse(address) + ":" + port : "Relay:" + joinCode;
            }
            // 不覆盖其他系统的准入回调，否则可能绕过原有的身份或业务规则。
            if (manager.ConnectionApprovalCallback != null && manager.ConnectionApprovalCallback != Approve)
                throw new InvalidOperationException("NetworkManager already has a connection approval callback.");

            starting = true;
            ActiveMaxPlayers = snapshot.MaxPlayers;
            stopRequested = false;
            intentionalStopPending = false;
            LastError = "";
            JoinCode = "";
            Mode = mode;
            hostAuthenticated = false; hostApprovalError = null;
            activeApplicationId = snapshot.ApplicationId;
            authenticationLifetime = new CancellationTokenSource();
            // 一个令牌覆盖整次启动，避免云请求耗时和网络建连分别重复计算等待期限。
            startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            startupCancellation.CancelAfter(TimeSpan.FromSeconds(snapshot.ConnectionTimeoutSeconds));
            startupTask = StartCoreAsync(snapshot, mode, host, server, address, port, listenAddress, joinCode, region, externalToken);
            return startupTask;
        }

        /// <summary>执行实际启动；成功进入在线状态，失败则先清理网络再把异常交还调用方。</summary>
        async Task StartCoreAsync(SessionSettings snapshot, ConnectionMode mode, bool host, bool server,
            string address, ushort port, string listenAddress, string joinCode, string region, CancellationToken externalToken)
        {
            var token = startupCancellation.Token;
            try
            {
                SetState(SessionState.Starting);
                // 让出一轮主循环，使 BeginStart 能保存 startupTask，关闭流程便可等待同一任务。
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                manager.NetworkConfig.NetworkTransport = transport;
                manager.NetworkConfig.TickRate = snapshot.TickRate;
                manager.NetworkConfig.ConnectionApproval = true;
                byte[] accountData = null;
                if (authenticator != null)
                {
                    try { accountData = await AsyncCancellation.WaitAsync(authenticator.CreateAuthenticationDataAsync(!server, token), token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { throw new InvalidOperationException("AUTH_CREDENTIAL_PROVIDER_FAILED"); }
                    if (authenticator.MaximumPendingAuthentications < 1 || authenticator.MaximumPendingAuthentications > 128 ||
                        float.IsNaN(authenticator.AuthenticationTimeoutSeconds) || authenticator.AuthenticationTimeoutSeconds < 1 || authenticator.AuthenticationTimeoutSeconds > 30)
                        throw new InvalidOperationException("AUTH_INVALID_CONFIGURATION");
                    if (host)
                    {
                        // NGO 自动批准 Host，且不等待其 Pending；必须在监听前完成真正的账号验证。
                        using (var check = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            check.CancelAfter(TimeSpan.FromSeconds(authenticator.AuthenticationTimeoutSeconds));
                            string rejection;
                            try { rejection = await AsyncCancellation.WaitAsync(authenticator.AuthenticateAsync(0, accountData, snapshot.ApplicationId, check.Token), check.Token); }
                            catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("AUTH_TIMEOUT"); }
                            catch (Exception) { throw new InvalidOperationException("AUTH_PROVIDER_FAILED"); }
                            if (rejection != null) throw new InvalidOperationException(SafeAuthenticationError(rejection));
                            hostAuthenticated = true;
                        }
                    }
                }
                manager.NetworkConfig.ConnectionData = ConnectionDataEnvelope.Encode(snapshot.CreateHandshake(), continuity?.CreateClientData(), accountData);
                // 预留期限略大于 NGO 的准入等待时间，给正常连接留出完成回调的余量。
                admission = new AdmissionPolicy(snapshot, manager.NetworkConfig.ClientConnectionBufferTimeout + 5);
                manager.ConnectionApprovalCallback = Approve;

                if (mode == ConnectionMode.Relay)
                {
                    var provider = (IRelayProvider)relayProvider;
                    var request = host ? provider.CreateAsync(snapshot.MaxPlayers, region, token) : provider.JoinAsync(joinCode, token);
                    // 先等云服务返回数据，确认没有取消后才允许修改 Transport。
                    var relay = await AsyncCancellation.WaitAsync(request, token);
                    token.ThrowIfCancellationRequested();
                    transport.SetRelayServerData(relay.ServerData);
                    transport.UseWebSockets = relay.UseWebSockets;
                    JoinCode = relay.JoinCode;
                }
                else
                {
                    // 上一次会话可能使用 Relay，直连前明确恢复普通 UDP 配置。
                    transport.UseWebSockets = false;
                    transport.UseEncryption = false;
                    transport.SetConnectionData(true, address, port, listenAddress);
                }

                // 必须先准备回调任务，再调用 StartHost；本地 Host 的连接回调可能很快到达。
                connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SetState(SessionState.Connecting);
                token.ThrowIfCancellationRequested();
                bool started = host ? manager.StartHost() : server ? manager.StartServer() : manager.StartClient();
                if (host && hostApprovalError != null) throw new InvalidOperationException(hostApprovalError);
                if (!started) throw new InvalidOperationException("Network startup failed. Check the port and Unity log.");
                // 纯服务器没有本地玩家连接回调，监听启动成功即可完成这个等待点。
                if (server) connected.TrySetResult(true);
                await AsyncCancellation.WaitAsync(connected.Task, token);
                token.ThrowIfCancellationRequested();
                if (!manager.IsListening || manager.ShutdownInProgress)
                    throw new InvalidOperationException("The network stopped during startup.");
                SetState(host ? SessionState.Host : server ? SessionState.Server : SessionState.Client);
            }
            catch (Exception error)
            {
                // 同样是取消异常：主动取消回到 Offline，总期限耗尽则报告 TimeoutException。
                bool cancelled = error is OperationCanceledException && (stopRequested || externalToken.IsCancellationRequested || destroyed);
                LastError = cancelled ? "" : error is OperationCanceledException ? "Connection timed out." : error.Message;
                await ShutdownNetworkAsync();
                JoinCode = "";
                SetState(cancelled ? SessionState.Offline : SessionState.Faulted);
                if (error is OperationCanceledException && !cancelled) throw new TimeoutException(LastError, error);
                throw;
            }
            finally
            {
                // 无论成功、超时或失败，都解除本次启动占用并释放取消资源。
                startupCancellation.Dispose();
                startupCancellation = null;
                connected = null;
                starting = false;
            }
        }

        /// <summary>取消尚未完成的启动或关闭当前连接；重复调用共享正在执行的关闭任务。</summary>
        /// <returns>NGO 完成关闭后的任务，随后才适合启动新会话。</returns>
        public Task StopAsync()
            => StopInternalAsync(true);

        Task StopInternalAsync(bool intentional)
        {
            if (intentional) intentionalStopPending = true;
            if (stopTask != null && !stopTask.IsCompleted) return stopTask;
            stopRequested = true;
            startupCancellation?.Cancel();
            stopTask = StopCoreAsync();
            return stopTask;
        }

        /// <summary>先等启动过程结束清理，再关闭网络并清空当前房间码。</summary>
        async Task StopCoreAsync()
        {
            SetState(SessionState.Stopping);
            // 延后一轮，让 stopTask 先保存完成，并避免在 NGO 回调中同步重入关闭流程。
            await Task.Yield();
            bool handledIntentional = intentionalStopPending;
            if (handledIntentional) await NotifyIntentionalStopAsync();
            if (startupTask != null && !startupTask.IsCompleted)
            {
                try { await startupTask; } catch (Exception) { /* 原始异常仍由启动调用方接收，关闭流程继续清理。 */ }
            }
            await ShutdownNetworkAsync();
            if (intentionalStopPending && !handledIntentional) await NotifyIntentionalStopAsync();
            JoinCode = "";
            SetState(SessionState.Offline);
        }

        async Task NotifyIntentionalStopAsync()
        {
            if (continuity == null) return;
            try { await continuity.BeforeIntentionalStopAsync(); }
            catch (Exception error) { Debug.LogException(error); } // 离场确认失败也必须关闭本地连接。
        }

        /// <summary>发起 NGO 关闭并逐帧等待结束，避免旧 Transport 尚未退出就开始新连接。</summary>
        async Task ShutdownNetworkAsync()
        {
            if (destroyed || manager == null) return;
            CancelAuthentications();
            if (manager.IsListening && !manager.ShutdownInProgress) manager.Shutdown();
            while (!destroyed && manager != null && (manager.IsListening || manager.ShutdownInProgress)) await Task.Yield();
            admission = null;
            ActiveMaxPlayers = 0;
        }

        void CancelAuthentications()
        {
            authenticationLifetime?.Cancel();
            var entries = new List<KeyValuePair<ulong, PendingAuthentication>>(pendingAuthentications);
            pendingAuthentications.Clear();
            foreach (var entry in entries)
            {
                entry.Value.Cancellation.Cancel();
                authenticator?.CompleteAuthentication(entry.Key, false);
                SetApprovalResponse(entry.Value.Response, "SESSION_NOT_READY");
            }
            authenticationLifetime?.Dispose(); authenticationLifetime = null;
        }

        /// <summary>阻止在组件销毁、启动中、关闭中或在线时重配会话。</summary>
        void EnsureIdle()
        {
            if (destroyed) throw new ObjectDisposedException(nameof(MultiplayerSession));
            if (starting || (stopTask != null && !stopTask.IsCompleted) || manager.IsListening || manager.ShutdownInProgress || IsOnline)
                throw new InvalidOperationException("A session is already active. Await StopAsync before starting another.");
        }

        /// <summary>服务器准入回调：检查握手和容量，决定是否批准并自动生成玩家对象。</summary>
        void Approve(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            bool decoded = ConnectionDataEnvelope.TryDecode(request.Payload, out var handshake, out var extension, out var account);
            string reason = !decoded ? "INVALID_CONNECTION_DATA" : admission == null ? "SESSION_NOT_READY" : admission.ValidateHandshake(handshake);
            if (reason != null) { FinishApproval(request.ClientNetworkId, handshake, extension, response, reason); return; }
            if (authenticator == null)
            { FinishApproval(request.ClientNetworkId, handshake, extension, response, account.Length > 0 ? "AUTH_UNSUPPORTED" : null); return; }
            if (request.ClientNetworkId == NetworkManager.ServerClientId)
            { FinishApproval(0, handshake, extension, response, hostAuthenticated ? null : "AUTH_HOST_NOT_VALIDATED"); return; }
            if (pendingAuthentications.Count >= authenticator.MaximumPendingAuthentications || pendingAuthentications.ContainsKey(request.ClientNetworkId))
            { SetApprovalResponse(response, "AUTH_BUSY"); return; }
            var pending = new PendingAuthentication { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(authenticationLifetime.Token), Response = response };
            pending.Cancellation.CancelAfter(TimeSpan.FromSeconds(authenticator.AuthenticationTimeoutSeconds));
            pendingAuthentications.Add(request.ClientNetworkId, pending);
            response.Pending = true;
            _ = AuthenticateAndApproveAsync(request.ClientNetworkId, handshake, extension, account, pending, activeApplicationId);
        }

        async Task AuthenticateAndApproveAsync(ulong id, byte[] handshake, byte[] extension, byte[] account, PendingAuthentication pending, string applicationId)
        {
            string reason;
            try
            {
                reason = await AsyncCancellation.WaitAsync(authenticator.AuthenticateAsync(id, account, applicationId, pending.Cancellation.Token), pending.Cancellation.Token);
                if (reason != null) reason = SafeAuthenticationError(reason);
            }
            catch (OperationCanceledException) { reason = "AUTH_TIMEOUT"; }
            catch (Exception) { reason = "AUTH_PROVIDER_FAILED"; } // 不记录第三方异常，异常可能含凭证或后端响应正文。
            bool current = !destroyed && manager != null && manager.IsServer && !manager.ShutdownInProgress &&
                pendingAuthentications.TryGetValue(id, out var tracked) && ReferenceEquals(tracked, pending);
            if (current)
            {
                pendingAuthentications.Remove(id);
                FinishApproval(id, handshake, extension, pending.Response, reason);
            }
            pending.Cancellation.Dispose();
        }

        void FinishApproval(ulong id, byte[] handshake, byte[] extension, NetworkManager.ConnectionApprovalResponse response, string reason)
        {
            try
            {
                if (reason == null) reason = continuity != null ? continuity.PrepareAdmission(id, extension) : extension.Length > 0 ? "RECOVERY_UNSUPPORTED" : null;
                if (reason == null) reason = admission.TryReserve(id, handshake, manager.ConnectedClientsIds,
                    Time.realtimeSinceStartupAsDouble, continuity?.HeldSeatCount ?? 0);
                if (reason == null)
                    foreach (var component in GetComponents<MonoBehaviour>())
                        if (component.isActiveAndEnabled && component is IConnectionAdmissionRule rule)
                        { reason = rule.CheckAdmission(id); if (reason != null) break; }
            }
            catch (Exception) { reason = "ADMISSION_CHECK_FAILED"; }
            continuity?.CompleteAdmission(id, reason == null);
            authenticator?.CompleteAuthentication(id, reason == null);
            if (reason != null) admission?.Release(id);
            if (id == 0 && reason != null) hostApprovalError = reason;
            SetApprovalResponse(response, reason, reason == null && manager.NetworkConfig.PlayerPrefab != null);
        }

        static void SetApprovalResponse(NetworkManager.ConnectionApprovalResponse response, string reason, bool createPlayer = false)
        {
            response.Approved = reason == null;
            response.Reason = reason ?? "";
            // 由实例入口随后填入是否生成玩家；Pending 必须最后清除。
            response.CreatePlayerObject = createPlayer;
            response.Pending = false;
        }

        static string SafeAuthenticationError(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !value.StartsWith("AUTH_", StringComparison.Ordinal)) return "AUTH_REJECTED";
            foreach (char c in value) if (!((c >= 'A' && c <= 'Z') || c == '_' || (c >= '0' && c <= '9'))) return "AUTH_REJECTED";
            return value;
        }

        /// <summary>连接完成后释放临时名额，并在本地玩家连接完成时结束启动等待。</summary>
        void OnConnected(ulong id)
        {
            admission?.Release(id);
            if (id == manager.LocalClientId) connected?.TrySetResult(true);
            Raise(PlayerConnected, id);
        }

        /// <summary>处理玩家离开；远端玩家退出不会让服务器自身进入失败状态。</summary>
        void OnDisconnected(ulong id)
        {
            admission?.Release(id);
            if (pendingAuthentications.TryGetValue(id, out var pending))
            {
                pendingAuthentications.Remove(id);
                pending.Cancellation.Cancel();
                authenticator?.CompleteAuthentication(id, false);
            }
            Raise(PlayerDisconnected, id);
            if (manager.IsServer || id != manager.LocalClientId || stopRequested) return;
            FailConnection(string.IsNullOrEmpty(manager.DisconnectReason) ? "Disconnected from the host." : manager.DisconnectReason);
        }

        /// <summary>把底层 Transport 故障转换为会话错误。</summary>
        void OnTransportFailure() => FailConnection("Transport failed. Check the network, port and Relay configuration.");
        /// <summary>捕获没有通过 StopAsync 发起的网络停止；wasHost 是 NGO 传入的历史角色信息。</summary>
        void OnNetworkStopped(bool wasHost)
        {
            if (!stopRequested && (starting || IsOnline)) FailConnection("The network stopped.");
        }

        /// <summary>启动中让等待任务失败；已经在线时启动统一的断线清理流程。</summary>
        void FailConnection(string reason)
        {
            if (stopRequested || destroyed) return;
            if (starting) { connected?.TrySetException(new InvalidOperationException(reason)); return; }
            if (!IsOnline) return;
            LastError = reason;
            _ = StopAfterFailureAsync();
        }

        /// <summary>完成资源清理后保留 Faulted 状态，便于 UI 展示意外断线原因。</summary>
        async Task StopAfterFailureAsync()
        {
            await StopInternalAsync(false);
            if (!destroyed && !intentionalStopPending) SetState(SessionState.Faulted);
        }

        /// <summary>由主机或服务器发起 NGO 同步场景切换。</summary>
        /// <param name="sceneName">已加入构建场景列表的场景名称或路径。</param>
        /// <returns>NGO 的请求受理状态，不表示所有客户端已经完成加载。</returns>
        public SceneEventProgressStatus LoadScene(string sceneName)
        {
            if (!IsOnline || !manager.IsServer) throw new InvalidOperationException("Only an active host/server can change the network scene.");
            if (!manager.NetworkConfig.EnableSceneManagement) throw new InvalidOperationException("Enable NGO scene management first.");
            if (!Application.CanStreamedLevelBeLoaded(sceneName)) throw new ArgumentException("Add the scene to the build scene list.", nameof(sceneName));
            return manager.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        }

        /// <summary>仅在状态实际变化时通知订阅者；销毁后的异步延续不能再推送事件。</summary>
        void SetState(SessionState value)
        {
            if (destroyed || State == value) return;
            State = value;
            Raise(StateChanged, value);
        }

        /// <summary>逐个通知订阅者，隔离单个 UI 或玩法回调异常，保持会话清理流程可继续。</summary>
        static void Raise<T>(Action<T> handlers, T value)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try { handler(value); } catch (Exception error) { Debug.LogException(error); }
            }
        }

        /// <summary>销毁时取消异步启动、解除事件订阅，并请求关闭仍在运行的网络。</summary>
        void OnDestroy()
        {
            destroyed = true;
            CancelAuthentications();
            startupCancellation?.Cancel();
            if (manager == null) return;
            manager.OnClientConnectedCallback -= OnConnected;
            manager.OnClientDisconnectCallback -= OnDisconnected;
            manager.OnTransportFailure -= OnTransportFailure;
            manager.OnClientStopped -= OnNetworkStopped;
            manager.OnServerStopped -= OnNetworkStopped;
            if (manager.ConnectionApprovalCallback == Approve) manager.ConnectionApprovalCallback = null;
            if (manager.IsListening && !manager.ShutdownInProgress) manager.Shutdown();
        }
    }
}
