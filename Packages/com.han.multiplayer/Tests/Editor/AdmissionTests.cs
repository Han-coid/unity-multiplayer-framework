// EditMode 测试：不启动 Unity 网络，验证准入策略、参数约束和异步取消的关键边界。
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    /// <summary>用可控的连接列表和时间戳验证核心策略，避免依赖实际网络时序。</summary>
    public class AdmissionTests
    {
        // 模拟“审批已通过，但 NGO 尚未把任何玩家加入已连接列表”的状态。
        static readonly ulong[] Empty = Array.Empty<ulong>();

        /// <summary>多个请求在相同时间批准时，临时预留也必须占用名额。</summary>
        [Test]
        public void SimultaneousApprovalsCannotOverbookRoom()
        {
            var settings = new SessionSettings { MaxPlayers = 2 };
            var policy = new AdmissionPolicy(settings);
            var payload = settings.CreateHandshake();
            // 已连接列表始终为空，第三次拒绝只能来自预留计数，而不是在线人数。
            Assert.IsNull(policy.TryReserve(0, payload, Empty, 0));
            Assert.IsNull(policy.TryReserve(1, payload, Empty, 0));
            Assert.AreEqual("ROOM_FULL", policy.TryReserve(2, payload, Empty, 0));
        }

        /// <summary>预留玩家进入已连接列表后，只计算一次容量，第二名玩家仍应能加入。</summary>
        [Test]
        public void ConnectedReservationIsNotCountedTwice()
        {
            var settings = new SessionSettings { MaxPlayers = 2 };
            var policy = new AdmissionPolicy(settings);
            policy.TryReserve(0, settings.CreateHandshake(), Empty, 0);
            Assert.IsNull(policy.TryReserve(1, settings.CreateHandshake(), new ulong[] { 0 }, 1));
        }

        /// <summary>未完成连接的名额可过期回收，主动 Release 也应立即腾出容量。</summary>
        [Test]
        public void StaleReservationsExpireAndDisconnectReleasesCapacity()
        {
            var settings = new SessionSettings { MaxPlayers = 2 };
            var policy = new AdmissionPolicy(settings, 5);
            policy.TryReserve(0, settings.CreateHandshake(), Empty, 0);
            policy.TryReserve(1, settings.CreateHandshake(), Empty, 0);
            // 预留时长为 5 秒：第 4 秒仍满员，第 6 秒旧名额已失效。
            Assert.AreEqual("ROOM_FULL", policy.TryReserve(2, settings.CreateHandshake(), Empty, 4));
            Assert.IsNull(policy.TryReserve(2, settings.CreateHandshake(), Empty, 6));
            policy.TryReserve(3, settings.CreateHandshake(), Empty, 6);
            policy.Release(2);
            Assert.IsNull(policy.TryReserve(4, settings.CreateHandshake(), Empty, 7));
        }

        /// <summary>同一客户端的重复请求必须幂等，不能挤占另一名玩家的位置。</summary>
        [Test]
        public void DuplicateApprovalDoesNotConsumeAnExtraSlot()
        {
            var settings = new SessionSettings { MaxPlayers = 2 };
            var policy = new AdmissionPolicy(settings);
            policy.TryReserve(1, settings.CreateHandshake(), Empty, 0);
            policy.TryReserve(1, settings.CreateHandshake(), Empty, 0);
            Assert.IsNull(policy.TryReserve(2, settings.CreateHandshake(), Empty, 0));
        }

        /// <summary>拒绝空握手、畸形内容、错误游戏和协议版本；拒绝后合法请求仍可加入。</summary>
        [Test]
        public void RejectsOtherGamesVersionsAndMalformedPayloads()
        {
            var settings = new SessionSettings();
            var policy = new AdmissionPolicy(settings);
            Assert.AreEqual("INCOMPATIBLE_GAME", policy.TryReserve(1, null, Empty, 0));
            Assert.AreEqual("INCOMPATIBLE_GAME", policy.TryReserve(1, new byte[1024], Empty, 0));
            Assert.AreEqual("INCOMPATIBLE_GAME", policy.TryReserve(1, new SessionSettings { ApplicationId = "another-game" }.CreateHandshake(), Empty, 0));
            Assert.AreEqual("INCOMPATIBLE_GAME", policy.TryReserve(1, new SessionSettings { ProtocolVersion = 2 }.CreateHandshake(), Empty, 0));
            Assert.IsNull(policy.TryReserve(1, settings.CreateHandshake(), Empty, 0));
        }

        /// <summary>容量必须落在配置允许范围内，包括检查上下边界外的值。</summary>
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(129)]
        public void RejectsInvalidCapacity(int capacity)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings { MaxPlayers = capacity }.Validate());

        /// <summary>零、NaN 和正无穷不能成为连接期限，防止异常配置破坏异步等待。</summary>
        [TestCase(0f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void RejectsInvalidTimeout(float timeout)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new SessionSettings { ConnectionTimeoutSeconds = timeout }.Validate());

        /// <summary>启动快照不受原始配置对象后续修改影响。</summary>
        [Test]
        public void SnapshotIsNotChangedByInspectorEdits()
        {
            var original = new SessionSettings();
            var snapshot = original.Snapshot();
            original.MaxPlayers = 128;
            Assert.AreEqual(8, snapshot.MaxPlayers);
        }

        /// <summary>回环、局域网 IPv4 与 IPv6 地址都属于有效的地址字面量。</summary>
        [TestCase("127.0.0.1")]
        [TestCase("192.168.1.2")]
        [TestCase("::1")]
        public void AcceptsIpLiterals(string address) => Assert.DoesNotThrow(() => SessionSettings.ValidateEndpoint(address, 7777));

        /// <summary>地址字段不接受 URL、附带端口的字符串或无效 IP。</summary>
        [TestCase("https://example.com")]
        [TestCase("127.0.0.1:7777")]
        [TestCase("999.1.1.1")]
        public void RejectsInvalidEndpoints(string address) => Assert.Throws<ArgumentException>(() => SessionSettings.ValidateEndpoint(address, 7777));

        /// <summary>房间码统一空白和大小写，同时拒绝空值及路径分隔符等无关字符。</summary>
        [Test]
        public void NormalizesJoinCodeAndRejectsUnexpectedCharacters()
        {
            Assert.AreEqual("AB12CD", SessionSettings.NormalizeJoinCode(" ab12cd "));
            Assert.Throws<ArgumentException>(() => SessionSettings.NormalizeJoinCode(""));
            Assert.Throws<ArgumentException>(() => SessionSettings.NormalizeJoinCode("a/b"));
        }

        /// <summary>调用方取消后，后台任务再成功也不能把原等待任务变成成功状态。</summary>
        [Test]
        public async Task CancellationDoesNotDeliverALateCloudResult()
        {
            var source = new TaskCompletionSource<string>();
            using (var cancellation = new CancellationTokenSource())
            {
                var waiting = AsyncCancellation.WaitAsync(source.Task, cancellation.Token);
                cancellation.Cancel();
                try { await waiting; Assert.Fail("Should cancel."); } catch (OperationCanceledException) { }
                // 模拟取消后才返回的云请求；原任务仍完成，但等待包装器必须保持已取消。
                source.SetResult("late room");
                Assert.IsTrue(waiting.IsCanceled);
            }
        }
    }
}
