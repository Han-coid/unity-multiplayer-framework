// 独立大厅示例生成器：保留原来的简单联机示例，另外生成 Lobby / Game 两个场景。
using System;
using System.IO;
using System.Linq;
using Han.Multiplayer.Demo;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Lobby;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Han.Multiplayer.Editor
{
    public static class LobbyDemoBuilder
    {
        public const string Folder = "Assets/MultiplayerLobbyDemo";
        public const string LobbyScene = Folder + "/Lobby.unity";
        public const string EntryScene = Folder + "/Entry.unity";
        public const string GameScene = Folder + "/Game.unity";

        /// <summary>只在资源缺失时生成，不重写用户已有的大厅场景或预制体。</summary>
        [MenuItem("Tools/Han Multiplayer/Create or Open Lobby Demo")]
        public static void Create()
        {
            DemoBuilder.CreateDemo();
            if (!File.Exists(DemoBuilder.ScenePath)) throw new InvalidOperationException("Demo creation was cancelled.");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "MultiplayerLobbyDemo");
            string prefabPath = Folder + "/LobbyRoom.prefab";
            var roomPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (roomPrefab == null)
            {
                var template = new GameObject("SharedLobbyRoom");
                template.AddComponent<NetworkObject>();
                template.AddComponent<LobbyRoom>();
                roomPrefab = PrefabUtility.SaveAsPrefabAsset(template, prefabPath);
                UnityEngine.Object.DestroyImmediate(template);
            }
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Folder + "/LobbyPrefabs.asset");
            if (list == null)
            {
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                list.Add(new NetworkPrefab { Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DemoBuilder.Folder + "/Player.prefab") });
                list.Add(new NetworkPrefab { Prefab = roomPrefab });
                AssetDatabase.CreateAsset(list, Folder + "/LobbyPrefabs.asset");
            }
            foreach (string path in new[] { LobbyScene, GameScene })
            {
                if (File.Exists(path)) continue;
                var scene = EditorSceneManager.OpenScene(DemoBuilder.ScenePath);
                foreach (var session in UnityEngine.Object.FindObjectsByType<MultiplayerSession>(FindObjectsSortMode.None))
                    UnityEngine.Object.DestroyImmediate(session.gameObject);
                EditorSceneManager.SaveScene(scene, path);
            }
            if (!File.Exists(EntryScene))
            {
                var scene = EditorSceneManager.OpenScene(DemoBuilder.ScenePath);
                var session = UnityEngine.Object.FindFirstObjectByType<MultiplayerSession>();
                var manager = session.GetComponent<NetworkManager>();
                manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
                manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(list);
                session.Settings.ApplicationId = "han.multiplayer.lobby-demo";
                session.Settings.MaxPlayers = 4;
                var service = session.gameObject.AddComponent<LobbyService>();
                service.RoomPrefab = roomPrefab;
                service.Settings.LobbyScene = LobbyScene;
                service.Settings.GameScene = GameScene;
                session.gameObject.AddComponent<DemoLobbyPanel>();
                session.gameObject.AddComponent<DemoLobbyEntry>();
                var panel = session.GetComponent<NetworkDiagnosticsPanel>() ?? session.gameObject.AddComponent<NetworkDiagnosticsPanel>();
                panel.Visible = false;
                EditorSceneManager.SaveScene(scene, EntryScene);
            }
            var scenes = EditorBuildSettings.scenes.ToList();
            foreach (string path in new[] { EntryScene, LobbyScene, GameScene })
                if (!scenes.Any(x => x.path == path)) scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            var entry = EditorSceneManager.OpenScene(EntryScene);
            var existingSession = UnityEngine.Object.FindFirstObjectByType<MultiplayerSession>();
            if (existingSession != null && existingSession.Settings.ProtocolVersion <= 5)
            {
                existingSession.Settings.ProtocolVersion = 6;
                EditorSceneManager.MarkSceneDirty(entry);
                EditorSceneManager.SaveScene(entry);
            }
            Debug.Log("[Lobby] Demo ready: " + EntryScene);
        }

        /// <summary>单独构建大厅示例，不覆盖原有 Windows 示例程序。</summary>
        [MenuItem("Tools/Han Multiplayer/Build Windows Lobby Demo")]
        public static void BuildWindows()
        {
            Create();
            Directory.CreateDirectory("Builds/LobbyWindows");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { EntryScene, LobbyScene, GameScene },
                locationPathName = "Builds/LobbyWindows/LobbyDemo.exe",
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Lobby build failed: " + report.summary.result);
            Debug.Log("[Lobby] Windows build succeeded.");
        }
    }
}
