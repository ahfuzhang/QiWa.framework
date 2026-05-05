#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Metrics;

using System;
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

public interface IMetricFormatter
{
    public void ToPrometheusText(ref RentedBuffer buf);
}

public class MetricsBase : IMetricFormatter
{
    public void ToPrometheusText(ref RentedBuffer buf)
    {
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
                case Type t when t == typeof(ulong):
                    var v = (ulong)field.GetValue(this)!;
                    if (v == 0)
                    {
                        continue;
                    }
                    buf.Append(attr.Name);
                    if (!string.IsNullOrWhiteSpace(attr.Labels))
                    {
                        buf.Append((byte)'{');
                        buf.Append(attr.Labels);
                        buf.Append((byte)'}');
                    }
                    buf.Append((byte)' ');
                    buf.Append(v);
                    buf.Append((byte)'\n');
                    break;
                case Type t when t == typeof(LatencyHistogram):
                    var hist = (LatencyHistogram)field.GetValue(this)!;
                    if (string.IsNullOrEmpty(hist.MetricName))
                    {
                        hist.MetricName = attr.Name;
                    }
                    if (string.IsNullOrEmpty(hist.Labels))
                    {
                        hist.Labels = attr.Labels;
                    }
                    hist.ToPrometheusText(ref buf);
                    break;
                default:
                    continue;
            }
        }
    }
}
