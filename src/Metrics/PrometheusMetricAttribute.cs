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

public class MetricsBase
{
    public void Output(ref RentedBuffer buf)
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
            if (field.FieldType != typeof(ulong))
            {
                continue;
            }
            var value = (ulong)field.GetValue(this)!;
            buf.Append(attr.Name);
            if (!string.IsNullOrWhiteSpace(attr.Labels))
            {
                buf.Append('{');
                buf.Append(attr.Labels);
                buf.Append('}');
            }
            buf.Append(' ');
            buf.Append(value);
            buf.Append('\n');
        }
    }
}
