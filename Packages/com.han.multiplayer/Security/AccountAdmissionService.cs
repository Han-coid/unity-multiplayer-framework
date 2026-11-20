// 账号准入编排：验证器可替换，重复登录默认拒绝，重连必须同时满足同账号和恢复凭证。
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    public enum AccountStatus { Offline, Preparing, Authenticated, ServerReady, Failed }

    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession), typeof(ServerRequestGate))]
    public sealed class AccountAdmissionService : MonoBehaviour, IConnectionAuthenticator, IConnectionAdmissionRule
    {
        public const int MaximumCredentialBytes = 256;
        const string AccountInfoMessage = "Han.Account.Info.v1";
        [SerializeField] MonoBehaviour credentialProvider;
        [SerializeField] MonoBehaviour tokenValidator;
        [Range(1, 30)] public float ValidationTimeoutSeconds = 5;
        [Range(1, 128)] public int PendingLimit = 16;
        IClientCredentialProvider activeCredentials;
        IAccountTokenValidator activeValidator;
        MultiplayerSession session;
        NetworkManager manager;
        readonly AccountBindingBook bindings = new AccountBindingBook();
        sealed class Connection
        {
            public AccountPrincipal Principal;
            public bool Approved, Confirmed;
            public double Until;
        }
        readonly Dictionary<ulong, Connection> accounts = new Dictionary<ulong, Connection>();
        readonly HashSet<string> banned = new HashSet<string>(StringComparer.Ordinal);
        readonly List<ulong> work = new List<ulong>();
        int activeLimit;
        float activeTimeout;
        public int MaximumPendingAuthentications => activeLimit;
        public float AuthenticationTimeoutSeconds => activeTimeout;
        public string LocalAccountId { get; private set; } = "";
        public AccountStatus Status { get; private set; }
        public string LastError { get; private set; } = "";

        void Awake() { session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>(); }
        void OnEnable()
        {
            session.StateChanged += OnState;
            session.PlayerConnected += OnConnected;
            manager.OnClientStarted += RegisterMessages; manager.OnServerStarted += RegisterMessages;
        }
        void OnDisable()
        {
            if (session == null) return;
            session.StateChanged -= OnState; session.PlayerConnected -= OnConnected;
            manager.OnClientStarted -= RegisterMessages; manager.OnServerStarted -= RegisterMessages;
            manager.CustomMessagingManager?.UnregisterNamedMessageHandler(AccountInfoMessage);
        }
        public void SetProviders(MonoBehaviour credentials, MonoBehaviour validator)
        {
            if (manager.IsListening || manager.ShutdownInProgress || (session.State != SessionState.Offline && session.State != SessionState.Faulted))
                throw new InvalidOperationException("Disconnect before changing account providers.");
            if (credentials != null && !(credentials is IClientCredentialProvider)) throw new ArgumentException("Invalid credential provider.");
            if (validator != null && !(validator is IAccountTokenValidator)) throw new ArgumentException("Invalid account validator.");
            credentialProvider = credentials; tokenValidator = validator;
        }
        void OnState(SessionState state)
        {
            if (state == SessionState.Starting)
            {
                accounts.Clear(); bindings.Clear(); banned.Clear(); LocalAccountId = ""; LastError = "";
                activeLimit = PendingLimit; activeTimeout = ValidationTimeoutSeconds;
                activeCredentials = credentialProvider != null && credentialProvider.isActiveAndEnabled ? credentialProvider as IClientCredentialProvider : null;
                activeValidator = tokenValidator != null && tokenValidator.isActiveAndEnabled ? tokenValidator as IAccountTokenValidator : null;
                Status = AccountStatus.Preparing;
            }
            if (state == SessionState.Server) Status = AccountStatus.ServerReady;
            if (state == SessionState.Offline) { LocalAccountId = ""; Status = AccountStatus.Offline; }
            if (state == SessionState.Faulted) { LastError = session.LastError; LocalAccountId = ""; Status = AccountStatus.Failed; }
        }
        public async Task<byte[]> CreateAuthenticationDataAsync(bool localPlayer, CancellationToken token)
        {
            if (!session.IsClientSession && activeValidator == null) throw new InvalidOperationException("AUTH_VALIDATOR_REQUIRED");
            if (!(Application.isEditor || Debug.isDebugBuild) && (activeValidator is DevelopmentAccountValidator || activeCredentials is DevelopmentCredentialProvider))
                throw new InvalidOperationException("AUTH_DEVELOPMENT_DISABLED");
            if (!localPlayer) return Array.Empty<byte>();
            if (activeCredentials == null) throw new InvalidOperationException("AUTH_CREDENTIAL_PROVIDER_REQUIRED");
            string value = await activeCredentials.GetCredentialAsync(token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();
            foreach (char c in value) if (char.IsControl(c)) throw new InvalidOperationException("AUTH_INVALID_TOKEN");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > MaximumCredentialBytes) throw new InvalidOperationException("AUTH_TOKEN_TOO_LARGE");
            return bytes;
        }
        public async Task<string> AuthenticateAsync(ulong clientId, byte[] data, string applicationId, CancellationToken token)
        {
            if (data == null || data.Length == 0) return "AUTH_REQUIRED";
            if (data.Length > MaximumCredentialBytes) return "AUTH_TOKEN_TOO_LARGE";
            string credential;
            try { credential = new UTF8Encoding(false, true).GetString(data); }
            catch (DecoderFallbackException) { return "AUTH_INVALID_TOKEN"; }
            foreach (char c in credential) if (char.IsControl(c)) return "AUTH_INVALID_TOKEN";
            if (activeValidator == null) return "AUTH_VALIDATOR_REQUIRED";
            var result = await activeValidator.ValidateAsync(credential, applicationId, token);
            token.ThrowIfCancellationRequested(); // 迟到的后端结果不得在取消/重启后写入身份表。
            if (result == null || result.Principal == null) return AccountValidationResult.SafeCode(result?.Error);
            var principal = result.Principal;
            if (principal.ApplicationId != applicationId) return "AUTH_WRONG_AUDIENCE";
            if (principal.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds())) return "AUTH_TOKEN_EXPIRED";
            if (banned.Contains(principal.AccountId)) return "AUTH_BANNED";
            accounts[clientId] = new Connection { Principal = principal, Until = Time.realtimeSinceStartupAsDouble + manager.NetworkConfig.ClientConnectionBufferTimeout + 5 };
            return null;
        }
        public string GetAccountId(ulong clientId) => accounts.TryGetValue(clientId, out var account) ? account.Principal.AccountId : null;
        public bool TryGetPrincipal(ulong clientId, out AccountPrincipal principal)
        {
            principal = null;
            if (!accounts.TryGetValue(clientId, out var account) || !account.Approved || !account.Confirmed) return false;
            principal = account.Principal;
            return true;
        }
        public string CheckAdmission(ulong clientId)
        {
            if (!accounts.TryGetValue(clientId, out var account)) return "AUTH_REQUIRED";
            session.TryGetPreviousClientId(clientId, out ulong previous);
            return bindings.TryClaim(account.Principal.AccountId, clientId, previous, Time.realtimeSinceStartupAsDouble,
                manager.NetworkConfig.ClientConnectionBufferTimeout + 5, IsConnected, session.IsHeldClient);
        }
        public void CompleteAuthentication(ulong clientId, bool approved)
        {
            if (!approved) { bindings.Rollback(clientId); accounts.Remove(clientId); }
            else if (accounts.TryGetValue(clientId, out var account)) account.Approved = true;
        }
        public void RevokeClient(ulong clientId)
        {
            ulong successor = bindings.Revoke(clientId);
            accounts.Remove(clientId);
            if (successor != ulong.MaxValue && successor != clientId)
            {
                accounts.Remove(successor);
                if (IsConnected(successor)) manager.DisconnectClient(successor, "AUTH_REVOKED");
            }
        }
        /// <summary>服务器会话内封禁；持久封禁仍由账号后端保存并在每次验证时返回。</summary>
        public void BanAccount(string accountId)
        {
            if (!manager.IsServer || !AccountPrincipal.ValidIdentifier(accountId) || banned.Count >= 1024) throw new InvalidOperationException("Cannot apply account ban.");
            banned.Add(accountId);
            if (bindings.TryGetClient(accountId, out ulong clientId))
            {
                if (clientId == 0) _ = session.StopAsync(); else session.DisconnectPlayer(clientId, "AUTH_BANNED");
            }
        }
        bool IsConnected(ulong id) => manager.IsServer && manager.ConnectedClients.ContainsKey(id);
        void RegisterMessages() => manager.CustomMessagingManager.RegisterNamedMessageHandler(AccountInfoMessage, ReceiveAccountInfo);
        void OnConnected(ulong id)
        {
            if (!manager.IsServer) return;
            if (!accounts.TryGetValue(id, out var account) || !account.Approved || account.Principal.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            { _ = RejectLateConnectionAsync(id); return; }
            account.Confirmed = true; bindings.Confirm(id);
            if (id == 0) { LocalAccountId = account.Principal.AccountId; Status = AccountStatus.Authenticated; return; }
            using (var writer = new FastBufferWriter(160, Allocator.Temp))
            {
                writer.WriteValueSafe(new FixedString128Bytes(account.Principal.AccountId));
                manager.CustomMessagingManager.SendNamedMessage(AccountInfoMessage, id, writer, NetworkDelivery.ReliableSequenced);
            }
        }
        async Task RejectLateConnectionAsync(ulong id)
        {
            await Task.Yield();
            if (manager == null || !manager.IsServer) return;
            if (id == 0) await session.StopAsync(); else session.DisconnectPlayer(id, "AUTH_REVOKED");
        }
        void ReceiveAccountInfo(ulong sender, FastBufferReader reader)
        {
            if (!manager.IsClient || manager.IsServer || sender != 0) return;
            try
            {
                reader.ReadValueSafe(out FixedString128Bytes id);
                if (!AccountPrincipal.ValidIdentifier(id.ToString())) return;
                LocalAccountId = id.ToString(); Status = AccountStatus.Authenticated;
            }
            catch (Exception) { LastError = "AUTH_INVALID_RESPONSE"; }
        }
        void Update()
        {
            if (!manager.IsServer || manager.ShutdownInProgress) return;
            bindings.Prune(Time.realtimeSinceStartupAsDouble, IsConnected, session.IsHeldClient);
            work.Clear(); work.AddRange(accounts.Keys);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (ulong id in work)
            {
                if (!accounts.TryGetValue(id, out var account)) continue;
                if (account.Principal.IsExpired(now) && account.Confirmed)
                {
                    if (id == 0) { LastError = "AUTH_TOKEN_EXPIRED"; _ = session.StopAsync(); }
                    else session.DisconnectPlayer(id, "AUTH_TOKEN_EXPIRED");
                    continue;
                }
                if ((!account.Confirmed && account.Until <= Time.realtimeSinceStartupAsDouble) ||
                    (account.Confirmed && !IsConnected(id) && !session.IsHeldClient(id)))
                { bindings.Rollback(id); accounts.Remove(id); }
            }
        }
    }
}
