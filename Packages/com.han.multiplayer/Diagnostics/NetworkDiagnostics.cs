// 网络观测组件：读取 UTP 统计并汇总可选响应测量，不记录 Relay 凭据或游戏载荷。
using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Han.Multiplayer.Diagnostics
{
    /// <summary>一次采样；流量是当前 Driver 所有连接的总和，包含 UTP 可统计的协议开销。</summary>
    [Serializable]
    public sealed class NetworkDiagnosticSample
    {
        public double elapsedSeconds;
        public string state;
        public string protocol;
        public bool driverAvailable;
        public int remotePeers;
        public ulong receivedBytes;
        public ulong sentBytes;
        public ulong receivedPackets;
        public ulong sentPackets;
        public float receiveKbitPerSecond;
        public float sendKbitPerSecond;
        public PeerRtt[] peers = Array.Empty<PeerRtt>();
        public NetworkSimulationSettings simulation;
        public NetworkTimingSnapshot timing;
    }

    /// <summary>远端 RTT，毫秒；available=false 表示尚无有效估计，而不是网络延迟为零。</summary>
    [Serializable]
    public sealed class PeerRtt
    {
        public ulong clientId;
        public bool available;
        public ulong milliseconds;
    }

    /// <summary>限制内存占用的本地诊断采集，可独立于调试面板安装。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class NetworkDiagnostics : MonoBehaviour
    {
        const int HistoryCapacity = 120; // 默认每半秒一项，保留约一分钟的最近采样。
        readonly Queue<NetworkDiagnosticSample> history = new Queue<NetworkDiagnosticSample>();
        readonly Queue<string> events = new Queue<string>();
        MultiplayerSession session;
        UnityTransport transport;
        NetworkResponseMetrics responseMetrics;
        double nextSampleAt;
        double startedAt;

        /// <summary>最近采样；仅在主线程读取，不要修改返回对象。</summary>
        public NetworkDiagnosticSample Latest { get; private set; } = new NetworkDiagnosticSample();
        /// <summary>最近的状态转换及错误；事件数最多 20。</summary>
        public string[] RecentEvents => events.ToArray();

        /// <summary>每次重新启用都重新计时并清除历史，避免旧会话混淆。</summary>
        void OnEnable()
        {
            session = GetComponent<MultiplayerSession>();
            transport = GetComponent<UnityTransport>();
            responseMetrics = GetComponent<NetworkResponseMetrics>() ?? gameObject.AddComponent<NetworkResponseMetrics>();
            history.Clear();
            events.Clear();
            startedAt = Time.realtimeSinceStartupAsDouble;
            nextSampleAt = 0;
            session.StateChanged += OnStateChanged;
        }

        /// <summary>在网络更新之后限频读取统计，避免每个 OnGUI 事件重复采集。</summary>
        void LateUpdate()
        {
            if (Time.realtimeSinceStartupAsDouble < nextSampleAt) return;
            Capture();
            nextSampleAt = Time.realtimeSinceStartupAsDouble + 0.5;
        }

        /// <summary>立即采样，供无图形测试和导出使用；未连接时不会伪造 RTT。</summary>
        public NetworkDiagnosticSample Capture()
        {
            var manager = session.Network;
            var sample = new NetworkDiagnosticSample
            {
                elapsedSeconds = Time.realtimeSinceStartupAsDouble - startedAt,
                state = session.State.ToString(), protocol = GetProtocol(),
                simulation = GetComponent<WeakNetworkSimulator>()?.Active,
                timing = responseMetrics.Capture()
            };
            var driver = transport.GetNetworkDriver();
            sample.driverAvailable = driver.IsCreated;
            if (driver.IsCreated)
            {
                var stats = driver.GetStatistics();
                sample.receivedBytes = stats.RxTotalBytes;
                sample.sentBytes = stats.TxTotalBytes;
                sample.receivedPackets = stats.RxTotalPackets;
                sample.sentPackets = stats.TxTotalPackets;
                sample.receiveKbitPerSecond = stats.RxBandwidth.Current;
                sample.sendKbitPerSecond = stats.TxBandwidth.Current;
            }
            var peers = new List<PeerRtt>();
            if (manager != null && manager.IsListening)
            {
                if (manager.IsServer)
                {
                    foreach (ulong id in manager.ConnectedClientsIds)
                    {
                        if (id == NetworkManager.ServerClientId) continue; // Host 本地玩家不经过远端链路。
                        peers.Add(ReadRtt(id));
                    }
                }
                else if (manager.IsConnectedClient) peers.Add(ReadRtt(NetworkManager.ServerClientId));
            }
            sample.peers = peers.ToArray();
            sample.remotePeers = peers.Count;
            Latest = sample;
            if (history.Count == HistoryCapacity) history.Dequeue();
            history.Enqueue(sample);
            return sample;
        }

        /// <summary>UTP RTT 基于可靠管线的估计，空闲或刚连接时可能暂时无法取得。</summary>
        PeerRtt ReadRtt(ulong clientId)
        {
            ulong value = transport.GetCurrentRtt(clientId);
            return new PeerRtt { clientId = clientId, available = value > 0, milliseconds = value };
        }

        /// <summary>只报告当前驱动/会话使用的协议，不把 Relay 配置误报成直连 UDP。</summary>
        string GetProtocol()
        {
            if (session.Mode == ConnectionMode.Relay) return transport.UseWebSockets ? "Relay WSS" : "Relay DTLS";
            return transport.UseWebSockets ? "WebSocket" : "Direct UDP";
        }

        /// <summary>记录有界的生命周期日志，不记录应用载荷、房间码、密钥或身份 Token。</summary>
        void OnStateChanged(SessionState state)
        {
            if (events.Count == 20) events.Dequeue();
            events.Enqueue(DateTime.UtcNow.ToString("HH:mm:ss 'UTC'") + " " + state +
                (string.IsNullOrEmpty(session.LastError) ? "" : " | " + session.LastError));
            if (state == SessionState.Starting) nextSampleAt = 0;
        }

        /// <summary>导出当前设备 JSON；默认目录为 persistentDataPath/NetworkReports。</summary>
        /// <param name="path">可选文件路径；无路径时使用带随机后缀的文件名避免多实例覆盖。</param>
        public string ExportReport(string path = null)
        {
            Capture();
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Application.persistentDataPath, "NetworkReports",
                    "network-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var report = new DiagnosticReport
            {
                utc = DateTime.UtcNow.ToString("O"), unityVersion = Application.unityVersion,
                platform = Application.platform.ToString(), samples = history.ToArray(), stateEvents = events.ToArray(),
                timingMeasurements = responseMetrics.Export()
            };
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log("[NetDiagnostics] Report: " + path);
            return path;
        }

        /// <summary>解除订阅；禁用采集器不改变网络连接。</summary>
        void OnDisable() { if (session != null) session.StateChanged -= OnStateChanged; }

        /// <summary>报告明确记录统计口径，避免将人工设置的丢包率当作实际测量值。</summary>
        [Serializable]
        sealed class DiagnosticReport
        {
            public string utc;
            public string unityVersion;
            public string platform;
            public string trafficScope = "UTP driver aggregate; estimated network-layer bytes; counters reset on reconnect";
            public string lossMeasurement = "Unavailable; simulation loss percentages are configured fault injection, not measured loss";
            public string rttScope = "UTP reliable-pipeline RTT; unavailable until an estimate exists";
            public NetworkDiagnosticSample[] samples;
            public string[] stateEvents;
            public NetworkTimingMeasurements timingMeasurements;
        }
    }
}
