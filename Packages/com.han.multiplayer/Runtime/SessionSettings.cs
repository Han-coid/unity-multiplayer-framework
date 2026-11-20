// 会话配置与输入校验：集中定义框架允许的连接参数和兼容性握手格式。
using System;
using System.Net;
using System.Text;
using UnityEngine;

namespace Han.Multiplayer
{
    /// <summary>本地会话的生命周期状态，供 UI 展示和业务逻辑订阅。</summary>
    public enum SessionState
    {
        Offline,    // 没有运行中的会话，可以发起连接。
        Starting,   // 正在准备配置或等待云服务，尚未完成网络连接。
        Connecting, // Transport 已进入启动流程，等待本地连接成功。
        Host,       // 同时运行服务器和本地玩家。
        Client,     // 作为玩家连接到其他服务器。
        Server,     // 只运行服务器，不创建本地玩家。
        Stopping,   // 正在取消启动或等待网络关闭。
        Faulted     // 启动失败或意外断线，错误原因保存在 LastError。
    }

    /// <summary>选择数据传输路径；两种路径共用 NGO 的玩家和状态同步逻辑。</summary>
    public enum ConnectionMode
    {
        Direct, // 按 IP 直接访问局域网主机或公网服务器。
        Relay   // 通过 Relay 服务分配的中继连接通信。
    }

    /// <summary>Inspector 可编辑的配置；每次启动时复制快照，避免运行中的配置被意外改变。</summary>
    [Serializable]
    public sealed class SessionSettings
    {
        /// <summary>区分不同游戏的标识，只做兼容检查，不能代替账号认证。</summary>
        [Tooltip("Give each game its own ID. This is a compatibility check, not authentication.")]
        public string ApplicationId = "my-game";
        /// <summary>不兼容的网络规则变更时递增；只有相同版本的客户端才能通过准入。</summary>
        public uint ProtocolVersion = 1;
        /// <summary>房间玩家上限；包含 Host 玩家，但不包含纯 Server 进程。</summary>
        [Range(2, 128)] public int MaxPlayers = 8;
        /// <summary>一次启动的总等待期限，包含 Relay 请求和 NGO 建连，单位为秒。</summary>
        [Range(2, 120)] public float ConnectionTimeoutSeconds = 30;
        /// <summary>NGO 每秒网络 Tick 数，不等同于渲染帧率或 FixedUpdate 频率。</summary>
        [Range(10, 120)] public uint TickRate = 30;

        /// <summary>校验后创建本次连接专用的配置副本。</summary>
        public SessionSettings Snapshot()
        {
            Validate();
            // 当前成员都是值类型或不可变字符串，浅拷贝即可隔离后续字段赋值。
            return (SessionSettings)MemberwiseClone();
        }

        /// <summary>启动前拒绝非法配置；Range 属性只影响 Inspector，运行时代码仍需校验。</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ApplicationId) || ApplicationId.Length > 64)
                throw new ArgumentException("ApplicationId must contain 1–64 letters, digits, dots, underscores or hyphens.");
            foreach (char c in ApplicationId)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '.' && c != '_' && c != '-')
                    throw new ArgumentException("ApplicationId contains an unsupported character.");
            if (MaxPlayers < 2 || MaxPlayers > 128) throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
            if (float.IsNaN(ConnectionTimeoutSeconds) || ConnectionTimeoutSeconds < 2 || ConnectionTimeoutSeconds > 120)
                throw new ArgumentOutOfRangeException(nameof(ConnectionTimeoutSeconds));
            if (TickRate < 10 || TickRate > 120) throw new ArgumentOutOfRangeException(nameof(TickRate));
        }

        /// <summary>生成客户端提交给服务器的游戏 ID 和协议版本字节序列。</summary>
        /// <returns>UTF-8 编码的 HMP1 格式握手；不包含密码或身份凭证。</returns>
        public byte[] CreateHandshake()
        {
            Validate();
            // 固定区域格式避免不同系统语言生成不同的数字字符串。
            return Encoding.UTF8.GetBytes("HMP1|" + ApplicationId + "|" + ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>校验直连地址和端口；此入口只接受 IP，不负责 DNS 域名解析。</summary>
        /// <param name="address">不附带端口号的 IPv4 或 IPv6 地址。</param>
        /// <param name="port">UDP 端口；ushort 已保证上限，额外拒绝端口 0。</param>
        public static void ValidateEndpoint(string address, ushort port)
        {
            if (!IPAddress.TryParse(address, out var ip) ||
                (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
                 ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6))
                throw new ArgumentException("Enter an IPv4 or IPv6 address (without a port). DNS names are not resolved by this API.", nameof(address));
            if (port == 0) throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        /// <summary>移除房间码两端空白并统一为大写，再拒绝空值或非法字符。</summary>
        /// <param name="code">玩家输入的 Relay 房间码。</param>
        /// <returns>适合提交给 Relay 接口的规范化房间码。</returns>
        public static string NormalizeJoinCode(string code)
        {
            code = code?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || code.Length > 32)
                throw new ArgumentException("Enter a valid Relay join code.", nameof(code));
            foreach (char c in code)
                if (!(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9'))
                    throw new ArgumentException("Relay join codes use letters and digits only.", nameof(code));
            return code;
        }
    }
}
