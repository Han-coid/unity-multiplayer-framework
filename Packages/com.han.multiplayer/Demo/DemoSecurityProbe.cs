// 仅 --security-test 安装的真实网络验证器；生产入口不注册测试消息，也不赋予测试权限。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Han.Multiplayer.Security;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoSecurityProbe : MonoBehaviour
    {
        const string RequestMessage = "Han.Security.TestRequest", ResultMessage = "Han.Security.TestResult";
        static readonly ServerRequestRule OwnRule = new ServerRequestRule("test.owner", 100, 100, 64, true, permission: "play", requireAccount: true);
        static readonly ServerRequestRule AdminRule = new ServerRequestRule("test.admin", 100, 100, 64, permission: "admin.test", requireAccount: true);
        static readonly ServerRequestRule RateRule = new ServerRequestRule("test.rate", 1, 2, 64, requireAccount: true);
        static readonly ServerRequestRule SequenceRule = new ServerRequestRule("test.sequence", 100, 100, 64, sequence: true, requireAccount: true);
        readonly Dictionary<uint, TaskCompletionSource<string>> replies = new Dictionary<uint, TaskCompletionSource<string>>();
        MultiplayerSession session;
        NetworkManager manager;
        bool host;
        string stopFile;
        uint requestId;
        void Awake()
        {
            session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>();
            var args = Environment.GetCommandLineArgs(); host = DemoLauncher.Argument(args, "--net-mode", "") == "host";
            stopFile = DemoLauncher.Argument(args, "--security-stop-file", "");
            var gate = GetComponent<ServerRequestGate>();
            foreach (var rule in new[] { OwnRule, AdminRule, RateRule, SequenceRule }) gate.RegisterRule(rule);
            manager.OnServerStarted += Register; manager.OnClientStarted += Register;
        }
        void Register()
        {
            manager.CustomMessagingManager.RegisterNamedMessageHandler(RequestMessage, ReceiveRequest);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(ResultMessage, ReceiveResult);
        }
        void OnDestroy() { if (manager != null) { manager.OnServerStarted -= Register; manager.OnClientStarted -= Register; } }
        async void Start()
        {
            int exit = 1;
            try
            {
                if (!(Application.isEditor || Debug.isDebugBuild) || stopFile.Length == 0) throw new Exception("Development security test only.");
                if (host)
                {
                    await Until(() => manager.IsHost && Players().Count(x => !x.IsOwner) == 1, 20, "primary account");
                    await Until(() => File.Exists(stopFile), 75, "test completion marker");
                    Require(manager.ConnectedClientsIds.Count == 2 && manager.ConnectedClientsIds.All(id => session.GetAccountId(id) == (id == 0 ? "host" : "guest")), "Rejected account spawned or replaced a player.");
                    Require(GetComponent<ServerRequestGate>().Rejected >= 6, "No real request rejections.");
                    Debug.Log("[SECURITY_TEST] NO_UNAUTHORIZED_PLAYERS");
                    File.WriteAllText(stopFile + ".host-checked", "checked");
                }
                else
                {
                    await Until(() => Players().Any(x => x.IsOwner) && GetComponent<AccountAdmissionService>().LocalAccountId == "guest", 20, "verified local account");
                    var own = Players().First(x => x.IsOwner); var remote = Players().First(x => !x.IsOwner);
                    Require(await Send(0, own.NetworkObjectId) == "OK", "Own request rejected.");
                    Require(await Send(0, remote.NetworkObjectId) == "REQUEST_NOT_OWNER", "Remote owner bypass.");
                    Require(await Send(1, own.NetworkObjectId) == "REQUEST_FORBIDDEN", "Permission bypass.");
                    Require(await Send(2, own.NetworkObjectId) == "REQUEST_INVALID", "Invalid float accepted.");
                    Require(await Send(3, own.NetworkObjectId) == "REQUEST_PHASE", "Phase bypass.");
                    Require(await Send(4, own.NetworkObjectId, padding: 100) == "REQUEST_INVALID", "Payload bound bypass.");
                    Require(await Send(5, own.NetworkObjectId, 5) == "OK", "Sequence first request failed.");
                    Require(await Send(5, own.NetworkObjectId, 5) == "REQUEST_REPLAY", "Duplicate request accepted.");
                    bool limited = false;
                    for (int i = 0; i < 6; i++) limited |= await Send(6, own.NetworkObjectId) == "RATE_LIMITED";
                    Require(limited, "Flood was not limited.");
                    Debug.Log("[SECURITY_TEST] REQUEST_CHECKS");
                    await Until(() => File.Exists(stopFile + ".host-checked"), 75, "server final account check");
                }
                Debug.Log("[SECURITY_TEST] PASS"); exit = 0;
            }
            catch (Exception error) { Debug.LogError("[SECURITY_TEST] FAIL " + error); }
            finally { try { await session.StopAsync(); } catch (Exception error) { Debug.LogError(error); exit = 1; } Application.Quit(exit); }
        }
        async Task<string> Send(int operation, ulong target, uint sequence = 0, int padding = 0)
        {
            uint id = ++requestId; var completion = new TaskCompletionSource<string>(); replies.Add(id, completion);
            using (var writer = new FastBufferWriter(256, Allocator.Temp))
            {
                writer.WriteValueSafe(operation); writer.WriteValueSafe(id); writer.WriteValueSafe(target);
                writer.WriteValueSafe(operation == 2 ? float.NaN : 1f); writer.WriteValueSafe(sequence);
                for (int i = 0; i < padding; i++) writer.WriteValueSafe((byte)0);
                manager.CustomMessagingManager.SendNamedMessage(RequestMessage, 0, writer, NetworkDelivery.ReliableSequenced);
            }
            await Until(() => completion.Task.IsCompleted, 5, "request response"); replies.Remove(id); return await completion.Task;
        }
        void ReceiveRequest(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsServer) return;
            int payloadBytes = reader.Length - reader.Position;
            try
            {
                reader.ReadValueSafe(out int operation); reader.ReadValueSafe(out uint id); reader.ReadValueSafe(out ulong target);
                reader.ReadValueSafe(out float value); reader.ReadValueSafe(out uint sequence);
                string result = "REQUEST_INVALID";
                if (manager.SpawnManager.SpawnedObjects.TryGetValue(target, out var actualObject))
                {
                    var rule = operation == 1 ? AdminRule : operation == 5 ? SequenceRule : operation == 6 ? RateRule : OwnRule;
                    result = session.ValidateRequest(rule, sender, actualObject.OwnerClientId, payloadBytes,
                        operation >= 0 && operation <= 6 && !float.IsNaN(value) && !float.IsInfinity(value), operation != 3,
                        operation == 5 ? (uint?)sequence : null, actualObject.NetworkObjectId) ?? "OK";
                }
                using (var writer = new FastBufferWriter(100, Allocator.Temp))
                {
                    writer.WriteValueSafe(id); writer.WriteValueSafe(new FixedString64Bytes(result));
                    manager.CustomMessagingManager.SendNamedMessage(ResultMessage, sender, writer, NetworkDelivery.ReliableSequenced);
                }
            }
            catch (Exception) { Debug.LogError("[SECURITY_TEST] Invalid test protocol."); }
        }
        void ReceiveResult(ulong sender, FastBufferReader reader)
        {
            if (manager.IsServer || sender != 0) return;
            reader.ReadValueSafe(out uint id); reader.ReadValueSafe(out FixedString64Bytes code);
            if (replies.TryGetValue(id, out var completion)) completion.TrySetResult(code.ToString());
        }
        static DemoPlayer[] Players() => FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None).Where(x => x.IsSpawned).ToArray();
        static async Task Until(Func<bool> condition, double seconds, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until) await Task.Yield();
            Require(condition(), "Timeout: " + label);
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
