// 客户端有界历史：从权威确认位置重算尚未确认的输入，不把客户端预测位置上传为事实。
using System;
using UnityEngine;
namespace Han.Multiplayer.Prediction
{
    public sealed class PredictionHistory
    {
        struct Entry { public uint Sequence; public ushort Input; public bool Jump; public MovementState After; }
        readonly Entry[] entries = new Entry[256];
        MovementState confirmed;
        public MovementState State { get; private set; }
        public Vector3 Position => State.Position;
        public float LastVelocityError { get; private set; }
        public bool LastGroundedMismatch { get; private set; }
        public uint LastGenerated { get; private set; }
        public uint LastAcknowledged { get; private set; }
        public int Count { get; private set; }
        public void Reset(Vector3 position, uint sequence = 0)
            => Reset(new MovementState(position), sequence);
        /// <summary>新代次同时清空跳跃和速度历史，旧指令不能跨传送或重连继续执行。</summary>
        public void Reset(MovementState state, uint sequence = 0)
        { State = confirmed = state; LastAcknowledged = LastGenerated = sequence; Count = 0; LastVelocityError = 0; LastGroundedMismatch = false; Array.Clear(entries, 0, entries.Length); }
        public bool Append(ushort input, MovementWorld world, float step, bool jump = false)
        {
            if (Count == entries.Length) return false;
            State = world.Step(State, input, jump, step); LastGenerated = unchecked(LastGenerated + 1); Count++;
            entries[LastGenerated % (uint)entries.Length] = new Entry { Sequence = LastGenerated, Input = input, Jump = jump, After = State };
            return true;
        }
        public MovementInputBatch BuildBatch()
        {
            int count = Math.Min(Count, MovementInputBatch.Capacity);
            var result = new MovementInputBatch { Count = (byte)count, FirstSequence = unchecked(LastGenerated - (uint)count + 1) };
            for (int i = 0; i < count; i++)
            { var entry = entries[unchecked(result.FirstSequence + (uint)i) % (uint)entries.Length]; result.Set(i, entry.Input); result.SetJump(i, entry.Jump); }
            return result;
        }
        public bool Reconcile(uint acknowledged, Vector3 authoritative, MovementWorld world, float step, out float error, out int replayed)
        {
            error = 0; replayed = 0;
            if (!world.IsFree(authoritative)) return false;
            return Reconcile(acknowledged, world.CreateState(authoritative), world, step, out error, out replayed);
        }
        public bool Reconcile(uint acknowledged, MovementState authoritative, MovementWorld world, float step, out float error, out int replayed)
        {
            error = 0; replayed = 0;
            int advance = unchecked((int)(acknowledged - LastAcknowledged));
            if (advance < 0 || advance > Count || !world.IsValid(authoritative)) return false;
            MovementState predictedAtAck = confirmed;
            if (advance > 0)
            {
                var entry = entries[acknowledged % (uint)entries.Length];
                if (entry.Sequence != acknowledged) return false;
                predictedAtAck = entry.After;
            }
            error = Vector3.Distance(predictedAtAck.Position, authoritative.Position);
            LastVelocityError = Mathf.Abs(predictedAtAck.VerticalVelocity - authoritative.VerticalVelocity);
            LastGroundedMismatch = predictedAtAck.Grounded != authoritative.Grounded;
            Count -= advance; LastAcknowledged = acknowledged; confirmed = State = authoritative;
            for (uint i = 1; i <= Count; i++)
            {
                uint sequence = unchecked(acknowledged + i); int index = (int)(sequence % (uint)entries.Length);
                var entry = entries[index]; if (entry.Sequence != sequence) return false;
                State = world.Step(State, entry.Input, entry.Jump, step); entry.After = State; entries[index] = entry; replayed++;
            }
            return true;
        }
        /// <summary>仅本机开发验收使用的误差注入；不发送任何客户端位置给服务器。</summary>
        public void OffsetForDevelopment(Vector3 delta)
        {
            var shifted = State; shifted.Position += delta; State = shifted; confirmed.Position += delta;
            for (uint i = 1; i <= Count; i++) { int index = (int)(unchecked(LastAcknowledged + i) % (uint)entries.Length); entries[index].After.Position += delta; }
        }
    }

    public sealed class ServerMovementQueue
    {
        struct Entry { public bool Present, Jump; public uint Sequence; public ushort Input; public double ReceivedAt; }
        readonly Entry[] entries = new Entry[128];
        public uint LastApplied { get; private set; }
        public int Count { get; private set; }
        public void Reset() { LastApplied = 0; Count = 0; Array.Clear(entries, 0, entries.Length); }
        public bool Enqueue(MovementInputBatch batch, double now)
        {
            if (batch.Count < 1 || batch.Count > MovementInputBatch.Capacity || double.IsNaN(now) || double.IsInfinity(now)) return false;
            // 先整体检查再写入，畸形尾部不能留下半个已接受的窗口。
            for (int i = 0; i < batch.Count; i++)
            {
                int ahead = unchecked((int)(batch.FirstSequence + (uint)i - LastApplied));
                if (ahead > entries.Length || !PackedMovementInput.Valid(batch.Get(i))) return false;
            }
            for (int i = 0; i < batch.Count; i++)
            {
                uint sequence = unchecked(batch.FirstSequence + (uint)i);
                if (!MovementClock.Newer(sequence, LastApplied)) continue;
                int index = (int)(sequence % (uint)entries.Length);
                if (entries[index].Present) continue;
                entries[index] = new Entry { Present = true, Sequence = sequence, Input = batch.Get(i), Jump = batch.JumpAt(i), ReceivedAt = now }; Count++;
            }
            return true;
        }
        /// <summary>调用者每个服务器 Tick 最多取一次，客户端补发不能增加权威移动速度。</summary>
        public bool TryConsume(out ushort input, out double receivedAt)
            => TryConsume(out input, out _, out receivedAt);
        public bool TryConsume(out ushort input, out bool jump, out double receivedAt)
        {
            uint next = unchecked(LastApplied + 1); int index = (int)(next % (uint)entries.Length);
            var entry = entries[index]; input = 0; jump = false; receivedAt = 0;
            if (!entry.Present || entry.Sequence != next) return false;
            entries[index].Present = false; Count--; LastApplied = next; input = entry.Input; jump = entry.Jump; receivedAt = entry.ReceivedAt; return true;
        }
    }
}
