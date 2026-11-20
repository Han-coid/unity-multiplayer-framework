// 可重放的运动模型：固定步长、量化输入、箱形角色与静态 AABB，不依赖 PhysX 的运行顺序。
using System;
using Unity.Netcode;
using UnityEngine;

namespace Han.Multiplayer.Prediction
{
    [Serializable]
    public struct MovementObstacle
    {
        public Vector2 Center, HalfSize;
        public float Bottom, Top;
        public MovementObstacle(Vector2 center, Vector2 halfSize, float bottom = 0, float top = 2)
        { Center = center; HalfSize = halfSize; Bottom = bottom; Top = top; }
    }
    [Serializable]
    public sealed class MovementSettings
    {
        public float Speed = 4, HalfWidth = .5f;
        // 默认保留纯水平接入；示例显式开启垂直运动，角色位置代表箱体中心。
        public bool EnableVertical;
        public float HalfHeight = 1, FloorHeight = 0, Gravity = 20, JumpSpeed = 7, TerminalSpeed = 30;
        public Vector2 Minimum = new Vector2(-8, -8), Maximum = new Vector2(8, 8);
        public MovementObstacle[] Obstacles = Array.Empty<MovementObstacle>();
        public MovementSettings Copy() => new MovementSettings { Speed = Speed, HalfWidth = HalfWidth, Minimum = Minimum, Maximum = Maximum,
            EnableVertical = EnableVertical, HalfHeight = HalfHeight, FloorHeight = FloorHeight, Gravity = Gravity, JumpSpeed = JumpSpeed, TerminalSpeed = TerminalSpeed,
            Obstacles = Obstacles == null ? null : (MovementObstacle[])Obstacles.Clone() };
    }
    /// <summary>确认、历史与重放使用同一完整状态；不能只修正位置而保留旧的上升速度。</summary>
    [Serializable]
    public struct MovementState : INetworkSerializable
    {
        public Vector3 Position;
        public float VerticalVelocity;
        public bool Grounded;
        public MovementState(Vector3 position, float velocity = 0, bool grounded = false)
        { Position = position; VerticalVelocity = velocity; Grounded = grounded; }
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        { serializer.SerializeValue(ref Position); serializer.SerializeValue(ref VerticalVelocity); serializer.SerializeValue(ref Grounded); }
    }
    public static class MovementClock
    {
        public static double Now => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        public static bool Newer(uint value, uint previous) => unchecked((int)(value - previous)) > 0;
    }
    public static class PackedMovementInput
    {
        public static ushort Encode(Vector2 input)
        {
            if (!Finite(input.x) || !Finite(input.y)) throw new ArgumentException("Movement input must be finite.");
            input = Vector2.ClampMagnitude(input, 1);
            int x = Mathf.RoundToInt(input.x * 127), z = Mathf.RoundToInt(input.y * 127);
            return (ushort)(unchecked((byte)(sbyte)x) | unchecked((byte)(sbyte)z) << 8);
        }
        public static bool Valid(ushort input) => unchecked((sbyte)(byte)input) != -128 && unchecked((sbyte)(byte)(input >> 8)) != -128;
        public static Vector2 Decode(ushort input) => Vector2.ClampMagnitude(new Vector2(unchecked((sbyte)(byte)input), unchecked((sbyte)(byte)(input >> 8))) / 127f, 1);
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }

    public sealed class MovementWorld
    {
        readonly MovementSettings settings;
        const float Epsilon = .00001f;
        public bool VerticalEnabled => settings.EnableVertical;
        public MovementWorld(MovementSettings source)
        {
            settings = source?.Copy() ?? throw new ArgumentNullException(nameof(source));
            if (!PackedMovementInput.Finite(settings.Speed) || settings.Speed <= 0 || settings.Speed > 100 ||
                !PackedMovementInput.Finite(settings.HalfWidth) || settings.HalfWidth <= 0 || settings.HalfWidth > 10 ||
                !Finite(settings.Minimum) || !Finite(settings.Maximum) || settings.Minimum.x + settings.HalfWidth * 2 >= settings.Maximum.x ||
                settings.Minimum.y + settings.HalfWidth * 2 >= settings.Maximum.y || settings.Obstacles == null || settings.Obstacles.Length > 32)
                throw new ArgumentException("Invalid static movement world.");
            if (!PackedMovementInput.Finite(settings.HalfHeight) || settings.HalfHeight <= 0 || settings.HalfHeight > 10 ||
                !PackedMovementInput.Finite(settings.FloorHeight) || Mathf.Abs(settings.FloorHeight) > 10000 ||
                !PackedMovementInput.Finite(settings.Gravity) || settings.Gravity <= 0 || settings.Gravity > 200 ||
                !PackedMovementInput.Finite(settings.JumpSpeed) || settings.JumpSpeed <= 0 || settings.JumpSpeed > 50 ||
                !PackedMovementInput.Finite(settings.TerminalSpeed) || settings.TerminalSpeed <= 0 || settings.TerminalSpeed > 100)
                throw new ArgumentException("Invalid vertical movement settings.");
            foreach (var box in settings.Obstacles)
                if (!Finite(box.Center) || !Finite(box.HalfSize) || box.HalfSize.x <= 0 || box.HalfSize.y <= 0 ||
                    (settings.EnableVertical && (!PackedMovementInput.Finite(box.Bottom) || !PackedMovementInput.Finite(box.Top) || box.Bottom >= box.Top)))
                    throw new ArgumentException("Invalid static movement obstacle.");
        }
        static bool Finite(Vector2 value) => PackedMovementInput.Finite(value.x) && PackedMovementInput.Finite(value.y);
        public Vector3 ClampToBounds(Vector3 position)
        {
            if (!PackedMovementInput.Finite(position)) throw new ArgumentException("Position must be finite.");
            position.x = Mathf.Clamp(position.x, settings.Minimum.x + settings.HalfWidth, settings.Maximum.x - settings.HalfWidth);
            position.z = Mathf.Clamp(position.z, settings.Minimum.y + settings.HalfWidth, settings.Maximum.y - settings.HalfWidth);
            return position;
        }
        public bool IsFree(Vector3 position)
        {
            if (!PackedMovementInput.Finite(position) || (ClampToBounds(position) - position).sqrMagnitude > .00000001f) return false;
            if (settings.EnableVertical && (position.y < settings.FloorHeight + settings.HalfHeight - Epsilon || Mathf.Abs(position.y) > 100000)) return false;
            foreach (var box in settings.Obstacles)
                if (OverlapsXZ(position, box) && OverlapsY(position, box)) return false;
            return true;
        }
        bool OverlapsXZ(Vector3 p, MovementObstacle box) => Mathf.Abs(p.x - box.Center.x) < box.HalfSize.x + settings.HalfWidth - Epsilon &&
            Mathf.Abs(p.z - box.Center.y) < box.HalfSize.y + settings.HalfWidth - Epsilon;
        bool OverlapsY(Vector3 p, MovementObstacle box) => !settings.EnableVertical ||
            (p.y + settings.HalfHeight > box.Bottom + Epsilon && p.y - settings.HalfHeight < box.Top - Epsilon);
        /// <summary>地面状态由共享静态几何判定，离开平台边缘时不保留上一帧的落地标记。</summary>
        public bool HasSupport(Vector3 position)
        {
            if (!settings.EnableVertical) return false;
            float feet = position.y - settings.HalfHeight;
            if (Mathf.Abs(feet - settings.FloorHeight) <= Epsilon) return true;
            foreach (var box in settings.Obstacles)
                if (Mathf.Abs(feet - box.Top) <= Epsilon && OverlapsXZ(position, box)) return true;
            return false;
        }
        public MovementState CreateState(Vector3 position, float velocity = 0)
        {
            var state = new MovementState(position, settings.EnableVertical ? velocity : 0, settings.EnableVertical && velocity <= 0 && HasSupport(position));
            if (state.Grounded) state.VerticalVelocity = 0;
            if (!IsValid(state)) throw new ArgumentException("Invalid authoritative movement state.");
            return state;
        }
        public bool IsValid(MovementState state) => IsFree(state.Position) && PackedMovementInput.Finite(state.VerticalVelocity) &&
            state.VerticalVelocity >= -settings.TerminalSpeed && state.VerticalVelocity <= settings.JumpSpeed &&
            (!state.Grounded || (settings.EnableVertical && state.VerticalVelocity == 0 && HasSupport(state.Position))) &&
            (settings.EnableVertical || (state.VerticalVelocity == 0 && !state.Grounded));
        /// <summary>轴向扫掠阻止穿过薄墙，再沿另一轴滑动；固定顺序保证同一输入可重放。</summary>
        public Vector3 Step(Vector3 position, ushort input, float seconds)
        {
            if (!PackedMovementInput.Valid(input) || !PackedMovementInput.Finite(seconds) || seconds < 0 || seconds > 1 || !IsFree(position))
                throw new ArgumentException("Invalid movement simulation state.");
            var direction = PackedMovementInput.Decode(input);
            Vector3 target = ClampToBounds(position + new Vector3(direction.x, 0, direction.y) * (settings.Speed * seconds));
            foreach (var box in settings.Obstacles)
            {
                if (!OverlapsY(position, box)) continue;
                float x0 = box.Center.x - box.HalfSize.x - settings.HalfWidth, x1 = box.Center.x + box.HalfSize.x + settings.HalfWidth;
                if (Mathf.Abs(position.z - box.Center.y) >= box.HalfSize.y + settings.HalfWidth - .000001f) continue;
                if (position.x <= x0 && target.x > x0) target.x = Mathf.Min(target.x, x0);
                if (position.x >= x1 && target.x < x1) target.x = Mathf.Max(target.x, x1);
            }
            foreach (var box in settings.Obstacles)
            {
                if (!OverlapsY(position, box)) continue;
                float z0 = box.Center.y - box.HalfSize.y - settings.HalfWidth, z1 = box.Center.y + box.HalfSize.y + settings.HalfWidth;
                if (Mathf.Abs(target.x - box.Center.x) >= box.HalfSize.x + settings.HalfWidth - .000001f) continue;
                if (position.z <= z0 && target.z > z0) target.z = Mathf.Min(target.z, z0);
                if (position.z >= z1 && target.z < z1) target.z = Mathf.Max(target.z, z1);
            }
            return target;
        }
        /// <summary>先水平扫掠，再垂直扫掠；一次跳跃只存在于一个序号的输入中，空中指令不会缓存到落地后。</summary>
        public MovementState Step(MovementState state, ushort input, bool jump, float seconds)
        {
            if (!IsValid(state)) throw new ArgumentException("Invalid movement state.");
            Vector3 next = Step(state.Position, input, seconds);
            if (!settings.EnableVertical) return new MovementState(next);
            if (seconds == 0) return state;
            bool supported = HasSupport(state.Position) && state.VerticalVelocity <= 0;
            float velocity = supported ? 0 : state.VerticalVelocity;
            if (jump && supported) velocity = settings.JumpSpeed;
            velocity = Mathf.Max(-settings.TerminalSpeed, velocity - settings.Gravity * seconds);
            float targetY = next.y + velocity * seconds;
            if (velocity <= 0)
            {
                float floor = settings.FloorHeight + settings.HalfHeight;
                foreach (var box in settings.Obstacles)
                    if (OverlapsXZ(next, box) && state.Position.y >= box.Top + settings.HalfHeight - Epsilon && targetY <= box.Top + settings.HalfHeight)
                        floor = Mathf.Max(floor, box.Top + settings.HalfHeight);
                if (targetY <= floor) { targetY = floor; velocity = 0; }
            }
            else
            {
                foreach (var box in settings.Obstacles)
                {
                    float ceiling = box.Bottom - settings.HalfHeight;
                    if (OverlapsXZ(next, box) && state.Position.y <= ceiling + Epsilon && targetY >= ceiling)
                    { targetY = Mathf.Min(targetY, ceiling); velocity = 0; }
                }
            }
            next.y = targetY;
            return new MovementState(next, velocity, velocity == 0 && HasSupport(next));
        }
        /// <summary>包含 Tick 和完整静态几何的协议指纹；配置不一致时停用移动，不能假装预测正确。</summary>
        public uint Fingerprint(uint tickRate)
        {
            uint hash = 2166136261;
            void Add(uint value) { hash = unchecked((hash ^ value) * 16777619); }
            void Float(float value) => Add(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
            Add(2); Add(tickRate); Float(settings.Speed); Float(settings.HalfWidth);
            Add(settings.EnableVertical ? 1u : 0u); Float(settings.HalfHeight); Float(settings.FloorHeight);
            Float(settings.Gravity); Float(settings.JumpSpeed); Float(settings.TerminalSpeed);
            Float(settings.Minimum.x); Float(settings.Minimum.y); Float(settings.Maximum.x); Float(settings.Maximum.y); Add((uint)settings.Obstacles.Length);
            foreach (var box in settings.Obstacles) { Float(box.Center.x); Float(box.Center.y); Float(box.HalfSize.x); Float(box.HalfSize.y); Float(box.Bottom); Float(box.Top); }
            return hash == 0 ? 1u : hash; // 0 留给旧移动协议。
        }
    }
}
