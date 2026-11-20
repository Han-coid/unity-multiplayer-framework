// 内存凭证供应器：从登录 UI 或 SDK 接收结果，不写入场景、PlayerPrefs 或日志。
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
namespace Han.Multiplayer.Security
{
    public sealed class MemoryCredentialProvider : MonoBehaviour, IClientCredentialProvider
    {
        string credential;
        public void SetCredential(string value) { credential = value; }
        public void ClearCredential() { credential = null; }
        public Task<string> GetCredentialAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(credential); }
    }
}
