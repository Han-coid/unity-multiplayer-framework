// 开发鉴权不验证真实身份，只用于本机验收；正式构建在验证器端强制拒绝，不能当作生产登录服务。
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    public sealed class DevelopmentAccountValidator : MonoBehaviour, IAccountTokenValidator
    {
        public async Task<AccountValidationResult> ValidateAsync(string credential, string applicationId, CancellationToken cancellationToken)
        {
            // 故意让出一帧，实际走异步 Pending 路径，避免测试只覆盖同步完成。
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            if (!(Application.isEditor || Debug.isDebugBuild)) return AccountValidationResult.Reject("AUTH_DEVELOPMENT_DISABLED");
            var parts = credential?.Split('|');
            if (parts == null || parts.Length != 4 || parts[0] != "dev1" || !AccountPrincipal.ValidIdentifier(parts[1]) ||
                parts[2] != applicationId || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long expires))
                return AccountValidationResult.Reject("AUTH_INVALID_TOKEN");
            if (expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return AccountValidationResult.Reject("AUTH_TOKEN_EXPIRED");
            if (parts[1] == "slow" || parts[1] == "late")
                await Task.Delay(6000, parts[1] == "late" ? CancellationToken.None : cancellationToken); // 测试超时和不合作的迟到后端。
            if (parts[1] == "banned") return AccountValidationResult.Reject("AUTH_BANNED");
            // 权限是验证器的服务器决定，不从客户端凭证中读取 role/admin 字段。
            return AccountValidationResult.Accept(new AccountPrincipal(parts[1], applicationId, expires, new[] { "play", "lobby.use" }));
        }
    }
}
