// 弱网参数测试：覆盖特殊浮点数、边界和命令行错误，避免无效配置静默进入 Driver。
using System;
using Han.Multiplayer.Diagnostics;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    public class NetworkSimulationSettingsTests
    {
        /// <summary>拒绝百分比范围外的数值以及 NaN / Infinity。</summary>
        [TestCase(-1f), TestCase(101f), TestCase(float.NaN), TestCase(float.PositiveInfinity)]
        public void RejectsInvalidLoss(float loss)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkSimulationSettings { SendLossPercent = loss }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkSimulationSettings { ReceiveLossPercent = loss }.Validate());
        }

        /// <summary>禁止负延迟或过大缓冲延迟，避免错误参数导致无界积压。</summary>
        [TestCase(-1), TestCase(2001)]
        public void RejectsInvalidDelayAndJitter(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkSimulationSettings { SendDelayMs = value }.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => new NetworkSimulationSettings { SendJitterMs = value }.Validate());
        }

        /// <summary>100% 丢包是有意支持的断网场景，不应被普通范围校验拒绝。</summary>
        [Test]
        public void AcceptsCompleteBlackoutAndCopiesIndependently()
        {
            var settings = new NetworkSimulationSettings { SendLossPercent = 100, ReceiveLossPercent = 100 };
            settings.Validate();
            var copy = settings.Copy();
            copy.SendLossPercent = 0;
            Assert.AreEqual(100, settings.SendLossPercent);
        }
    }
}
