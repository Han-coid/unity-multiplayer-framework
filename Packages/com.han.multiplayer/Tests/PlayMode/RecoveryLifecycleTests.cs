// 真实 NGO 主机生命周期：有恢复扩展和无扩展的核心可分别启停，不遗留名额或网络消息处理器。
using System.Collections;
using System.Threading.Tasks;
using Han.Multiplayer.Recovery;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Han.Multiplayer.Tests
{
    public class RecoveryLifecycleTests
    {
        GameObject root;
        MultiplayerSession session;
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Recovery lifecycle");
            root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = false };
            session = root.AddComponent<MultiplayerSession>();
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return Wait(session.StopAsync());
            Object.Destroy(root); yield return null;
        }
        [UnityTest]
        public IEnumerator HostCanRestartAsServerAndThenRunWithoutRecoveryModule()
        {
            var recovery = root.AddComponent<RecoveryService>();
            yield return Wait(session.StartLanHostAsync(18786, "127.0.0.1"));
            Assert.AreEqual(RecoveryStatus.Online, recovery.Status);
            Assert.IsTrue(ConnectionDataEnvelope.TryDecode(session.Network.NetworkConfig.ConnectionData, out _, out var extra));
            Assert.IsNotEmpty(extra);
            Assert.AreEqual(0, recovery.HeldSeatCount);
            yield return Wait(session.StopAsync());
            Assert.AreEqual(RecoveryStatus.Offline, recovery.Status);
            yield return Wait(session.StartDedicatedServerAsync(18786, "127.0.0.1"));
            Assert.AreEqual(0, recovery.HeldSeatCount);
            yield return Wait(session.StopAsync());
            recovery.enabled = false;
            yield return Wait(session.StartLanHostAsync(18786, "127.0.0.1"));
            Assert.IsTrue(ConnectionDataEnvelope.TryDecode(session.Network.NetworkConfig.ConnectionData, out _, out var plain));
            Assert.IsEmpty(plain);
        }
        static IEnumerator Wait(Task task)
        {
            double until = Time.realtimeSinceStartupAsDouble + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.IsTrue(task.IsCompleted);
            if (task.IsFaulted) throw task.Exception;
            Assert.IsFalse(task.IsCanceled);
        }
    }
}
