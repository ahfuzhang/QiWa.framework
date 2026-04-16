#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.ConsoleLogger;

using QiWa.Common;

public partial class TaskLogger
{
    const int defaultPrefixLen = 512;
    internal RentedBuffer prefix;
    internal TaskLogger()
    {
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        prefix = new(Logger.Instance.TagPrefix.Length + defaultPrefixLen);
        prefix.Append(Logger.Instance.TagPrefix);
    }

    ~TaskLogger()
    {
        prefix.Dispose();
    }

    public void Dispose()
    {
        prefix.Dispose();
    }
}
