// 真实断网集成探针：只由 --recovery-test 安装，测试参数不进入正常游戏配置。
using System;
using System.Linq;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Lobby;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Security;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoRecoveryProbe : MonoBehaviour
    {
        string testCase;
        bool host;
        MultiplayerSession session;
        RecoveryService recovery;
        bool restoredOnServer;
        Vector3 expectedPosition;
        string originalIdentity;

        void Awake()
        {
            var args = Environment.GetCommandLineArgs();
            testCase = DemoLauncher.Argument(args, "--recovery-case", "Recover");
            host = DemoLauncher.Argument(args, "--net-mode", "client") == "host";
            session = GetComponent<MultiplayerSession>(); recovery = GetComponent<RecoveryService>();
            // 测试明确缩短 UTP 超时，以便 6 秒的双向丢包触发真正的连接关闭。
            var transport = GetComponent<UnityTransport>(); transport.DisconnectTimeoutMS = 1500; transport.HeartbeatTimeoutMS = 250;
            session.Settings.ConnectionTimeoutSeconds = 2;
            recovery.Settings.RetentionSeconds = testCase == "AccountSwitch" ? 10 : testCase == "Expire" || testCase == "Cancel" || testCase == "Leave" ? 3 : 20;
            recovery.Settings.RetryWindowSeconds = 20; recovery.Settings.AttemptTimeoutSeconds = 2; recovery.Settings.RestoreTimeoutSeconds = 10;
            recovery.PlayerRestored += OnServerRestored;
        }
        void OnServerRestored(ulong id, string identity)
        {
            var player = Players().FirstOrDefault(x => x.OwnerClientId == id);
            Require(player != null && player.Ready.Value && identity == originalIdentity && Vector3.Distance(player.transform.position, expectedPosition) < .15f,
                "Authoritative snapshot was not restored.");
            restoredOnServer = true;
        }
        async void Start()
        {
            int exit = 1;
            try
            {
                if (testCase == "Lobby") await EnterGameAsync();
                if (host) await HostAsync(); else await ClientAsync();
                Debug.Log("[RECOVERY_TEST] PASS " + testCase);
                exit = 0;
            }
            catch (Exception error) { Debug.LogError("[RECOVERY_TEST] FAIL " + error); }
            finally
            {
                try { await session.StopAsync(); } catch (Exception error) { Debug.LogError(error); exit = 1; }
                recovery.PlayerRestored -= OnServerRestored;
                Application.Quit(exit);
            }
        }
        async Task EnterGameAsync()
        {
            var lobby = GetComponent<LobbyService>();
            await Until(() => lobby.Room != null && lobby.Room.IsSpawned && lobby.Room.Players.Count == 2, 15, "lobby roster");
            await Until(() => lobby.Room.GetPlayersSnapshot().Any(x => x.ClientId != 0 && x.Nickname.ToString() == "恢复玩家"), 8, "initial nickname");
            lobby.Room.RequestReady(true);
            if (host)
            {
                await Until(() => lobby.Room.GetPlayersSnapshot().All(x => x.IsReady), 10, "ready");
                lobby.Room.RequestStartGame();
            }
            await Until(() => lobby.Room != null && lobby.Room.Phase.Value == LobbyPhase.Playing, 15, "Playing");
        }
        async Task HostAsync()
        {
            await Until(() => Players().Any(x => !x.IsOwner && x.Ready.Value), 20, "remote frozen player");
            var remote = Players().First(x => !x.IsOwner);
            ulong oldId = remote.OwnerClientId;
            originalIdentity = remote.GetComponent<RecoverablePlayer>().SessionId.Value.ToString();
            expectedPosition = remote.transform.position;
            if (testCase == "Kick")
            {
                await Delay(.5);
                session.DisconnectPlayer(oldId, "KICKED_FROM_LOBBY");
                Require(recovery.HeldSeatCount == 0, "Kicked player retained a lease.");
                await Delay(3);
                Require(Players().All(x => x.OwnerClientId != oldId), "Kicked player returned.");
                Debug.Log("[RECOVERY_TEST] KICK_REVOKED");
                return;
            }
            await Until(() => recovery.HeldSeatCount == 1, 12, "held seat after real transport timeout");
            Require(Players().All(x => x.OwnerClientId != oldId), "Disconnected avatar still accepts input.");
            if (testCase == "Lobby")
                Require(GetComponent<LobbyService>().Room.GetPlayersSnapshot().Any(x => x.ClientId == oldId && !x.IsConnected), "Missing offline lobby seat.");
            Debug.Log("[RECOVERY_TEST] SEAT_HELD");
            if (testCase == "Expire" || testCase == "Cancel" || testCase == "Leave" || testCase == "AccountSwitch")
            {
                await Until(() => recovery.HeldSeatCount == 0, 15, "lease expiry");
                await Delay(5);
                Require(!restoredOnServer, "Expired/cancelled player unexpectedly resumed.");
                Debug.Log("[RECOVERY_TEST] SEAT_EXPIRED");
                return;
            }
            await Until(() => restoredOnServer, 25, "server snapshot restoration");
            await Until(() => Players().Any(x => !x.IsOwner && x.OwnerClientId != oldId), 10, "new connection binding");
            if (testCase == "Lobby")
            {
                var room = GetComponent<LobbyService>().Room;
                // 玩家对象会先生成，场景同步完成后才触发名单重新绑定，不能把 Spawn 当作完整连接。
                await Until(() => room.GetPlayersSnapshot().Any(x => x.ClientId != 0 && x.ClientId != oldId && x.IsConnected), 10, "lobby rebind after synchronization");
                var restored = room.GetPlayersSnapshot().First(x => x.ClientId != 0);
                Require(room.Phase.Value == LobbyPhase.Playing && room.Players.Count == 2 && restored.ClientId != oldId && restored.IsConnected &&
                    restored.Nickname.ToString() == "恢复玩家", "Lobby phase/identity/nickname was not restored.");
            }
            Debug.Log("[RECOVERY_TEST] SERVER_RESTORED");
            await Delay(3);
        }
        async Task ClientAsync()
        {
            await Until(() => Own() != null && recovery.LocalSessionId.Length > 0, 15, "ticket and local player");
            var own = Own();
            ulong oldClient = own.OwnerClientId, oldObject = own.NetworkObjectId;
            originalIdentity = recovery.LocalSessionId;
            Vector3 initial = own.transform.position;
            own.AutomatedMovement = true;
            await Until(() => Vector3.Distance(initial, own.transform.position) > .8f, 8, "movement before disconnect");
            own.AutomatedMovement = false;
            await Delay(1);
            own.ToggleReady();
            await Until(() => own.Ready.Value, 5, "business state before disconnect");
            expectedPosition = own.transform.position;
            if (testCase == "Leave")
            {
                // 与已经开始的意外关闭竞争：公共 StopAsync 必须仍清除恢复资格。
                recovery.SimulateUnexpectedDisconnect();
                await session.StopAsync();
                await Delay(4);
                Require(!recovery.IsBusy && !session.IsOnline && recovery.LocalSessionId.Length == 0 && recovery.Attempts == 0,
                    "Intentional stop during unexpected shutdown triggered reconnect.");
                Debug.Log("[RECOVERY_TEST] LEAVE_CANCELLED_RECOVERY");
                return;
            }
            if (testCase == "Kick")
            {
                await Until(() => recovery.Status == RecoveryStatus.Failed, 10, "terminal kick");
                await Delay(2);
                Require(!recovery.IsBusy && recovery.Attempts == 0 && recovery.LocalSessionId.Length == 0, "Kick retried or retained credentials.");
                Debug.Log("[RECOVERY_TEST] KICK_TERMINAL");
                return;
            }
            if (testCase == "AccountSwitch") GetComponent<DevelopmentCredentialProvider>().AccountId = "other-account";
            GetComponent<WeakNetworkSimulator>().BeginBlackout(6);
            await Until(() => recovery.IsBusy, 10, "automatic reconnect after transport timeout");
            Debug.Log("[RECOVERY_TEST] AUTO_RETRY_STARTED");
            if (testCase == "Cancel")
            {
                await recovery.CancelReconnectAsync();
                int attempts = recovery.Attempts;
                await Delay(3);
                Require(!recovery.IsBusy && !session.IsOnline && recovery.Attempts == attempts && recovery.LocalSessionId.Length == 0, "Cancellation did not stop reconnect.");
                Debug.Log("[RECOVERY_TEST] CANCELLED");
                return;
            }
            if (testCase == "Expire" || testCase == "AccountSwitch")
            {
                await Until(() => recovery.Status == RecoveryStatus.Failed, 25, "expired ticket rejection");
                string expected = testCase == "AccountSwitch" ? "AUTH_ACCOUNT_MISMATCH" : "RECOVERY_SESSION_EXPIRED";
                Require(recovery.LastError.Contains(expected) && recovery.LocalSessionId.Length == 0 && !session.IsOnline,
                    "Expired ticket created a fresh player or wrong terminal error.");
                Debug.Log("[RECOVERY_TEST] EXPIRED_TERMINAL");
                return;
            }
            await Until(() => recovery.Status == RecoveryStatus.Recovered && Own() != null, 30, "client recovered");
            own = Own();
            Require(recovery.LocalSessionId == originalIdentity && own.GetComponent<RecoverablePlayer>().SessionId.Value.ToString() == originalIdentity &&
                own.OwnerClientId != oldClient && own.NetworkObjectId != oldObject && own.Ready.Value &&
                Vector3.Distance(own.transform.position, expectedPosition) < .15f, "Client identity/object/state restoration failed.");
            Require(Players().Count(x => x.GetComponent<RecoverablePlayer>().SessionId.Value.ToString() == originalIdentity) == 1, "Duplicate logical player.");
            if (testCase == "Lobby") Require(SceneManager.GetActiveScene().name == "Game" && GetComponent<LobbyService>().Room.Phase.Value == LobbyPhase.Playing, "Game scene not synchronized.");
            Debug.Log("[RECOVERY_TEST] CLIENT_RESTORED");
            await Delay(1); // 给服务器留出独立的名单验收窗口，不立即主动离场。
        }
        static DemoPlayer[] Players() => FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None).Where(x => x.IsSpawned).ToArray();
        static DemoPlayer Own() => Players().FirstOrDefault(x => x.IsOwner);
        static async Task Until(Func<bool> condition, double seconds, string label)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition() && Time.realtimeSinceStartupAsDouble < until) await Task.Yield();
            Require(condition(), "Timeout: " + label);
        }
        static async Task Delay(double seconds)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            while (Time.realtimeSinceStartupAsDouble < until) await Task.Yield();
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
