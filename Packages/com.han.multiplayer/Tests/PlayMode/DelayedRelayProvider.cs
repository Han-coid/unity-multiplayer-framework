// 测试用 Relay 替身：由测试主动决定请求何时完成，无需连接任何真实云服务。
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Han.Multiplayer.Tests
{
    /// <summary>提供一个不会自行结束的任务，用于重现慢请求、超时和迟到结果。</summary>
    public sealed class DelayedRelayProvider : MonoBehaviour, IRelayProvider
    {
        /// <summary>测试通过该任务源模拟 Relay 请求成功或失败。</summary>
        public readonly TaskCompletionSource<RelayConnection> Completion = new TaskCompletionSource<RelayConnection>();
        /// <summary>记录会话是否已调用适配器，便于在取消前确认测试确实进入云请求阶段。</summary>
        public bool Requested;
        /// <summary>返回可控任务；故意不响应 token，以模拟不能被中止的底层 HTTP 请求。</summary>
        public Task<RelayConnection> CreateAsync(int maxPlayersIncludingHost, string region, CancellationToken token)
        {
            Requested = true;
            return Completion.Task;
        }
        /// <summary>加入路径复用同一个延迟任务，测试只关心异步生命周期，不区分云请求内容。</summary>
        public Task<RelayConnection> JoinAsync(string joinCode, CancellationToken token) => CreateAsync(2, null, token);
    }
}
