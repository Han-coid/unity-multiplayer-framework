// 开发双进程验收：实际跳跃脉冲、服务器去重、平台/顶板、无输入重力，以及空中断线后的速度恢复。
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Prediction;
using Han.Multiplayer.Recovery;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoJumpProbe : MonoBehaviour
    {
        const string Control = "Han.Jump.TestControl", Reply = "Han.Jump.TestReply";
        static readonly ServerRequestRule Rule = new ServerRequestRule("jump.test-control", 10, 10, 4, true);
        MultiplayerSession session;
        NetworkManager manager;
        bool host, done, failed, monitorJump, monitorRecovery, observedRecovery;
        bool monitorBlackout, landedDuringBlackout;
        double blackoutCheckAt;
        int response, jumps;
        float previousVelocity;
        ulong previousObject;
        string identity;
        void Awake()
        {
            session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>();
            host = DemoLauncher.Argument(Environment.GetCommandLineArgs(), "--net-mode", "") == "host";
            GetComponent<Security.ServerRequestGate>().RegisterRule(Rule);
            manager.OnServerStarted += Register; manager.OnClientStarted += Register;
        }
        void Register()
        {
            manager.CustomMessagingManager.RegisterNamedMessageHandler(Control, OnControl);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(Reply, OnReply);
        }
        void OnDestroy() { if (manager != null) { manager.OnServerStarted -= Register; manager.OnClientStarted -= Register; } }
        // 直接观察服务器运动状态；测试不能仅用客户端展示值宣称服务端已正确落地。
        void Update()
        {
            if (!host || !manager.IsListening) return;
            var player = Players().FirstOrDefault(p => p.OwnerClientId != 0);
            if (player == null) return;
            var state = player.GetComponent<PredictedMovement>().AuthoritativeState;
            // 在客户端三秒断网尚未结束时检查，防止“恢复收包后才落地”掩盖无输入时冻结重力的缺陷。
            if (monitorBlackout && Time.realtimeSinceStartupAsDouble >= blackoutCheckAt)
            {
                landedDuringBlackout = state.Grounded && Mathf.Abs(state.Position.y - 1) < .01f;
                monitorBlackout = false;
                if (!landedDuringBlackout) { failed = true; Debug.LogError("[JUMP_TEST] Gravity did not land during blackout."); }
                else Debug.Log("[JUMP_TEST] SERVER_LANDED_DURING_BLACKOUT");
            }
            // 落地也会把负速度变为零，只把显著正向速度跃升计为起跳。
            if (monitorJump && state.VerticalVelocity > 1 && state.VerticalVelocity > previousVelocity + 3) jumps++;
            previousVelocity = state.VerticalVelocity;
            if (monitorRecovery && player.NetworkObjectId != previousObject)
            {
                observedRecovery = player.GetComponent<RecoverablePlayer>().SessionId.Value.ToString() == identity && state.VerticalVelocity < -1 && !state.Grounded;
                if (!observedRecovery) { failed = true; Debug.LogError("[JUMP_TEST] Restored airborne state lost identity or velocity."); }
                monitorRecovery = false;
            }
        }
        async void Start()
        {
            int exit = 1;
            try
            {
                Require(Application.isEditor || Debug.isDebugBuild, "Development test only.");
                if (host) { await Until(() => done || failed, 100, "client checks"); Require(!failed, "Server checks failed."); }
                else await ClientAsync();
                Debug.Log("[JUMP_TEST] PASS"); exit = 0;
            }
            catch (Exception error) { Debug.LogError("[JUMP_TEST] FAIL " + error); }
            finally { try { await session.StopAsync(); } catch (Exception error) { Debug.LogError(error); exit = 1; } Application.Quit(exit); }
        }
        async Task ClientAsync()
        {
            await Until(() => Own() != null && !Movement().WaitingForAuthority, 20, "initial movement");
            Own().UseScriptedInput = true;
            await Teleport(1); await Delay(.3);
            var movement = Movement(); movement.RequestJump();
            await Until(() => movement.SimulatedState.Position.y > 1.05f, 3, "jump rise");
            movement.RequestJump(); // 空中额外按下不得改变速度或缓存到落地后。
            await Delay(2);
            Require(movement.SimulatedState.Grounded && Mathf.Abs(movement.SimulatedPosition.y - 1) < .01f, "Jump did not settle.");
            await Send(8); Debug.Log("[JUMP_TEST] SINGLE_JUMP_LANDED");

            await Teleport(2);
            await Until(() => movement.SimulatedState.Grounded && Mathf.Abs(movement.SimulatedPosition.y - 1.6f) < .01f, 5, "platform landing");
            Own().ScriptedInput = Vector2.right;
            await Until(() => movement.SimulatedPosition.x > 5.6f, 5, "platform edge");
            Own().ScriptedInput = Vector2.zero;
            await Until(() => movement.SimulatedState.Grounded && Mathf.Abs(movement.SimulatedPosition.y - 1) < .01f, 5, "fall from edge");
            Debug.Log("[JUMP_TEST] PLATFORM_AND_EDGE");

            await Teleport(3); movement.RequestJump(); float peak = 1;
            await Observe(2, () => { peak = Mathf.Max(peak, movement.SimulatedPosition.y); Require(movement.SimulatedPosition.y <= 1.701f, "Passed through ceiling."); });
            Require(peak > 1.4f && movement.SimulatedState.Grounded, "Ceiling jump was not exercised.");
            Debug.Log("[JUMP_TEST] CEILING_BLOCKED");

            await Teleport(4);
            await Until(() => movement.AuthoritativeState.VerticalVelocity < -1 && movement.LatestAuthority.y > 2, 4, "airborne authoritative velocity");
            GetComponent<WeakNetworkSimulator>().BeginBlackout(3);
            movement.RequestJump(); // 空中断网产生的旧脉冲不能在重新定基后补跳。
            await Delay(4); await Send(6);
            await Until(() => !movement.WaitingForAuthority && movement.SimulatedState.Grounded, 8, "blackout recovery");
            Require(Mathf.Abs(movement.SimulatedPosition.y - 1) < .01f, "Blackout left stale vertical state.");
            Debug.Log("[JUMP_TEST] GRAVITY_WITHOUT_INPUT");

            // 恢复检查单独恢复正常网络；前面的跳跃/碰撞已经覆盖所选弱网配置。
            GetComponent<WeakNetworkSimulator>().Apply(new NetworkSimulationSettings());
            await Teleport(7); var recovery = GetComponent<RecoveryService>();
            await Until(() => recovery.LocalSessionId.Length > 0, 5, "recovery credential");
            ulong oldObject = Own().NetworkObjectId; string oldIdentity = recovery.LocalSessionId;
            movement.RequestJump(); recovery.SimulateUnexpectedDisconnect();
            await Until(() => recovery.Status == RecoveryStatus.Recovered && Own() != null && Own().NetworkObjectId != oldObject && !Movement().WaitingForAuthority, 20, "airborne reconnect");
            Require(recovery.LocalSessionId == oldIdentity, "Logical identity changed.");
            Own().UseScriptedInput = true;
            await Until(() => Movement().SimulatedState.Grounded, 10, "restored fall lands");
            await Delay(.3); await Send(9);
            Debug.Log("[JUMP_TEST] AIRBORNE_RECOVERY");
            string report = DemoLauncher.Argument(Environment.GetCommandLineArgs(), "--jump-report", "");
            if (report.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
                File.WriteAllText(report, JsonUtility.ToJson(new Result { passed = true, ceilingPeak = peak, finalState = Movement().SimulatedState }, true));
            }
            await Send(5);
        }
        [Serializable] sealed class Result { public bool passed; public float ceilingPeak; public MovementState finalState; }
        async Task Teleport(int action)
        { uint epoch = Movement().Epoch; await Send(action); await Until(() => Movement().Epoch != epoch && !Movement().WaitingForAuthority, 6, "teleport state"); }
        async Task Send(int action)
        {
            response = 0;
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            { writer.WriteValueSafe(action); manager.CustomMessagingManager.SendNamedMessage(Control, 0, writer, NetworkDelivery.ReliableSequenced); }
            await Until(() => response != 0, 6, "test reply"); Require(response == action, "Server rejected action " + action);
        }
        void OnControl(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsServer || reader.Length - reader.Position != 4) return;
            reader.ReadValueSafe(out int action); int result = action;
            try
            {
                var player = Players().FirstOrDefault(p => p.OwnerClientId == sender);
                Require(player != null && session.ValidateRequest(Rule, sender, player.OwnerClientId, 4, action >= 1 && action <= 9) == null, "Unauthorized control.");
                var movement = player.GetComponent<PredictedMovement>(); var world = GetComponent<MovementPredictionSession>().World;
                if (action != 8) monitorJump = false;
                if (action == 1) { movement.TeleportOnServer(new Vector3(-6, 1, -4)); jumps = 0; previousVelocity = 0; monitorJump = true; }
                if (action == 2) movement.TeleportOnServer(new Vector3(4, 5, -4));
                if (action == 3) movement.TeleportOnServer(new Vector3(-4, 1, 4));
                if (action == 4 || action == 7)
                {
                    var position = new Vector3(-6, action == 7 ? 100 : 15, -4);
                    movement.TeleportOnServer(position); movement.CorrectOnServer(world.CreateState(position, -4));
                }
                if (action == 4) { monitorBlackout = true; landedDuringBlackout = false; blackoutCheckAt = Time.realtimeSinceStartupAsDouble + 2.5; }
                if (action == 5) done = true;
                if (action == 6) Require(landedDuringBlackout && movement.AuthoritativeState.Grounded && Mathf.Abs(player.transform.position.y - 1) < .01f, "Server froze gravity without inputs.");
                if (action == 7) { previousObject = player.NetworkObjectId; identity = player.GetComponent<RecoverablePlayer>().SessionId.Value.ToString(); monitorRecovery = true; }
                if (action == 8) { Require(jumps == 1 && movement.AuthoritativeState.Grounded, "Repeated/missing authoritative jump: " + jumps); monitorJump = false; }
                if (action == 9) Require(observedRecovery && movement.AuthoritativeState.Grounded, "Airborne recovery was not verified on server.");
            }
            catch (Exception error) { failed = true; result = -1; Debug.LogError("[JUMP_TEST] " + error.Message); }
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            { writer.WriteValueSafe(result); manager.CustomMessagingManager.SendNamedMessage(Reply, sender, writer, NetworkDelivery.ReliableSequenced); }
        }
        void OnReply(ulong sender, FastBufferReader reader)
        { if (!manager.IsServer && sender == 0 && reader.Length - reader.Position == 4) reader.ReadValueSafe(out response); }
        static DemoPlayer[] Players() => FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None).Where(p => p.IsSpawned).ToArray();
        static DemoPlayer Own() => Players().FirstOrDefault(p => p.IsOwner);
        static PredictedMovement Movement() => Own().GetComponent<PredictedMovement>();
        static async Task Until(Func<bool> condition, double seconds, string label)
        { double end = Time.realtimeSinceStartupAsDouble + seconds; while (!condition() && Time.realtimeSinceStartupAsDouble < end) await Task.Yield(); Require(condition(), "Timeout: " + label); }
        static Task Delay(double seconds) => Observe(seconds, () => { });
        static async Task Observe(double seconds, Action check)
        { double end = Time.realtimeSinceStartupAsDouble + seconds; while (Time.realtimeSinceStartupAsDouble < end) { check(); await Task.Yield(); } }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
