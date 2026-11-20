// Relay 适配接口与异步等待辅助：核心会话只接收连接数据，不直接依赖 Unity 云 SDK。
using System.Threading;
using System.Threading.Tasks;
using Unity.Networking.Transport.Relay;

namespace Han.Multiplayer
{
    /// <summary>Relay 请求成功后的连接资料；其中包含传输层密钥等信息，不应完整写入日志。</summary>
    public sealed class RelayConnection
    {
        /// <summary>交给 UnityTransport 的中继端点、分配标识和连接凭据。</summary>
        public RelayServerData ServerData { get; }
        /// <summary>供玩家分享或显示的加入码。</summary>
        public string JoinCode { get; }
        /// <summary>是否使用安全 WebSocket；浏览器端使用该传输方式。</summary>
        public bool UseWebSockets { get; }
        /// <summary>封装中继连接资料，不在这里配置或启动任何网络组件。</summary>
        public RelayConnection(RelayServerData serverData, string joinCode, bool useWebSockets)
        {
            ServerData = serverData;
            JoinCode = joinCode;
            UseWebSockets = useWebSockets;
        }
    }

    /// <summary>云服务适配器只返回数据；配置 Transport 和启动网络由 MultiplayerSession 统一负责。</summary>
    public interface IRelayProvider
    {
        /// <summary>为主机创建中继连接资料。</summary>
        /// <param name="maxPlayersIncludingHost">房间总人数，包含主机玩家。</param>
        /// <param name="region">可选地区，null 表示交由服务选择。</param>
        /// <param name="cancellationToken">取消等待的令牌；实现应避免应用迟到结果。</param>
        Task<RelayConnection> CreateAsync(int maxPlayersIncludingHost, string region, CancellationToken cancellationToken);
        /// <summary>根据加入码取得客户端所需的中继连接资料。</summary>
        /// <param name="joinCode">待加入房间的 Relay 码。</param>
        /// <param name="cancellationToken">调用方的取消或超时令牌。</param>
        Task<RelayConnection> JoinAsync(string joinCode, CancellationToken cancellationToken);
    }

    /// <summary>为不能直接取消的异步操作提供“取消等待”的统一封装。</summary>
    public static class AsyncCancellation
    {
        /// <summary>取消时忽略迟到结果并观察迟到异常；不会中止底层 UGS HTTP 请求。</summary>
        /// <typeparam name="T">底层任务成功时返回的数据类型。</typeparam>
        /// <param name="task">已经开始执行的异步任务。</param>
        /// <param name="token">决定是否提前结束等待的令牌。</param>
        /// <returns>未取消时返回原任务结果；取消时抛出取消异常。</returns>
        public static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken token)
        {
            // 异步延续避免在触发取消的线程中直接执行后续业务逻辑。
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                // 原任务完成与取消信号竞争；即使同时到达，也优先尊重取消意图。
                await Task.WhenAny(task, cancelled.Task);
                if (token.IsCancellationRequested)
                {
                    // 不再等待原任务后仍读取其异常，避免形成未观察的后台失败。
                    _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    token.ThrowIfCancellationRequested();
                }
                token.ThrowIfCancellationRequested();
                return await task;
            }
        }
    }
}
