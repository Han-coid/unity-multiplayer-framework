// 仅测试程序集使用：可模拟拒绝、延迟及不合作的后端，确认超时后不能复活身份。
using System;
using System.Threading;
using System.Threading.Tasks;
using Han.Multiplayer.Security;
using UnityEngine;
namespace Han.Multiplayer.Tests
{
    public sealed class ControlledAccountValidator : MonoBehaviour, IAccountTokenValidator
    {
        public bool Reject, IgnoreCancellation;
        public int DelayMilliseconds;
        public async Task<AccountValidationResult> ValidateAsync(string credential, string applicationId, CancellationToken token)
        {
            if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, IgnoreCancellation ? CancellationToken.None : token);
            if (Reject) return AccountValidationResult.Reject("AUTH_INVALID_TOKEN");
            return AccountValidationResult.Accept(new AccountPrincipal("alice", applicationId, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, new[] { "play", "lobby.use" }));
        }
    }
}
