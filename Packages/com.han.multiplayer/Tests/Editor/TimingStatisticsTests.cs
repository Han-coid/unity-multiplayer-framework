// 验证有界统计和命令序号，防止弱网迟到回执污染诊断结果。
using System;
using Han.Multiplayer.Diagnostics;
using NUnit.Framework;

namespace Han.Multiplayer.Tests
{
    public class TimingStatisticsTests
    {
        [Test]
        public void SummaryUsesNearestRankAndEvictsOldest()
        {
            var series = new TimingSeries(20);
            for (int i = 0; i <= 20; i++) Assert.IsTrue(series.Add(i, 100 + i));
            CollectionAssert.AreEqual(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }, series.Values());
            var result = series.Summary(121);
            Assert.AreEqual(10.5, result.meanMs);
            Assert.AreEqual(19, result.p95Ms);
            Assert.AreEqual(20, result.latestMs);
            Assert.AreEqual(1000, result.ageMs);
            series.Clear();
            Assert.IsFalse(series.Summary(122).available);
        }

        [Test]
        public void InvalidSamplesCannotPoisonPercentiles()
        {
            var series = new TimingSeries();
            Assert.IsFalse(series.Add(double.NaN, 1));
            Assert.IsFalse(series.Add(double.PositiveInfinity, 1));
            Assert.IsFalse(series.Add(-1, 1));
            Assert.IsFalse(series.Add(1, double.NaN));
            Assert.IsTrue(series.Add(0, 1));
            Assert.AreEqual(1, series.Summary(1).count);
            Assert.Throws<ArgumentOutOfRangeException>(() => new TimingSeries(0));
        }

        [Test]
        public void SequenceHandlesWrapDuplicateAndReordering()
        {
            Assert.IsTrue(NetworkResponseMetrics.IsNewerSequence(0, uint.MaxValue));
            Assert.IsFalse(NetworkResponseMetrics.IsNewerSequence(uint.MaxValue, 0));
            Assert.IsFalse(NetworkResponseMetrics.IsNewerSequence(10, 10));
            Assert.IsFalse(NetworkResponseMetrics.IsNewerSequence(9, 10));
            Assert.IsTrue(NetworkResponseMetrics.IsNewerSequence(11, 10));
        }
    }
}
