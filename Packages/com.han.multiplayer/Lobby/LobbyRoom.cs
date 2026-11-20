// 服务器权威网络大厅：客户端提交意图，服务端状态机执行，再同步名单与房间状态。
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Lobby
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class LobbyRoom : NetworkBehaviour
    {
        // 默认写权限为 Server；客户端只能通过下方 RPC 请求修改。
        public NetworkList<LobbyPlayerState> Players;
        public readonly NetworkVariable<LobbyRules> Rules = new NetworkVariable<LobbyRules>();
        public readonly NetworkVariable<LobbyPhase> Phase = new NetworkVariable<LobbyPhase>();
        public readonly NetworkVariable<ulong> LeaderId = new NetworkVariable<ulong>(LobbyModel.NoLeader);
        public readonly NetworkVariable<FixedString128Bytes> TargetScene = new NetworkVariable<FixedString128Bytes>();
        public readonly NetworkVariable<FixedString128Bytes> LoadError = new NetworkVariable<FixedString128Bytes>();
        readonly Dictionary<ulong, double> nicknameTimes = new Dictionary<ulong, double>();
        readonly Dictionary<ulong, double> readyTimes = new Dictionary<ulong, double>();
        readonly Dictionary<ulong, double> rateFeedbackTimes = new Dictionary<ulong, double>();
        LobbyModel model;
        LobbySettings settings;
        int capacity;
        LobbyService service;
        MultiplayerSession session;
        bool loadingGame;
        public event Action Changed;
        public event Action<string> Feedback;
        public string AdmissionRejection => model?.CheckAdmission();
        public bool IsLocalLeader => IsSpawned && IsClient && LeaderId.Value == NetworkManager.LocalClientId;

        /// <summary>NetworkList 在 Awake 分配，由 NetworkBehaviour.OnDestroy 统一释放。</summary>
        void Awake() { Players = new NetworkList<LobbyPlayerState>(); }

        /// <summary>由服务器在 Spawn 前传入快照；不在未生成时写 NetworkVariable。</summary>
        public void ConfigureServer(LobbySettings profile, int maximumPlayers)
        {
            settings = profile.Copy();
            settings.Validate(maximumPlayers);
            capacity = maximumPlayers;
        }

        public override void OnNetworkSpawn()
        {
            service = NetworkManager.GetComponent<LobbyService>();
            session = NetworkManager.GetComponent<MultiplayerSession>();
            Players.OnListChanged += OnPlayersChanged;
            Rules.OnValueChanged += OnRulesChanged;
            Phase.OnValueChanged += OnPhaseChanged;
            LeaderId.OnValueChanged += OnLeaderChanged;
            TargetScene.OnValueChanged += OnTextChanged;
            LoadError.OnValueChanged += OnTextChanged;
            if (IsServer)
            {
                model = new LobbyModel(new LobbyRules { MinimumPlayers = settings.MinimumPlayers, MaximumPlayers = capacity,
                    RequireAllReady = settings.RequireAllReady, AllowLateJoin = settings.AllowLateJoin });
                foreach (ulong id in NetworkManager.ConnectedClientsIds) model.AddPlayer(id);
                NetworkManager.OnClientConnectedCallback += OnPlayerConnected;
                NetworkManager.OnClientDisconnectCallback += OnPlayerDisconnected;
                NetworkManager.SceneManager.OnSceneEvent += OnSceneEvent;
                Publish();
            }
            service?.Attach(this);
            Changed?.Invoke();
        }

        public override void OnNetworkDespawn()
        {
            Players.OnListChanged -= OnPlayersChanged;
            Rules.OnValueChanged -= OnRulesChanged;
            Phase.OnValueChanged -= OnPhaseChanged;
            LeaderId.OnValueChanged -= OnLeaderChanged;
            TargetScene.OnValueChanged -= OnTextChanged;
            LoadError.OnValueChanged -= OnTextChanged;
            // 关闭期间 IsServer 可能已改变；无条件解除自身回调，避免重开房间仍调用旧对象。
            if (NetworkManager != null)
            {
                NetworkManager.OnClientConnectedCallback -= OnPlayerConnected;
                NetworkManager.OnClientDisconnectCallback -= OnPlayerDisconnected;
                if (NetworkManager.SceneManager != null) NetworkManager.SceneManager.OnSceneEvent -= OnSceneEvent;
            }
            service?.Detach(this);
        }

        /// <summary>只读查询，UI 不需要遍历场景中的角色来推导玩家名单。</summary>
        public bool ContainsPlayer(ulong id)
        {
            foreach (var player in Players) if (player.ClientId == id) return true;
            return false;
        }

        /// <summary>给交互 UI 独立的值快照，避免按钮触发同步修改时继续枚举 NativeList。</summary>
        public LobbyPlayerState[] GetPlayersSnapshot()
        {
            var result = new LobbyPlayerState[Players.Count];
            for (int i = 0; i < result.Length; i++) result[i] = Players[i];
            return result;
        }

        /// <summary>昵称在本地与服务器双重校验；发送者身份由 NGO RPC 上下文提供。</summary>
        public void RequestNickname(string nickname)
        {
            RequireClient();
            RenameRpc(new FixedString128Bytes(LobbySettings.NormalizeNickname(nickname)));
        }
        public void RequestReady(bool ready) { RequireClient(); SetReadyRpc(ready); }
        public void RequestStartGame() { RequireClient(); StartGameRpc(); }
        public void RequestReturnToLobby() { RequireClient(); ReturnToLobbyRpc(); }
        public void RequestKick(ulong target) { RequireClient(); KickRpc(target); }
        public void RequestRules(int minimumPlayers, bool requireReady, bool allowLateJoin)
        { RequireClient(); RulesRpc(minimumPlayers, requireReady, allowLateJoin); }

        /// <summary>纯独立服务器可由自己的管理逻辑调用；不为远端客户端开放此特权。</summary>
        public string StartGameOnServer()
        {
            if (!IsSpawned || !IsServer) throw new InvalidOperationException("Only the active server can use this API.");
            return StartTransition(NetworkManager.ServerClientId, false, true);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RenameRpc(FixedString128Bytes nickname, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            bool valid = true;
            try { LobbySettings.NormalizeNickname(nickname.ToString()); } catch (ArgumentException) { valid = false; }
            string error = session.ValidateRequest(FrameworkRequestRules.Nickname, sender, sender, nickname.Length, valid, model.Phase != LobbyPhase.Loading)
                ?? (Throttle(nicknameTimes, sender) ? "RATE_LIMITED" : model.Rename(sender, nickname.ToString()));
            Finish(sender, error);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SetReadyRpc(bool ready, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            string error = session.ValidateRequest(FrameworkRequestRules.LobbyReady, sender, sender, 1, phaseAllowed: model.Phase == LobbyPhase.Waiting)
                ?? (Throttle(readyTimes, sender) ? "RATE_LIMITED" : model.SetReady(sender, ready));
            Finish(sender, error);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RulesRpc(int minimum, bool requireReady, bool allowLateJoin, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            string error = session.ValidateRequest(FrameworkRequestRules.LobbyRules, sender, sender, 6, minimum >= 1 && minimum <= capacity, model.Phase == LobbyPhase.Waiting);
            Finish(sender, error ?? model.UpdateRules(sender, minimum, requireReady, allowLateJoin));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void StartGameRpc(RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            Finish(sender, session.ValidateRequest(FrameworkRequestRules.Start, sender, sender, 0, phaseAllowed: model.Phase == LobbyPhase.Waiting) ?? StartTransition(sender, false, false));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void ReturnToLobbyRpc(RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            Finish(sender, session.ValidateRequest(FrameworkRequestRules.Return, sender, sender, 0, phaseAllowed: model.Phase == LobbyPhase.Playing || model.Phase == LobbyPhase.LoadFailed) ?? StartTransition(sender, true, false));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void KickRpc(ulong target, RpcParams rpc = default)
        {
            ulong sender = rpc.Receive.SenderClientId;
            string error = session.ValidateRequest(FrameworkRequestRules.Kick, sender, sender, 8) ?? model.CheckKick(sender, target);
            if (error == null)
            {
                session.DisconnectPlayer(target, "KICKED_FROM_LOBBY");
                model.RemovePlayer(target); // 离线席位也可撤销，无需等待不存在的连接回调。
            }
            Finish(sender, error);
        }

        /// <summary>只向请求者回传拒绝原因，其他玩家通过状态同步观察成功结果。</summary>
        [Rpc(SendTo.SpecifiedInParams)]
        void FeedbackRpc(FixedString128Bytes message, RpcParams rpc = default) { Feedback?.Invoke(message.ToString()); }

        void Finish(ulong sender, string error)
        {
            // 限流反馈本身也限频，避免每个被丢弃的请求都产生一条回复。
            if (error == "RATE_LIMITED")
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (rateFeedbackTimes.TryGetValue(sender, out double previous) && now - previous < .5) return;
                rateFeedbackTimes[sender] = now;
            }
            if (error == null) Publish();
            if (NetworkManager.ConnectedClients.ContainsKey(sender))
                FeedbackRpc(new FixedString128Bytes(error ?? "OK"), RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        /// <summary>验证与切换原子执行；一旦进入 Loading，新连接会被准入扩展拒绝。</summary>
        string StartTransition(ulong sender, bool returning, bool serverOverride)
        {
            if (returning)
            {
                if (!serverOverride && (sender != model.LeaderId || !ContainsPlayer(sender))) return "NOT_LEADER";
                if (model.Phase != LobbyPhase.Playing && model.Phase != LobbyPhase.LoadFailed) return "NOT_PLAYING";
                if (session.PendingApprovalCount > 0) return "JOIN_IN_PROGRESS";
            }
            else
            {
                string error = model.CheckStart(sender, session.PendingApprovalCount, serverOverride);
                if (error != null) return error;
            }
            string scene = returning ? settings.LobbyScene : settings.GameScene;
            if (!Application.CanStreamedLevelBeLoaded(scene)) return "SCENE_NOT_IN_BUILD";
            var previous = model.Phase;
            model.BeginLoading(returning);
            loadingGame = !returning;
            TargetScene.Value = new FixedString128Bytes(Path.GetFileNameWithoutExtension(scene));
            LoadError.Value = default;
            Publish();
            try
            {
                var status = session.LoadScene(scene);
                if (status == SceneEventProgressStatus.Started) return null;
                model.RestorePhase(previous);
                Publish();
                return "SCENE_REQUEST_" + status;
            }
            catch (Exception error)
            {
                model.RestorePhase(previous);
                Publish();
                Debug.LogWarning("[Lobby] " + error.Message);
                return "SCENE_REQUEST_FAILED";
            }
        }

        /// <summary>以 NGO 的服务端场景事件确认加载，避免把请求受理当作游戏开始。</summary>
        void OnSceneEvent(SceneEvent sceneEvent)
        {
            if (model.Phase != LobbyPhase.Loading || Path.GetFileNameWithoutExtension(sceneEvent.SceneName) != TargetScene.Value.ToString()) return;
            if (sceneEvent.SceneEventType == SceneEventType.LoadComplete)
            {
                model.MarkLoaded(sceneEvent.ClientId);
                Publish();
            }
            if (sceneEvent.SceneEventType == SceneEventType.LoadEventCompleted)
            {
                if (sceneEvent.ClientsThatCompleted != null)
                    foreach (ulong id in sceneEvent.ClientsThatCompleted) model.MarkLoaded(id);
                bool timedOut = sceneEvent.ClientsThatTimedOut != null && sceneEvent.ClientsThatTimedOut.Count > 0;
                if (!model.CompleteLoading(timedOut)) LoadError.Value = new FixedString128Bytes("SCENE_LOAD_FAILED_OR_TIMED_OUT");
                Publish();
                Debug.Log("[Lobby] Transition complete: " + model.Phase + " game=" + loadingGame);
            }
        }

        void OnPlayerConnected(ulong id)
        {
            bool resumed = session.TryGetPreviousClientId(id, out ulong previous);
            // 核心准入可能先于本帧 Update 释放过期席位；先同步清理名单，避免占用已释放的最后一格。
            PruneOfflineSeats(resumed ? previous : ulong.MaxValue);
            if (resumed) model.ResumePlayer(previous, id);
            else model.AddPlayer(id);
            Publish();
        }
        void OnPlayerDisconnected(ulong id)
        {
            if (session.IsHeldClient(id)) model.SuspendPlayer(id); else model.RemovePlayer(id);
            nicknameTimes.Remove(id); readyTimes.Remove(id);
            rateFeedbackTimes.Remove(id);
            Publish();
        }

        // 服务器保留期限届满后才移除离线记录，房间容量与 UI 使用相同的席位语义。
        void Update()
        {
            if (!IsSpawned || !IsServer || NetworkManager.ShutdownInProgress) return;
            if (PruneOfflineSeats(ulong.MaxValue)) Publish();
        }
        bool PruneOfflineSeats(ulong keepId)
        {
            bool changed = false;
            for (int i = model.Players.Count - 1; i >= 0; i--)
            {
                var player = model.Players[i];
                if (!player.IsConnected && player.ClientId != keepId && !session.IsHeldClient(player.ClientId)) { model.RemovePlayer(player.ClientId); changed = true; }
            }
            return changed;
        }

        /// <summary>将纯状态机结果增量写入网络字段；每次最多处理房间人数数量的记录。</summary>
        void Publish()
        {
            Rules.Value = model.Rules;
            Phase.Value = model.Phase;
            LeaderId.Value = model.LeaderId;
            while (Players.Count > model.Players.Count) Players.RemoveAt(Players.Count - 1);
            for (int i = 0; i < model.Players.Count; i++)
            {
                if (i >= Players.Count) Players.Add(model.Players[i]);
                else if (!Players[i].Equals(model.Players[i])) Players[i] = model.Players[i];
            }
            Changed?.Invoke();
        }

        static bool Throttle(Dictionary<ulong, double> timestamps, ulong sender)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (timestamps.TryGetValue(sender, out double previous) && now - previous < 0.25) return true;
            timestamps[sender] = now;
            return false;
        }

        void RequireClient()
        {
            if (!IsSpawned || !IsClient) throw new InvalidOperationException("Wait for a connected lobby before sending requests.");
        }
        void OnPlayersChanged(NetworkListEvent<LobbyPlayerState> change) => Changed?.Invoke();
        void OnRulesChanged(LobbyRules previous, LobbyRules current) => Changed?.Invoke();
        void OnPhaseChanged(LobbyPhase previous, LobbyPhase current) => Changed?.Invoke();
        void OnLeaderChanged(ulong previous, ulong current) => Changed?.Invoke();
        void OnTextChanged(FixedString128Bytes previous, FixedString128Bytes current) => Changed?.Invoke();
    }
}
