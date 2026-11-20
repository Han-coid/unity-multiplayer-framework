// 可选时序配置：只更改下一次会话的 Tick，不改变全项目物理步长，不热改在线连接。
using System;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Diagnostics
{
    public enum NetworkTimingPreset { Legacy30, Aligned30, Responsive60 }

    [Serializable]
    public sealed class NetworkTimingSettings
    {
        public NetworkTimingPreset preset;
        public uint networkTickRate;
        public bool useNetworkTickSimulation;
        public bool interpolatePosition = true;
        public bool extraPositionSmoothing;
        public float positionSmoothingSeconds;
        public NetworkTimingSettings Copy() => (NetworkTimingSettings)MemberwiseClone();
        public static NetworkTimingSettings Create(NetworkTimingPreset preset)
        {
            if (!Enum.IsDefined(typeof(NetworkTimingPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
            return new NetworkTimingSettings
            {
                preset = preset, networkTickRate = preset == NetworkTimingPreset.Responsive60 ? 60u : 30u,
                useNetworkTickSimulation = preset != NetworkTimingPreset.Legacy30,
                extraPositionSmoothing = preset != NetworkTimingPreset.Responsive60,
                positionSmoothingSeconds = preset == NetworkTimingPreset.Responsive60 ? 0.04f : 0.1f
            };
        }
    }

    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class NetworkTimingController : MonoBehaviour
    {
        [SerializeField] NetworkTimingPreset preset = NetworkTimingPreset.Responsive60;
        MultiplayerSession session;
        NetworkManager manager;
        NetworkTimingSettings active;
        public NetworkTimingPreset Selected => preset;
        public NetworkTimingSettings Active => (active ?? NetworkTimingSettings.Create(preset)).Copy();
        public bool CanSelect => session != null && (session.State == SessionState.Offline || session.State == SessionState.Faulted)
            && !manager.IsListening && !manager.ShutdownInProgress;

        void Awake() { session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>(); }
        void OnEnable() { session.StateChanged += OnStateChanged; if (CanSelect) Select(preset); }
        void OnDisable() { if (session != null) session.StateChanged -= OnStateChanged; }

        /// <summary>两端需要选择一致的 Tick 配置；NGO 也会检查网络配置哈希。</summary>
        public void Select(NetworkTimingPreset value)
        {
            if (!CanSelect) throw new InvalidOperationException("Disconnect before changing the network timing preset.");
            var settings = NetworkTimingSettings.Create(value);
            preset = value;
            session.Settings.TickRate = settings.networkTickRate;
            active = null;
        }

        void OnStateChanged(SessionState state)
        {
            if (state == SessionState.Starting) active = NetworkTimingSettings.Create(preset);
            if (state == SessionState.Offline) active = null;
        }

        /// <summary>默认提供 60 Hz 配置；参数错误不静默回退，避免两端使用不同频率。</summary>
        public void ConfigureFromArguments(string[] args)
        {
            var chosen = preset;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--net-timing") continue;
                if (++i >= args.Length || !Enum.TryParse(args[i], true, out chosen) || !Enum.IsDefined(typeof(NetworkTimingPreset), chosen))
                    throw new ArgumentException("Use Legacy30, Aligned30 or Responsive60 for --net-timing.");
            }
            Select(chosen);
        }

        public static string DisplayName(NetworkTimingPreset value)
        {
            switch (value) { case NetworkTimingPreset.Legacy30: return "原版 30 Hz（对比基线）"; case NetworkTimingPreset.Aligned30: return "对齐 30 Hz"; default: return "响应优先 60 Hz"; }
        }
    }
}
