// 垂直运动的行为测试：跳跃、碰顶、平台边缘、终端速度和完整状态重放使用真实共享模拟器。
using System;
using Han.Multiplayer.Prediction;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Netcode;

namespace Han.Multiplayer.Tests
{
    public class VerticalMovementTests
    {
        const float Dt = 1f / 60;
        static MovementWorld World() => new MovementWorld(new MovementSettings {
            EnableVertical = true, Obstacles = new[] {
                new MovementObstacle(new Vector2(4, -4), Vector2.one, 0, .6f),
                new MovementObstacle(new Vector2(-4, 4), Vector2.one, 2.7f, 3)
            }
        });
        [Test]
        public void JumpRisesFallsAndSettlesWithoutAnotherPulse()
        {
            var world = World(); var state = world.CreateState(new Vector3(0, 1, 0));
            Assert.IsTrue(state.Grounded);
            state = world.Step(state, 0, true, Dt); Assert.Greater(state.VerticalVelocity, 0); Assert.IsFalse(state.Grounded);
            float peak = state.Position.y;
            for (int i = 0; i < 180; i++) { state = world.Step(state, 0, false, Dt); peak = Mathf.Max(peak, state.Position.y); Assert.IsTrue(world.IsValid(state)); }
            Assert.That(peak, Is.InRange(2.1f, 2.3f)); Assert.AreEqual(1, state.Position.y); Assert.AreEqual(0, state.VerticalVelocity); Assert.IsTrue(state.Grounded);
        }
        [Test]
        public void AirborneJumpDoesNotBoostOrQueueUntilLanding()
        {
            var world = World(); var state = world.Step(world.CreateState(new Vector3(0, 1, 0)), 0, true, Dt);
            var expected = world.Step(state, 0, false, Dt); var actual = world.Step(state, 0, true, Dt);
            Assert.AreEqual(expected.Position, actual.Position); Assert.AreEqual(expected.VerticalVelocity, actual.VerticalVelocity);
            for (int i = 0; i < 180; i++) actual = world.Step(actual, 0, false, Dt);
            Assert.AreEqual(1, actual.Position.y); Assert.IsTrue(actual.Grounded);
        }
        [Test]
        public void CeilingStopsAscentAndPlayerFallsBackToGround()
        {
            var world = World(); var state = world.CreateState(new Vector3(-4, 1, 4)); bool touched = false;
            for (int i = 0; i < 120; i++)
            {
                state = world.Step(state, 0, i == 0, Dt);
                Assert.LessOrEqual(state.Position.y, 1.70001f); Assert.IsTrue(world.IsValid(state));
                if (Mathf.Abs(state.Position.y - 1.7f) < .0001f) { touched = true; Assert.AreEqual(0, state.VerticalVelocity); Assert.IsFalse(state.Grounded); }
            }
            Assert.IsTrue(touched); Assert.IsTrue(state.Grounded); Assert.AreEqual(1, state.Position.y);
        }
        [Test]
        public void LandingOnPlatformAndWalkingOffRecomputesSupport()
        {
            var world = World(); var state = world.CreateState(new Vector3(4, 5, -4));
            for (int i = 0; i < 120; i++) state = world.Step(state, 0, false, Dt);
            Assert.AreEqual(1.6f, state.Position.y, .00001); Assert.IsTrue(state.Grounded);
            bool falling = false;
            for (int i = 0; i < 70; i++) { state = world.Step(state, PackedMovementInput.Encode(Vector2.right), false, Dt); falling |= !state.Grounded && state.VerticalVelocity < 0; }
            Assert.IsTrue(falling); Assert.AreEqual(1, state.Position.y); Assert.IsTrue(state.Grounded);
        }
        [Test]
        public void LowPlatformRequiresJumpAndCanBeLandedOn()
        {
            var world = World(); var state = world.CreateState(new Vector3(2.5f, 1, -4)); ushort right = PackedMovementInput.Encode(Vector2.right);
            state = world.Step(state, right, false, Dt); Assert.AreEqual(2.5f, state.Position.x, .00001);
            for (int i = 0; i < 30; i++) state = world.Step(state, right, i == 0, Dt);
            for (int i = 0; i < 80; i++) state = world.Step(state, 0, false, Dt);
            Assert.Greater(state.Position.x, 3); Assert.AreEqual(1.6f, state.Position.y, .00001); Assert.IsTrue(state.Grounded);
        }
        [Test]
        public void FallingUsesTerminalSpeedAndCannotTunnelThroughThinPlatform()
        {
            var world = World(); var state = world.CreateState(new Vector3(4, 100, -4));
            for (int i = 0; i < 90; i++) state = world.Step(state, 0, false, Dt);
            Assert.AreEqual(-30, state.VerticalVelocity);
            state = world.Step(world.CreateState(new Vector3(4, 5, -4), -30), 0, false, .5f);
            Assert.AreEqual(1.6f, state.Position.y, .00001); Assert.IsTrue(state.Grounded);
        }
        [Test]
        public void ReconciliationCorrectsVelocityEvenWithIdenticalPosition()
        {
            var world = World(); var history = new PredictionHistory(); var state = world.CreateState(new Vector3(0, 4, 0), 3);
            history.Reset(state); history.Append(0, world, Dt); history.Append(0, world, Dt);
            var corrected = world.CreateState(state.Position, -2);
            Assert.IsTrue(history.Reconcile(0, corrected, world, Dt, out float error, out int replayed));
            Assert.AreEqual(0, error); Assert.AreEqual(5, history.LastVelocityError); Assert.AreEqual(2, replayed);
            var expected = world.Step(world.Step(corrected, 0, false, Dt), 0, false, Dt);
            Assert.AreEqual(expected.Position, history.Position); Assert.AreEqual(expected.VerticalVelocity, history.State.VerticalVelocity);
        }
        [Test]
        public void UnacknowledgedJumpReplaysAndAcknowledgedJumpIsRemoved()
        {
            var world = World(); var start = world.CreateState(new Vector3(0, 1, 0)); var history = new PredictionHistory(); history.Reset(start);
            history.Append(0, world, Dt, true); history.Append(0, world, Dt); var expected = history.State;
            Assert.IsTrue(history.BuildBatch().JumpAt(0)); Assert.IsFalse(history.BuildBatch().JumpAt(1));
            Assert.IsTrue(history.Reconcile(0, start, world, Dt, out _, out _)); Assert.AreEqual(expected.Position, history.Position);
            var first = world.Step(start, 0, true, Dt);
            Assert.IsTrue(history.Reconcile(1, first, world, Dt, out _, out _)); Assert.AreEqual(expected.VerticalVelocity, history.State.VerticalVelocity);
            Assert.IsFalse(history.BuildBatch().JumpAt(0));
        }
        [Test]
        public void DuplicateJumpBatchCannotExecuteTwiceAndResetClearsPulse()
        {
            var queue = new ServerMovementQueue(); var batch = new MovementInputBatch { FirstSequence = 1, Count = 1 }; batch.SetJump(0, true);
            Assert.IsTrue(queue.Enqueue(batch, 0)); Assert.IsTrue(queue.Enqueue(batch, 1));
            Assert.IsTrue(queue.TryConsume(out _, out bool jump, out _)); Assert.IsTrue(jump);
            Assert.IsTrue(queue.Enqueue(batch, 2)); Assert.IsFalse(queue.TryConsume(out _, out _, out _));
            queue.Reset(); Assert.IsFalse(queue.TryConsume(out _, out _, out _));
            batch.SetJump(31, true); Assert.IsTrue(batch.JumpAt(31)); batch.SetJump(31, false); Assert.IsFalse(batch.JumpAt(31));
        }
        [Test]
        public void InvalidStatesAndVerticalConfigurationsAreRejected()
        {
            var world = World();
            Assert.IsFalse(world.IsValid(new MovementState(new Vector3(0, 4, 0), 0, true)));
            Assert.IsFalse(world.IsValid(new MovementState(new Vector3(0, 4, 0), float.NaN)));
            Assert.IsFalse(world.IsFree(new Vector3(0, .5f, 0)));
            Assert.Throws<ArgumentException>(() => new MovementWorld(new MovementSettings { EnableVertical = true, Gravity = 0 }));
            Assert.Throws<ArgumentException>(() => new MovementWorld(new MovementSettings { EnableVertical = true, Obstacles = new[] { new MovementObstacle(Vector2.zero, Vector2.one, 3, 2) } }));
            var settings = new MovementSettings { EnableVertical = true }; var frozen = new MovementWorld(settings); uint signature = frozen.Fingerprint(60);
            settings.Gravity = 30; Assert.AreEqual(signature, frozen.Fingerprint(60)); Assert.AreNotEqual(signature, new MovementWorld(settings).Fingerprint(60));
        }
        [Test]
        public void RandomJumpAndHorizontalReplayStaysOutsideGeometry()
        {
            var world = World(); var random = new System.Random(314); var a = world.CreateState(new Vector3(0, 1, 0)); var b = a;
            for (int i = 0; i < 6000; i++)
            {
                ushort input = PackedMovementInput.Encode(new Vector2((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1)); bool jump = i % 47 == 0;
                a = world.Step(a, input, jump, Dt); b = world.Step(b, input, jump, Dt);
                Assert.IsTrue(world.IsValid(a), "tick " + i); Assert.AreEqual(a.Position, b.Position); Assert.AreEqual(a.VerticalVelocity, b.VerticalVelocity); Assert.AreEqual(a.Grounded, b.Grounded);
            }
        }
        [Test]
        public void NetworkSerializationPreservesJumpBitmapAndFullState()
        {
            var batch = new MovementInputBatch { FirstSequence = uint.MaxValue - 1, Count = 32 };
            batch.Set(31, PackedMovementInput.Encode(Vector2.left)); batch.SetJump(0, true); batch.SetJump(31, true);
            var state = new MovementState(new Vector3(2, 4, -3), -7, false);
            using (var writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteNetworkSerializable(batch); Assert.AreEqual(73, writer.Length);
                writer.WriteNetworkSerializable(state);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadNetworkSerializable(out MovementInputBatch copy); reader.ReadNetworkSerializable(out MovementState restored);
                    Assert.AreEqual(batch.FirstSequence, copy.FirstSequence); Assert.AreEqual(32, copy.Count);
                    Assert.IsTrue(copy.JumpAt(0)); Assert.IsTrue(copy.JumpAt(31)); Assert.IsFalse(copy.JumpAt(1)); Assert.AreEqual(batch.Get(31), copy.Get(31));
                    Assert.AreEqual(state.Position, restored.Position); Assert.AreEqual(state.VerticalVelocity, restored.VerticalVelocity); Assert.IsFalse(restored.Grounded);
                    Assert.AreEqual(writer.Length, reader.Position);
                }
            }
        }
        [Test]
        public void HorizontalOnlyModeDoesNotApplyGravityOrJump()
        {
            var world = new MovementWorld(new MovementSettings()); var state = world.CreateState(new Vector3(0, 7, 0));
            for (int i = 0; i < 120; i++) state = world.Step(state, 0, true, Dt);
            Assert.AreEqual(7, state.Position.y); Assert.AreEqual(0, state.VerticalVelocity); Assert.IsFalse(state.Grounded);
        }
    }
}
