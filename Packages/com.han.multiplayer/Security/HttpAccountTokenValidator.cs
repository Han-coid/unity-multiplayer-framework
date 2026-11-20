// 服务器通过 HTTPS 向自己的账号后端验证不透明短票据；不自行解析或“解码即信任”JWT。
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    public sealed class HttpAccountTokenValidator : MonoBehaviour, IAccountTokenValidator
    {
        public string Endpoint = "https://example.invalid/game/validate";
        public string ServerKeyEnvironmentVariable = "HAN_ACCOUNT_BACKEND_KEY";
        public bool AllowHttpLoopbackForDevelopment;
        HttpClient http;
        const int MaximumResponseBytes = 8192;
        [Serializable] sealed class RequestBody { public string token; public string applicationId; }
        [Serializable] sealed class ResponseBody
        {
            public bool valid;
            public string error, accountId, applicationId;
            public long expiresUnixSeconds;
            public string[] permissions;
        }

        public async Task<AccountValidationResult> ValidateAsync(string credential, string applicationId, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && AllowHttpLoopbackForDevelopment && (Application.isEditor || Debug.isDebugBuild))))
                return AccountValidationResult.Reject("AUTH_BACKEND_CONFIGURATION");
            if (string.IsNullOrEmpty(credential) || Encoding.UTF8.GetByteCount(credential) > AccountAdmissionService.MaximumCredentialBytes)
                return AccountValidationResult.Reject("AUTH_INVALID_TOKEN");
            if (http == null)
            {
                // 不跟随重定向，避免票据或服务器密钥被重定向到另一个站点；使用系统默认 TLS 证书校验。
                http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
            }
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
                {
                    request.Content = new StringContent(JsonUtility.ToJson(new RequestBody { token = credential, applicationId = applicationId }), Encoding.UTF8, "application/json");
                    if (!string.IsNullOrEmpty(ServerKeyEnvironmentVariable))
                    {
                        string key = Environment.GetEnvironmentVariable(ServerKeyEnvironmentVariable);
                        if (string.IsNullOrEmpty(key)) return AccountValidationResult.Reject("AUTH_BACKEND_KEY_MISSING");
                        foreach (char c in key) if (char.IsControl(c)) return AccountValidationResult.Reject("AUTH_BACKEND_CONFIGURATION");
                        request.Headers.Add("X-Game-Server-Key", key);
                    }
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            int status = (int)response.StatusCode;
                            return AccountValidationResult.Reject(status >= 500 || status == 429 || status == 408 ? "AUTH_BACKEND_UNAVAILABLE" : "AUTH_BACKEND_REJECTED");
                        }
                        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > MaximumResponseBytes)
                            return AccountValidationResult.Reject("AUTH_BACKEND_INVALID_RESPONSE");
                        byte[] bytes;
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var output = new MemoryStream())
                        {
                            var buffer = new byte[1024];
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaximumResponseBytes + 1 - (int)output.Length), cancellationToken)) > 0)
                            {
                                output.Write(buffer, 0, read);
                                if (output.Length > MaximumResponseBytes) return AccountValidationResult.Reject("AUTH_BACKEND_INVALID_RESPONSE");
                            }
                            bytes = output.ToArray();
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        var body = JsonUtility.FromJson<ResponseBody>(new UTF8Encoding(false, true).GetString(bytes));
                        if (body == null) return AccountValidationResult.Reject("AUTH_BACKEND_INVALID_RESPONSE");
                        if (!body.valid) return AccountValidationResult.Reject(body.error ?? "AUTH_INVALID_TOKEN");
                        if (body.applicationId != applicationId) return AccountValidationResult.Reject("AUTH_WRONG_AUDIENCE");
                        var principal = new AccountPrincipal(body.accountId, body.applicationId, body.expiresUnixSeconds, body.permissions);
                        if (principal.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return AccountValidationResult.Reject("AUTH_TOKEN_EXPIRED");
                        return AccountValidationResult.Accept(principal);
                    }
                }
            }
            catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); return AccountValidationResult.Reject("AUTH_BACKEND_UNAVAILABLE"); }
            catch (HttpRequestException) { return AccountValidationResult.Reject("AUTH_BACKEND_UNAVAILABLE"); }
            catch (Exception) { return AccountValidationResult.Reject("AUTH_BACKEND_INVALID_RESPONSE"); } // 不记录 URL 参数、票据、密钥或响应正文。
        }
        void OnDestroy() { http?.Dispose(); }
    }
}
