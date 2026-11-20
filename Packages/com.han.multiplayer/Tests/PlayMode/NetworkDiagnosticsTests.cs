// 真实 Unity 主循环测试：确认参数进入 UTP Driver、暂停时恢复故障、重连及释放全局构造器。
using System.Collections;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.TestTools;

namespace Han.Multiplayer.Tests
{
    public class NetworkDiagnosticsTests
    {
        GameObject root;
        MultiplayerSession session;
        UnityTransport transport;
        WeakNetworkSimulator simulator;
        INetworkStreamDriverConstructor originalConstructor;

        /// <summary>创建不依赖示例玩家/云账号的测试网络，记录原构造器便于验证清理。</summary>
        [SetUp]
        public void SetUp()
        {
            originalConstructor = UnityTransport.s_DriverConstructor;
            root = new GameObject("Diagnostics test");
            transport = root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = false };
            session = root.AddComponent<MultiplayerSession>();
            simulator = root.AddComponent<WeakNetworkSimulator>();
        }

        /// <summary>所有测试路径都恢复时间倍率、关闭网络，并检查没有遗留全局钩子。</summary>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Time.timeScale = 1;
            if (session != null) yield return Wait(session.StopAsync());
            Object.Destroy(root);
            yield return null;
            Assert.AreSame(originalConstructor, UnityTransport.s_DriverConstructor);
        }

        /// <summary>模拟参数必须在真实 Driver 中存在，运行时修改、恢复和重启后仍然正确。</summary>
        [UnityTest]
        public IEnumerator DriverReceivesFaultsAndBlackoutRestoresWhilePaused()
        {
            simulator.Apply(new NetworkSimulationSettings { SendDelayMs = 80, SendJitterMs = 15, SendLossPercent = 2 });
            yield return Wait(session.StartLanHostAsync(18781, "127.0.0.1"));
            Assert.AreEqual(80, Parameters().SendDelayMS);
            Assert.AreEqual(15, Parameters().SendJitterMS);
            Assert.AreEqual(2, Parameters().SendPacketLossPercent);
            simulator.BeginBlackout(0.15f);
            Assert.AreEqual(100, Parameters().ReceivePacketLossPercent);
            Time.timeScale = 0;
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.IsFalse(simulator.IsBlackout);
            Assert.AreEqual(2, Parameters().SendPacketLossPercent);
            Assert.AreEqual(0, Parameters().ReceivePacketLossPercent);
            Time.timeScale = 1;
            yield return Wait(session.StopAsync());
            yield return Wait(session.StartDedicatedServerAsync(18781, "127.0.0.1"));
            Assert.AreEqual(80, Parameters().SendDelayMS);
            simulator.enabled = false;
            Assert.AreEqual(0, Parameters().SendDelayMS);
            Assert.AreEqual(0, Parameters().SendPacketLossPercent);
        }

        /// <summary>离线和仅本地 Host 都不能显示假的远端 RTT；关闭 Driver 后清除当前计数。</summary>
        [UnityTest]
        public IEnumerator DiagnosticsDistinguishOfflineHostAndStoppedDriver()
        {
            var diagnostics = root.AddComponent<NetworkDiagnostics>();
            Assert.IsFalse(diagnostics.Capture().driverAvailable);
            yield return Wait(session.StartLanHostAsync(18782, "127.0.0.1"));
            var online = diagnostics.Capture();
            Assert.IsTrue(online.driverAvailable);
            Assert.AreEqual(0, online.remotePeers);
            Assert.IsEmpty(online.peers);
            yield return Wait(session.StopAsync());
            var offline = diagnostics.Capture();
            Assert.IsFalse(offline.driverAvailable);
            Assert.AreEqual(0, offline.sentBytes);
            Assert.AreEqual(0, offline.remotePeers);
        }

        /// <summary>读取 Driver 当前参数，而非只检查组件保存的副本。</summary>
        [UnityTest]
        public IEnumerator TimingCanRestartWithNewRateButCannotChangeOnline()
        {
            var timing = root.AddComponent<NetworkTimingController>();
            float originalStep = Time.fixedDeltaTime;
            timing.Select(NetworkTimingPreset.Aligned30);
            yield return Wait(session.StartLanHostAsync(18783, "127.0.0.1"));
            Assert.AreEqual(30, session.Network.NetworkTickSystem.TickRate);
            Assert.Throws<System.InvalidOperationException>(() => timing.Select(NetworkTimingPreset.Responsive60));
            yield return Wait(session.StopAsync());
            timing.Select(NetworkTimingPreset.Responsive60);
            yield return Wait(session.StartLanHostAsync(18783, "127.0.0.1"));
            Assert.AreEqual(60, session.Network.NetworkTickSystem.TickRate);
            Assert.AreEqual(originalStep, Time.fixedDeltaTime);
        }

        // 使用真实组件生命周期验证环形命令缓冲、重复确认和关闭测量后的行为。
        [Test]
        public void MetricsRejectDuplicateOverwrittenAndInvalidReceipts()
        {
            var metrics = root.AddComponent<NetworkResponseMetrics>();
            metrics.RegisterLocalPlayer(1);
            double now = MonotonicClock.Now;
            metrics.RecordSend(1, now, now);
            metrics.RecordSend(257, now, now);
            Assert.IsFalse(metrics.RecordConfirmation(1, now + .02, 1, 1, 60));
            Assert.IsFalse(metrics.RecordConfirmation(257, double.NaN, 1, 1, 60));
            Assert.IsFalse(metrics.RecordConfirmation(257, now + .02, double.NaN, 1, 60));
            Assert.IsTrue(metrics.RecordConfirmation(257, now + .02, 1, 1, 60));
            Assert.IsFalse(metrics.RecordConfirmation(257, now + .03, 1, 1, 60));
            Assert.AreEqual(1, metrics.Capture().confirmation.count);
            metrics.CollectResponses = false;
            Assert.IsFalse(metrics.Capture().confirmation.available);
            metrics.RecordSend(258, now, now);
            Assert.IsFalse(metrics.RecordConfirmation(258, now + .02, 1, 1, 60));
        }

        /// <summary>读取 Driver 当前参数，而非只检查组件保存的副本。</summary>
        NetworkSimulatorParameter Parameters()
        {
            Assert.IsTrue(transport.GetNetworkDriver().CurrentSettings.TryGet<NetworkSimulatorParameter>(out var result));
            return result;
        }

        /// <summary>以真实时间限制测试等待，并传播任务失败。</summary>
        static IEnumerator Wait(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted);
            if (task.IsFaulted) throw task.Exception;
            Assert.IsFalse(task.IsCanceled);
        }
    }
}
