// 真实双进程预测验收：静态墙、滑动、误差重放、权威校正、输入定基和配置不一致。
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Prediction;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoPredictionProbe : MonoBehaviour
    {
        const string Control = "Han.Prediction.TestControl", Reply = "Han.Prediction.TestReply";
        static readonly ServerRequestRule Rule = new ServerRequestRule("prediction.test-control", 10, 10, 4, true);
        MultiplayerSession session;
        NetworkManager manager;
        bool host, mismatch, done, serverFailure;
        int response;
        void Awake()
        {
            session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>();
            var args = Environment.GetCommandLineArgs(); host = DemoLauncher.Argument(args, "--net-mode", "") == "host";
            mismatch = DemoLauncher.Argument(args, "--prediction-test-case", "functional") == "mismatch";
            if (mismatch && !host) GetComponent<MovementPredictionSession>().Settings.Speed = 4.5f;
            GetComponent<Security.ServerRequestGate>().RegisterRule(Rule);
            manager.OnServerStarted += Register; manager.OnClientStarted += Register;
        }
        void Register()
        {
            manager.CustomMessagingManager.RegisterNamedMessageHandler(Control, OnControl);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(Reply, OnReply);
        }
        void OnDestroy() { if (manager != null) { manager.OnServerStarted -= Register; manager.OnClientStarted -= Register; } }
        async void Start()
        {
            int exit = 1;
            try
            {
                if (!(Application.isEditor || Debug.isDebugBuild)) throw new Exception("Development prediction test only.");
                if (host)
                {
                    await Until(() => done || serverFailure, 70, "client prediction checks");
                    Require(!serverFailure, "Authoritative server check failed.");
                }
                else if (mismatch)
                {
                    await Until(() => Own() != null && Own().GetComponent<PredictedMovement>().ConfigurationMismatch, 20, "configuration mismatch");
                    await Send(5); Debug.Log("[PREDICTION_TEST] CONFIGURATION_REJECTED");
                }
                else await ClientAsync();
                Debug.Log("[PREDICTION_TEST] PASS"); exit = 0;
            }
            catch (Exception error) { Debug.LogError("[PREDICTION_TEST] FAIL " + error); }
            finally
            {
                try { await session.StopAsync(); } catch (Exception error) { Debug.LogError(error); exit = 1; }
                Application.Quit(exit);
            }
        }
        async Task ClientAsync()
        {
            await Until(() => Own() != null && !Own().GetComponent<PredictedMovement>().WaitingForAuthority, 20, "initial authoritative state");
            var player = Own(); var movement = player.GetComponent<PredictedMovement>();
            var world = GetComponent<MovementPredictionSession>().World;
            Require(movement.LocalPredictionActive && !movement.ConfigurationMismatch, "Prediction did not activate.");
            player.UseScriptedInput = true; player.ScriptedInput = Vector2.zero; await Delay(.5);
            player.ScriptedInput = Vector2.left;
            await Observe(1.2, () => Require(world.IsFree(movement.PresentationPosition), "Predicted visual crossed a wall."));
            player.ScriptedInput = Vector2.zero; await Delay(1);
            Require(Mathf.Abs(movement.SimulatedPosition.x - 1) < .02f, "Static wall did not stop movement.");
            await Send(3); Debug.Log("[PREDICTION_TEST] WALL_BLOCKED");
            float beforeZ = movement.SimulatedPosition.z;
            player.ScriptedInput = new Vector2(-1, .3f); await Delay(.2);
            player.ScriptedInput = Vector2.zero; await Delay(1);
            Require(movement.SimulatedPosition.z > beforeZ + .1f && Mathf.Abs(movement.SimulatedPosition.x - 1) < .02f, "Wall slide failed.");
            Debug.Log("[PREDICTION_TEST] WALL_SLIDING");
            uint epoch = movement.Epoch; await Send(1); await Until(() => movement.Epoch != epoch, 6, "server teleport epoch");
            var simulator = GetComponent<WeakNetworkSimulator>();
            var previousNetwork = simulator.Active;
            // 回环网络可能在同一帧确认全部输入；显式制造 80 ms 上行延迟，保证实际走“尚有输入待重放”分支。
            simulator.Apply(new NetworkSimulationSettings { SendDelayMs = 80 });
            player.ScriptedInput = Vector2.right; await Delay(.2);
            int corrections = movement.Corrections, replayed = movement.ReplayedInputs;
            Require(movement.InjectLocalErrorForDevelopment(new Vector3(0, 0, .5f)), "Could not inject safe local error.");
            await Until(() => movement.Corrections > corrections && movement.ReplayedInputs > replayed, 6, "correction with unconfirmed input replay");
            player.ScriptedInput = Vector2.zero; await Delay(1);
            Require(Mathf.Abs(movement.SimulatedPosition.z + 4) < .01f, "Local error remained after authority replay.");
            simulator.Apply(previousNetwork);
            Debug.Log("[PREDICTION_TEST] CORRECTED_AND_REPLAYED");
            corrections = movement.Corrections; await Send(2);
            await Until(() => movement.Corrections > corrections && Mathf.Abs(movement.SimulatedPosition.z + 3.5f) < .01f, 6, "authoritative correction");
            Debug.Log("[PREDICTION_TEST] SERVER_CORRECTION");
            epoch = movement.Epoch; await Send(1); await Until(() => movement.Epoch != epoch, 6, "second teleport");
            epoch = movement.Epoch; int rebases = movement.RebaseCount;
            GetComponent<WeakNetworkSimulator>().BeginBlackout(3);
            player.ScriptedInput = Vector2.right; await Delay(.5); player.ScriptedInput = Vector2.zero;
            await Until(() => movement.Epoch != epoch && movement.RebaseCount > rebases && !movement.WaitingForAuthority, 12, "bounded rebase after long loss");
            await Delay(1);
            Require(Vector3.Distance(movement.SimulatedPosition, new Vector3(-6, 1, -4)) < .05f && world.IsFree(movement.PresentationPosition), "Old input moved the rebased player.");
            await Send(4); Debug.Log("[PREDICTION_TEST] LOSS_REBASED");
            string report = DemoLauncher.Argument(Environment.GetCommandLineArgs(), "--prediction-report", "");
            if (report.Length > 0)
            {
                var data = new Result { corrections = movement.Corrections, replayedInputs = movement.ReplayedInputs, rebases = movement.RebaseCount,
                    maximumErrorMeters = movement.MaximumCorrectionError, simulated = movement.SimulatedPosition, authoritative = movement.LatestAuthority };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))); File.WriteAllText(report, JsonUtility.ToJson(data, true));
            }
            await Send(5);
        }
        [Serializable] sealed class Result { public int corrections, replayedInputs, rebases; public float maximumErrorMeters; public Vector3 simulated, authoritative; }
        async Task Send(int action)
        {
            response = 0;
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            { writer.WriteValueSafe(action); manager.CustomMessagingManager.SendNamedMessage(Control, 0, writer, NetworkDelivery.ReliableSequenced); }
            await Until(() => response != 0, 6, "server test acknowledgement"); Require(response == action, "Server rejected prediction check.");
        }
        void OnControl(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsServer) return;
            int size = reader.Length - reader.Position;
            reader.ReadValueSafe(out int action);
            var player = FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None).FirstOrDefault(p => p.IsSpawned && p.OwnerClientId == sender);
            int result = action;
            try
            {
                Require(player != null && session.ValidateRequest(Rule, sender, player.OwnerClientId, size, action >= 1 && action <= 5) == null, "Unauthorized prediction control.");
                var movement = player.GetComponent<PredictedMovement>();
                if (action == 1) movement.TeleportOnServer(new Vector3(-6, 1, -4));
                if (action == 2) movement.CorrectOnServer(player.transform.position + new Vector3(0, 0, .5f));
                if (action == 3) Require(Mathf.Abs(player.transform.position.x - 1) < .02f, "Server crossed static wall.");
                if (action == 4) Require(Vector3.Distance(player.transform.position, new Vector3(-6, 1, -4)) < .05f, "Server replayed obsolete input.");
                if (action == 5) done = true;
            }
            catch (Exception error) { serverFailure = true; result = -1; Debug.LogError("[PREDICTION_TEST] " + error.Message); }
            using (var writer = new FastBufferWriter(4, Allocator.Temp))
            { writer.WriteValueSafe(result); manager.CustomMessagingManager.SendNamedMessage(Reply, sender, writer, NetworkDelivery.ReliableSequenced); }
        }
        void OnReply(ulong sender, FastBufferReader reader) { if (!manager.IsServer && sender == 0) reader.ReadValueSafe(out response); }
        static DemoPlayer Own() => FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None).FirstOrDefault(p => p.IsSpawned && p.IsOwner);
        static async Task Until(Func<bool> condition, double seconds, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until) await Task.Yield();
            Require(condition(), "Timeout: " + label);
        }
        static Task Delay(double seconds) => Observe(seconds, () => { });
        static async Task Observe(double seconds, Action check)
        { double until = Time.realtimeSinceStartupAsDouble + seconds; while (Time.realtimeSinceStartupAsDouble < until) { check(); await Task.Yield(); } }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
