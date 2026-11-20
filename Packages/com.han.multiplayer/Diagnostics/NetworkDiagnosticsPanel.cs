// 基于 IMGUI 的可选开发面板，不要求 Canvas/TMP，可直接挂在网络根对象上。
using System;
using System.Globalization;
using UnityEngine;
using Han.Multiplayer.UI;

namespace Han.Multiplayer.Diagnostics
{
    /// <summary>展示真实统计与弱网控制；默认只在编辑器和 Development Build 显示。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkDiagnostics), typeof(WeakNetworkSimulator))]
    public sealed class NetworkDiagnosticsPanel : MonoBehaviour
    {
        public bool Visible = true;
        NetworkDiagnostics diagnostics;
        WeakNetworkSimulator simulator;
        Rect window = new Rect(380, 20, 440, 570);
        Vector2 scroll;
        string delay = "0", jitter = "0", sendLoss = "0", receiveLoss = "0";
        string message = "";
        bool collapsed; // 收起面板仍保持采集和弱网设置，展开按钮始终可达。

        /// <summary>缓存同对象的可选工具，输入框从真实应用参数初始化。</summary>
        void Start()
        {
            diagnostics = GetComponent<NetworkDiagnostics>();
            simulator = GetComponent<WeakNetworkSimulator>();
            FillInputs();
        }

        /// <summary>适配小屏幕，限制窗口高度并提供滚动；无图形服务端不会绘制。</summary>
        void OnGUI()
        {
            if (Application.isBatchMode) return;
            using (ChineseGui.Use()) DrawChineseGui();
        }

        // 在同一中文皮肤作用域中绘制控件，结束后恢复外部界面样式。
        void DrawChineseGui()
        {
            if (!Visible || Application.isBatchMode || !(Application.isEditor || Debug.isDebugBuild) || diagnostics == null) return;
            if (collapsed)
            {
                if (GUI.Button(new Rect(Mathf.Max(0, Screen.width - 210), Screen.height - 40, 200, 30), "展开网络诊断")) collapsed = false;
                return;
            }
            window.width = Mathf.Min(440, Screen.width - 20);
            window.height = Mathf.Min(570, Screen.height - 20);
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            window = GUI.Window(GetInstanceID(), window, DrawWindow, "网络诊断 / 弱网测试");
        }

        /// <summary>显示本机聚合流量和每个远端 RTT，不虚构未暴露的实测丢包率。</summary>
        void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            if (GUILayout.Button("收起面板（继续采集）")) collapsed = true;
            var data = diagnostics.Latest;
            GUILayout.Label("状态：" + ChineseGui.State(data.state) + " | " + ChineseGui.Protocol(data.protocol));
            GUILayout.Label("远端连接数：" + data.remotePeers + " | 传输已启动：" + ChineseGui.YesNo(data.driverAvailable));
            GUILayout.Label("接收 " + data.receiveKbitPerSecond.ToString("F1") + " kbit/s | 发送 " + data.sendKbitPerSecond.ToString("F1") + " kbit/s");
            GUILayout.Label("累计字节（接收 / 发送）：" + data.receivedBytes + " / " + data.sentBytes);
            GUILayout.Label("累计数据包（接收 / 发送）：" + data.receivedPackets + " / " + data.sentPackets);
            if (data.peers.Length == 0) GUILayout.Label("往返延迟：暂无（没有远端连接）");
            for (int i = 0; i < Math.Min(data.peers.Length, 8); i++)
                GUILayout.Label("连接 " + data.peers[i].clientId + " 往返延迟：" +
                    (data.peers[i].available ? data.peers[i].milliseconds + " ms" : "暂无估计"));
            GUILayout.Label("流量为所有连接的合计，包含协议开销估计。");
            GUILayout.Label("实测丢包率：暂不提供。下方数值是人为模拟参数。");
            DrawTiming(data.timing);
            GUILayout.Space(8);
            GUILayout.Label("仅影响当前窗口；两端都设置会叠加弱网效果。");
            GUILayout.BeginHorizontal();
            PresetButton("正常", NetworkSimulationPreset.None);
            PresetButton("轻度", NetworkSimulationPreset.Light);
            PresetButton("中度", NetworkSimulationPreset.Moderate);
            PresetButton("严重", NetworkSimulationPreset.Severe);
            GUILayout.EndHorizontal();
            delay = Field("发送延迟 / 毫秒（0～2000）", delay);
            jitter = Field("发送抖动 / 毫秒（0～2000）", jitter);
            sendLoss = Field("发送丢包率 %（0～100）", sendLoss);
            receiveLoss = Field("接收丢包率 %（0～100）", receiveLoss);
            if (GUILayout.Button("应用自定义参数")) Execute(() => simulator.Apply(new NetworkSimulationSettings
            {
                Preset = NetworkSimulationPreset.Custom,
                SendDelayMs = int.Parse(delay, CultureInfo.InvariantCulture),
                SendJitterMs = int.Parse(jitter, CultureInfo.InvariantCulture),
                SendLossPercent = float.Parse(sendLoss, CultureInfo.InvariantCulture),
                ReceiveLossPercent = float.Parse(receiveLoss, CultureInfo.InvariantCulture)
            }));
            if (GUILayout.Button("模拟断网 3 秒（自动恢复）")) Execute(() => simulator.BeginBlackout(3));
            if (GUILayout.Button("恢复正常网络")) Execute(() => { simulator.Apply(new NetworkSimulationSettings()); FillInputs(); });
            var active = simulator.Active;
            GUILayout.Label("当前配置：" + ChineseGui.Preset(active.Preset.ToString()) + " | " + active.SendDelayMs + " 毫秒 / 抖动 " + active.SendJitterMs +
                " | 发送 / 接收丢包 " + active.SendLossPercent + "% / " + active.ReceiveLossPercent + "%");
            GUILayout.Label("模拟层已就绪：" + ChineseGui.YesNo(simulator.IsPrepared) + " | 正在断网：" + ChineseGui.YesNo(simulator.IsBlackout));
            if (GUILayout.Button("导出本机 JSON 报告")) Execute(() => message = diagnostics.ExportReport());
            foreach (string entry in diagnostics.RecentEvents) GUILayout.Label(ChineseGui.Event(entry));
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(ChineseGui.Message(message));
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, window.width, 22));
        }

        /// <summary>明确区分传输 RTT、服务器处理确认及本机起步位置响应，避免混用统计口径。</summary>
        void DrawTiming(NetworkTimingSnapshot data)
        {
            if (data == null) return;
            GUILayout.Space(8);
            GUILayout.Label("输入 / 同步时序（最近 120 个样本）");
            if (data.settings != null) GUILayout.Label(NetworkTimingController.DisplayName(data.settings.preset));
            GUILayout.Label("实测帧率 " + data.framesPerSecond.ToString("F1") + " | 输入发送 " + data.observedInputHz.ToString("F1") + " Hz");
            GUILayout.Label("本地玩家：" + ChineseGui.YesNo(data.localPlayerAvailable) + " | 服务端模拟 " + data.observedServerSimulationHz.ToString("F1") + " Hz（回执观测）");
            var metrics = GetComponent<NetworkResponseMetrics>();
            metrics.CollectResponses = GUILayout.Toggle(metrics.CollectResponses, "采集响应耗时（额外回执最多 10 次/秒/玩家）");
            Summary("帧耗时", data.frameTime);
            Summary("最新输入采样 → 发出", data.inputQueue);
            Summary("输入采样 → 服务器应用确认", data.confirmation);
            Summary("服务端排队", data.serverQueue);
            Summary("服务端模拟执行", data.serverWork);
            Summary("静止起步 → 本机位置响应", data.visibleOnset);
            GUILayout.Label("起步需先静止 0.2 秒；位置响应不含 GPU / 显示器延迟。确认耗时也不是画面延迟。");
            if (GUILayout.Button("清除响应样本")) metrics.ClearMeasurements();
        }

        // 空样本显示暂无，年龄帮助识别断线/暂停后留存的旧值。
        static void Summary(string label, TimingSummary summary)
        {
            if (summary == null || !summary.available) { GUILayout.Label(label + "：暂无样本"); return; }
            GUILayout.Label(label + "：均值 " + summary.meanMs.ToString("F1") + " / P95 " + summary.p95Ms.ToString("F1") +
                " ms（" + summary.count + " 项，距今 " + (summary.ageMs / 1000).ToString("F1") + " 秒）");
        }

        /// <summary>单行参数输入；使用字符串以保留尚未输入完整的数值。</summary>
        static string Field(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(235));
            value = GUILayout.TextField(value, 12);
            GUILayout.EndHorizontal();
            return value;
        }

        /// <summary>选中预设后立即应用并刷新输入框。</summary>
        void PresetButton(string label, NetworkSimulationPreset preset)
        {
            if (GUILayout.Button(label)) Execute(() => { simulator.Apply(NetworkSimulationSettings.Create(preset)); FillInputs(); });
        }

        /// <summary>按实际配置更新文本，避免预设按钮与手动输入显示不一致。</summary>
        void FillInputs()
        {
            var value = simulator.Active;
            delay = value.SendDelayMs.ToString(CultureInfo.InvariantCulture);
            jitter = value.SendJitterMs.ToString(CultureInfo.InvariantCulture);
            sendLoss = value.SendLossPercent.ToString(CultureInfo.InvariantCulture);
            receiveLoss = value.ReceiveLossPercent.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>将输入错误留在面板上，不让 GUI 事件异常中断绘制。</summary>
        void Execute(Action action)
        {
            try { message = ""; action(); }
            catch (Exception error) { message = error.Message; Debug.LogWarning("[WeakNet] " + message); }
        }
    }
}
