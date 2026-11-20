// Unity 云服务适配：负责登录、Relay 分配和加入，实际网络启动仍由核心会话控制。
using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

namespace Han.Multiplayer.Relay
{
    /// <summary>Relay 使用的安全传输协议；桌面默认 DTLS，浏览器使用 WSS。</summary>
    public enum RelayProtocol
    {
        Dtls,            // 基于 UDP 的加密数据报传输。
        SecureWebSocket  // 安全 WebSocket；包含 WebGL 玩家的示例房间统一选择此项。
    }

    /// <summary>把 Unity Authentication 和 Relay SDK 封装为可替换的 IRelayProvider。</summary>
    [DisallowMultipleComponent]
    public sealed class UnityRelayProvider : MonoBehaviour, IRelayProvider
    {
        /// <summary>本地身份缓存配置名称；同机多个测试玩家应使用不同 Profile。</summary>
        [Tooltip("Use different profiles when testing multiple players on the same computer. Restart before changing an authenticated profile.")]
        public string AuthenticationProfile = "default";
        /// <summary>桌面或手机实例选择的 Relay 协议；WebGL 构建会强制使用 WSS。</summary>
        [Tooltip("Select SecureWebSocket on ALL players if a WebGL player will join the room.")]
        public RelayProtocol Protocol = RelayProtocol.Dtls;
        // UGS 身份服务为进程共享实例，串行化登录流程以避免多个请求同时初始化或切换身份。
        static readonly SemaphoreSlim AuthenticationGate = new SemaphoreSlim(1, 1);

        /// <summary>将 Inspector 配置转换为 Relay SDK 要求的小写协议字符串。</summary>
        string ConnectionType
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return "wss";
#else
                return Protocol == RelayProtocol.SecureWebSocket ? "wss" : "dtls";
#endif
            }
        }

        /// <summary>登录后为主机创建 Relay 分配并获取房间码。</summary>
        /// <param name="maxPlayersIncludingHost">框架定义的总人数，包含主机玩家。</param>
        /// <param name="region">可选地区；空值交由 Relay 服务选择。</param>
        /// <param name="cancellationToken">取消当前等待，阻止后续步骤继续消费结果。</param>
        /// <returns>连接资料与加入码；本方法不会调用 NetworkManager.StartHost。</returns>
        public async Task<RelayConnection> CreateAsync(int maxPlayersIncludingHost, string region, CancellationToken cancellationToken)
        {
            if (maxPlayersIncludingHost < 2 || maxPlayersIncludingHost > 128)
                throw new ArgumentOutOfRangeException(nameof(maxPlayersIncludingHost));
            string connectionType = ConnectionType;
            await EnsureAuthenticatedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Relay 的 maxConnections 不计主机，框架人数包含主机，因此在边界处减一。
            var allocation = await AsyncCancellation.WaitAsync(
                RelayService.Instance.CreateAllocationAsync(maxPlayersIncludingHost - 1, string.IsNullOrWhiteSpace(region) ? null : region), cancellationToken);
            // 房间码属于刚创建的分配，不能当作永久房间标识保存复用。
            var code = await AsyncCancellation.WaitAsync(RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId), cancellationToken);
            return new RelayConnection(AllocationUtils.ToRelayServerData(allocation, connectionType), code, connectionType == "wss");
        }

        /// <summary>登录后使用加入码请求客户端的 Relay 连接资料。</summary>
        /// <param name="joinCode">由主机分享的加入码。</param>
        /// <param name="cancellationToken">控制等待和后续处理的取消令牌。</param>
        public async Task<RelayConnection> JoinAsync(string joinCode, CancellationToken cancellationToken)
        {
            joinCode = SessionSettings.NormalizeJoinCode(joinCode);
            string connectionType = ConnectionType;
            await EnsureAuthenticatedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var allocation = await AsyncCancellation.WaitAsync(RelayService.Instance.JoinAllocationAsync(joinCode), cancellationToken);
            return new RelayConnection(AllocationUtils.ToRelayServerData(allocation, connectionType), joinCode, connectionType == "wss");
        }

        /// <summary>使用调用开始时的 Profile 登录，允许外层会话停止等待。</summary>
        async Task EnsureAuthenticatedAsync(CancellationToken token)
        {
            string profile = AuthenticationProfile;
            // UGS 请求本身不可中止。调用方取消等待后，底层登录仍持有锁直到真正完成。
            await AsyncCancellation.WaitAsync(InitializeAndSignInAsync(profile), token);
        }

        /// <summary>在共享锁内初始化 UGS 并建立身份；不会擅自退出游戏已经登录的账号。</summary>
        /// <returns>成功时返回 true，便于复用泛型异步取消封装。</returns>
        static async Task<bool> InitializeAndSignInAsync(string profile)
        {
            await AuthenticationGate.WaitAsync();
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                    await UnityServices.InitializeAsync(new InitializationOptions().SetProfile(profile));

                var authentication = AuthenticationService.Instance;
                if (authentication.Profile != profile)
                {
                    // 已登录的其他 Profile 可能承载游戏账号，不能为了演示联机把它替换掉。
                    if (authentication.IsSignedIn)
                        throw new InvalidOperationException("UGS is already signed in with another profile. Restart the player to change profiles.");
                    authentication.SwitchProfile(profile);
                }
                // 若游戏已完成平台账号登录则沿用；只有未登录时才尝试匿名登录。
                if (!authentication.IsSignedIn) await authentication.SignInAnonymouslyAsync();
                return true;
            }
            finally { AuthenticationGate.Release(); }
        }
    }
}
