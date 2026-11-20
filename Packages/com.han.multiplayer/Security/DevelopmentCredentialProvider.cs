// 开发鉴权不验证真实身份，只用于本机验收；正式构建在验证器端强制拒绝，不能当作生产登录服务。
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    public sealed class DevelopmentCredentialProvider : MonoBehaviour, IClientCredentialProvider
    {
        public string AccountId = "guest";
        public string TestCredentialKind = "valid";
        public Task<string> GetCredentialAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!(Application.isEditor || Debug.isDebugBuild)) throw new InvalidOperationException("AUTH_DEVELOPMENT_DISABLED");
            if (TestCredentialKind == "missing") return Task.FromResult<string>(null);
            if (TestCredentialKind == "forged") return Task.FromResult("invalid-test-credential");
            var session = GetComponent<MultiplayerSession>();
            long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (TestCredentialKind == "expired" ? -60 : 3600);
            return Task.FromResult("dev1|" + AccountId + "|" + session.Settings.ApplicationId + "|" + expires.ToString(CultureInfo.InvariantCulture));
        }
    }
}
