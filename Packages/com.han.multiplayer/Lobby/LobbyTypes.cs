// 大厅协议数据与规则校验：不引用 Demo、FPS、Relay 或 UI。
using System;
using System.Text;
using Unity.Collections;
using Unity.Netcode;

namespace Han.Multiplayer.Lobby
{
    /// <summary>Waiting 等待准备；Loading 同步加载；Playing 开始玩法；LoadFailed 等待房主处理。</summary>
    public enum LobbyPhase : byte { Waiting, Loading, Playing, LoadFailed }

    /// <summary>由服务器维护的单个连接记录；ID 只在本次连接中有效，不是持久账号。</summary>
    public struct LobbyPlayerState : INetworkSerializable, IEquatable<LobbyPlayerState>
    {
        public ulong ClientId;
        public FixedString128Bytes Nickname;
        public bool IsReady;
        public bool IsLoaded;
        public bool IsConnected; // 离线保留席位不具有操作权限，也不能计为全员准备。

        /// <summary>按固定顺序序列化，所有平台必须使用同一版本和字段顺序。</summary>
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref Nickname);
            serializer.SerializeValue(ref IsReady);
            serializer.SerializeValue(ref IsLoaded);
            serializer.SerializeValue(ref IsConnected);
        }

        public bool Equals(LobbyPlayerState other) => ClientId == other.ClientId && Nickname.Equals(other.Nickname) && IsReady == other.IsReady && IsLoaded == other.IsLoaded && IsConnected == other.IsConnected;
    }

    /// <summary>同步给所有玩家的房间规则；容量取会话的启动快照，在线时不变。</summary>
    public struct LobbyRules : INetworkSerializable, IEquatable<LobbyRules>
    {
        public int MinimumPlayers;
        public int MaximumPlayers;
        public bool RequireAllReady;
        public bool AllowLateJoin;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref MinimumPlayers);
            serializer.SerializeValue(ref MaximumPlayers);
            serializer.SerializeValue(ref RequireAllReady);
            serializer.SerializeValue(ref AllowLateJoin);
        }

        public bool Equals(LobbyRules other) => MinimumPlayers == other.MinimumPlayers && MaximumPlayers == other.MaximumPlayers && RequireAllReady == other.RequireAllReady && AllowLateJoin == other.AllowLateJoin;
    }

    /// <summary>服务器启动前设置的场景与默认规则，运行期间使用副本。</summary>
    [Serializable]
    public sealed class LobbySettings
    {
        public string LobbyScene = "Assets/MultiplayerLobbyDemo/Lobby.unity";
        public string GameScene = "Assets/MultiplayerLobbyDemo/Game.unity";
        public int MinimumPlayers = 2;
        public bool RequireAllReady = true;
        public bool AllowLateJoin;
        public int SceneLoadTimeoutSeconds = 30;

        public LobbySettings Copy() => (LobbySettings)MemberwiseClone();

        /// <summary>在开始联网前校验；场景是否在构建列表中会在切换前再次检查。</summary>
        public void Validate(int maximumPlayers)
        {
            if (MinimumPlayers < 1 || MinimumPlayers > maximumPlayers) throw new ArgumentOutOfRangeException(nameof(MinimumPlayers));
            if (SceneLoadTimeoutSeconds < 5 || SceneLoadTimeoutSeconds > 300) throw new ArgumentOutOfRangeException(nameof(SceneLoadTimeoutSeconds));
            if (string.IsNullOrWhiteSpace(LobbyScene) || string.IsNullOrWhiteSpace(GameScene) || LobbyScene == GameScene)
                throw new ArgumentException("Configure different lobby/game scenes.");
        }

        /// <summary>限制显示名称长度及控制字符，UTF-8 严格编码拒绝损坏的代理字符。</summary>
        public static string NormalizeNickname(string value)
        {
            value = value?.Trim().Normalize(NormalizationForm.FormC);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 24) throw new ArgumentException("Nickname must contain 1-24 characters.");
            foreach (char c in value) if (char.IsControl(c)) throw new ArgumentException("Nickname cannot contain control characters.");
            if (new UTF8Encoding(false, true).GetByteCount(value) > 120) throw new ArgumentException("Nickname is too long in UTF-8.");
            return value;
        }
    }
}
