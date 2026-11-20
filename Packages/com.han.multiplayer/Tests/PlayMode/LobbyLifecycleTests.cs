#if UNITY_EDITOR
// 编辑器 PlayMode 实测：动态大厅对象生成、清理、场景屏障和返回，不依赖云服务。
using System;
using System.Collections;
using System.Threading.Tasks;
using Han.Multiplayer.Lobby;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Han.Multiplayer.Tests
{
    public class LobbyLifecycleTests
    {
        GameObject root;
        MultiplayerSession session;
        LobbyService service;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Lobby lifecycle test");
            root.AddComponent<UnityTransport>();
            var manager = root.AddComponent<NetworkManager>();
            manager.NetworkConfig = new NetworkConfig { EnableSceneManagement = true };
            session = root.AddComponent<MultiplayerSession>();
            service = root.AddComponent<LobbyService>();
            service.RoomPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/MultiplayerLobbyDemo/LobbyRoom.prefab");
            Assert.IsNotNull(service.RoomPrefab, "Generate Lobby Demo before running these tests.");
            service.Settings.MinimumPlayers = 1;
            service.LocalNickname = "Host";
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (session != null) yield return Wait(session.StopAsync());
            UnityEngine.Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator RosterClearsAndDedicatedServerHasNoLocalPlayer()
        {
            yield return Wait(session.StartLanHostAsync(18791, "127.0.0.1"));
            Assert.IsNotNull(service.Room);
            Assert.AreEqual(1, service.Room.Players.Count);
            Assert.AreEqual(0, service.Room.LeaderId.Value);
            yield return Wait(session.StopAsync());
            Assert.IsTrue(service.Room == null);
            yield return Wait(session.StartDedicatedServerAsync(18791, "127.0.0.1"));
            Assert.AreEqual(0, service.Room.Players.Count);
            Assert.AreEqual(LobbyModel.NoLeader, service.Room.LeaderId.Value);
        }

        [UnityTest]
        public IEnumerator HostLoadsGameAndReturnsThroughRealSceneEvents()
        {
            yield return Wait(session.StartLanHostAsync(18792, "127.0.0.1"));
            var room = service.Room;
            room.RequestReady(true);
            yield return null;
            Assert.IsTrue(room.Players[0].IsReady);
            room.RequestStartGame();
            yield return Until(() => room.Phase.Value == LobbyPhase.Playing);
            Assert.AreEqual("Game", SceneManager.GetActiveScene().name);
            Assert.IsTrue(room.Players[0].IsLoaded);
            Assert.AreEqual("GAME_IN_PROGRESS", service.CheckAdmission(123));
            room.RequestReturnToLobby();
            yield return Until(() => room.Phase.Value == LobbyPhase.Waiting);
            Assert.AreEqual("Lobby", SceneManager.GetActiveScene().name);
            Assert.IsFalse(room.Players[0].IsReady);
            Assert.IsFalse(room.Players[0].IsLoaded);
            Assert.AreSame(room, service.Room);
        }

        static IEnumerator Wait(Task task)
        {
            yield return Until(() => task.IsCompleted);
            if (task.IsFaulted) throw task.Exception;
            Assert.IsFalse(task.IsCanceled);
        }
        static IEnumerator Until(Func<bool> condition)
        {
            float deadline = Time.realtimeSinceStartup + 20;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), "Lobby test timed out.");
        }
    }
}
#endif
