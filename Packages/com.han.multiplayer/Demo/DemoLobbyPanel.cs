// 通用大厅的可替换示例 UI；核心大厅程序集不引用这个界面。
using System;
using Han.Multiplayer.Diagnostics;
using Han.Multiplayer.Lobby;
using UnityEngine;
using Han.Multiplayer.UI;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer.Demo
{
    [RequireComponent(typeof(LobbyService))]
    public sealed class DemoLobbyPanel : MonoBehaviour
    {
        LobbyService service;
        LobbyRoom subscribedRoom;
        string nickname = "Player";
        string minimum = "2";
        bool requireReady = true, allowLateJoin;
        string message = "";
        bool startupNicknameAssigned;
        Rect window = new Rect(380, 20, 480, 540);
        Vector2 scroll;

        /// <summary>昵称在连接前也能输入；房间对象生成后切换事件订阅。</summary>
        void Start() { service = GetComponent<LobbyService>(); if (!startupNicknameAssigned) nickname = service.LocalNickname; }

        /// <summary>启动器显式设置昵称，避免不同 Start 执行顺序让面板覆盖命令行昵称。</summary>
        public void SetStartupNickname(string value) { nickname = value; startupNicknameAssigned = true; }
        void Update()
        {
            if (service == null || subscribedRoom == service.Room) return;
            Subscribe(service.Room);
        }
        void OnDestroy() { Subscribe(null); }
        void Subscribe(LobbyRoom room)
        {
            if (subscribedRoom != null) subscribedRoom.Feedback -= OnFeedback;
            subscribedRoom = room;
            if (room != null)
            {
                room.Feedback += OnFeedback;
                minimum = room.Rules.Value.MinimumPlayers.ToString();
                requireReady = room.Rules.Value.RequireAllReady;
                allowLateJoin = room.Rules.Value.AllowLateJoin;
            }
        }

        /// <summary>明确显示服务器拒绝原因；客户端不能靠隐藏按钮获得权限。</summary>
        void OnFeedback(string value) { message = value == "OK" ? "操作成功" : value; }
        void Execute(Action action)
        {
            try { message = ""; action(); }
            catch (Exception error) { message = error.Message; }
        }

        void OnGUI()
        {
            if (Application.isBatchMode) return;
            using (ChineseGui.Use()) DrawChineseGui();
        }

        // 在同一中文皮肤作用域中绘制控件，结束后恢复外部界面样式。
        void DrawChineseGui()
        {
            if (service == null || Application.isBatchMode) return;
            window.width = Mathf.Min(480, Screen.width - 20);
            window.height = Mathf.Min(540, Screen.height - 20);
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            window = GUI.Window(GetInstanceID(), window, Draw, "多人大厅 · 服务器同步");
        }

        /// <summary>UI 直接读取同步名单；准备、加载完成和连接是不同状态。</summary>
        void Draw(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("当前场景：" + ChineseGui.Scene(SceneManager.GetActiveScene().name));
            GUILayout.Label("昵称（1～24 个字符）");
            nickname = GUILayout.TextField(nickname, 24);
            service.LocalNickname = nickname;
            var room = service.Room;
            if (room == null || !room.IsSpawned)
            {
                GUILayout.Label("请先在左侧创建或加入房间，等待大厅同步。");
                if (!string.IsNullOrEmpty(service.StartupError)) GUILayout.Label(ChineseGui.Message(service.StartupError));
            }
            else
            {
                if (GUILayout.Button("更新昵称")) Execute(() => room.RequestNickname(nickname));
                GUILayout.Label("阶段：" + ChineseGui.Phase(room.Phase.Value.ToString()) + " | 房主编号：" + room.LeaderId.Value);
                var rules = room.Rules.Value;
                var players = room.GetPlayersSnapshot();
                GUILayout.Label("人数：" + room.Players.Count + " / " + rules.MaximumPlayers + " | 最少人数：" + rules.MinimumPlayers);
                GUILayout.Label("要求全员准备：" + ChineseGui.Enabled(rules.RequireAllReady) + " | 允许中途加入：" + ChineseGui.Enabled(rules.AllowLateJoin));
                foreach (var player in players)
                {
                    GUILayout.BeginHorizontal();
                    string state = room.Phase.Value == LobbyPhase.Waiting ? (player.IsReady ? "已准备" : "未准备") : (player.IsLoaded ? "已加载" : "加载中");
                    GUILayout.Label("#" + player.ClientId + " " + player.Nickname + (player.ClientId == room.LeaderId.Value ? " [房主]" : "") +
                        (player.IsConnected ? " | 已连接 | " + state : " | 离线，等待重连（保留席位）"));
                    if (room.IsLocalLeader && player.ClientId != room.NetworkManager.LocalClientId && GUILayout.Button("移出", GUILayout.Width(50)))
                    { ulong target = player.ClientId; Execute(() => room.RequestKick(target)); }
                    GUILayout.EndHorizontal();
                }
                if (room.Phase.Value == LobbyPhase.Waiting)
                {
                    foreach (var player in players)
                        if (player.ClientId == room.NetworkManager.LocalClientId && GUILayout.Button(player.IsReady ? "取消准备" : "准备"))
                        { bool next = !player.IsReady; Execute(() => room.RequestReady(next)); }
                    if (room.IsLocalLeader)
                    {
                        GUILayout.Space(6);
                        GUILayout.Label("房主设置（修改后需要全员重新准备）");
                        minimum = GUILayout.TextField(minimum, 3);
                        requireReady = GUILayout.Toggle(requireReady, "开始前要求所有玩家准备");
                        allowLateJoin = GUILayout.Toggle(allowLateJoin, "允许游戏开始后中途加入");
                        if (GUILayout.Button("保存房间规则")) Execute(() => room.RequestRules(int.Parse(minimum), requireReady, allowLateJoin));
                        if (GUILayout.Button("开始游戏")) Execute(room.RequestStartGame);
                    }
                }
                if (room.Phase.Value == LobbyPhase.Loading)
                    GUILayout.Label("等待全员加载完成：" + ChineseGui.Scene(room.TargetScene.Value.ToString()));
                if (room.Phase.Value == LobbyPhase.LoadFailed) GUILayout.Label(ChineseGui.Message(room.LoadError.Value.ToString()));
                if (room.IsLocalLeader && (room.Phase.Value == LobbyPhase.Playing || room.Phase.Value == LobbyPhase.LoadFailed) && GUILayout.Button("全员返回大厅"))
                    Execute(room.RequestReturnToLobby);
            }
            var diagnostics = GetComponent<NetworkDiagnosticsPanel>();
            if (diagnostics != null && GUILayout.Button(diagnostics.Visible ? "隐藏网络诊断" : "显示网络诊断")) diagnostics.Visible = !diagnostics.Visible;
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(ChineseGui.Message(message), GUI.skin.box);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, window.width, 22));
        }
    }
}
