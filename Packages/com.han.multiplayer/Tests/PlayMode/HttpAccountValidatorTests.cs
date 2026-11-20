// 回环 TCP 假后端验证真实 HTTP 适配器；允许明文回环仅限开发测试，不访问外部账号服务。
using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Han.Multiplayer.Security;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Han.Multiplayer.Tests
{
    public class HttpAccountValidatorTests
    {
        sealed class Backend : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            public string Body, ContentType = "application/json";
            public string ResponseHeaders = "";
            public string Request;
            public int Status = 200;
            public string Url { get; private set; }
            public Backend(string body)
            {
                Body = body; listener.Start();
                Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/validate";
                _ = RunAsync();
            }
            async Task RunAsync()
            {
                try
                {
                    using (var client = await listener.AcceptTcpClientAsync())
                    using (var stream = client.GetStream())
                    {
                        var headers = new StringBuilder(); var single = new byte[1];
                        while (headers.Length < 8192 && !headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        { if (await stream.ReadAsync(single, 0, 1, cancellation.Token) == 0) return; headers.Append((char)single[0]); }
                        int length = 0;
                        foreach (string line in headers.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None))
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                        if (length > 4096) return;
                        if (headers.ToString().IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                            await stream.WriteAsync(interim, 0, interim.Length, cancellation.Token);
                        }
                        var data = new byte[length]; int received = 0;
                        while (received < length)
                        { int count = await stream.ReadAsync(data, received, length - received, cancellation.Token); if (count == 0) return; received += count; }
                        Request = headers + Encoding.UTF8.GetString(data);
                        var body = Encoding.UTF8.GetBytes(Body);
                        var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + Status + " Test\r\nContent-Type: " + ContentType + "\r\nContent-Length: " + body.Length + "\r\n" + ResponseHeaders + "Connection: close\r\n\r\n");
                        await stream.WriteAsync(response, 0, response.Length, cancellation.Token);
                        await stream.WriteAsync(body, 0, body.Length, cancellation.Token);
                    }
                }
                catch (Exception) { /* 测试销毁监听器或被验证器提前拒绝时允许连接关闭。 */ }
            }
            public void Dispose() { cancellation.Cancel(); listener.Stop(); }
        }
        GameObject root;
        HttpAccountTokenValidator validator;
        Backend backend;
        string environmentName;
        string ValidBody => "{\"valid\":true,\"accountId\":\"alice\",\"applicationId\":\"game\",\"expiresUnixSeconds\":" +
            (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600) + ",\"permissions\":[\"play\"]}";
        [SetUp]
        public void SetUp()
        {
            root = new GameObject("HTTP account test"); validator = root.AddComponent<HttpAccountTokenValidator>();
            validator.AllowHttpLoopbackForDevelopment = true;
            environmentName = "HAN_ACCOUNT_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(environmentName, "test-server-key"); validator.ServerKeyEnvironmentVariable = environmentName;
        }
        [TearDown]
        public void TearDown()
        { backend?.Dispose(); Environment.SetEnvironmentVariable(environmentName, null); UnityEngine.Object.DestroyImmediate(root); }
        Task<AccountValidationResult> Run(string body)
        { backend = new Backend(body); validator.Endpoint = backend.Url; return validator.ValidateAsync("opaque-test-ticket", "game", CancellationToken.None); }
        [UnityTest]
        public IEnumerator ValidBackendProvidesPrincipalAndReceivesPostBody()
        {
            var task = Run(ValidBody); yield return Wait(task);
            Assert.AreEqual("alice", task.Result.Principal.AccountId); Assert.IsTrue(task.Result.Principal.HasPermission("play"));
            StringAssert.StartsWith("POST /validate HTTP/1.1", backend.Request);
            StringAssert.Contains("X-Game-Server-Key: test-server-key", backend.Request);
            StringAssert.Contains("\"token\":\"opaque-test-ticket\"", backend.Request);
        }
        [UnityTest]
        public IEnumerator HttpRequiresExplicitDevelopmentLoopbackOverride()
        {
            validator.Endpoint = "http://127.0.0.1:1/validate"; validator.AllowHttpLoopbackForDevelopment = false;
            var task = validator.ValidateAsync("test", "game", CancellationToken.None); yield return Wait(task);
            Assert.AreEqual("AUTH_BACKEND_CONFIGURATION", task.Result.Error);
        }
        [UnityTest]
        public IEnumerator WrongAudienceIsRejected()
        {
            var task = Run(ValidBody.Replace("\"game\"", "\"other\"")); yield return Wait(task);
            Assert.AreEqual("AUTH_WRONG_AUDIENCE", task.Result.Error);
        }
        [UnityTest]
        public IEnumerator LargeAndMalformedResponsesFailClosed()
        {
            var task = Run(new string('x', 8193)); yield return Wait(task);
            Assert.AreEqual("AUTH_BACKEND_INVALID_RESPONSE", task.Result.Error);
        }
        [UnityTest]
        public IEnumerator BackendErrorCannotEchoTokenOrBody()
        {
            var task = Run("{\"valid\":false,\"error\":\"token=private-value\"}"); yield return Wait(task);
            Assert.AreEqual("AUTH_REJECTED", task.Result.Error);
        }
        [UnityTest]
        public IEnumerator ServerOutageIsRetryable()
        {
            backend = new Backend("outage") { Status = 503 }; validator.Endpoint = backend.Url;
            var task = validator.ValidateAsync("test", "game", CancellationToken.None); yield return Wait(task);
            Assert.AreEqual("AUTH_BACKEND_UNAVAILABLE", task.Result.Error);
        }
        [UnityTest]
        public IEnumerator MissingServerKeyNeverSendsCredential()
        {
            backend = new Backend(ValidBody); validator.Endpoint = backend.Url;
            Environment.SetEnvironmentVariable(environmentName, null);
            var task = validator.ValidateAsync("test", "game", CancellationToken.None); yield return Wait(task);
            Assert.AreEqual("AUTH_BACKEND_KEY_MISSING", task.Result.Error); Assert.IsNull(backend.Request);
        }
        [UnityTest]
        public IEnumerator BackendCookiesAreNotSharedBetweenAccountValidations()
        {
            backend = new Backend(ValidBody) { ResponseHeaders = "Set-Cookie: account-session=first; Path=/\r\n" };
            validator.Endpoint = backend.Url;
            var first = validator.ValidateAsync("first-ticket", "game", CancellationToken.None); yield return Wait(first);
            Assert.IsNotNull(first.Result.Principal); backend.Dispose();
            backend = new Backend(ValidBody); validator.Endpoint = backend.Url;
            var second = validator.ValidateAsync("second-ticket", "game", CancellationToken.None); yield return Wait(second);
            Assert.IsNotNull(second.Result.Principal); StringAssert.DoesNotContain("Cookie:", backend.Request);
        }
        static IEnumerator Wait(Task task)
        {
            double until = Time.realtimeSinceStartupAsDouble + 12;
            while (!task.IsCompleted && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.IsTrue(task.IsCompleted); if (task.IsFaulted) throw task.Exception;
        }
    }
}
