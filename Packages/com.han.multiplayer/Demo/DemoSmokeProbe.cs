// 独立进程集成验证器：与脚本 SmokeTest.ps1 配合，通过日志标记和退出码返回测试结果。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Han.Multiplayer.Diagnostics;
using UnityEngine;

namespace Han.Multiplayer.Demo
{
    /// <summary>仅由 --net-smoke 参数启用；检查真实网络同步，完成后主动结束测试进程。</summary>
    public sealed class DemoSmokeProbe : MonoBehaviour
    {
        /// <summary>运行验证并保证会话清理；成功退出码为 0，失败或清理异常为 1。</summary>
        async void Start()
        {
            var session = GetComponent<MultiplayerSession>();
            // 默认按失败处理，只有所有检查完成后才标记成功。
            int exitCode = 1;
            try
            {
                await CheckAsync(session);
                Debug.Log("[SMOKE] PASS");
                exitCode = 0;
            }
            catch (Exception exception) { Debug.LogError("[SMOKE] FAIL: " + exception); }
            finally
            {
                // 在关闭 Driver 前保存真实在线统计；报告路径由本地测试脚本显式指定。
                try
                {
                    string path = DemoLauncher.Argument(Environment.GetCommandLineArgs(), "--net-report", "");
                    if (path.Length > 0) GetComponent<NetworkDiagnostics>().ExportReport(path);
                }
                catch (Exception exception) { Debug.LogError(exception); exitCode = 1; }
                try { await session.StopAsync(); }
                catch (Exception exception) { Debug.LogError(exception); exitCode = 1; }
                Application.Quit(exitCode);
            }
        }

        /// <summary>在有限时间内观察拒绝原因，或验证远端玩家生成、移动和准备状态。</summary>
        /// <param name="session">由 DemoLauncher 按命令行模式启动的会话。</param>
        static async Task CheckAsync(MultiplayerSession session)
        {
            Application.targetFrameRate = 60;
            var args = Environment.GetCommandLineArgs();
            string expectedError = DemoLauncher.Argument(args, "--net-expect-error", "");
            int minimumPlayers = int.Parse(DemoLauncher.Argument(args, "--net-min-players", "2"));
            int duration = int.Parse(DemoLauncher.Argument(args, "--net-smoke-duration", "15"));
            bool reconnect = Array.IndexOf(args, "--net-reconnect") >= 0;
            bool didReconnect = false; // 最多执行一次主动断开再加入。
            bool observed = false;     // 是否已经满足本轮的全部同步观察条件。
            bool movement = false;     // 是否曾观察到远端对象的位置发生明显变化。
            bool ready = false;        // 是否曾读到远端玩家同步过来的准备状态。
            var simulator = session.GetComponent<WeakNetworkSimulator>();
            float blackoutAfter = float.Parse(DemoLauncher.Argument(args, "--net-blackout-after", "-1"), System.Globalization.CultureInfo.InvariantCulture);
            float blackoutSeconds = float.Parse(DemoLauncher.Argument(args, "--net-blackout-seconds", "3"), System.Globalization.CultureInfo.InvariantCulture);
            double onlineAt = -1;
            bool blackoutStarted = false, blackoutRestored = false;
            // 用网络对象 ID 保存初始位置，重连生成的新对象不会沿用旧对象的基准。
            var initialPositions = new Dictionary<ulong, Vector3>();
            var initializedOwners = new HashSet<ulong>(); // 防止每帧重复切换本地准备状态。
            double deadline = Time.realtimeSinceStartupAsDouble + duration;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                // 满员、错误游戏 ID 和连接超时都是测试预期，可按错误文本单独判定成功。
                if (expectedError.Length > 0 && session.State == SessionState.Faulted)
                {
                    if (session.LastError.IndexOf(expectedError, StringComparison.OrdinalIgnoreCase) < 0)
                        throw new Exception("Wrong rejection: " + session.LastError);
                    Debug.Log("[SMOKE] EXPECTED_ERROR=" + session.LastError);
                    return;
                }
                if (session.IsOnline)
                {
                    if (onlineAt < 0) onlineAt = Time.realtimeSinceStartupAsDouble;
                    if (blackoutAfter >= 0 && !blackoutStarted && Time.realtimeSinceStartupAsDouble - onlineAt >= blackoutAfter)
                    {
                        simulator.BeginBlackout(blackoutSeconds);
                        blackoutStarted = true;
                        Debug.Log("[SMOKE] BLACKOUT_STARTED");
                    }
                    if (blackoutStarted && !blackoutRestored && !simulator.IsBlackout)
                    {
                        blackoutRestored = true;
                        // 恢复后重新观察远端移动，不能仅凭断网前的同步证据通过。
                        observed = movement = ready = false;
                        initialPositions.Clear();
                        Debug.Log("[SMOKE] BLACKOUT_RESTORED");
                    }
                    if (simulator != null && simulator.IsBlackout) { await Task.Yield(); continue; }
                    int spawned = 0;
                    foreach (var player in FindObjectsByType<DemoPlayer>(FindObjectsSortMode.None))
                    {
                        if (!player.IsSpawned) continue;
                        spawned++;
                        ulong id = player.NetworkObjectId;
                        // 自动产生真实输入和 RPC，而不是直接在观察端伪造网络结果。
                        if (player.IsOwner && initializedOwners.Add(id))
                        {
                            player.AutomatedMovement = true;
                            player.ToggleReady();
                        }
                        if (!initialPositions.ContainsKey(id)) initialPositions[id] = player.transform.position;
                        // 只把远端对象算入同步证据，避免本地移动造成“联机成功”的假象。
                        if (!player.IsOwner)
                        {
                            movement |= Vector3.Distance(initialPositions[id], player.transform.position) > 0.5f;
                            ready |= player.Ready.Value;
                        }
                    }
                    // 独立服务器没有本地玩家，因此它观察到的玩家状态全部来自远端。
                    if (!observed && spawned >= minimumPlayers && movement && ready)
                    {
                        observed = true;
                        Debug.Log("[SMOKE] REPLICATION players=" + spawned + " remote_movement=True ready=True");
                        if (reconnect && !didReconnect)
                        {
                            await session.StopAsync();
                            await session.JoinDirectAsync(DemoLauncher.Argument(args, "--net-address", "127.0.0.1"),
                                ushort.Parse(DemoLauncher.Argument(args, "--net-port", "7777")));
                            didReconnect = true;
                            // 重连后必须重新取得同步证据，不能复用第一次连接的成功结果。
                            observed = movement = ready = false;
                            initialPositions.Clear();
                            initializedOwners.Clear();
                            Debug.Log("[SMOKE] RECONNECTED");
                        }
                    }
                }
                // 返回 Unity 主循环，让 Transport、RPC 和 NetworkTransform 有机会继续更新。
                await Task.Yield();
            }
            // 截止时间内没有观察到预期结果时明确失败，不允许挂起的测试一直等待。
            if (expectedError.Length > 0) throw new Exception("Expected rejection was not received.");
            if (blackoutAfter >= 0 && (!blackoutStarted || !blackoutRestored)) throw new Exception("Blackout did not run and recover.");
            if (!observed || (reconnect && !didReconnect))
                throw new Exception("Did not observe remote spawn, movement and ready state. " + session.LastError);
            if (Array.IndexOf(args, "--net-require-diagnostics") >= 0)
            {
                var sample = session.GetComponent<NetworkDiagnostics>().Capture();
                if (!sample.driverAvailable || sample.remotePeers == 0 || sample.receivedBytes == 0 || sample.sentBytes == 0)
                    throw new Exception("No real network diagnostics were captured.");
                Debug.Log("[SMOKE] DIAGNOSTICS rx=" + sample.receivedBytes + " tx=" + sample.sentBytes + " peers=" + sample.remotePeers);
            }
        }
    }
}
