#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.ConsoleLogger;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.ObjectPool;
using QiWa.Common;

/// <summary>
/// 日志库支持的日志级别
/// </summary>
public enum LogLevel
{
    Fatal,
    Error,
    Warn,
    Info,
    Debug
}

public class Logger
{
    internal static Logger? Instance;  // 全局的日志对象

    const int minLogFlushIntervalMs = 100;  // 最小的日志 flush 时间
    const int defaultLogBufferSize = 1024 * 128;  // 默认的日志内存空间
    const int minLogBufferSize = 1024 * 4;  // 最小的日志内存空间
    const int defaultFlushIntervalMs = 1000;  // 默认的日志 flush 时间

    internal int FlushIntervalMs = defaultFlushIntervalMs;  // 输出日志的间隔时间
    internal LogLevel Level = LogLevel.Info;  // 全局的日志级别
    internal readonly byte[] TagPrefix = [];  // 全局的日志前缀
    internal readonly int LogBufferSize = defaultLogBufferSize;  // 日志的内存 buffer 大小
    internal readonly CancellationTokenSource LoggerToken;  // 系统退出的信号
    internal readonly string JsonLineUrl;  // 日志使用 jsonline push 的地址

    /// <summary>
    /// 当日志库自身出问题时，把诊断信息写到标准错误输出，避免依赖仅在较新框架组合中才完整可用的日志扩展方法。
    /// Prompt intent: 把 VS Code 中对 AddConsole/SetMinimumLevel 的红线改成兼容 net8.0 的实现。
    /// </summary>
    /// <param name="exception">需要附带输出的异常对象，可为 null。</param>
    /// <param name="message">诊断消息文本。</param>
    internal static void LogDiagnosticsError(Exception? exception, string message)
    {
        var output = $"[{DateTimeOffset.UtcNow:u}] ConsoleLogger diagnostics error: {message}";
        Console.Error.WriteLine(output);
        if (exception != null)
        {
            Console.Error.WriteLine(exception);
        }
        Console.Error.Flush();
    }

    // 内存池
    readonly DefaultObjectPool<TaskLogger> pool;  // 每个 Task 都可以有自己的 TaskLogger 对象

    /// <summary>
    /// 日志库全局初始化
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <param name="flushIntervalMs">日志 flush 时间</param>
    /// <param name="tags">日志的全局 tags </param>
    /// <param name="logBufferSize">日志缓冲区大小</param>
    /// <param name="jsonlineUrl">设置日志的 jsonline push url</param>
    public static void Init(
        LogLevel level = LogLevel.Warn,
        int flushIntervalMs = 1000,
        Dictionary<string, string>? tags = null,
        int logBufferSize = defaultLogBufferSize,
        string jsonlineUrl = "")
    {
        Instance = new Logger(logBufferSize, tags, jsonlineUrl);
        Instance.Level = level;
        if (flushIntervalMs < minLogFlushIntervalMs)
        {
            flushIntervalMs = minLogFlushIntervalMs;
        }
        Instance.FlushIntervalMs = flushIntervalMs;
    }

    /// <summary>
    /// 对象池的配置
    /// </summary>
    sealed class BufferPolicy : PooledObjectPolicy<TaskLogger>
    {
        public override TaskLogger Create()
            => new TaskLogger();
        const int maxBufferSize = 1024 * 4;
        public override bool Return(TaskLogger l)
        {
            if (l == null || l.prefix.Data == null)
            {
                return false;
            }
            if (l.prefix.Data.Length >= maxBufferSize)
            {
                l.prefix.Dispose();
                return false;
            }
            l.prefix.Length = 0;
            return true; // true = 放回池
        }
    }

    internal Logger(int logBufferSize, Dictionary<string, string>? tags, string jsonlineUrl)
    {
        if (logBufferSize < minLogBufferSize)
        {
            logBufferSize = minLogBufferSize;
        }
        LogBufferSize = logBufferSize;
        if (tags != null && tags.Count > 0)
        {
            TagPrefix = SetGlobalTags(tags);
        }
        JsonLineUrl = jsonlineUrl;
        if (!string.IsNullOrEmpty(JsonLineUrl) && !Uri.TryCreate(JsonLineUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Invalid jsonlineUrl.", nameof(jsonlineUrl));
        }
        LoggerToken = new CancellationTokenSource();
        pool = new DefaultObjectPool<TaskLogger>(new BufferPolicy());
    }

    /// <summary>
    /// 从内存池获取一个对象
    /// </summary>
    /// <returns></returns>
    public static TaskLogger Get()
    {
        Debug.Assert(Logger.Instance != null);
        var l = Logger.Instance.pool.Get();
        l.prefix.Length = 0;
        l.prefix.Append(Logger.Instance.TagPrefix);
        return l;
    }

    /// <summary>
    /// 把对象放回内存池
    /// </summary>
    /// <param name="l"></param>
    public static void Return(TaskLogger l)
    {
        Debug.Assert(Logger.Instance != null);
        Logger.Instance.pool.Return(l);
    }

    internal void Dispose()
    {
        LoggerToken.Cancel();
        LoggerToken.Dispose();
    }

    public static void Shutdown()
    {
        Debug.Assert(Instance != null);
        Instance.Dispose();
    }

    private static byte[] SetGlobalTags(Dictionary<string, string> tags)
    {
        RentedBuffer buf = new(512);
        buf.Append((byte)'{');
        bool isFirst = true;
        foreach (var (key, value) in tags)
        {
            if (isFirst)
            {
                isFirst = false;
            }
            else
            {
                buf.Append((byte)',');
            }
            Field.String(Encoding.UTF8.GetBytes(key), value).WriteTo(ref buf);
        }
        var temp = new byte[buf.Length];
        Array.Copy(buf.Data!, temp, buf.Length);
        buf.Dispose();
        return temp;
    }

    /// <summary>
    /// 将日志级别字符串（不区分大小写）解析为 <see cref="LogLevel"/> 枚举。
    /// 支持 fatal / error / warn / info / debug，无法识别时默认返回 Warn。
    /// </summary>
    /// <param name="level">日志级别字符串，如 "info"、"debug" 等。</param>
    /// <returns>对应的 <see cref="LogLevel"/> 枚举值。</returns>
    public static LogLevel ParseLogLevel(string level)
    {
        return level.ToLowerInvariant() switch
        {
            "fatal" => LogLevel.Fatal,
            "error" => LogLevel.Error,
            "warn"  => LogLevel.Warn,
            "info"  => LogLevel.Info,
            "debug" => LogLevel.Debug,
            _       => LogLevel.Warn
        };
    }

    /// <summary>
    /// 设置全局的日志级别
    /// </summary>
    /// <param name="level"></param>
    public static void SetLevel(LogLevel level)
    {
        Debug.Assert(Instance != null);
        Instance.Level = level;
    }

    /// <summary>
    /// 设置全局的日志 flush 时间
    /// </summary>
    /// <param name="ms"></param>
    public static void SetFlushIntervalMs(int ms)
    {
        Debug.Assert(Instance != null);
        if (ms < minLogFlushIntervalMs)
        {
            ms = minLogFlushIntervalMs;
        }
        Instance.FlushIntervalMs = ms;
    }

    public static string CutFilePath(string file)
    {
        for (int i = file.Length - 1; i >= 0; i--)
        {
            if (file[i] == '/' || file[i] == '\\')
            {
                return file.Substring(i + 1);
            }
        }
        return file;
    }

    /// <summary>
    /// 解析日志全局 tags，格式为 URL query string（a=b&amp;c=d）。
    /// </summary>
    public static (Dictionary<string, string>?, Error) ParseTags(string? tags)
    {
        if (string.IsNullOrEmpty(tags))
        {
            return (null, Error.WithLoc(code: 250, message: "No tags provided"));
        }
        var result = new Dictionary<string, string>();
        foreach (var pair in tags.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                return (null, Error.WithLoc(code: 251, message: $"Invalid tag format: '{pair}'. Expected key=value."));
            }
            result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
        }
        return (result, default);
    }
}
