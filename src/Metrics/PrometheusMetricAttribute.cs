#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Metrics;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using QiWa.Common;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class PrometheusMetricAttribute : Attribute
{
    public string Name { get; }
    public string Labels { get; }
    public PrometheusMetricAttribute(string name, string labels = "")
    {
        Name = name;
        Labels = labels;
    }
}

/// <summary>
/// 如果一个类想要产生 prometheus 格式的 metrics 文本，那么就需要实现这个接口
/// </summary>
public interface IMetricFormatter
{
    public void ToPrometheusText(ref RentedBuffer dst);
}

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)]
public class MetricsBase : IMetricFormatter
{
    /// <summary>
    /// 通过反射遍历所有的 Uint64 和 LatencyHistogram 类型的成员，把这些成员转换为 prometheus 格式的 metrics 文本
    /// </summary>
    /// <param name="dst">目标缓冲区</param>
    public void ToPrometheusText(ref RentedBuffer dst)
    {
        // todo：修改为 source generator 的实现方式
        foreach (var field in GetType().GetFields(
                             BindingFlags.Instance |
                             BindingFlags.Public))
        {
            var attr = field.GetCustomAttribute<PrometheusMetricAttribute>();
            if (attr == null)
            {
                continue;
            }
            switch (field.FieldType)
            {
                case Type t when t == typeof(ulong):  // 使用 Uint64 来作为 counter 使用
                    var v = (ulong)field.GetValue(this)!;
                    if (v == 0)
                    {
                        continue;
                    }
                    dst.Append(attr.Name);
                    if (!string.IsNullOrWhiteSpace(attr.Labels))
                    {
                        dst.Append((byte)'{');
                        dst.Append(attr.Labels);
                        dst.Append((byte)'}');
                    }
                    dst.Append((byte)' ');
                    dst.Append(v);
                    dst.Append((byte)'\n');
                    break;
                case Type t when t == typeof(LatencyHistogram):  // 用于统计延迟分布的 Histogram
                    var hist = (LatencyHistogram)field.GetValue(this)!;
                    if (string.IsNullOrEmpty(hist.MetricName))
                    {
                        hist.MetricName = attr.Name;
                    }
                    if (string.IsNullOrEmpty(hist.Labels))
                    {
                        hist.Labels = attr.Labels;
                    }
                    hist.ToPrometheusText(ref dst);
                    break;
                default:
                    continue;
            }
        }
    }
}
