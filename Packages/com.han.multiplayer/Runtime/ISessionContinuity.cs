// 可选会话连续性协议；核心只认识此契约，不依赖 Recovery、Demo 或大厅程序集。
using System;
using System.Threading.Tasks;

namespace Han.Multiplayer
{
    public interface ISessionContinuity
    {
        byte[] CreateClientData();
        string PrepareAdmission(ulong clientId, byte[] data);
        int HeldSeatCount { get; }
        bool IsLocalResume { get; }
        void CompleteAdmission(ulong clientId, bool approved);
        bool TryGetPreviousClientId(ulong clientId, out ulong previousClientId);
        bool IsHeldClient(ulong clientId);
        void RevokeClient(ulong clientId);
        Task BeforeIntentionalStopAsync();
    }

    /// <summary>有界扩展封包，基础游戏握手始终由核心校验，扩展不能替换或绕过它。</summary>
    public static class ConnectionDataEnvelope
    {
        public const int MaximumBytes = 512;
        public static byte[] Encode(byte[] handshake, byte[] extension)
        {
            if (handshake == null || handshake.Length == 0 || handshake.Length > MaximumBytes)
                throw new ArgumentException("Invalid base connection handshake.");
            if (extension == null || extension.Length == 0) return handshake;
            if (handshake == null || handshake.Length + extension.Length + 8 > MaximumBytes)
                throw new ArgumentException("Connection data exceeds the bounded envelope.");
            var bytes = new byte[handshake.Length + extension.Length + 8];
            bytes[0] = (byte)'H'; bytes[1] = (byte)'M'; bytes[2] = (byte)'X'; bytes[3] = 1;
            bytes[4] = (byte)handshake.Length; bytes[5] = (byte)(handshake.Length >> 8);
            bytes[6] = (byte)extension.Length; bytes[7] = (byte)(extension.Length >> 8);
            Buffer.BlockCopy(handshake, 0, bytes, 8, handshake.Length);
            Buffer.BlockCopy(extension, 0, bytes, 8 + handshake.Length, extension.Length);
            return bytes;
        }

        public static bool TryDecode(byte[] bytes, out byte[] handshake, out byte[] extension)
        {
            // 旧调用不能静默忽略账号段；含账号的新格式必须使用完整解码入口。
            return TryDecode(bytes, out handshake, out extension, out var account) && account.Length == 0;
        }

        public static byte[] Encode(byte[] handshake, byte[] extension, byte[] authentication)
        {
            if (authentication == null || authentication.Length == 0) return Encode(handshake, extension);
            extension = extension ?? Array.Empty<byte>();
            if (handshake == null || handshake.Length == 0 || handshake.Length + extension.Length + authentication.Length + 10 > MaximumBytes)
                throw new ArgumentException("Connection data exceeds the bounded envelope.");
            var bytes = new byte[handshake.Length + extension.Length + authentication.Length + 10];
            bytes[0] = (byte)'H'; bytes[1] = (byte)'M'; bytes[2] = (byte)'X'; bytes[3] = 2;
            bytes[4] = (byte)handshake.Length; bytes[5] = (byte)(handshake.Length >> 8);
            bytes[6] = (byte)extension.Length; bytes[7] = (byte)(extension.Length >> 8);
            bytes[8] = (byte)authentication.Length; bytes[9] = (byte)(authentication.Length >> 8);
            Buffer.BlockCopy(handshake, 0, bytes, 10, handshake.Length);
            Buffer.BlockCopy(extension, 0, bytes, 10 + handshake.Length, extension.Length);
            Buffer.BlockCopy(authentication, 0, bytes, 10 + handshake.Length + extension.Length, authentication.Length);
            return bytes;
        }

        public static bool TryDecode(byte[] bytes, out byte[] handshake, out byte[] extension, out byte[] authentication)
        {
            handshake = null; extension = authentication = Array.Empty<byte>();
            if (bytes == null || bytes.Length > MaximumBytes) return false;
            if (bytes.Length < 3 || bytes[0] != 'H' || bytes[1] != 'M' || bytes[2] != 'X')
            { handshake = bytes; return true; }
            if (bytes.Length < 8 || (bytes[3] != 1 && bytes[3] != 2)) return false;
            int first = bytes[4] | bytes[5] << 8, second = bytes[6] | bytes[7] << 8;
            int header = bytes[3] == 1 ? 8 : 10;
            if (bytes.Length < header) return false;
            int third = header == 10 ? bytes[8] | bytes[9] << 8 : 0;
            if (first == 0 || (header == 8 ? second == 0 : third == 0) || first + second + third + header != bytes.Length) return false;
            handshake = new byte[first]; extension = new byte[second];
            authentication = new byte[third];
            Buffer.BlockCopy(bytes, header, handshake, 0, first);
            Buffer.BlockCopy(bytes, header + first, extension, 0, second);
            Buffer.BlockCopy(bytes, header + first + second, authentication, 0, third);
            return true;
        }
    }
}
