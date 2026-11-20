// 示例使用独立入口场景，Lobby/Game 都不重复放置 NetworkManager，避免返回大厅时出现第二套网络根对象。
using System;
using System.Threading.Tasks;
using Han.Multiplayer.Lobby;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Han.Multiplayer.Demo
{
    public sealed class DemoLobbyEntry : MonoBehaviour
    {
        /// <summary>只在离线启动阶段本地加载大厅；建立会话后的场景切换由 LobbyRoom 控制。</summary>
        public async Task OpenLobbyBeforeConnectingAsync()
        {
            var session = GetComponent<MultiplayerSession>();
            var service = GetComponent<LobbyService>();
            if (session.State != SessionState.Offline) throw new InvalidOperationException("Lobby entry must run before connecting.");
            if (SceneManager.GetActiveScene().path == service.Settings.LobbyScene) return;
            var operation = SceneManager.LoadSceneAsync(service.Settings.LobbyScene);
            while (!operation.isDone) await Task.Yield();
        }
    }
}
