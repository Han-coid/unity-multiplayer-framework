// 真实 NGO Host 验证：覆盖异步账号验证、Host 自动批准漏洞、超时晚到结果与可选关闭。
using System.Collections;
using System.Threading.Tasks;
using Han.Multiplayer.Security;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;
namespace Han.Multiplayer.Tests
{
    public class AccountLifecycleTests
    {
        GameObject root;
        MultiplayerSession session;
        AccountAdmissionService accounts;
        ControlledAccountValidator validator;
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Account lifecycle"); root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>(); manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = false };
            session = root.AddComponent<MultiplayerSession>();
            accounts = root.AddComponent<AccountAdmissionService>();
            var credentials = root.AddComponent<MemoryCredentialProvider>(); credentials.SetCredential("opaque-test-ticket");
            validator = root.AddComponent<ControlledAccountValidator>();
            accounts.SetProviders(credentials, validator);
        }
        [UnityTearDown]
        public IEnumerator TearDown()
        { yield return Wait(session.StopAsync()); Object.Destroy(root); yield return null; }
        [UnityTest]
        public IEnumerator HostIsValidatedBeforeListeningAndRequestsUseServerPrincipal()
        {
            validator.DelayMilliseconds = 50;
            var start = session.StartLanHostAsync(18788, "127.0.0.1");
            yield return null; Assert.IsFalse(session.Network.IsListening);
            yield return Wait(start);
            Assert.AreEqual("alice", session.GetAccountId(0)); Assert.AreEqual("alice", accounts.LocalAccountId);
            Assert.AreEqual("REQUEST_NOT_OWNER", session.ValidateRequest(FrameworkRequestRules.Move, 0, 1, 13, sequence: 1));
            Assert.IsNull(session.ValidateRequest(FrameworkRequestRules.Move, 0, 0, 13, sequence: 1));
        }
        [UnityTest]
        public IEnumerator InvalidHostTokenCannotUseNgoAutomaticApproval()
        {
            validator.Reject = true;
            var start = session.StartLanHostAsync(18788, "127.0.0.1");
            yield return Wait(start, true);
            Assert.IsTrue(start.IsFaulted); Assert.IsFalse(session.Network.IsListening);
            StringAssert.Contains("AUTH_INVALID_TOKEN", session.LastError);
        }
        [UnityTest]
        public IEnumerator IgnoredCancellationCannotWriteLatePrincipalAfterTimeout()
        {
            validator.DelayMilliseconds = 1500; validator.IgnoreCancellation = true;
            accounts.ValidationTimeoutSeconds = 1;
            var start = session.StartLanHostAsync(18788, "127.0.0.1");
            yield return Wait(start, true);
            StringAssert.Contains("AUTH_TIMEOUT", session.LastError);
            yield return new WaitForSecondsRealtime(.7f);
            Assert.IsFalse(session.Network.IsListening); Assert.IsNull(session.GetAccountId(0));
        }
        [UnityTest]
        public IEnumerator AccountComponentCanBeDisabledWithoutRemovingRequestValidation()
        {
            accounts.enabled = false;
            yield return Wait(session.StartLanHostAsync(18788, "127.0.0.1"));
            Assert.IsFalse(session.AccountAuthenticationEnabled);
            Assert.IsNull(session.ValidateRequest(FrameworkRequestRules.PlayerReady, 0, 0, 0));
        }
        static IEnumerator Wait(Task task, bool expectFailure = false)
        {
            double until = Time.realtimeSinceStartupAsDouble + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.IsTrue(task.IsCompleted);
            if (expectFailure) { Assert.IsNotNull(task.Exception); yield break; }
            if (task.IsFaulted) throw task.Exception;
            Assert.IsFalse(task.IsCanceled);
        }
    }
}
