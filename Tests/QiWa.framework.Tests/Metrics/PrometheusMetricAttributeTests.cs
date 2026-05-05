// 意图：为 PrometheusMetricAttribute.cs 生成单元测试，覆盖属性构造、AttributeUsage 与 MetricsBase 输出分支，尽量达到 100% 代码覆盖率。
#pragma warning disable CS1591
namespace Tests.Metrics;

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using QiWa.Common;
using QiWa.Metrics;
using Xunit;

public class PrometheusMetricAttributeTests
{
    // 用于把 IMetricFormatter 的输出转换成字符串，便于断言 Prometheus 文本。
    private static string FormatMetric(IMetricFormatter formatter)
    {
        var buf = new RentedBuffer(512);
        formatter.ToPrometheusText(ref buf);
        string text = Encoding.UTF8.GetString(buf.AsSpan());
        buf.Dispose();
        return text;
    }

    [Fact]
    public void Constructor_WithoutLabels_StoresNameAndEmptyLabels()
    {
        var attr = new PrometheusMetricAttribute("request_total");

        Assert.Equal("request_total", attr.Name);
        Assert.Equal(string.Empty, attr.Labels);
    }

    [Fact]
    public void Constructor_WithLabels_StoresProvidedValues()
    {
        var attr = new PrometheusMetricAttribute("request_total", "service=\"orders\"");

        Assert.Equal("request_total", attr.Name);
        Assert.Equal("service=\"orders\"", attr.Labels);
    }

    [Fact]
    public void AttributeUsage_AllowsFieldsAndProperties()
    {
        var usage = typeof(PrometheusMetricAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(usage);
        Assert.Equal(AttributeTargets.Field | AttributeTargets.Property, usage!.ValidOn);
    }

    [Fact]
    public void ToPrometheusText_WritesOnlySupportedPublicUlongFields()
    {
        IMetricFormatter formatter = new MetricsBaseTestProbe();

        string text = FormatMetric(formatter);
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("plain_metric 7", lines);
        Assert.Contains("labeled_metric{service=\"orders\"} 3", lines);
        Assert.Contains("whitespace_metric 5", lines);
        Assert.DoesNotContain("whitespace_metric{", text);
        Assert.DoesNotContain("zero_metric", text);
        Assert.DoesNotContain("text_metric", text);
        Assert.DoesNotContain("hidden_metric", text);
        Assert.DoesNotContain("property_metric", text);
        Assert.Equal(11UL, ((MetricsBaseTestProbe)formatter).HiddenMetricValue);
    }

    [Fact]
    public void ToPrometheusText_LatencyHistogramField_EmptyHistogram_OutputsSumAndCount()
    {
        var probe = new MetricsBaseWithHistogramTestProbe();

        string text = FormatMetric(probe);

        Assert.Contains("api_latency_sum 0\n", text);
        Assert.Contains("api_latency_count 0\n", text);
        Assert.DoesNotContain("_bucket", text);
    }

    [Fact]
    public void ToPrometheusText_LatencyHistogramField_AfterReportLatency_OutputsBucketLines()
    {
        var probe = new MetricsBaseWithHistogramTestProbe();
        long startTs = Stopwatch.GetTimestamp() -
            (long)Math.Ceiling(500.0 * Stopwatch.Frequency / 1_000_000.0);
        probe.Latency.ReportLatency(startTs);

        string text = FormatMetric(probe);

        Assert.Contains("api_latency_bucket{", text);
        Assert.Contains("api_latency_count 1\n", text);
    }

    [Fact]
    public void ToPrometheusText_WhenNoFieldQualifies_ReturnsEmptyString()
    {
        var formatter = new MetricsBaseTestProbe
        {
            PlainMetric = 0,
            LabeledMetric = 0,
            WhitespaceMetric = 0,
            ZeroMetric = 0,
            TextMetric = "ignored",
            PlainFieldWithoutAttribute = 9,
            PropertyMetric = 21,
        };

        string text = FormatMetric(formatter);

        Assert.Equal(string.Empty, text);
    }
}
