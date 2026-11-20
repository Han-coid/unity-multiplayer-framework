// 大厅端到端探针：验证网络名单、权限、准备、双端场景加载、返回及踢人，不替代用户最终操作验收。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Han.Multiplayer.Lobby;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoLobbySmokeProbe : MonoBehaviour
    {
        readonly Queue<string> feedback = new Queue<string>();

        /// <summary>只由 --lobby-test 添加；每个测试进程在清理连接后返回明确退出码。</summary>
        async void Start()
        {
            int exit = 1;
            var session = GetComponent<MultiplayerSession>();
            try
            {
                Application.targetFrameRate = 60;
                await RunAsync(session);
                Debug.Log("[LOBBY_TEST] PASS");
                exit = 0;
            }
            catch (Exception error) { Debug.LogError("[LOBBY_TEST] FAIL " + error); }
            finally
            {
                try { await session.StopAsync(); }
                catch (Exception error) { Debug.LogError(error); exit = 1; }
                Application.Quit(exit);
            }
        }

        async Task RunAsync(MultiplayerSession session)
        {
            var service = GetComponent<LobbyService>();
            string role = DemoLauncher.Argument(Environment.GetCommandLineArgs(), "--lobby-role", "member");
            if (role == "late")
            {
                await Until(() => session.State == SessionState.Faulted, 15, "late join rejection");
                Require(session.LastError.Contains("GAME_IN_PROGRESS"), "wrong late rejection: " + session.LastError);
                Debug.Log("[LOBBY_TEST] LATE_JOIN_REJECTED");
                return;
            }
            await Until(() => service.Room != null && service.Room.IsSpawned && service.Room.Players.Count == 2, 25, "shared roster");
            var room = service.Room;
            room.Feedback += value => feedback.Enqueue(value);
            await Until(() => HasName(room, "Host") && HasName(room, "Guest"), 10, "nicknames on both ends");
            Debug.Log("[LOBBY_TEST] ROSTER_SYNCED");
            if (role == "host") await HostAsync(room);
            else await MemberAsync(session, room);
        }

        async Task HostAsync(LobbyRoom room)
        {
            await Expect(() => room.RequestStartGame(), "PLAYERS_NOT_READY");
            await Expect(() => room.RequestRules(1, true, false), "OK");
            await Until(() => room.Rules.Value.MinimumPlayers == 1, 5, "rule replication");
            await Expect(() => room.RequestReady(true), "OK");
            await Until(() => Roster(room).All(x => x.IsReady), 15, "every player ready");
            await Expect(room.RequestStartGame, "OK");
            await Until(() => room.Phase.Value == LobbyPhase.Playing, 35, "game load barrier");
            CheckPlaying(room);
            Debug.Log("[LOBBY_TEST] PLAYING");
            // 给外部脚本启动一个新进程验证进行中锁房；不接受客户端伪造加载完成。
            await Delay(10);
            await Expect(room.RequestReturnToLobby, "OK");
            await Until(() => room.Phase.Value == LobbyPhase.Waiting, 35, "return barrier");
            Require(SceneManager.GetActiveScene().name == "Lobby", "not in lobby scene");
            Require(Roster(room).All(x => !x.IsLoaded), "load state did not reset");
            Debug.Log("[LOBBY_TEST] RETURNED");
            await Delay(1);
            ulong guest = Roster(room).First(x => x.Nickname.ToString() == "Guest").ClientId;
            await Expect(() => room.RequestKick(guest), "OK");
            await Until(() => !room.ContainsPlayer(guest), 10, "kicked player removed");
            Debug.Log("[LOBBY_TEST] KICKED_AND_REMOVED");
        }

        async Task MemberAsync(MultiplayerSession session, LobbyRoom room)
        {
            // 真正从远端发起越权 RPC，不只检查按钮是否隐藏。
            await Expect(room.RequestStartGame, "NOT_LEADER");
            await Expect(() => room.RequestRules(1, false, true), "NOT_LEADER");
            await Expect(() => room.RequestKick(room.LeaderId.Value), "NOT_LEADER");
            Debug.Log("[LOBBY_TEST] UNAUTHORIZED_REQUESTS_REJECTED");
            bool played = false, returned = false;
            double deadline = Time.realtimeSinceStartupAsDouble + 85;
            double nextReady = 0;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (session.State == SessionState.Faulted)
                {
                    Require(played && returned && session.LastError.Contains("KICKED_FROM_LOBBY"), "unexpected disconnect: " + session.LastError);
                    Debug.Log("[LOBBY_TEST] KICK_REASON_RECEIVED");
                    return;
                }
                if (room != null && room.IsSpawned)
                {
                    if (room.Phase.Value == LobbyPhase.Waiting)
                    {
                        if (played) { returned = true; Require(SceneManager.GetActiveScene().name == "Lobby", "member did not return"); }
                        if (Time.realtimeSinceStartupAsDouble >= nextReady && Roster(room).Any(x => x.ClientId == room.NetworkManager.LocalClientId && !x.IsReady))
                        { room.RequestReady(true); nextReady = Time.realtimeSinceStartupAsDouble + 0.8; }
                    }
                    if (room.Phase.Value == LobbyPhase.Playing)
                    {
                        CheckPlaying(room);
                        if (!played) Debug.Log("[LOBBY_TEST] PLAYING");
                        played = true;
                    }
                }
                await Task.Yield();
            }
            throw new TimeoutException("Member did not complete game/return/kick checks.");
        }

        /// <summary>只有服务器阶段和本地实际场景都正确，才认为加载屏障通过。</summary>
        static void CheckPlaying(LobbyRoom room)
        {
            Require(SceneManager.GetActiveScene().name == "Game", "wrong active game scene");
            Require(room.Players.Count == 2 && Roster(room).All(x => x.IsLoaded), "playing before all players loaded");
            Require(UnityEngine.Object.FindObjectsByType<Unity.Netcode.NetworkManager>(FindObjectsSortMode.None).Length == 1, "duplicate NetworkManager");
        }

        async Task Expect(Action action, string expected)
        {
            feedback.Clear();
            action();
            await Until(() => feedback.Contains(expected), 10, "feedback " + expected);
        }
        // NetworkList 提供 foreach 枚举器，但不实现 IEnumerable<T>，显式适配后再做测试查询。
        static IEnumerable<LobbyPlayerState> Roster(LobbyRoom room) { foreach (var player in room.Players) yield return player; }
        static bool HasName(LobbyRoom room, string name) => Roster(room).Any(x => x.Nickname.ToString() == name);
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Until(Func<bool> condition, double seconds, string label)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + seconds;
            while (!condition()) { if (Time.realtimeSinceStartupAsDouble >= deadline) throw new TimeoutException(label); await Task.Yield(); }
        }
        static async Task Delay(double seconds)
        {
            double until = Time.realtimeSinceStartupAsDouble + seconds;
            await Until(() => Time.realtimeSinceStartupAsDouble >= until, seconds + 2, "delay");
        }
    }
}
