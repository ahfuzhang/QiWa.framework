// 意图：为 PrometheusMetricAttribute.cs 的 MetricsBase 测试提供探针类型，覆盖字段筛选和标签输出分支。
#pragma warning disable CS1591
namespace Tests.Metrics;

using QiWa.Metrics;

/// <summary>
/// 用于验证 MetricsBase 只会输出公开的 ulong 字段，并按属性配置拼接 Prometheus 文本。
/// </summary>
internal sealed class MetricsBaseTestProbe : MetricsBase
{
    // 用于验证默认空标签的公开 ulong 字段会被正常输出。
    [PrometheusMetric("plain_metric")]
    public ulong PlainMetric = 7;

    // 用于验证带标签的公开 ulong 字段会附带 labels 输出。
    [PrometheusMetric("labeled_metric", "service=\"orders\"")]
    public ulong LabeledMetric = 3;

    // 用于验证空白标签会被视为“没有标签”。
    [PrometheusMetric("whitespace_metric", "   ")]
    public ulong WhitespaceMetric = 5;

    // 用于验证零值指标会被跳过，不输出任何文本。
    [PrometheusMetric("zero_metric")]
    public ulong ZeroMetric;

    // 用于验证非 ulong 的公开字段即使带属性也不会输出。
    [PrometheusMetric("text_metric")]
    public string TextMetric = "ignored";

    // 用于验证没有 PrometheusMetricAttribute 的字段不会输出。
    public ulong PlainFieldWithoutAttribute = 9;

    // 用于验证私有字段不会被 MetricsBase 的公开字段扫描逻辑输出。
    [PrometheusMetric("hidden_metric")]
    private readonly ulong _hiddenMetric = 11;

    // 用于验证属性即使带 PrometheusMetricAttribute，也不会被 GetFields() 的结果输出。
    [PrometheusMetric("property_metric")]
    public ulong PropertyMetric { get; set; } = 13;

    // 用于在测试中读取私有字段，避免该测试探针的意图丢失。
    public ulong HiddenMetricValue => _hiddenMetric;
}

/// <summary>
/// 用于验证 MetricsBase.ToPrometheusText 能正确委托到 LatencyHistogram 字段（覆盖 switch 的 LatencyHistogram 分支）。
/// </summary>
internal sealed class MetricsBaseWithHistogramTestProbe : MetricsBase
{
    [PrometheusMetric("api_latency")]
    public LatencyHistogram Latency;

    public MetricsBaseWithHistogramTestProbe()
    {
        Latency = new LatencyHistogram { MetricName = "api_latency" };
    }
}
