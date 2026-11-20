// 可选账号适配契约：核心不依赖任何账号 SDK，也不相信客户端自报的账号或权限。
using System.Threading;
using System.Threading.Tasks;

namespace Han.Multiplayer
{
    public interface IConnectionAuthenticator
    {
        int MaximumPendingAuthentications { get; }
        float AuthenticationTimeoutSeconds { get; }
        Task<byte[]> CreateAuthenticationDataAsync(bool localPlayer, CancellationToken token);
        Task<string> AuthenticateAsync(ulong clientId, byte[] data, string applicationId, CancellationToken token);
        string GetAccountId(ulong clientId);
        void CompleteAuthentication(ulong clientId, bool approved);
        void RevokeClient(ulong clientId);
    }
}
