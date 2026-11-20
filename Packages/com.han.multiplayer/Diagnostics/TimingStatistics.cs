// 有界计时统计：所有耗时使用本进程单调时钟，跨设备不比较绝对时间戳。
using System;
using System.Diagnostics;

namespace Han.Multiplayer.Diagnostics
{
    public static class MonotonicClock
    {
        public static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
    }

    /// <summary>保留最近样本的摘要；0 毫秒也可能是有效测量，available 单独表示是否有数据。</summary>
    [Serializable]
    public sealed class TimingSummary
    {
        public bool available;
        public int count;
        public double latestMs, meanMs, p95Ms, maximumMs, ageMs;
    }

    /// <summary>固定容量环形缓冲，拒绝负数/NaN/Infinity，避免异常测量污染分位数。</summary>
    public sealed class TimingSeries
    {
        readonly double[] values;
        int next, count;
        double latestAt;
        public TimingSeries(int capacity = 120)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            values = new double[capacity];
        }
        public bool Add(double milliseconds, double now)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0 ||
                double.IsNaN(now) || double.IsInfinity(now)) return false;
            values[next] = milliseconds;
            next = (next + 1) % values.Length;
            count = Math.Min(count + 1, values.Length);
            latestAt = now;
            return true;
        }
        public void Clear() { next = count = 0; latestAt = 0; }
        public double[] Values()
        {
            var copy = new double[count];
            for (int i = 0; i < count; i++) copy[i] = values[(next - count + values.Length + i) % values.Length];
            return copy;
        }
        public TimingSummary Summary(double now)
        {
            var result = new TimingSummary { available = count > 0, count = count };
            if (count == 0) return result;
            var sorted = Values();
            result.latestMs = sorted[count - 1];
            foreach (double value in sorted) result.meanMs += value;
            result.meanMs /= count;
            Array.Sort(sorted);
            result.p95Ms = sorted[Math.Max(0, (int)Math.Ceiling(count * 0.95) - 1)];
            result.maximumMs = sorted[count - 1];
            result.ageMs = Math.Max(0, now - latestAt) * 1000;
            return result;
        }
    }

    /// <summary>按真实时间统计事件频率，不把目标频率直接当作实际频率。</summary>
    public sealed class EventRate
    {
        double start = MonotonicClock.Now;
        int count;
        double rate;
        public void Record(double now) { Refresh(now); count++; }
        public double Read(double now) { Refresh(now); return rate; }
        void Refresh(double now)
        {
            double duration = now - start;
            if (duration < 1) return;
            rate = count / duration;
            start = now; count = 0;
        }
        public void Clear() { start = MonotonicClock.Now; count = 0; rate = 0; }
    }
}
