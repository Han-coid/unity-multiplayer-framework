// 可选重连编排：进程内凭证、服务器保留期、取消/退避与权威状态恢复，不依赖大厅或具体玩法。
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Recovery
{
    public enum RecoveryStatus { Offline, Online, Waiting, Connecting, Restoring, Recovered, Cancelled, Failed }

    [Serializable]
    public sealed class RecoverySettings
    {
        public bool AutoReconnect = true;
        public float RetentionSeconds = 30;
        public float RetryWindowSeconds = 45;
        public float AttemptTimeoutSeconds = 5;
        public float RestoreTimeoutSeconds = 30;
        public RecoverySettings Copy()
        {
            foreach (float value in new[] { RetentionSeconds, RetryWindowSeconds, AttemptTimeoutSeconds, RestoreTimeoutSeconds })
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > 300) throw new ArgumentOutOfRangeException("Recovery timing must be 1 to 300 seconds.");
            return (RecoverySettings)MemberwiseClone();
        }
    }

    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class RecoveryService : MonoBehaviour, ISessionContinuity
    {
        const string TicketMessage = "Han.Recovery.Ticket.v1", LeaveMessage = "Han.Recovery.Leave.v1", LeaveAck = "Han.Recovery.Left.v1";
        public RecoverySettings Settings = new RecoverySettings();
        RecoverySettings active;
        MultiplayerSession session;
        NetworkManager manager;
        RecoveryRegistry registry;
        byte[] credential;
        string credentialTarget;
        bool confirmed, localPlayer, eligible, reconnectPending, intentional, leaveAcknowledged;
        CancellationTokenSource retryCancellation;
        Task reconnectTask;
        double deadline, nextAttemptAt;
        public RecoveryStatus Status { get; private set; }
        public string LastError { get; private set; } = "";
        public string LocalSessionId { get; private set; } = "";
        public bool IsLocalResume { get; private set; }
        public bool IsBusy => reconnectPending || (reconnectTask != null && !reconnectTask.IsCompleted);
        public int Attempts { get; private set; }
        public double RetryRemainingSeconds => IsBusy ? Math.Max(0, deadline - Now) : 0;
        public double NextAttemptSeconds => Status == RecoveryStatus.Waiting ? Math.Max(0, nextAttemptAt - Now) : 0;
        public int HeldSeatCount => registry?.HeldCount(Now) ?? 0;
        public event Action<RecoveryStatus> StatusChanged;
        public event Action<ulong, string> PlayerRestored;
        static double Now => Time.realtimeSinceStartupAsDouble;

        void Awake() { session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>(); }
        void OnEnable()
        {
            session.StateChanged += OnState;
            session.PlayerConnected += OnConnected;
            session.PlayerDisconnected += OnDisconnected;
            manager.OnClientStarted += RegisterMessages;
            manager.OnServerStarted += RegisterMessages;
        }
        void OnDisable()
        {
            retryCancellation?.Cancel();
            if (session == null) return;
            session.StateChanged -= OnState; session.PlayerConnected -= OnConnected; session.PlayerDisconnected -= OnDisconnected;
            manager.OnClientStarted -= RegisterMessages; manager.OnServerStarted -= RegisterMessages;
            UnregisterMessages();
        }

        void Update()
        {
            if (manager.IsServer && !manager.ShutdownInProgress) registry?.Prune(Now);
            if (reconnectPending && !IsTaskRunning()) { reconnectPending = false; reconnectTask = ReconnectAsync(); }
            TryFinishRestoring();
        }
        bool IsTaskRunning() => reconnectTask != null && !reconnectTask.IsCompleted;

        /// <summary>每次启动重新注册消息；NGO 关闭时会销毁旧 CustomMessagingManager。</summary>
        void RegisterMessages()
        {
            var messaging = manager.CustomMessagingManager;
            if (messaging == null) return;
            messaging.RegisterNamedMessageHandler(TicketMessage, ReceiveTicket);
            messaging.RegisterNamedMessageHandler(LeaveMessage, ReceiveLeave);
            messaging.RegisterNamedMessageHandler(LeaveAck, ReceiveLeaveAck);
        }
        void UnregisterMessages()
        {
            var messaging = manager != null ? manager.CustomMessagingManager : null;
            if (messaging == null) return;
            messaging.UnregisterNamedMessageHandler(TicketMessage);
            messaging.UnregisterNamedMessageHandler(LeaveMessage);
            messaging.UnregisterNamedMessageHandler(LeaveAck);
        }

        void OnState(SessionState state)
        {
            if (state == SessionState.Starting)
            {
                active = null; registry = null;
                active = Settings.Copy();
                confirmed = localPlayer = false; intentional = false;
                if (!session.IsClientSession)
                    registry = new RecoveryRegistry(session.ActiveMaxPlayers, active.RetentionSeconds, manager.NetworkConfig.ClientConnectionBufferTimeout + 5);
            }
            if (state == SessionState.Client)
            { SetStatus(IsLocalResume ? RecoveryStatus.Restoring : RecoveryStatus.Online); TryFinishRestoring(); }
            if (state == SessionState.Host || state == SessionState.Server) SetStatus(RecoveryStatus.Online);
            if (state == SessionState.Faulted && session.IsClientSession && !intentional && !IsTaskRunning())
            {
                LastError = session.LastError;
                if (IsTerminal(LastError) || !eligible || credential == null) { ForgetLocal(); SetStatus(RecoveryStatus.Failed); }
                else if (active != null && active.AutoReconnect)
                {
                    reconnectPending = true; deadline = Now + active.RetryWindowSeconds;
                    SetStatus(RecoveryStatus.Waiting);
                }
                else SetStatus(RecoveryStatus.Failed);
            }
            if (state == SessionState.Offline && !IsBusy && intentional) SetStatus(RecoveryStatus.Offline);
        }

        public byte[] CreateClientData()
        {
            if (active == null) throw new InvalidOperationException("Invalid recovery settings.");
            var prefab = manager.NetworkConfig.PlayerPrefab;
            if (prefab != null && prefab.GetComponent<RecoverablePlayer>() == null)
                throw new InvalidOperationException("Recovery requires RecoverablePlayer on the registered PlayerPrefab.");
            if (!session.IsClientSession) return Encoding.ASCII.GetBytes("R1|N");
            if (credential != null && credentialTarget != session.ClientTargetKey) ForgetLocal();
            IsLocalResume = credential != null;
            return IsLocalResume ? (byte[])credential.Clone() : Encoding.ASCII.GetBytes("R1|N");
        }
        public string PrepareAdmission(ulong clientId, byte[] data)
        {
            if (!isActiveAndEnabled || registry == null) return "RECOVERY_NOT_READY";
            string error = registry.Prepare(clientId, data, Now, session.GetAccountId(clientId));
            if (error != null) return error;
            var record = registry.ForClient(clientId);
            if (record.PreviousClientId != ulong.MaxValue && manager.NetworkConfig.PlayerPrefab != null && record.Snapshot == null)
            { registry.Revoke(clientId); return "RECOVERY_RESTORE_FAILED"; }
            return null;
        }
        public void CompleteAdmission(ulong clientId, bool approved) { if (!approved) registry?.Rollback(clientId); }
        public bool TryGetPreviousClientId(ulong id, out ulong previousClientId)
        {
            var record = registry?.ForClient(id);
            previousClientId = record?.PreviousClientId ?? ulong.MaxValue;
            return record != null && previousClientId != ulong.MaxValue && record.State != RecoveryLeaseState.Held;
        }
        public bool IsHeldClient(ulong id) => registry != null && registry.IsHeldClient(id, Now);
        public void RevokeClient(ulong id) => registry?.Revoke(id);

        void OnConnected(ulong id)
        {
            if (!manager.IsServer) return;
            var record = registry?.Connect(id, Now);
            if (record == null)
            {
                if (id != NetworkManager.ServerClientId) _ = RejectRestoreAsync(id);
                return;
            }
            byte[] ticket = registry.CreateResumeData(id);
            bool resumed = record.PreviousClientId != ulong.MaxValue;
            if (manager.IsHost && id == NetworkManager.ServerClientId) return; // Host 不进行客户端重连。
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString128Bytes(Encoding.ASCII.GetString(ticket)));
                writer.WriteValueSafe(resumed);
                manager.CustomMessagingManager.SendNamedMessage(TicketMessage, id, writer, NetworkDelivery.ReliableSequenced);
            }
        }
        void OnDisconnected(ulong id)
        {
            if (manager.IsServer && !manager.ShutdownInProgress) registry?.Disconnect(id, Now);
        }

        void ReceiveTicket(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsClient || manager.IsServer || sender != NetworkManager.ServerClientId || intentional) return;
            try
            {
                reader.ReadValueSafe(out FixedString128Bytes payload); reader.ReadValueSafe(out bool resumed);
                var parts = payload.ToString().Split('|');
                if (parts.Length != 4 || parts[0] != "R1" || parts[1] != "R" || !RecoveryRegistry.IsHex(parts[2], 32) || !RecoveryRegistry.IsHex(parts[3], 64)) return;
                if (IsLocalResume && (credential == null || payload.ToString() != Encoding.ASCII.GetString(credential))) return;
                credential = Encoding.ASCII.GetBytes(payload.ToString()); credentialTarget = session.ClientTargetKey;
                LocalSessionId = parts[2]; confirmed = eligible = true;
                IsLocalResume = resumed;
                TryFinishRestoring();
            }
            catch (Exception) { LastError = "RECOVERY_INVALID_TICKET_MESSAGE"; } // 不输出载荷或秘密。
        }

        void ReceiveLeave(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsServer || !manager.ConnectedClients.ContainsKey(sender) || sender == NetworkManager.ServerClientId) return;
            if (registry?.ForClient(sender) == null) return; // 重复离场请求不重复产生回执。
            // NamedMessage 的 reader 已越过消息名哈希；只计算剩余的应用载荷。
            if (session.ValidateRequest(FrameworkRequestRules.RecoveryLeave, sender, sender, reader.Length - reader.Position) != null) return;
            // 身份仅使用 NGO 提供的真实发送者，不接受客户端指定要撤销的其他玩家。
            registry?.Revoke(sender);
            using (var writer = new FastBufferWriter(1, Allocator.Temp))
                manager.CustomMessagingManager.SendNamedMessage(LeaveAck, sender, writer, NetworkDelivery.ReliableSequenced);
        }
        void ReceiveLeaveAck(ulong sender, FastBufferReader reader)
        { if (manager.IsClient && !manager.IsServer && sender == NetworkManager.ServerClientId && intentional) leaveAcknowledged = true; }

        /// <summary>核心主动退出的异步钩子：先撤销服务器记录并等待确认，再关闭 Transport。</summary>
        public async Task BeforeIntentionalStopAsync()
        {
            intentional = true; eligible = reconnectPending = false; retryCancellation?.Cancel();
            if (manager.IsConnectedClient && !manager.IsServer && manager.CustomMessagingManager != null)
            {
                leaveAcknowledged = false;
                using (var writer = new FastBufferWriter(1, Allocator.Temp))
                    manager.CustomMessagingManager.SendNamedMessage(LeaveMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
                double until = Now + 1;
                while (!leaveAcknowledged && manager.IsConnectedClient && Now < until) await Task.Yield();
            }
            ForgetLocal();
            if (manager.IsServer) registry?.Clear();
        }

        public async Task CancelReconnectAsync()
        {
            intentional = true; eligible = reconnectPending = false; retryCancellation?.Cancel();
            if (IsTaskRunning()) await reconnectTask;
            await session.StopAsync();
            ForgetLocal(); SetStatus(RecoveryStatus.Cancelled);
        }

        /// <summary>开发环境中关闭本机网络以触发意外断线流程；不会撤销服务器的重连租约。</summary>
        public void SimulateUnexpectedDisconnect()
        {
            if (!(Application.isEditor || Debug.isDebugBuild) || manager.IsServer || session.State != SessionState.Client || IsBusy)
                throw new InvalidOperationException("Only an online development client can simulate a connection loss.");
            manager.Shutdown();
        }

        async Task ReconnectAsync()
        {
            retryCancellation = new CancellationTokenSource();
            var token = retryCancellation.Token;
            Attempts = 0;
            try
            {
                while (Now < deadline)
                {
                    nextAttemptAt = Now + Math.Min(5, Math.Pow(2, Math.Min(Attempts, 3)));
                    SetStatus(RecoveryStatus.Waiting);
                    while (Now < nextAttemptAt && Now < deadline) { token.ThrowIfCancellationRequested(); await Task.Yield(); }
                    token.ThrowIfCancellationRequested();
                    if (Now >= deadline) break;
                    Attempts++;
                    SetStatus(RecoveryStatus.Connecting);
                    bool connected = false;
                    using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        attempt.CancelAfter(TimeSpan.FromSeconds(Math.Max(.01, Math.Min(active.AttemptTimeoutSeconds, deadline - Now))));
                        try { await session.RejoinLastClientAsync(attempt.Token); connected = true; }
                        catch (Exception error)
                        {
                            token.ThrowIfCancellationRequested();
                            LastError = session.LastError.Length > 0 ? session.LastError : error is OperationCanceledException ? "RECOVERY_ATTEMPT_TIMEOUT" : error.Message;
                            if (IsTerminal(LastError)) break;
                        }
                    }
                    if (!connected) continue;
                    double restoreDeadline = Math.Min(deadline, Now + active.RestoreTimeoutSeconds);
                    SetStatus(RecoveryStatus.Restoring);
                    while (Now < restoreDeadline && session.State == SessionState.Client && !(confirmed && HasLocalPlayer()))
                    { token.ThrowIfCancellationRequested(); await Task.Yield(); }
                    token.ThrowIfCancellationRequested();
                    if (session.State == SessionState.Client && confirmed && HasLocalPlayer())
                    { LastError = ""; SetStatus(RecoveryStatus.Recovered); return; }
                    LastError = session.LastError.Length > 0 ? session.LastError : "RECOVERY_RESTORE_TIMEOUT";
                    // 未恢复完成不能重新建一个新玩家继续游戏；下一轮先清理此次连接。
                    if (session.State == SessionState.Client)
                    { await session.StopAsync(); SetStatus(RecoveryStatus.Failed); return; }
                    if (IsTerminal(LastError)) break;
                }
                ForgetLocal();
                if (Now >= deadline && !IsTerminal(LastError)) LastError = "RECOVERY_WINDOW_EXPIRED";
                if (!intentional) { if (LastError.Length == 0) LastError = "RECOVERY_WINDOW_EXPIRED"; SetStatus(RecoveryStatus.Failed); }
            }
            catch (OperationCanceledException) { SetStatus(RecoveryStatus.Cancelled); }
            catch (Exception error) { LastError = error.Message; ForgetLocal(); SetStatus(RecoveryStatus.Failed); }
            finally { retryCancellation.Dispose(); retryCancellation = null; }
        }

        public void LocalPlayerSpawned() { localPlayer = true; TryFinishRestoring(); }
        public void LocalPlayerDespawned() { localPlayer = false; }
        bool HasLocalPlayer() => manager.NetworkConfig.PlayerPrefab == null || localPlayer;
        void TryFinishRestoring()
        {
            if (session.State == SessionState.Client && confirmed && HasLocalPlayer() && IsLocalResume && !IsTaskRunning())
            { LastError = ""; SetStatus(RecoveryStatus.Recovered); }
        }
        void ForgetLocal() { credential = null; credentialTarget = null; LocalSessionId = ""; confirmed = eligible = false; IsLocalResume = false; }
        void SetStatus(RecoveryStatus value)
        {
            if (Status == value) return;
            Status = value;
            var handlers = StatusChanged;
            if (handlers == null) return;
            foreach (Action<RecoveryStatus> handler in handlers.GetInvocationList())
                try { handler(value); } catch (Exception error) { Debug.LogException(error); }
        }
        public static bool IsTerminal(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            // 后端临时故障可在原预算内重试；身份/权限拒绝不能靠循环重连规避。
            if (reason.Contains("AUTH_") && !reason.Contains("AUTH_TIMEOUT") && !reason.Contains("AUTH_BUSY") && !reason.Contains("AUTH_BACKEND_UNAVAILABLE")) return true;
            foreach (string code in new[] { "INCOMPATIBLE_GAME", "RECOVERY_INVALID_TICKET", "RECOVERY_SESSION_EXPIRED", "RECOVERY_UNSUPPORTED",
                "RECOVERY_REQUIRED", "KICKED_FROM_LOBBY", "RECOVERY_RESTORE_FAILED", "INVALID_CONNECTION_DATA", "BANNED", "UNAUTHORIZED" })
                if (reason.IndexOf(code, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        internal void BindPlayer(RecoverablePlayer player)
        {
            var record = registry?.ForClient(player.OwnerClientId);
            if (record == null) return;
            player.SessionId.Value = new FixedString64Bytes(record.SessionId);
            if (record.PreviousClientId == ulong.MaxValue) return;
            try
            {
                if (record.Snapshot == null) throw new InvalidOperationException("No registered player snapshot.");
                player.Restore(record.Snapshot);
            }
            catch (Exception)
            {
                registry.Revoke(player.OwnerClientId);
                // Spawn 回调中延迟一帧移除，避免重入 NGO 的生成流程。
                _ = RejectRestoreAsync(player.OwnerClientId);
                return;
            }
            // 业务/UI 订阅者抛错不撤销已经正确恢复的角色。
            var handlers = PlayerRestored;
            if (handlers != null)
                foreach (Action<ulong, string> handler in handlers.GetInvocationList())
                    try { handler(player.OwnerClientId, record.SessionId); } catch (Exception error) { Debug.LogException(error); }
        }
        async Task RejectRestoreAsync(ulong id)
        {
            await Task.Yield();
            if (manager != null && manager.IsServer && manager.ConnectedClients.ContainsKey(id)) session.DisconnectPlayer(id, "RECOVERY_RESTORE_FAILED");
        }
        internal void CapturePlayer(RecoverablePlayer player)
        {
            var record = registry?.ForClient(player.OwnerClientId);
            if (record == null) return;
            try { record.Snapshot = player.Capture(); }
            catch (Exception) { registry.Revoke(player.OwnerClientId); Debug.LogWarning("[Recovery] Player snapshot rejected; lease revoked."); }
        }
    }
}
