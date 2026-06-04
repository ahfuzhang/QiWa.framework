#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.ConsoleLogger;

using QiWa.Common;

public partial class TaskLogger : IDisposable
{
    const int defaultPrefixLen = 512;
    internal RentedBuffer prefix;
    internal TaskLogger()
    {
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        prefix = new(Logger.Instance.TagPrefix.Length + defaultPrefixLen);
        prefix.Append(Logger.Instance.TagPrefix);
    }

    private bool _disposed = false;

#pragma warning disable MA0055
    ~TaskLogger()
    {
        Dispose(false);  // GC 兜底调用
    }
#pragma warning restore MA0055

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);  // 告诉 GC：不必再调 finalizer
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            prefix.Dispose();  // 只在正常 Dispose 时释放托管资源
        }
        _disposed = true;
    }
}
