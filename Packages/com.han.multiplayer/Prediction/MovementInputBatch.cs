// 固定尺寸的输入窗口：32 个 X/Z 指令与 32 位跳跃脉冲，不从网络分配客户端指定大小的数组。
using System;
using Unity.Netcode;
namespace Han.Multiplayer.Prediction
{
    public struct MovementInputBatch : INetworkSerializable
    {
        public const int Capacity = 32;
        public uint FirstSequence;
        public byte Count;
        uint jumps;
        /// <summary>跳跃脉冲随同一输入序号重传，去重后最多执行一次。</summary>
        public bool JumpAt(int index)
        { if (index < 0 || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index)); return (jumps & (1u << index)) != 0; }
        public void SetJump(int index, bool pressed)
        { if (index < 0 || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index)); if (pressed) jumps |= 1u << index; else jumps &= ~(1u << index); }
        ulong a, b, c, d, e, f, g, h;
        public ushort Get(int index)
        {
            if (index < 0 || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index));
            ulong word;
            switch (index >> 2) { case 0: word = a; break; case 1: word = b; break; case 2: word = c; break; case 3: word = d; break;
                case 4: word = e; break; case 5: word = f; break; case 6: word = g; break; default: word = h; break; }
            return (ushort)(word >> ((index & 3) * 16));
        }
        public void Set(int index, ushort input)
        {
            if (index < 0 || index >= Capacity) throw new ArgumentOutOfRangeException(nameof(index));
            int shift = (index & 3) * 16;
            ulong Put(ulong word) => (word & ~(65535UL << shift)) | (ulong)input << shift;
            switch (index >> 2) { case 0: a = Put(a); break; case 1: b = Put(b); break; case 2: c = Put(c); break; case 3: d = Put(d); break;
                case 4: e = Put(e); break; case 5: f = Put(f); break; case 6: g = Put(g); break; default: h = Put(h); break; }
        }
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref FirstSequence); serializer.SerializeValue(ref Count);
            serializer.SerializeValue(ref jumps);
            serializer.SerializeValue(ref a); serializer.SerializeValue(ref b); serializer.SerializeValue(ref c); serializer.SerializeValue(ref d);
            serializer.SerializeValue(ref e); serializer.SerializeValue(ref f); serializer.SerializeValue(ref g); serializer.SerializeValue(ref h);
        }
    }
}
