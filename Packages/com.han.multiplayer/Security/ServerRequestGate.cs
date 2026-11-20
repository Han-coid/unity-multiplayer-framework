// 把纯请求校验连接到 NGO 的真实发送者、服务器时钟和已验证账号，不代替玩法自身的规则。
using System;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Security
{
    [DisallowMultipleComponent, RequireComponent(typeof(MultiplayerSession))]
    public sealed class ServerRequestGate : MonoBehaviour, IServerRequestValidator
    {
        readonly RequestLedger ledger = new RequestLedger();
        MultiplayerSession session;
        NetworkManager manager;
        AccountAdmissionService accounts;
        public long Allowed => ledger.Allowed;
        public long Rejected => ledger.Rejected;
        void Awake()
        {
            session = GetComponent<MultiplayerSession>(); manager = GetComponent<NetworkManager>();
            foreach (var rule in FrameworkRequestRules.All) ledger.Register(rule);
        }
        void OnEnable() { session.StateChanged += OnState; }
        void OnDisable() { if (session != null) session.StateChanged -= OnState; }
        void OnState(SessionState state)
        {
            if (state == SessionState.Starting)
            { ledger.Reset(); accounts = GetComponent<AccountAdmissionService>(); }
        }
        /// <summary>在连接前注册不可变规则；运行时不能由客户端请求增加新规则或改变预算。</summary>
        public void RegisterRule(ServerRequestRule rule)
        {
            if (manager.IsListening || manager.ShutdownInProgress) throw new InvalidOperationException("Register request rules before connecting.");
            ledger.Register(rule);
        }
        public string Validate(ServerRequestRule rule, ulong sender, ulong owner, int payloadBytes, bool validPayload, bool phaseAllowed, uint? sequence, ulong sequenceScope)
        {
            bool connected = manager.IsServer && !manager.ShutdownInProgress && manager.ConnectedClients.ContainsKey(sender);
            AccountPrincipal principal = null;
            if (session.AccountAuthenticationEnabled && accounts != null) accounts.TryGetPrincipal(sender, out principal);
            return ledger.Check(rule, sender, owner, payloadBytes, validPayload, phaseAllowed, sequence, sequenceScope,
                Time.realtimeSinceStartupAsDouble, connected, session.AccountAuthenticationEnabled, principal, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        public void ForgetScope(ulong sender, ulong sequenceScope)
        { if (manager.IsServer) ledger.ForgetScope(sender, sequenceScope); }
        void Update()
        {
            if (manager.IsServer && !manager.ShutdownInProgress) ledger.Prune(id => manager.ConnectedClients.ContainsKey(id));
        }
    }
}
