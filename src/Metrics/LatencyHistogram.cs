// 意图：为 API 接口延迟分布统计实现 histogram struct。
// 延迟下界 100µs（倍增序列的基数），上界约 64.72s，倍增系数 1.5，共 34 个桶。
// Bucket 0 覆盖 [0, 150µs)，Bucket 33 覆盖 [~64.72s, +∞)。
#pragma warning disable CS1591
namespace QiWa.Metrics;

using System;
using System.Diagnostics;
using System.Threading;
using QiWa.Common;

/// <summary>
/// 用于 API 接口延迟分布统计的 histogram，实现为 unsafe struct（使用 fixed 数组存储 34 个桶计数器）。
/// 使用方式：在请求开始前调用 Stopwatch.GetTimestamp() 获取 startTimestamp，
/// 请求处理完成后调用 ReportLatency(startTimestamp) 即可。
/// </summary>
public struct LatencyHistogram : IMetricFormatter
{
    /// <summary>桶的总数量</summary>
    public const int BucketCount = 34;

    /// <summary>倍增序列的基数（微秒），即 100µs</summary>
    private const double BoundaryBase = 100.0;

    /// <summary>桶宽倍增系数</summary>
    private const double GrowthFactor = 1.5;

    /// <summary>1/ln(1.5)，用于 O(1) 公式计算桶下标，避免热路径中的除法</summary>
    private static readonly double InvLogGrowthFactor = 1.0 / Math.Log(GrowthFactor);

    /// <summary>
    /// 预计算的 33 个桶上界（微秒）：Boundaries[i] = floor(100 * 1.5^(i+1))。
    /// Boundaries[0] = 150µs，Boundaries[32] ≈ 64,715,982µs（约 64.72s）。
    /// 第 i 个桶覆盖 [Boundaries[i-1], Boundaries[i])，桶 33 覆盖 [Boundaries[32], +∞)。
    /// 对应提示词“修改为固定长度的数组，并减少堆上的对象数量”：这里改为编译期固定的只读边界表。
    /// </summary>
    public static ReadOnlySpan<long> Boundaries => [
        150L, 225L, 337L, 506L, 759L, 1139L, 1708L, 2562L, 3844L, 5766L, 8649L,
        12974L, 19461L, 29192L, 43789L, 65684L, 98526L, 147789L, 221683L, 332525L,
        498788L, 748182L, 1122274L, 1683411L, 2525116L, 3787675L, 5681512L, 8522269L,
        12783403L, 19175105L, 28762658L, 43143988L, 64715982L
    ];

    [System.Runtime.CompilerServices.InlineArray(BucketCount)]
    private struct BucketArray { private ulong _element; }

    /// <summary>34 个桶的请求计数器</summary>
    private BucketArray _buckets;

    /// <summary>自首次调用以来，所有请求延迟的微秒累计总量</summary>
    public ulong LatencyUsTotal;

    /// <summary>总请求次数</summary>
    public ulong RequestCount;

    /// <summary>Prometheus 输出中使用的 metric 基础名称，例如 "api_latency_us"</summary>
    public string MetricName;

    /// <summary>附加的 Prometheus 标签键值对，例如 handler="foo"（不含花括号）</summary>
    public string Labels;

    /// <summary>
    /// 上报一次请求的延迟：计算从 startTimestamp 到当前时刻的微秒数，
    /// 对对应桶的计数器 +1，并累计总延迟微秒数与请求总次数。
    /// </summary>
    /// <param name="startTimestamp">请求开始时的 Stopwatch 时间戳（由 Stopwatch.GetTimestamp() 获取）</param>
    public void ReportLatency(long startTimestamp)
    {
        long elapsedUs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds;
        if (elapsedUs < 0)
        {
            elapsedUs = 0;
        }

        Interlocked.Add(ref LatencyUsTotal, (ulong)elapsedUs);
        Interlocked.Add(ref RequestCount, 1UL);

        var idx = FindBucket(elapsedUs);
        Interlocked.Add(ref _buckets[idx], 1UL);
    }

    // 公式推导：桶 k 覆盖 [100*1.5^k, 100*1.5^(k+1))，故 k = floor(log(x/100) / log(1.5))
    private static int FindBucket(long latencyUs)
    {
        if (latencyUs <= 0)
        {
            return 0;
        }
        var idx = (int)Math.Floor(Math.Log(latencyUs / BoundaryBase) * InvLogGrowthFactor);
        if (idx < 0)
        {
            return 0;
        }
        if (idx >= BucketCount)
        {
            return BucketCount - 1;
        }
        return idx;
    }

    /// <summary>
    /// 将 histogram 数据格式化为 Prometheus text format，追加写入 buf。
    /// 输出累积桶计数（le=...）、{MetricName}_sum 和 {MetricName}_count。
    /// </summary>
    public readonly void ToPrometheusText(ref RentedBuffer dst)
    {
        bool hasLabels = !string.IsNullOrEmpty(Labels);

        // 输出 HELP / TYPE 元数据行，声明该 metric family 为 histogram 类型，
        // 与 Prometheus text exposition format 对齐（否则会被当作 untyped）。
        dst.Append("# HELP "u8);
        AppendMetricName(ref dst, MetricName);
        dst.Append(" latency distribution in microseconds\n"u8);
        dst.Append("# TYPE "u8);
        AppendMetricName(ref dst, MetricName);
        dst.Append(" histogram\n"u8);

        // 对应“类似位置的字符串常量进行修改，避免 utf-16 到 utf-8 的转换”：
        // 常量直接写为 UTF-8 字面量或单字节，避免走 Append(string) 的编码路径。
        // 输出各有限上界桶（累积计数）。
        // 必须始终输出全部桶（即使计数为 0），否则 Prometheus 的 histogram_quantile
        // 会把第一个出现的桶误当作插值下界为 0 的起始桶，导致跳过的低延迟桶造成分位数插值偏差。
        ReadOnlySpan<long> boundaries = Boundaries;
        ulong cumulative = 0;
        for (int i = 0; i < boundaries.Length; i++)
        {
            cumulative += _buckets[i];
            AppendMetricName(ref dst, MetricName);
            dst.Append("_bucket{le=\""u8);
            dst.Append(boundaries[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (hasLabels)
            {
                dst.Append("\","u8);
                dst.Append(Labels);
                dst.Append((byte)'}');
            }
            else
            {
                dst.Append((byte)'"');
                dst.Append((byte)'}');
            }
            dst.Append((byte)' ');
            dst.Append(cumulative);
            dst.Append((byte)'\n');
        }
        {
            // 输出 +Inf 桶（累积所有请求），同样始终输出
            cumulative += _buckets[BucketCount - 1];
            AppendMetricName(ref dst, MetricName);
            dst.Append("_bucket{le=\"+Inf\""u8);
            if (hasLabels)
            {
                dst.Append((byte)',');
                dst.Append(Labels);
            }
            dst.Append("} "u8);
            dst.Append(cumulative);
            dst.Append((byte)'\n');
        }

        // 输出延迟总量（微秒）
        AppendMetricName(ref dst, MetricName);
        dst.Append("_sum"u8);
        if (hasLabels)
        {
            dst.Append((byte)'{');
            dst.Append(Labels);
            dst.Append((byte)'}');
        }
        dst.Append((byte)' ');
        dst.Append(LatencyUsTotal);
        dst.Append((byte)'\n');

        // 输出请求总次数
        AppendMetricName(ref dst, MetricName);
        dst.Append("_count"u8);
        if (hasLabels)
        {
            dst.Append((byte)'{');
            dst.Append(Labels);
            dst.Append((byte)'}');
        }
        dst.Append((byte)' ');
        dst.Append(RequestCount);
        dst.Append((byte)'\n');
    }

    /// <summary>
    /// 将 metric 名称写入缓冲区；未指定名称时直接写入默认 UTF-8 常量，避免额外的 UTF-16 转 UTF-8。
    /// </summary>
    /// <param name="buf">Prometheus 文本输出缓冲区</param>
    /// <param name="metricName">用户配置的 metric 名称</param>
    private static void AppendMetricName(ref RentedBuffer buf, string metricName)
    {
        if (string.IsNullOrEmpty(metricName))
        {
            buf.Append("latency_us"u8);
            return;
        }

        buf.Append(metricName);
    }

    public void Sum(ref LatencyHistogram src)
    {
        for (int i = 0; i < BucketCount; i++)
        {
            _buckets[i] += src._buckets[i];
        }
        LatencyUsTotal += src.LatencyUsTotal;
        RequestCount += src.RequestCount;
    }

    public void Reset()
    {
        for (int i = 0; i < BucketCount; i++)
        {
            _buckets[i] = 0;
        }
        LatencyUsTotal = 0;
        RequestCount = 0;
    }
}
