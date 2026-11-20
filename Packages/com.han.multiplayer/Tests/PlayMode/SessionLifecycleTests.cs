// PlayMode 测试：利用 Unity 主循环验证真实 Transport 的启动关闭，以及云请求取消竞态。
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Han.Multiplayer.Tests
{
    /// <summary>检查会话是否能正确释放资源，并在失败或取消后继续复用。</summary>
    public class SessionLifecycleTests
    {
        GameObject root;           // 每个测试独立创建并销毁的网络对象。
        MultiplayerSession session; // 本次测试的会话入口，不复用其他测试的状态。

        /// <summary>准备最小网络环境；不注册玩家和业务场景，使测试聚焦连接生命周期。</summary>
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Session test");
            root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>();
            // 运行时 AddComponent 不保证已有 Inspector 配置，因此显式创建 NetworkConfig。
            manager.NetworkConfig = new NetworkConfig();
            manager.NetworkConfig.EnableSceneManagement = false;
            session = root.AddComponent<MultiplayerSession>();
        }

        /// <summary>测试结束先等网络关闭再销毁对象，避免遗留端口或 Singleton 影响下一项测试。</summary>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (session != null) yield return Wait(session.StopAsync());
            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        /// <summary>同一个会话先作为 Host，再停止并切到纯 Server，直连路径不需要云登录。</summary>
        [UnityTest]
        public IEnumerator CanStartStopAndRestartWithoutCloudServices()
        {
            yield return Wait(session.StartLanHostAsync(18771, "127.0.0.1"));
            Assert.AreEqual(SessionState.Host, session.State);
            Assert.IsTrue(session.Network.IsHost);
            yield return Wait(session.StopAsync());
            Assert.AreEqual(SessionState.Offline, session.State);
            Assert.IsFalse(session.Network.IsListening);
            yield return Wait(session.StartDedicatedServerAsync(18771, "127.0.0.1"));
            Assert.AreEqual(SessionState.Server, session.State);
            Assert.IsFalse(session.Network.IsClient);
        }

        /// <summary>云请求尚未完成时停止会话，之后到达的连接资料不得重新启动网络。</summary>
        [UnityTest]
        public IEnumerator CancellingCloudStartupPreventsLateNetworkStart()
        {
            var provider = root.AddComponent<DelayedRelayProvider>();
            session.SetRelayProvider(provider);
            var start = session.StartRelayHostAsync();
            yield return null;
            Assert.IsTrue(provider.Requested);
            yield return Wait(session.StopAsync());
            Assert.IsTrue(start.IsCanceled);
            // 故意使用无效的空 Relay 资料：正确逻辑应丢弃它，根本不尝试启动 Transport。
            provider.Completion.SetResult(new RelayConnection(default, "ABC123", false));
            yield return null;
            yield return null;
            Assert.IsFalse(session.Network.IsListening);
            Assert.AreEqual(SessionState.Offline, session.State);
            yield return Wait(session.StartLanHostAsync(18772, "127.0.0.1"));
            Assert.AreEqual(SessionState.Host, session.State);
        }

        /// <summary>已有启动占用时拒绝第二次启动，外部令牌取消后恢复 Offline。</summary>
        [UnityTest]
        public IEnumerator ConcurrentStartupIsRejectedAndExternalCancellationWorks()
        {
            session.SetRelayProvider(root.AddComponent<DelayedRelayProvider>());
            using (var cancellation = new CancellationTokenSource())
            {
                var start = session.StartRelayHostAsync(cancellationToken: cancellation.Token);
                Assert.Throws<InvalidOperationException>(() => session.StartLanHostAsync());
                cancellation.Cancel();
                yield return WaitCompletion(start);
                Assert.IsTrue(start.IsCanceled);
                Assert.AreEqual(SessionState.Offline, session.State);
                Assert.IsFalse(session.Network.IsListening);
            }
        }

        /// <summary>云请求永远不完成时达到总超时，并且超时后仍能开启新的直连主机。</summary>
        [UnityTest]
        public IEnumerator CloudTimeoutLeavesSessionReusable()
        {
            session.Settings.ConnectionTimeoutSeconds = 2;
            session.SetRelayProvider(root.AddComponent<DelayedRelayProvider>());
            var start = session.StartRelayHostAsync();
            yield return WaitCompletion(start);
            Assert.IsTrue(start.IsFaulted);
            Assert.IsInstanceOf<TimeoutException>(start.Exception.InnerException);
            Assert.AreEqual(SessionState.Faulted, session.State);
            Assert.IsFalse(session.Network.IsListening);
            yield return Wait(session.StartLanHostAsync(18773, "127.0.0.1"));
            Assert.AreEqual(SessionState.Host, session.State);
        }

        /// <summary>将应成功的 Task 转成 Unity 测试协程等待，并传播错误或意外取消。</summary>
        static IEnumerator Wait(Task task)
        {
            yield return WaitCompletion(task);
            if (task.IsFaulted) throw task.Exception;
            Assert.IsFalse(task.IsCanceled, "Unexpected cancellation.");
        }

        /// <summary>逐帧等待 Task 结束，最多 12 秒；失败任务由具体测试检查其预期异常。</summary>
        static IEnumerator WaitCompletion(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(task.IsCompleted, "Async operation did not finish within 12 seconds.");
        }
    }
}
