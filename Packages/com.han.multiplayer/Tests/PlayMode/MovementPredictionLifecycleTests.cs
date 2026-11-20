// 可选移动配置的真实会话生命周期：在线冻结，停止后可切回旧协议，不更改全局物理步长。
using System.Collections;
using System.Threading.Tasks;
using Han.Multiplayer.Prediction;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;
namespace Han.Multiplayer.Tests
{
    public class MovementPredictionLifecycleTests
    {
        GameObject root;
        MultiplayerSession session;
        [UnityTest]
        public IEnumerator PredictionSettingsFreezeAndCanRestartAsLegacy()
        {
            root = new GameObject("Prediction lifecycle"); root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>(); manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = false };
            session = root.AddComponent<MultiplayerSession>(); var prediction = root.AddComponent<MovementPredictionSession>();
            prediction.Select(MovementPredictionMode.Off); float physicsStep = Time.fixedDeltaTime;
            yield return Wait(session.StartLanHostAsync(18789, "127.0.0.1"));
            uint signature = prediction.World.Fingerprint(30); prediction.Settings.Speed = 8;
            Assert.AreEqual(signature, prediction.World.Fingerprint(30));
            Assert.IsTrue(prediction.UsesCommandMovement); Assert.IsFalse(prediction.PredictLocally);
            Assert.Throws<System.InvalidOperationException>(() => prediction.Select(MovementPredictionMode.On));
            yield return Wait(session.StopAsync()); prediction.Select(MovementPredictionMode.Legacy);
            yield return Wait(session.StartLanHostAsync(18789, "127.0.0.1"));
            Assert.IsFalse(prediction.UsesCommandMovement); Assert.AreEqual(physicsStep, Time.fixedDeltaTime);
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (session != null) yield return Wait(session.StopAsync());
            if (root != null) Object.Destroy(root); yield return null;
        }
        static IEnumerator Wait(Task task)
        {
            double until = Time.realtimeSinceStartupAsDouble + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.IsTrue(task.IsCompleted); if (task.IsFaulted) throw task.Exception; Assert.IsFalse(task.IsCanceled);
        }
    }
}
