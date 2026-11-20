// 编辑器辅助：生成可直接运行的联机示例资源，或构建用于多进程验证的 Windows 客户端。
using System;
using System.IO;
using System.Linq;
using Han.Multiplayer.Demo;
using Han.Multiplayer.Relay;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Recovery;
using Han.Multiplayer.Prediction;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer.Editor
{
    /// <summary>仅在编辑器中编译的示例场景和 Windows 构建入口。</summary>
    public static class DemoBuilder
    {
        /// <summary>生成资源写入 Assets，便于用户检查和替换，不修改安装包内的源码资源。</summary>
        public const string Folder = "Assets/MultiplayerDemo";
        /// <summary>Windows 和 Linux 服务端共同使用的示例场景路径。</summary>
        public const string ScenePath = Folder + "/MultiplayerDemo.unity";

        /// <summary>已有场景直接打开；首次使用时生成材质、玩家预制体、网络组件和演示环境。</summary>
        [MenuItem("Tools/Han Multiplayer/Create or Open Demo")]
        public static void CreateDemo()
        {
            // 交互式使用时先处理尚未保存的场景，避免直接切换场景导致用户编辑丢失。
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (File.Exists(ScenePath))
            {
                UpgradeRecoveryPlayer();
                // 重复执行不重建场景，保留用户已经对演示资源做出的修改。
                var existing = EditorSceneManager.OpenScene(ScenePath);
                var existingSession = UnityEngine.Object.FindFirstObjectByType<MultiplayerSession>();
                // 示例 RPC 格式升级，拒绝旧示例二进制；保留用户自行增加的协议版本。
                if (existingSession != null && existingSession.Settings.ProtocolVersion <= 5)
                {
                    existingSession.Settings.ProtocolVersion = 6;
                    EditorSceneManager.MarkSceneDirty(existing);
                    EditorSceneManager.SaveScene(existing);
                }
                return;
            }
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "MultiplayerDemo");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var material = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/Player.mat");
            if (material == null)
            {
                // 优先匹配 URP；没有 URP Shader 时使用内置渲染管线的 Standard。
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                material = new Material(shader) { color = new Color(0.2f, 0.7f, 0.9f) };
                AssetDatabase.CreateAsset(material, Folder + "/Player.mat");
            }
            var player = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/Player.prefab");
            if (player == null)
            {
                var template = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                template.name = "Player";
                template.GetComponent<Renderer>().sharedMaterial = material;
                // 网络对象负责身份/生成，NetworkTransform 负责位置，DemoPlayer 只实现玩法。
                template.AddComponent<NetworkObject>();
                template.AddComponent<NetworkTransform>();
                template.AddComponent<DemoPlayer>();
                template.AddComponent<RecoverablePlayer>();
                template.AddComponent<PredictedMovement>();
                player = PrefabUtility.SaveAsPrefabAsset(template, Folder + "/Player.prefab");
                // 临时场景对象已保存为预制体，不留在场景里重复生成一个玩家。
                UnityEngine.Object.DestroyImmediate(template);
            }
            var prefabs = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Folder + "/NetworkPrefabs.asset");
            if (prefabs == null)
            {
                // 所有参与者必须注册同一套网络预制体，才能正确解析远端生成消息。
                prefabs = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                prefabs.Add(new NetworkPrefab { Prefab = player });
                AssetDatabase.CreateAsset(prefabs, Folder + "/NetworkPrefabs.asset");
            }

            // 网络组件放在同一个根对象上，统一交给 MultiplayerSession 管理。
            var root = new GameObject("Multiplayer");
            var manager = root.AddComponent<NetworkManager>();
            var transport = root.AddComponent<UnityTransport>();
            manager.NetworkConfig.NetworkTransport = transport;
            manager.NetworkConfig.PlayerPrefab = player;
            manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Clear();
            manager.NetworkConfig.Prefabs.NetworkPrefabsLists.Add(prefabs);
            manager.NetworkConfig.EnableSceneManagement = true;
            var session = root.AddComponent<MultiplayerSession>();
            session.Settings.ApplicationId = "han.multiplayer.demo";
            session.Settings.ProtocolVersion = 6;
            var relay = root.AddComponent<UnityRelayProvider>();
            // 编辑器中通过序列化字段配置引用，不调用依赖运行时 Awake 的会话方法。
            var serializedSession = new SerializedObject(session);
            serializedSession.FindProperty("relayProvider").objectReferenceValue = relay;
            serializedSession.ApplyModifiedPropertiesWithoutUndo();
            root.AddComponent<DemoLauncher>();
            // 工具层为可选程序集；默认不施加弱网，仅在开发环境开放故障注入。
            root.AddComponent<NetworkDiagnostics>();
            root.AddComponent<WeakNetworkSimulator>();
            root.AddComponent<NetworkDiagnosticsPanel>();

            // 下面的平面、相机和灯光仅供示例观察，不属于通用连接框架的依赖。
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Arena";
            floor.transform.localScale = Vector3.one * 2;
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.transform.position = new Vector3(0, 16, -12);
            camera.transform.LookAt(Vector3.zero);
            camera.orthographic = true;
            camera.orthographicSize = 11;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.055f, 0.075f, 0.11f);
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.6f);
            EditorSceneManager.SaveScene(scene, ScenePath);
            // 追加示例场景而不是覆盖整个构建列表，以保留项目原有场景配置。
            var scenes = EditorBuildSettings.scenes.ToList();
            if (!scenes.Any(item => item.path == ScenePath)) scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log("[Multiplayer] Demo ready: " + ScenePath);
        }

        // 只为自带示例补充恢复组件，保留预制体原有组件和用户设置。
        static void UpgradeRecoveryPlayer()
        {
            string path = Folder + "/Player.prefab";
            if (!File.Exists(path)) return;
            var template = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (template.GetComponent<RecoverablePlayer>() == null || template.GetComponent<PredictedMovement>() == null)
                {
                    if (template.GetComponent<RecoverablePlayer>() == null) template.AddComponent<RecoverablePlayer>();
                    if (template.GetComponent<PredictedMovement>() == null) template.AddComponent<PredictedMovement>();
                    PrefabUtility.SaveAsPrefabAsset(template, path);
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(template); }
        }

        /// <summary>构建 Windows x64 Development 程序，方便观察网络日志并运行多进程测试。</summary>
        [MenuItem("Tools/Han Multiplayer/Build Windows Demo")]
        public static void BuildWindowsDemo()
        {
            CreateDemo();
            if (!File.Exists(ScenePath)) throw new InvalidOperationException("Demo creation was cancelled.");
            Directory.CreateDirectory("Builds/Windows");
            // 只构建这个示例场景；游戏自己的正式构建流程应自行选择业务场景。
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/Windows/MultiplayerDemo.exe",
                target = BuildTarget.StandaloneWindows64,
                // 显式选择客户端，避免之前构建 Linux Server 后继承 Server 子目标。
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development
            });
            // 让命令行构建能够通过异常/退出码识别失败，不仅在日志里打印提示。
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Demo build failed: " + report.summary.result);
            Debug.Log("[Multiplayer] Build succeeded: Builds/Windows/MultiplayerDemo.exe");
        }

        /// <summary>单独生成正式模式验收包，用于确认开发鉴权在非 Development Build 中强制禁用。</summary>
        public static void BuildSecurityReleaseCheck()
        {
            CreateDemo();
            Directory.CreateDirectory("Builds/SecurityRelease");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath }, locationPathName = "Builds/SecurityRelease/ReleaseCheck.exe",
                target = BuildTarget.StandaloneWindows64, subtarget = (int)StandaloneBuildSubtarget.Player, options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Release security check build failed.");
        }
    }
}
