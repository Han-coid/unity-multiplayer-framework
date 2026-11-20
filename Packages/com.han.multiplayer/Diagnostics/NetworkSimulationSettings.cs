// 弱网配置数据：与 FPS 等具体玩法无关，可供 Inspector、UI 和测试脚本共用。
using System;
using Unity.Networking.Transport;

namespace Han.Multiplayer.Diagnostics
{
    /// <summary>开发环境的弱网预设；Custom 表示手动填写参数。</summary>
    public enum NetworkSimulationPreset { None, Light, Moderate, Severe, Custom }

    /// <summary>仅作用于当前进程的 UTP 数据；延迟和抖动作用于发送方向。</summary>
    [Serializable]
    public sealed class NetworkSimulationSettings
    {
        public NetworkSimulationPreset Preset = NetworkSimulationPreset.None;
        public int SendDelayMs; // 人为增加的单向发送延迟，不是目标 RTT。
        public int SendJitterMs; // UTP 附加发送抖动参数，不保证是对称的 ± 分布。
        public float SendLossPercent; // 丢弃本进程发出的数据包。
        public float ReceiveLossPercent; // 丢弃本进程收到的数据包，独立于发送丢包。

        /// <summary>生成独立副本，避免 UI 修改尚未应用的配置时影响运行中的模拟。</summary>
        public NetworkSimulationSettings Copy() => (NetworkSimulationSettings)MemberwiseClone();

        /// <summary>拒绝非法配置；100% 丢包可用于断网测试，NaN/Infinity 必须拒绝。</summary>
        public void Validate()
        {
            if (!Enum.IsDefined(typeof(NetworkSimulationPreset), Preset)) throw new ArgumentOutOfRangeException(nameof(Preset));
            if (SendDelayMs < 0 || SendDelayMs > 2000) throw new ArgumentOutOfRangeException(nameof(SendDelayMs));
            if (SendJitterMs < 0 || SendJitterMs > 2000) throw new ArgumentOutOfRangeException(nameof(SendJitterMs));
            ValidatePercent(SendLossPercent, nameof(SendLossPercent));
            ValidatePercent(ReceiveLossPercent, nameof(ReceiveLossPercent));
        }

        /// <summary>校验百分比，防止浮点特殊值绕过范围比较。</summary>
        static void ValidatePercent(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0 || value > 100)
                throw new ArgumentOutOfRangeException(name);
        }

        /// <summary>转换成 UTP 2.7 全局模拟层参数；不使用已失效的 DebugSimulator API。</summary>
        public NetworkSimulatorParameter ToTransportParameters()
        {
            Validate();
            return new NetworkSimulatorParameter
            {
                SendDelayMS = (uint)SendDelayMs, SendJitterMS = (uint)SendJitterMs,
                SendPacketLossPercent = SendLossPercent, ReceivePacketLossPercent = ReceiveLossPercent
            };
        }

        /// <summary>预设是可复现的配置组合，不代表某个运营商或真实网络的测量值。</summary>
        public static NetworkSimulationSettings Create(NetworkSimulationPreset preset)
        {
            var value = new NetworkSimulationSettings { Preset = preset };
            switch (preset)
            {
                case NetworkSimulationPreset.Light:
                    value.SendDelayMs = 40; value.SendJitterMs = 10; value.SendLossPercent = 1; value.ReceiveLossPercent = 1; break;
                case NetworkSimulationPreset.Moderate:
                    value.SendDelayMs = 100; value.SendJitterMs = 30; value.SendLossPercent = 3; value.ReceiveLossPercent = 3; break;
                case NetworkSimulationPreset.Severe:
                    value.SendDelayMs = 250; value.SendJitterMs = 80; value.SendLossPercent = 10; value.ReceiveLossPercent = 10; break;
            }
            value.Validate();
            return value;
        }
    }
}
