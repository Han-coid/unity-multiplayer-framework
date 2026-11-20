// 验证预测/服务器共享的真实算法：碰撞、确认回放、环形窗口、重复/越界输入和静态配置隔离。
using System;
using Han.Multiplayer.Prediction;
using NUnit.Framework;
using UnityEngine;
namespace Han.Multiplayer.Tests
{
    public class MovementPredictionTests
    {
        static MovementWorld World(bool wall = false) => new MovementWorld(new MovementSettings { Obstacles = wall ?
            new[] { new MovementObstacle(Vector2.zero, new Vector2(.5f, 2)) } : Array.Empty<MovementObstacle>() });
        [Test]
        public void PackedInputsAreBoundedAndFinite()
        {
            var packed = PackedMovementInput.Encode(new Vector2(1, 1));
            Assert.IsTrue(PackedMovementInput.Valid(packed)); Assert.LessOrEqual(PackedMovementInput.Decode(packed).magnitude, 1.000001f);
            Assert.IsFalse(PackedMovementInput.Valid(128));
            Assert.Throws<ArgumentException>(() => PackedMovementInput.Encode(new Vector2(float.NaN, 0)));
        }
        [Test]
        public void SweepsDoNotTunnelAndSlideAlongStaticWall()
        {
            var world = World(true);
            var position = world.Step(new Vector3(3, 1, 0), PackedMovementInput.Encode(Vector2.left), 1);
            Assert.AreEqual(1, position.x, .00001); Assert.IsTrue(world.IsFree(position));
            position = world.Step(position, PackedMovementInput.Encode(new Vector2(-1, 1)), .1f);
            Assert.AreEqual(1, position.x, .00001); Assert.Greater(position.z, 0); Assert.IsTrue(world.IsFree(position));
        }
        [Test]
        public void SharedMotorRepeatsExactlyAndClampsWorldBounds()
        {
            var first = World(true); var second = World(true); Vector3 a = new Vector3(3, 1, 0), b = a;
            var random = new System.Random(1234);
            for (int i = 0; i < 2000; i++)
            {
                var input = PackedMovementInput.Encode(new Vector2((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1));
                a = first.Step(a, input, 1f / 60); b = second.Step(b, input, 1f / 60);
                Assert.AreEqual(a, b); Assert.IsTrue(first.IsFree(a));
            }
            var edge = World().Step(new Vector3(7.4f, 1, 7.4f), PackedMovementInput.Encode(Vector2.one), 1);
            Assert.LessOrEqual(edge.x, 7.5f); Assert.LessOrEqual(edge.z, 7.5f);
        }
        [Test]
        public void ReconciliationReplaysOnlyUnconfirmedInputsFromAuthority()
        {
            var world = World(); var history = new PredictionHistory(); history.Reset(new Vector3(0, 1, -4));
            ushort input = PackedMovementInput.Encode(Vector2.right);
            for (int i = 0; i < 10; i++) Assert.IsTrue(history.Append(input, world, 1f / 60));
            Vector3 authority = new Vector3(.1f, 1, -4), expected = authority;
            for (int i = 0; i < 5; i++) expected = world.Step(expected, input, 1f / 60);
            Assert.IsTrue(history.Reconcile(5, authority, world, 1f / 60, out float error, out int replay));
            Assert.AreEqual(5, replay); Assert.AreEqual(5, history.Count); Assert.Greater(error, .2f);
            Assert.AreEqual(expected, history.Position);
            Assert.IsFalse(history.Reconcile(4, authority, world, 1f / 60, out _, out _));
            Assert.IsFalse(history.Reconcile(11, authority, world, 1f / 60, out _, out _));
        }
        [Test]
        public void InputHistoryIsBoundedAndSurvivesSequenceWrap()
        {
            var world = World(); var history = new PredictionHistory(); history.Reset(new Vector3(0, 1, -4), uint.MaxValue - 1);
            history.Append(0, world, 1f / 60); history.Append(0, world, 1f / 60);
            Assert.AreEqual(0, history.LastGenerated);
            Assert.IsTrue(history.Reconcile(0, history.Position, world, 1f / 60, out _, out _));
            for (int i = 0; i < 256; i++) Assert.IsTrue(history.Append(0, world, 1f / 60));
            Assert.IsFalse(history.Append(0, world, 1f / 60));
            Assert.AreEqual(32, history.BuildBatch().Count);
        }
        [Test]
        public void ServerOverlappingBatchesAreIdempotentAndConsumedInOrder()
        {
            var queue = new ServerMovementQueue(); var batch = new MovementInputBatch { FirstSequence = 1, Count = 3 };
            batch.Set(0, 1); batch.Set(1, 2); batch.Set(2, 3);
            Assert.IsTrue(queue.Enqueue(batch, 1)); Assert.IsTrue(queue.Enqueue(batch, 2)); Assert.AreEqual(3, queue.Count);
            Assert.IsTrue(queue.TryConsume(out ushort first, out double at)); Assert.AreEqual(1, first); Assert.AreEqual(1, at);
            Assert.AreEqual(1, queue.LastApplied); Assert.AreEqual(2, queue.Count);
            Assert.IsTrue(queue.TryConsume(out ushort second, out _)); Assert.AreEqual(2, second);
            Assert.IsTrue(queue.TryConsume(out ushort third, out _)); Assert.AreEqual(3, third);
            Assert.IsFalse(queue.TryConsume(out _, out _));
        }
        [Test]
        public void GapsAndFarFutureInputsCannotAdvanceServerTime()
        {
            var queue = new ServerMovementQueue(); var future = new MovementInputBatch { FirstSequence = 2, Count = 1 };
            Assert.IsTrue(queue.Enqueue(future, 0)); Assert.IsFalse(queue.TryConsume(out _, out _)); Assert.AreEqual(0, queue.LastApplied);
            var oversized = new MovementInputBatch { FirstSequence = 128, Count = 2 };
            Assert.IsFalse(queue.Enqueue(oversized, 0)); Assert.AreEqual(1, queue.Count);
            var invalid = new MovementInputBatch { FirstSequence = 1, Count = 2 }; invalid.Set(1, 128);
            Assert.IsFalse(queue.Enqueue(invalid, 0)); Assert.AreEqual(1, queue.Count);
        }
        [Test]
        public void FingerprintIncludesSettingsAndUsesFrozenCopy()
        {
            var settings = new MovementSettings(); var world = new MovementWorld(settings); uint fingerprint = world.Fingerprint(60);
            settings.Speed = 30; Assert.AreEqual(fingerprint, world.Fingerprint(60));
            Assert.AreNotEqual(fingerprint, world.Fingerprint(30)); Assert.AreNotEqual(fingerprint, new MovementWorld(settings).Fingerprint(60));
            Assert.Throws<ArgumentException>(() => new MovementWorld(new MovementSettings { Speed = float.PositiveInfinity }));
        }
    }
}
