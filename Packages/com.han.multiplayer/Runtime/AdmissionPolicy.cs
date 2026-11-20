// 入房准入策略：将兼容性检查和名额预留从 Unity 网络回调中分离，便于独立验证。
using System;
using System.Collections.Generic;

namespace Han.Multiplayer
{
    /// <summary>记录已经批准但尚未完成连接的玩家，防止同一时刻的多个请求超出房间容量。</summary>
    public sealed class AdmissionPolicy
    {
        readonly byte[] handshake; // 服务器期望收到的完整握手内容。
        readonly int capacity;     // 本次会话的固定容量，不随 Inspector 的后续修改变化。
        // 键为 NGO 客户端 ID，值为预留失效的单调时间戳。
        readonly Dictionary<ulong, double> reservations = new Dictionary<ulong, double>();
        // 先收集再删除，避免遍历 Dictionary 时修改集合；复用列表减少分配。
        readonly List<ulong> expired = new List<ulong>();
        readonly double reservationSeconds;

        /// <summary>使用会话快照初始化容量、握手和临时名额保留时长。</summary>
        /// <param name="settings">本次启动已校验的配置。</param>
        /// <param name="reservationSeconds">未完成连接的名额最多保留多少秒。</param>
        public AdmissionPolicy(SessionSettings settings, double reservationSeconds = 40)
        {
            handshake = settings.CreateHandshake();
            capacity = settings.MaxPlayers;
            this.reservationSeconds = reservationSeconds;
        }

        /// <summary>检查兼容性和剩余容量，通过后为客户端预留一个位置。</summary>
        /// <param name="clientId">NGO 为此次连接分配的 ID。</param>
        /// <param name="payload">客户端发送的握手字节。</param>
        /// <param name="connected">服务器已经连接的玩家 ID 列表。</param>
        /// <param name="now">与预留期限使用同一时间基准的当前秒数。</param>
        /// <returns>null 表示允许加入；否则返回可反馈给客户端的拒绝原因。</returns>
        public string TryReserve(ulong clientId, byte[] payload, IReadOnlyList<ulong> connected, double now, int heldSeats = 0)
        {
            // 在占用名额之前检查完整握手，畸形或不兼容请求不会消耗容量。
            string compatibility = ValidateHandshake(payload);
            if (compatibility != null) return compatibility;

            // 已连接的玩家由 connected 计数，不应再通过 reservations 重复计数。
            Prune(connected, now);

            // 相同客户端的重复准入保持幂等，避免重复扣除名额。
            if (Contains(connected, clientId) || reservations.ContainsKey(clientId)) return null;
            if (heldSeats < 0 || (long)connected.Count + reservations.Count + heldSeats >= capacity) return "ROOM_FULL";
            reservations.Add(clientId, now + reservationSeconds);
            return null;
        }

        public string ValidateHandshake(byte[] payload)
        {
            if (payload == null || payload.Length != handshake.Length) return "INCOMPATIBLE_GAME";
            for (int i = 0; i < handshake.Length; i++) if (payload[i] != handshake[i]) return "INCOMPATIBLE_GAME";
            return null;
        }

        /// <summary>当前尚未完成连接的批准数，用于大厅开始游戏前避免与入房发生竞态。</summary>
        public int PendingCount(IReadOnlyList<ulong> connected, double now)
        {
            Prune(connected, now);
            return reservations.Count;
        }

        /// <summary>移除过期或已经转为正式连接的预留。</summary>
        void Prune(IReadOnlyList<ulong> connected, double now)
        {
            expired.Clear();
            foreach (var item in reservations)
                if (item.Value <= now || Contains(connected, item.Key)) expired.Add(item.Key);
            foreach (ulong id in expired) reservations.Remove(id);

        }

        /// <summary>玩家完成连接或断开时释放临时预留；已连接玩家仍由 NGO 的列表计数。</summary>
        public void Release(ulong clientId) => reservations.Remove(clientId);

        /// <summary>遍历小规模房间的只读 ID 列表，避免为了查询再构造一个集合。</summary>
        static bool Contains(IReadOnlyList<ulong> ids, ulong id)
        {
            for (int i = 0; i < ids.Count; i++) if (ids[i] == id) return true;
            return false;
        }
    }
}
