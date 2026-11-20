// 账号供应器契约：客户端只提供不透明凭证，账号 ID、权限和有效期由服务器验证结果决定。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    public interface IClientCredentialProvider
    {
        Task<string> GetCredentialAsync(CancellationToken cancellationToken);
    }
    public interface IAccountTokenValidator
    {
        Task<AccountValidationResult> ValidateAsync(string credential, string applicationId, CancellationToken cancellationToken);
    }

    /// <summary>验证后的不可变主体；权限精确匹配，不默认给予通配或管理员权限。</summary>
    public sealed class AccountPrincipal
    {
        readonly HashSet<string> permissions;
        public string AccountId { get; }
        public string ApplicationId { get; }
        public long ExpiresUnixSeconds { get; }
        public AccountPrincipal(string accountId, string applicationId, long expiresUnixSeconds, string[] grantedPermissions)
        {
            if (!ValidIdentifier(accountId) || !ValidIdentifier(applicationId) || expiresUnixSeconds <= 0 ||
                grantedPermissions == null || grantedPermissions.Length > 32) throw new ArgumentException("Invalid verified account claims.");
            permissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (string permission in grantedPermissions)
            {
                if (!ValidIdentifier(permission)) throw new ArgumentException("Invalid verified account permission.");
                permissions.Add(permission);
            }
            AccountId = accountId; ApplicationId = applicationId; ExpiresUnixSeconds = expiresUnixSeconds;
        }
        public bool HasPermission(string permission) => permission == null || permissions.Contains(permission);
        public bool IsExpired(long now) => ExpiresUnixSeconds <= now;
        public static bool ValidIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (char c in value)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
            return true;
        }
    }

    public sealed class AccountValidationResult
    {
        public AccountPrincipal Principal { get; }
        public string Error { get; }
        AccountValidationResult(AccountPrincipal principal, string error) { Principal = principal; Error = error; }
        public static AccountValidationResult Accept(AccountPrincipal principal) => new AccountValidationResult(principal ?? throw new ArgumentNullException(nameof(principal)), null);
        public static AccountValidationResult Reject(string code) => new AccountValidationResult(null, SafeCode(code));
        public static string SafeCode(string code)
        {
            if (code == null || code.Length > 64 || !code.StartsWith("AUTH_", StringComparison.Ordinal)) return "AUTH_REJECTED";
            foreach (char c in code) if (!(c == '_' || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return "AUTH_REJECTED";
            return code;
        }
    }

}
