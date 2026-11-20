// 可选弱网工具：在 UTP 创建 Driver 时插入全局模拟层，运行时可切换和恢复参数。
using System;
using System.Globalization;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using UnityEngine;

namespace Han.Multiplayer.Diagnostics
{
    /// <summary>仅 Editor / Development Build 生效；不修改系统网络或第三方包源码。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(UnityTransport))]
    public sealed class WeakNetworkSimulator : MonoBehaviour
    {
        [SerializeField] NetworkSimulationSettings configuration = new NetworkSimulationSettings();
        static readonly DriverConstructor Constructor = new DriverConstructor();
        static int registrations; // UTP 构造器是进程共享入口，仅在最后一个使用者关闭后释放。
        UnityTransport transport;
        bool registered;
        bool prepared;
        NetworkSimulationSettings active = new NetworkSimulationSettings();
        NetworkSimulationSettings beforeBlackout;
        double restoreAt;

        /// <summary>正式非开发构建禁止注入弱网；诊断数据采集不受此限制。</summary>
        public bool IsSupported => Application.isEditor || Debug.isDebugBuild;
        /// <summary>本实例实际应用的参数快照，不是 Inspector 中尚未应用的编辑值。</summary>
        public NetworkSimulationSettings Active => active.Copy();
        /// <summary>当前是否处于有限时间的双向 100% 丢包。</summary>
        public bool IsBlackout => beforeBlackout != null;
        /// <summary>模拟层是否已经随当前工具创建过；运行中途添加工具需要重新连接。</summary>
        public bool IsPrepared => prepared && transport != null && transport.GetNetworkDriver().IsCreated;

        /// <summary>注册按组件筛选的 Driver 构造器，未安装工具的 Transport 继续走默认实现。</summary>
        void OnEnable()
        {
            transport = GetComponent<UnityTransport>();
            if (!IsSupported) return;
            if (UnityTransport.s_DriverConstructor != null && UnityTransport.s_DriverConstructor != Constructor)
            {
                Debug.LogWarning("[WeakNet] Another UTP driver constructor is installed; simulator was not registered.");
                return;
            }
            configuration.Validate();
            active = configuration.Copy();
            UnityTransport.s_DriverConstructor = Constructor;
            registrations++;
            registered = true;
        }

        /// <summary>应用前完整校验；运行中通过 UTP 公共 API 修改模拟参数。</summary>
        public void Apply(NetworkSimulationSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            if (!IsSupported || !registered || UnityTransport.s_DriverConstructor != Constructor)
                throw new InvalidOperationException("Weak-network simulation needs an enabled tool in an Editor/Development Build with no conflicting UTP constructor.");
            var driver = transport.GetNetworkDriver();
            if (driver.IsCreated && !prepared)
                throw new InvalidOperationException("Reconnect once: this driver was created before the simulator was enabled.");
            if (driver.IsCreated) driver.ModifyNetworkSimulatorParameters(settings.ToTransportParameters());
            active = settings.Copy();
            configuration = settings.Copy();
            beforeBlackout = null;
            Debug.Log("[WeakNet] Applied " + JsonUtility.ToJson(active));
        }

        /// <summary>模拟暂时断网，到期自动恢复之前的配置；使用真实时间，不依赖 Time.timeScale。</summary>
        public void BeginBlackout(float seconds = 3)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds <= 0 || seconds > 60)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (!IsPrepared) throw new InvalidOperationException("Start a connection before running a blackout.");
            var previous = beforeBlackout ?? active.Copy();
            var blocked = active.Copy();
            blocked.Preset = NetworkSimulationPreset.Custom;
            blocked.SendLossPercent = blocked.ReceiveLossPercent = 100;
            Apply(blocked);
            beforeBlackout = previous;
            restoreAt = Time.realtimeSinceStartupAsDouble + seconds;
        }

        /// <summary>即使游戏暂停也会恢复短时断网；已断开的会话不会自动重连。</summary>
        void Update()
        {
            if (beforeBlackout != null && Time.realtimeSinceStartupAsDouble >= restoreAt)
                Apply(beforeBlackout);
        }

        /// <summary>仅由示例启动器显式调用；参数错误立即失败，不静默退回正常网络。</summary>
        public void ConfigureFromArguments(string[] args)
        {
            var requested = configuration.Copy();
            bool specified = false;
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (key != "--net-sim" && key != "--net-delay" && key != "--net-jitter" && key != "--net-send-loss" && key != "--net-receive-loss") continue;
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                specified = true;
                string value = args[i];
                if (key == "--net-sim")
                {
                    if (!Enum.TryParse(value, true, out NetworkSimulationPreset preset) || !Enum.IsDefined(typeof(NetworkSimulationPreset), preset))
                        throw new ArgumentException("Unknown weak-network preset: " + value);
                    requested = NetworkSimulationSettings.Create(preset);
                }
                else
                {
                    requested.Preset = NetworkSimulationPreset.Custom;
                    switch (key)
                    {
                        case "--net-delay": requested.SendDelayMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                        case "--net-jitter": requested.SendJitterMs = int.Parse(value, CultureInfo.InvariantCulture); break;
                        case "--net-send-loss": requested.SendLossPercent = float.Parse(value, CultureInfo.InvariantCulture); break;
                        case "--net-receive-loss": requested.ReceiveLossPercent = float.Parse(value, CultureInfo.InvariantCulture); break;
                    }
                }
            }
            if (specified) Apply(requested);
        }

        /// <summary>关闭工具时停止继续注入故障，并释放自己注册的共享入口。</summary>
        void OnDisable()
        {
            if (!registered) return;
            if (transport != null && prepared && transport.GetNetworkDriver().IsCreated)
                transport.GetNetworkDriver().ModifyNetworkSimulatorParameters(default);
            active = new NetworkSimulationSettings();
            beforeBlackout = null;
            prepared = false;
            registered = false;
            registrations = Math.Max(0, registrations - 1);
            if (registrations == 0 && UnityTransport.s_DriverConstructor == Constructor)
                UnityTransport.s_DriverConstructor = null;
        }

        /// <summary>只扩展挂有本工具的 Transport；保留 UTP 默认端点、Relay、加密及管线配置。</summary>
        sealed class DriverConstructor : INetworkStreamDriverConstructor
        {
            public void CreateDriver(UnityTransport source, out NetworkDriver driver, out NetworkPipeline unreliable,
                out NetworkPipeline sequenced, out NetworkPipeline reliable)
            {
                var simulator = source.GetComponent<WeakNetworkSimulator>();
                if (simulator == null || !simulator.isActiveAndEnabled || !simulator.registered)
                {
                    source.CreateDriver(source, out driver, out unreliable, out sequenced, out reliable);
                    return;
                }
                var settings = source.GetDefaultNetworkSettings();
                try
                {
                    var parameters = simulator.active.ToTransportParameters();
                    // 在 Driver 创建前加入此参数，才能在运行期间真正修改模拟层。
                    settings.AddRawParameterStruct(ref parameters);
#if UNITY_WEBGL && !UNITY_EDITOR
                    driver = NetworkDriver.Create(new WebSocketNetworkInterface(), settings);
#else
                    driver = source.UseWebSockets
                        ? NetworkDriver.Create(new WebSocketNetworkInterface(), settings)
                        : NetworkDriver.Create(new UDPNetworkInterface(), settings);
#endif
                }
                finally { settings.Dispose(); }
                try
                {
                    source.GetDefaultPipelineConfigurations(ref driver, out var a, out var b, out var c);
                    unreliable = driver.CreatePipeline(a);
                    sequenced = driver.CreatePipeline(b);
                    reliable = driver.CreatePipeline(c);
                    simulator.prepared = true;
                }
                catch { driver.Dispose(); throw; }
            }
        }
    }
}
