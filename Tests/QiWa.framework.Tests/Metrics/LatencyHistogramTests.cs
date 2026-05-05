// 意图：为 LatencyHistogram 生成测试用例，覆盖全部 34 个桶及所有公共方法，力求 100% 代码覆盖率。
#pragma warning disable CS1591
namespace Tests.Metrics;

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using QiWa.Common;
using QiWa.Metrics;
using Xunit;

public class LatencyHistogramTests
{
    // 将目标微秒数转换为对应的 startTimestamp（即当前时间戳减去目标微秒）
    private static long TimestampUsAgo(long targetUs)
        => Stopwatch.GetTimestamp() - (long)Math.Ceiling(targetUs * (double)Stopwatch.Frequency / 1_000_000.0);

    // 将 LatencyHistogram 序列化为 Prometheus 文本
    private static string ToPrometheusString(ref LatencyHistogram h)
    {
        var buf = new RentedBuffer(8192);
        h.ToPrometheusText(ref buf);
        var s = Encoding.UTF8.GetString(buf.AsSpan());
        buf.Dispose();
        return s;
    }

    // 通过反射调用私有静态方法 FindBucket，避免计时抖动影响桶边界测试
    private static int CallFindBucket(long latencyUs)
    {
        var method = typeof(LatencyHistogram).GetMethod(
            "FindBucket",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (int)method!.Invoke(null, new object[] { latencyUs })!;
    }

    // ---- Boundaries 只读边界表测试 ----

    [Fact]
    public void Boundaries_LengthIsBucketCountMinus1()
        => Assert.Equal(LatencyHistogram.BucketCount - 1, LatencyHistogram.Boundaries.Length);

    [Fact]
    public void Boundaries_FirstElementIs150()
        => Assert.Equal(150L, LatencyHistogram.Boundaries[0]);

    [Fact]
    public void Boundaries_IsStrictlyIncreasing()
    {
        for (int i = 1; i < LatencyHistogram.Boundaries.Length; i++)
            Assert.True(
                LatencyHistogram.Boundaries[i] > LatencyHistogram.Boundaries[i - 1],
                $"Boundaries[{i}]={LatencyHistogram.Boundaries[i]} 应大于 Boundaries[{i - 1}]={LatencyHistogram.Boundaries[i - 1]}");
    }

    [Fact]
    public void Boundaries_LastElementIsAbout64Seconds()
    {
        // floor(100 * 1.5^33) ≈ 64,715,982µs
        long last = LatencyHistogram.Boundaries[LatencyHistogram.Boundaries.Length - 1];
        Assert.InRange(last, 60_000_000L, 70_000_000L);
    }

    // ---- FindBucket 内部方法测试（通过反射，无计时干扰，覆盖全部 34 个桶） ----

    public static TheoryData<long, int> FindBucketData()
    {
        var data = new TheoryData<long, int>();
        ReadOnlySpan<long> bounds = LatencyHistogram.Boundaries;

        // 桶 0：latency ≤ 0
        data.Add(0L, 0);
        data.Add(-1L, 0);

        // 桶 0：0 < latency < Boundaries[0]=150
        data.Add(1L, 0);
        data.Add(bounds[0] - 1, 0);  // 149µs

        // 桶 1-32：测试每个桶的下界+1 和上界-1
        // 注意：bounds[k-1] = floor(100 * 1.5^k)，因截断可能略小于真实下界，
        // 所以用 bounds[k-1]+1 确保落在桶 k 内（ceil(100*1.5^k) > 真实下界）。
        for (int k = 1; k <= 32; k++)
        {
            data.Add(bounds[k - 1] + 1, k);  // 桶 k 下界（+1 跨过截断误差）
            data.Add(bounds[k] - 1, k);       // 桶 k 上界 -1
        }

        // 桶 33：>= Boundaries[32]（同样用 +1 跨过截断误差）
        data.Add(bounds[32] + 1, 33);
        data.Add(bounds[32] + 1_000_000L, 33);
        data.Add(long.MaxValue / 2, 33);     // 极大值

        return data;
    }

    [Theory]
    [MemberData(nameof(FindBucketData))]
    public void FindBucket_ReturnsCorrectBucket(long latencyUs, int expectedBucket)
    {
        int actual = CallFindBucket(latencyUs);
        Assert.Equal(expectedBucket, actual);
    }

    // ---- ReportLatency 基础测试 ----

    [Fact]
    public void ReportLatency_IncreasesRequestCount()
    {
        var h = new LatencyHistogram();
        h.ReportLatency(TimestampUsAgo(500));
        Assert.Equal(1UL, h.RequestCount);
    }

    [Fact]
    public void ReportLatency_IncreasesLatencyUsTotal()
    {
        var h = new LatencyHistogram();
        h.ReportLatency(TimestampUsAgo(1000));
        Assert.True(h.LatencyUsTotal > 0, "LatencyUsTotal 在有请求后应大于 0");
    }

    [Fact]
    public void ReportLatency_MultipleCallsAccumulate()
    {
        var h = new LatencyHistogram();
        h.ReportLatency(TimestampUsAgo(1000));
        h.ReportLatency(TimestampUsAgo(2000));
        Assert.Equal(2UL, h.RequestCount);
        Assert.True(h.LatencyUsTotal >= 3000UL, "两次上报总延迟应 >= 3000µs");
    }

    [Fact]
    public void ReportLatency_NegativeElapsed_ClampsToZero()
    {
        // 未来时间戳使 elapsed < 0，应被截断为 0
        var h = new LatencyHistogram();
        long futureTs = Stopwatch.GetTimestamp() + (long)(10.0 * Stopwatch.Frequency);
        h.ReportLatency(futureTs);
        Assert.Equal(1UL, h.RequestCount);
        Assert.Equal(0UL, h.LatencyUsTotal);
    }

    // ---- 通过实际计时验证 ReportLatency 落入正确的桶（覆盖全部 34 个桶） ----

    public static TheoryData<int, long> BucketReportTestData()
    {
        var data = new TheoryData<int, long>();
        ReadOnlySpan<long> bounds = LatencyHistogram.Boundaries;

        // 桶 0：0µs（当前时间戳，elapsed ≈ 0）
        data.Add(0, 0L);

        // 桶 1-32：使用各桶中间值 = (lower + upper) / 2，保留约 25% 宽度的安全余量
        for (int k = 1; k <= 32; k++)
        {
            long lower = bounds[k - 1];
            long upper = bounds[k];
            long mid = (lower + upper) / 2;
            data.Add(k, mid);
        }

        // 桶 33：超出最大上界 5s
        data.Add(33, bounds[32] + 5_000_000L);

        return data;
    }

    [Theory]
    [MemberData(nameof(BucketReportTestData))]
    public void ReportLatency_HitsCorrectBucket(int expectedBucket, long targetUs)
    {
        var h = new LatencyHistogram();
        h.MetricName = "bkt_test";

        h.ReportLatency(TimestampUsAgo(targetUs));
        string output = ToPrometheusString(ref h);

        if (expectedBucket <= 32)
        {
            // 该桶以 Boundaries[expectedBucket] 为上界对应的 le= 行，累积计数应为 1
            string expected = $"bkt_test_bucket{{le=\"{LatencyHistogram.Boundaries[expectedBucket]}\"}} 1";
            Assert.Contains(expected, output);
        }
        else
        {
            // 桶 33：+Inf 行出现
            Assert.Contains("bkt_test_bucket{le=\"+Inf\"}", output);
        }
    }

    // ---- ToPrometheusText 测试 ----

    [Fact]
    public void ToPrometheusText_EmptyHistogram_OnlyOutputsSumAndCount()
    {
        var h = new LatencyHistogram();
        h.MetricName = "empty_metric";
        string output = ToPrometheusString(ref h);

        // 无请求时不应输出任何桶行
        Assert.DoesNotContain("_bucket", output);
        Assert.Contains("empty_metric_sum 0\n", output);
        Assert.Contains("empty_metric_count 0\n", output);
    }

    [Fact]
    public void ToPrometheusText_NullMetricName_DefaultsToLatencyUs()
    {
        var h = new LatencyHistogram();
        // MetricName 默认为 null
        string output = ToPrometheusString(ref h);
        Assert.Contains("latency_us_sum", output);
        Assert.Contains("latency_us_count", output);
    }

    [Fact]
    public void ToPrometheusText_EmptyMetricName_DefaultsToLatencyUs()
    {
        var h = new LatencyHistogram();
        h.MetricName = "";
        string output = ToPrometheusString(ref h);
        Assert.Contains("latency_us_sum", output);
        Assert.Contains("latency_us_count", output);
    }

    [Fact]
    public void ToPrometheusText_WithLabels_BucketLineIncludesLabel()
    {
        var h = new LatencyHistogram();
        h.MetricName = "api_latency";
        h.Labels = "handler=\"/health\"";
        h.ReportLatency(TimestampUsAgo(500));
        string output = ToPrometheusString(ref h);

        // 桶行含标签
        Assert.Contains("handler=\"/health\"", output);
        // sum/count 行含标签
        Assert.Contains("api_latency_sum{handler=\"/health\"}", output);
        Assert.Contains("api_latency_count{handler=\"/health\"}", output);
    }

    [Fact]
    public void ToPrometheusText_WithoutLabels_SumCountHaveNoBraces()
    {
        var h = new LatencyHistogram();
        h.MetricName = "api_latency";
        // Labels 为 null
        h.ReportLatency(TimestampUsAgo(500));
        string output = ToPrometheusString(ref h);

        var sumLine = output.Split('\n').First(l => l.Contains("_sum "));
        Assert.DoesNotContain("{", sumLine);
        var countLine = output.Split('\n').First(l => l.Contains("_count "));
        Assert.DoesNotContain("{", countLine);
    }

    [Fact]
    public void ToPrometheusText_CumulativeCounts_AreNonDecreasing()
    {
        var h = new LatencyHistogram();
        h.MetricName = "cumul_test";
        // 在不同桶中各写入一个请求
        h.ReportLatency(TimestampUsAgo(300));    // 小延迟
        h.ReportLatency(TimestampUsAgo(5000));   // 中延迟
        h.ReportLatency(TimestampUsAgo(50000));  // 大延迟

        string output = ToPrometheusString(ref h);

        var counts = output.Split('\n')
            .Where(l => l.Contains("cumul_test_bucket{le=") && !l.Contains("+Inf"))
            .Select(l => ulong.Parse(l.Split(' ').Last()))
            .ToList();

        Assert.True(counts.Count > 0, "输出中应存在有限上界桶行");
        for (int i = 1; i < counts.Count; i++)
            Assert.True(counts[i] >= counts[i - 1],
                $"累积计数在位置 {i} 应 >= {counts[i - 1]}，实际 {counts[i]}");
    }

    [Fact]
    public void ToPrometheusText_WithLabels_PlusInfBucketIncludesLabel()
    {
        var h = new LatencyHistogram();
        h.MetricName = "inf_labeled";
        h.Labels = "env=\"prod\"";
        // 超出最大上界触发 +Inf 桶
        long hugeUs = LatencyHistogram.Boundaries[LatencyHistogram.Boundaries.Length - 1] + 5_000_000L;
        h.ReportLatency(TimestampUsAgo(hugeUs));
        string output = ToPrometheusString(ref h);

        Assert.Contains("inf_labeled_bucket{le=\"+Inf\",env=\"prod\"}", output);
    }

    [Fact]
    public void ToPrometheusText_Bucket33_ShowsPlusInfWithoutLabels()
    {
        var h = new LatencyHistogram();
        h.MetricName = "inf_test";
        long hugeUs = LatencyHistogram.Boundaries[LatencyHistogram.Boundaries.Length - 1] + 5_000_000L;
        h.ReportLatency(TimestampUsAgo(hugeUs));
        string output = ToPrometheusString(ref h);

        Assert.Contains("inf_test_bucket{le=\"+Inf\"} ", output);
    }

    [Fact]
    public void ToPrometheusText_AllBucketsFilled_CountEquals34()
    {
        var h = new LatencyHistogram();
        h.MetricName = "all_buckets";
        ReadOnlySpan<long> bounds = LatencyHistogram.Boundaries;

        // 桶 0：0µs
        h.ReportLatency(TimestampUsAgo(0));

        // 桶 1-32：各桶中间值
        for (int k = 1; k <= 32; k++)
        {
            long mid = (bounds[k - 1] + bounds[k]) / 2;
            h.ReportLatency(TimestampUsAgo(mid));
        }

        // 桶 33：超出最大上界
        h.ReportLatency(TimestampUsAgo(bounds[32] + 5_000_000L));

        string output = ToPrometheusString(ref h);

        // +Inf 桶出现（桶 33 有数据）
        Assert.Contains("all_buckets_bucket{le=\"+Inf\"}", output);
        // 总请求数 = 34
        Assert.Contains($"all_buckets_count {LatencyHistogram.BucketCount}\n", output);
    }

    // ---- Sum 方法测试 ----

    [Fact]
    public void Sum_AccumulatesRequestCountAndLatencyUsTotal()
    {
        var dst = new LatencyHistogram();
        dst.ReportLatency(TimestampUsAgo(500));

        var src = new LatencyHistogram();
        src.ReportLatency(TimestampUsAgo(1000));
        src.ReportLatency(TimestampUsAgo(2000));

        dst.Sum(ref src);

        Assert.Equal(3UL, dst.RequestCount);
        Assert.True(dst.LatencyUsTotal >= src.LatencyUsTotal, "Sum 后总延迟应包含 src 的延迟");
    }

    [Fact]
    public void Sum_AccumulatesBucketCounts()
    {
        var dst = new LatencyHistogram { MetricName = "sum_dst" };
        dst.ReportLatency(TimestampUsAgo(500));  // 落在某个有限桶

        var src = new LatencyHistogram();
        src.ReportLatency(TimestampUsAgo(500));  // 同一个桶再计入一次

        dst.Sum(ref src);

        string output = ToPrometheusString(ref dst);
        // 两次上报同一范围，最终累积桶计数应为 2
        Assert.Contains("sum_dst_count 2\n", output);
        // 某个 le= 桶的累积计数应 >= 2
        var counts = output.Split('\n')
            .Where(l => l.Contains("sum_dst_bucket{le=") && !l.Contains("+Inf"))
            .Select(l => ulong.Parse(l.Split(' ').Last()))
            .ToList();
        Assert.True(counts.Any(c => c >= 2), "Sum 后应有累积计数 >= 2 的桶");
    }

    [Fact]
    public void Sum_WithEmptySrc_LeavesDestUnchanged()
    {
        var dst = new LatencyHistogram();
        dst.ReportLatency(TimestampUsAgo(500));
        var countBefore = dst.RequestCount;
        var totalBefore = dst.LatencyUsTotal;

        var src = new LatencyHistogram();  // 空的 src
        dst.Sum(ref src);

        Assert.Equal(countBefore, dst.RequestCount);
        Assert.Equal(totalBefore, dst.LatencyUsTotal);
    }

    // ---- Reset 方法测试 ----

    [Fact]
    public void Reset_ClearsRequestCountAndLatencyUsTotal()
    {
        var h = new LatencyHistogram();
        h.ReportLatency(TimestampUsAgo(500));
        h.ReportLatency(TimestampUsAgo(1000));

        h.Reset();

        Assert.Equal(0UL, h.RequestCount);
        Assert.Equal(0UL, h.LatencyUsTotal);
    }

    [Fact]
    public void Reset_ClearsBuckets_ToPrometheusTextShowsNoBucketLines()
    {
        var h = new LatencyHistogram();
        h.MetricName = "reset_test";
        h.ReportLatency(TimestampUsAgo(500));
        h.ReportLatency(TimestampUsAgo(LatencyHistogram.Boundaries[^1] + 5_000_000L));

        h.Reset();

        string output = ToPrometheusString(ref h);
        Assert.DoesNotContain("_bucket", output);
        Assert.Contains("reset_test_sum 0\n", output);
        Assert.Contains("reset_test_count 0\n", output);
    }

    [Fact]
    public void Reset_AllowsReuse_CountsFromZeroAfterReset()
    {
        var h = new LatencyHistogram { MetricName = "reuse_test" };
        h.ReportLatency(TimestampUsAgo(500));
        h.Reset();

        h.ReportLatency(TimestampUsAgo(500));

        Assert.Equal(1UL, h.RequestCount);
    }
}
