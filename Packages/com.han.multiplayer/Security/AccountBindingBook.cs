// 服务器账号占位表：拒绝重复登录，允许同账号+合法恢复连接替换离线连接，失败时原子回滚。
using System;
using System.Collections.Generic;

namespace Han.Multiplayer.Security
{
    public sealed class AccountBindingBook
    {
        sealed class Binding
        {
            public ulong Client;
            public double PendingUntil;
            public bool Pending;
            public Binding Previous;
        }
        readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>(StringComparer.Ordinal);
        readonly List<string> keys = new List<string>();
        public int Count => bindings.Count;
        public string TryClaim(string account, ulong client, ulong previousClient, double now, double pendingSeconds,
            Func<ulong, bool> connected, Func<ulong, bool> held)
        {
            if (!AccountPrincipal.ValidIdentifier(account) || double.IsNaN(now) || double.IsInfinity(now) || pendingSeconds <= 0)
                return "AUTH_INVALID_CLAIMS";
            Prune(now, connected, held);
            if (bindings.TryGetValue(account, out var existing))
            {
                if (existing.Client == client) return null;
                if (existing.Pending || connected(existing.Client)) return "AUTH_ALREADY_CONNECTED";
                if (!held(existing.Client) || previousClient != existing.Client) return "AUTH_SESSION_HELD";
            }
            if (bindings.Count >= 128 && existing == null) return "AUTH_BUSY";
            bindings[account] = new Binding { Client = client, Pending = true, PendingUntil = now + pendingSeconds, Previous = existing };
            return null;
        }
        public void Confirm(ulong client)
        {
            foreach (var binding in bindings.Values)
                if (binding.Client == client) { binding.Pending = false; binding.Previous = null; return; }
        }
        public void Rollback(ulong client)
        {
            string account = null; Binding replacement = null;
            foreach (var pair in bindings)
                if (pair.Value.Client == client && pair.Value.Pending) { account = pair.Key; replacement = pair.Value.Previous; break; }
            if (account == null) return;
            if (replacement == null) bindings.Remove(account); else bindings[account] = replacement;
        }
        public bool TryGetClient(string account, out ulong client)
        {
            if (bindings.TryGetValue(account, out var binding)) { client = binding.Client; return true; }
            client = ulong.MaxValue; return false;
        }
        /// <summary>撤销旧离线 ID 时也匹配正在恢复的继任连接，防止“恢复中踢人”竞态。</summary>
        public ulong Revoke(ulong client)
        {
            string account = null; ulong current = ulong.MaxValue;
            foreach (var pair in bindings)
                if (pair.Value.Client == client || (pair.Value.Previous != null && pair.Value.Previous.Client == client))
                { account = pair.Key; current = pair.Value.Client; break; }
            if (account != null) bindings.Remove(account);
            return current;
        }
        public void Prune(double now, Func<ulong, bool> connected, Func<ulong, bool> held)
        {
            keys.Clear(); keys.AddRange(bindings.Keys);
            foreach (string key in keys)
            {
                var binding = bindings[key];
                if (binding.Pending && binding.PendingUntil <= now)
                {
                    Rollback(binding.Client);
                    if (!bindings.TryGetValue(key, out binding)) continue;
                }
                if (!binding.Pending && !connected(binding.Client) && !held(binding.Client)) bindings.Remove(key);
            }
        }
        public void Clear() { bindings.Clear(); keys.Clear(); }
    }
}
