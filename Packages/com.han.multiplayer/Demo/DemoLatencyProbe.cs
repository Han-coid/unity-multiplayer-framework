// 专供时序对比的真实输入探针；正常启动不会安装，结果保存后主动退出当前进程。
using System;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using UnityEngine;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoLatencyProbe : MonoBehaviour
    {
        async void Start()
        {
            var session = GetComponent<MultiplayerSession>();
            int exit = 1;
            try
            {
                var args = Environment.GetCommandLineArgs();
                double duration = double.Parse(DemoLauncher.Argument(args, "--net-latency-duration", "20"), System.Globalization.CultureInfo.InvariantCulture);
                double deadline = MonotonicClock.Now + 20;
                DemoPlayer own = null;
                while (own == null && MonotonicClock.Now < deadline)
                {
                    foreach (var player in FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None))
                        if (player.IsSpawned && player.IsOwner) own = player;
                    if (session.State == SessionState.Faulted) throw new Exception(session.LastError);
                    await Task.Yield();
                }
                if (own == null) throw new Exception("No local player before deadline.");
                own.LatencyTestMovement = true;
                // 排除建连/对象生成预热，再重复静止起步；不覆盖普通人工移动。
                double began = MonotonicClock.Now;
                while (MonotonicClock.Now - began < 2.5) await Task.Yield();
                var metrics = GetComponent<NetworkResponseMetrics>();
                metrics.ClearMeasurements();
                began = MonotonicClock.Now;
                while (MonotonicClock.Now - began < duration)
                {
                    if (!session.IsOnline) throw new Exception("Disconnected during timing test.");
                    await Task.Yield();
                }
                var sample = metrics.Capture();
                if (!sample.confirmation.available || sample.confirmation.count < 20 || sample.visibleOnset.count < 3)
                    throw new Exception("Insufficient confirmation or stationary onset samples.");
                string path = DemoLauncher.Argument(args, "--net-report", "");
                if (path.Length == 0) throw new Exception("--net-report is required for latency test.");
                GetComponent<NetworkDiagnostics>().ExportReport(path);
                Debug.Log("[TIMING] PASS");
                exit = 0;
            }
            catch (Exception error) { Debug.LogError("[TIMING] FAIL: " + error); }
            finally
            {
                try { await session.StopAsync(); } catch (Exception error) { Debug.LogError(error); exit = 1; }
                Application.Quit(exit);
            }
        }
    }
}
