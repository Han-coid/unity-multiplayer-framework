// Linux 专用服务器构建器：使用同一场景构建无图形服务端，并附带本地部署模板。
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Han.Multiplayer.Editor
{
    /// <summary>生成 Linux x86_64 / Mono Dedicated Server；不会上传或启动远程服务。</summary>
    public static class LinuxServerBuilder
    {
        /// <summary>相对于当前 Unity 项目根目录的完整服务端输出目录。</summary>
        public const string OutputDirectory = "Builds/LinuxServer";
        /// <summary>Linux 启动器使用的可执行文件名，需与 run-server.sh 保持一致。</summary>
        public const string ExecutableName = "MultiplayerServer.x86_64";

        /// <summary>检查目标支持、构建服务端、复制模板，最后恢复用户原有的脚本后端配置。</summary>
        [MenuItem("Tools/Han Multiplayer/Build Linux Dedicated Server (x64)")]
        public static void Build()
        {
            // 模块必须与编辑器版本匹配；这里只检查支持情况，不自动安装系统软件。
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneLinux64))
                throw new BuildFailedException("Install Linux Dedicated Server Build Support for this exact Editor version in Unity Hub, then restart the Editor.");

            DemoBuilder.CreateDemo();
            if (!File.Exists(DemoBuilder.ScenePath)) throw new BuildFailedException("Demo scene is missing.");
            Directory.CreateDirectory(OutputDirectory);
            // 临时设置构建所需后端，避免永久改变用户其他 Server 构建的选择。
            var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Server);
            try
            {
                // 该 x64 示例使用 Mono，可避免为 IL2CPP 额外配置交叉编译工具链。
                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, ScriptingImplementation.Mono2x);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { DemoBuilder.ScenePath },
                    locationPathName = Path.Combine(OutputDirectory, ExecutableName),
                    target = BuildTarget.StandaloneLinux64,
                    // 选择真正的 Dedicated Server 子目标，Unity 会定义 UNITY_SERVER 编译符号。
                    subtarget = (int)StandaloneBuildSubtarget.Server,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new BuildFailedException("Linux server build failed: " + report.summary.result);

                // 从程序集定位包根目录，兼容嵌入包和通过 Package Manager 安装的包。
                string packageRoot = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(LinuxServerBuilder).Assembly).resolvedPath;
                foreach (string file in new[] { "run-server.sh", "han-multiplayer.service", "README.md" })
                {
                    // 即使源码在 Windows 检出，也写入 LF 换行和无 BOM UTF-8，便于 Linux 解释器读取。
                    string contents = File.ReadAllText(Path.Combine(packageRoot, "Server~", file)).Replace("\r\n", "\n");
                    File.WriteAllText(Path.Combine(OutputDirectory, file), contents, new UTF8Encoding(false));
                }
                Debug.Log("[Multiplayer] Linux x64 Dedicated Server built: " + Path.GetFullPath(OutputDirectory));
            }
            // 构建失败时也恢复设置；服务端产物保留构建时所选择的 Mono 后端。
            finally { PlayerSettings.SetScriptingBackend(NamedBuildTarget.Server, previousBackend); }
        }
    }
}
