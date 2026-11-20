// 会话与大厅的桥接：仅此可选组件负责注册、生成大厅对象及附加锁房规则。
using System;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Lobby
{
    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class LobbyService : MonoBehaviour, IConnectionAdmissionRule
    {
        public GameObject RoomPrefab;
        public LobbySettings Settings = new LobbySettings();
        public string LocalNickname = "Player";
        MultiplayerSession session;
        NetworkManager manager;
        LobbySettings activeSettings;
        string startupError;
        bool nicknameSent;
        public LobbyRoom Room { get; private set; }
        public string StartupError => startupError;
        public event Action<LobbyRoom> RoomChanged;

        /// <summary>直接取得组件，避免依赖其他组件 Awake 执行顺序。</summary>
        void Awake() { session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>(); }
        void OnEnable() { session.StateChanged += OnSessionState; }
        void OnDisable() { if (session != null) session.StateChanged -= OnSessionState; }

        /// <summary>等待本地连接完成且已出现在服务器名单后，仅发送一次初始昵称。</summary>
        void Update()
        {
            if (!nicknameSent && Room != null && Room.IsSpawned && manager.IsClient && manager.IsConnectedClient && Room.ContainsPlayer(manager.LocalClientId))
            {
                nicknameSent = true;
                if (session.IsResumingLocalClient) return; // 使用服务器保留的昵称，不以初始输入覆盖它。
                try { Room.RequestNickname(LocalNickname); }
                catch (ArgumentException error) { Debug.LogWarning("[Lobby] Initial nickname rejected: " + error.Message); }
            }
        }

        /// <summary>启动时注册一致的预制体；服务器在线后生成唯一的动态大厅对象。</summary>
        void OnSessionState(SessionState state)
        {
            if (state == SessionState.Starting)
            {
                startupError = null;
                nicknameSent = false;
                try
                {
                    activeSettings = Settings.Copy();
                    activeSettings.Validate(session.ActiveMaxPlayers);
                    if (!manager.NetworkConfig.EnableSceneManagement) throw new InvalidOperationException("Lobby requires NGO scene management.");
                    if (RoomPrefab == null || RoomPrefab.GetComponent<NetworkObject>() == null || RoomPrefab.GetComponent<LobbyRoom>() == null)
                        throw new InvalidOperationException("Assign a registered prefab with NetworkObject and LobbyRoom.");
                    if (!manager.NetworkConfig.Prefabs.Contains(RoomPrefab)) manager.AddNetworkPrefab(RoomPrefab);
                    manager.NetworkConfig.LoadSceneTimeOut = activeSettings.SceneLoadTimeoutSeconds;
                }
                catch (Exception error) { startupError = error.Message; Debug.LogError("[Lobby] " + startupError); }
            }
            if ((state == SessionState.Host || state == SessionState.Server) && startupError == null && Room == null)
            {
                var instance = Instantiate(RoomPrefab);
                var room = instance.GetComponent<LobbyRoom>();
                room.ConfigureServer(activeSettings, session.ActiveMaxPlayers);
                instance.GetComponent<NetworkObject>().Spawn(false);
            }
        }

        /// <summary>由已生成的大厅对象在各端调用；连接结束时大厅对象会被 NGO 清理。</summary>
        internal void Attach(LobbyRoom room)
        {
            Room = room;
            nicknameSent = false;
            RoomChanged?.Invoke(room);
        }

        internal void Detach(LobbyRoom room)
        {
            if (Room != room) return;
            Room = null;
            nicknameSent = false;
            RoomChanged?.Invoke(null);
        }

        /// <summary>在核心兼容/容量检查之后拒绝加载中或已锁定的房间。</summary>
        public string CheckAdmission(ulong clientId)
        {
            if (startupError != null) return "LOBBY_NOT_CONFIGURED";
            string rejection = Room != null ? Room.AdmissionRejection : null;
            if (rejection == "GAME_IN_PROGRESS" && session.TryGetPreviousClientId(clientId, out _)) return null;
            return rejection;
        }
    }
}
