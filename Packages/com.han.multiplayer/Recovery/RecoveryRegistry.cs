// 纯服务器会话表：随机凭证、原子占位、保留期限与撤销；不使用昵称或客户端位置作为身份依据。
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Han.Multiplayer.Recovery
{
    public enum RecoveryLeaseState { PendingNew, PendingResume, Connected, Held }

    public sealed class RecoveryLease
    {
        public string SessionId { get; internal set; }
        public string AccountId { get; internal set; } // 来自服务器验证主体，不来自恢复载荷。
        internal string Secret;
        public ulong ClientId { get; internal set; }
        public ulong PreviousClientId { get; internal set; } = ulong.MaxValue;
        public RecoveryLeaseState State { get; internal set; }
        internal double Expires, PendingUntil;
        public PlayerRecoverySnapshot Snapshot { get; internal set; }
    }

    public sealed class RecoveryRegistry
    {
        readonly Dictionary<string, RecoveryLease> leases = new Dictionary<string, RecoveryLease>();
        readonly List<RecoveryLease> work = new List<RecoveryLease>();
        readonly int capacity;
        readonly double retention, pendingTimeout;
        public RecoveryRegistry(int capacity, double retentionSeconds, double pendingTimeoutSeconds = 40)
        {
            if (capacity < 1 || retentionSeconds < 1 || retentionSeconds > 300 || double.IsNaN(retentionSeconds) ||
                pendingTimeoutSeconds < 1 || double.IsNaN(pendingTimeoutSeconds) || double.IsInfinity(pendingTimeoutSeconds))
                throw new ArgumentOutOfRangeException(nameof(retentionSeconds));
            this.capacity = capacity; retention = retentionSeconds; pendingTimeout = pendingTimeoutSeconds;
        }

        /// <summary>预占在核心容量和大厅规则之前执行；之后任何规则拒绝都必须 Rollback。</summary>
        public string Prepare(ulong clientId, byte[] payload, double now, string accountId = null)
        {
            Prune(now);
            if (ForClient(clientId) != null) return "RECOVERY_ALREADY_CONNECTED";
            if (payload == null || payload.Length == 0) return "RECOVERY_REQUIRED";
            if (payload.Length > 128) return "RECOVERY_INVALID_TICKET";
            // 严格 ASCII：拒绝无效字节和宽松 UTF-8 替换，错误信息不回显任何凭证。
            foreach (byte value in payload) if (value > 127) return "RECOVERY_INVALID_TICKET";
            string text = Encoding.ASCII.GetString(payload);
            if (text == "R1|N")
            {
                if (leases.Count >= capacity) return "ROOM_FULL";
                var lease = new RecoveryLease { SessionId = RandomHex(16), Secret = RandomHex(32), ClientId = clientId, AccountId = accountId,
                    State = RecoveryLeaseState.PendingNew, PendingUntil = now + pendingTimeout };
                leases.Add(lease.SessionId, lease);
                return null;
            }
            var parts = text.Split('|');
            if (parts.Length != 4 || parts[0] != "R1" || parts[1] != "R" || !IsHex(parts[2], 32) || !IsHex(parts[3], 64)) return "RECOVERY_INVALID_TICKET";
            if (!leases.TryGetValue(parts[2], out var record)) return "RECOVERY_SESSION_EXPIRED";
            if (!SameSecret(record.Secret, parts[3])) return "RECOVERY_INVALID_TICKET";
            if (!string.Equals(record.AccountId, accountId, StringComparison.Ordinal)) return "AUTH_ACCOUNT_MISMATCH";
            if (record.State != RecoveryLeaseState.Held) return "RECOVERY_ALREADY_CONNECTED";
            record.PreviousClientId = record.ClientId;
            record.ClientId = clientId;
            record.State = RecoveryLeaseState.PendingResume;
            record.PendingUntil = Math.Min(now + pendingTimeout, record.Expires);
            return null;
        }

        public void Rollback(ulong clientId)
        {
            var record = ForClient(clientId);
            if (record == null) return;
            if (record.State == RecoveryLeaseState.PendingNew) leases.Remove(record.SessionId);
            else if (record.State == RecoveryLeaseState.PendingResume)
            { record.ClientId = record.PreviousClientId; record.State = RecoveryLeaseState.Held; }
        }

        public RecoveryLease Connect(ulong clientId, double? now = null)
        {
            if (now.HasValue) Prune(now.Value);
            var record = ForClient(clientId);
            if (record != null && record.State == RecoveryLeaseState.Connected) return record;
            if (record == null || (record.State != RecoveryLeaseState.PendingNew && record.State != RecoveryLeaseState.PendingResume)) return null;
            record.State = RecoveryLeaseState.Connected;
            return record;
        }

        public void Disconnect(ulong clientId, double now)
        {
            var record = ForClient(clientId);
            if (record == null) return;
            if (record.State == RecoveryLeaseState.PendingNew) { leases.Remove(record.SessionId); return; }
            if (record.State == RecoveryLeaseState.PendingResume) { Rollback(clientId); return; }
            if (record.State != RecoveryLeaseState.Connected) return;
            record.State = RecoveryLeaseState.Held;
            record.Expires = now + retention;
        }

        public void Revoke(ulong clientId)
        {
            var record = ForClient(clientId);
            if (record == null)
                foreach (var candidate in leases.Values)
                    if (candidate.State == RecoveryLeaseState.PendingResume && candidate.PreviousClientId == clientId) { record = candidate; break; }
            if (record != null) leases.Remove(record.SessionId);
        }
        public RecoveryLease ForClient(ulong id)
        {
            foreach (var record in leases.Values) if (record.ClientId == id) return record;
            return null;
        }
        public bool IsHeldClient(ulong id, double now)
        {
            Prune(now);
            foreach (var record in leases.Values)
                if ((record.State == RecoveryLeaseState.Held && record.ClientId == id) ||
                    (record.State == RecoveryLeaseState.PendingResume && record.PreviousClientId == id)) return true;
            return false;
        }
        public int HeldCount(double now)
        {
            Prune(now);
            int count = 0;
            foreach (var record in leases.Values) if (record.State == RecoveryLeaseState.Held) count++;
            return count;
        }
        public void Prune(double now)
        {
            work.Clear(); work.AddRange(leases.Values);
            foreach (var record in work)
            {
                if ((record.State == RecoveryLeaseState.PendingNew || record.State == RecoveryLeaseState.PendingResume) && record.PendingUntil <= now)
                    Rollback(record.ClientId);
                if (record.State == RecoveryLeaseState.Held && record.Expires <= now) leases.Remove(record.SessionId);
            }
        }
        public void Clear() { leases.Clear(); work.Clear(); }
        /// <summary>仅供服务器向该玩家私发；含敏感凭证，不得记录、广播或持久化到公开文件。</summary>
        public byte[] CreateResumeData(ulong clientId)
        {
            var record = ForClient(clientId) ?? throw new InvalidOperationException("No recovery lease.");
            return Encoding.ASCII.GetBytes("R1|R|" + record.SessionId + "|" + record.Secret);
        }
        internal static string RandomHex(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            var result = new StringBuilder(count * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2"));
            return result.ToString();
        }
        public static bool IsHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        // 长度固定后遍历全部字节，不在第一个错误字符处提前返回。
        static bool SameSecret(string first, string second)
        {
            if (first.Length != second.Length) return false;
            int difference = 0;
            for (int i = 0; i < first.Length; i++) difference |= first[i] ^ second[i];
            return difference == 0;
        }
    }
}
