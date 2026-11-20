// 有界令牌桶与序号检查；无 Unity 依赖，时间来自服务器单调时钟，不使用客户端时间戳。
using System;
using System.Collections.Generic;

namespace Han.Multiplayer.Security
{
    public sealed class RequestLedger
    {
        sealed class Bucket
        {
            public double Tokens, At;
            public bool HasSequence;
            public uint Sequence;
        }
        sealed class Client
        {
            public Bucket Global;
            public readonly Dictionary<(string, ulong), Bucket> Operations = new Dictionary<(string, ulong), Bucket>();
        }
        readonly Dictionary<string, ServerRequestRule> rules = new Dictionary<string, ServerRequestRule>(StringComparer.Ordinal);
        readonly Dictionary<ulong, Client> clients = new Dictionary<ulong, Client>();
        readonly List<ulong> work = new List<ulong>();
        readonly double globalRate;
        readonly int globalBurst;
        public long Allowed { get; private set; }
        public long Rejected { get; private set; }
        public int ClientCount => clients.Count;
        public RequestLedger(double totalRate = 240, int totalBurst = 240)
        {
            if (double.IsNaN(totalRate) || double.IsInfinity(totalRate) || totalRate <= 0 || totalRate > 10000 || totalBurst < 1 || totalBurst > 10000)
                throw new ArgumentException("Invalid aggregate request budget.");
            globalRate = totalRate; globalBurst = totalBurst;
        }
        public void Register(ServerRequestRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));
            if (rules.TryGetValue(rule.Name, out var existing))
            { if (!ReferenceEquals(existing, rule)) throw new InvalidOperationException("A different policy already uses this request name."); return; }
            if (rules.Count >= 64) throw new InvalidOperationException("Request rule catalogue is full.");
            rules.Add(rule.Name, rule);
        }
        public string Check(ServerRequestRule rule, ulong sender, ulong owner, int bytes, bool validPayload, bool phaseAllowed,
            uint? sequence, ulong scope, double now, bool connected, bool authenticationEnabled, AccountPrincipal principal, long utcNow)
        {
            string error = CheckCore(rule, sender, owner, bytes, validPayload, phaseAllowed, sequence, scope, now, connected, authenticationEnabled, principal, utcNow);
            if (error == null) Allowed++; else Rejected++;
            return error;
        }
        string CheckCore(ServerRequestRule rule, ulong sender, ulong owner, int bytes, bool validPayload, bool phaseAllowed,
            uint? sequence, ulong scope, double now, bool connected, bool authenticationEnabled, AccountPrincipal principal, long utcNow)
        {
            if (!connected) return "REQUEST_NOT_CONNECTED";
            if (rule == null || !rules.TryGetValue(rule.Name, out var registered) || !ReferenceEquals(registered, rule)) return "REQUEST_UNKNOWN_RULE";
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) return "REQUEST_INVALID";
            if (!clients.TryGetValue(sender, out var client))
            {
                if (clients.Count >= 128) return "REQUEST_BUSY";
                client = new Client { Global = new Bucket { Tokens = globalBurst, At = now } }; clients.Add(sender, client);
            }
            // 无效/越权请求同样消耗预算，避免通过构造错误参数绕过限流。
            if (!Spend(client.Global, globalRate, globalBurst, now)) return "RATE_LIMITED";
            var key = (rule.Name, rule.RequireSequence ? scope : 0);
            if (!client.Operations.TryGetValue(key, out var bucket))
            {
                if (client.Operations.Count >= 128) return "REQUEST_BUSY";
                bucket = new Bucket { Tokens = rule.Burst, At = now }; client.Operations.Add(key, bucket);
            }
            if (!Spend(bucket, rule.RatePerSecond, rule.Burst, now)) return "RATE_LIMITED";
            if (authenticationEnabled || rule.RequireAccount)
            {
                if (principal == null) return "AUTH_REQUIRED";
                if (principal.IsExpired(utcNow)) return "AUTH_TOKEN_EXPIRED";
                if (!principal.HasPermission(rule.Permission)) return "REQUEST_FORBIDDEN";
            }
            if (bytes < 0 || bytes > rule.MaximumPayloadBytes || !validPayload) return "REQUEST_INVALID";
            if (rule.RequireOwnership && sender != owner) return "REQUEST_NOT_OWNER";
            if (!phaseAllowed) return "REQUEST_PHASE";
            if (rule.RequireSequence)
            {
                if (!sequence.HasValue || (bucket.HasSequence && unchecked((int)(sequence.Value - bucket.Sequence)) <= 0)) return "REQUEST_REPLAY";
                bucket.HasSequence = true; bucket.Sequence = sequence.Value;
            }
            return null;
        }
        static bool Spend(Bucket bucket, double rate, int burst, double now)
        {
            if (now < bucket.At) return false;
            bucket.Tokens = Math.Min(burst, bucket.Tokens + (now - bucket.At) * rate); bucket.At = now;
            if (bucket.Tokens < 1) return false;
            bucket.Tokens -= 1; return true;
        }
        public void Prune(Func<ulong, bool> connected)
        {
            work.Clear(); foreach (ulong id in clients.Keys) if (!connected(id)) work.Add(id);
            foreach (ulong id in work) clients.Remove(id);
        }
        public void ForgetScope(ulong sender, ulong scope)
        {
            if (!clients.TryGetValue(sender, out var client)) return;
            var removed = new List<(string, ulong)>();
            foreach (var key in client.Operations.Keys)
                if (key.Item2 == scope && rules[key.Item1].RequireSequence) removed.Add(key);
            foreach (var key in removed) client.Operations.Remove(key);
        }
        public void Reset() { clients.Clear(); work.Clear(); Allowed = Rejected = 0; }
    }
}
