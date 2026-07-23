#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.ConsoleLogger;

using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using QiWa.Common;
using QiWa.Compress;
using static QiWa.DebugUtils.Utils;
using static QiWa.Syscall.NativeWrite;

internal sealed class BufferWrapper  // 包装一层是为了做原子轮换
{
    internal RentedBuffer Rented;
    internal BufferWrapper(int len)
    {
        Rented = new RentedBuffer(len);
    }
}

public partial class ThreadLocalLogger : IDisposable
{
    private const int ReservedBufferLen = 1024;  // 预留的 buffer 长度
    internal BufferWrapper Buffer;  // 便于做原子轮换. 这个线程上的日志缓冲区
    private readonly Task timerTask;  // 定时器 Task
    private readonly PeriodicTimer flushTimer;  // 定时器
#if NET9_0_OR_GREATER
    private readonly System.Threading.Lock locker = new();  // 锁
#else
    private readonly object locker = new();  // 锁
#endif
    private readonly HttpClient? httpClient;  // 当使用 jsonline 模式推送日志时，此对象有效

    /// <summary>
    /// 仅供测试使用，用于截获输出文本，避免直接写入 stdout。
    /// </summary>
#pragma warning disable CS0649  // 字段仅在测试代码中赋值，此处声明为 null 是正确的
    internal static Action<string>? TestOutputCapture;
#pragma warning restore CS0649

    // ThreadLocal
    internal static readonly ThreadLocal<ThreadLocalLogger> _threadLocal =
        new ThreadLocal<ThreadLocalLogger>(() => new ThreadLocalLogger(), trackAllValues: true);
    public static ThreadLocalLogger Current => _threadLocal.Value!;
    internal static Int64 ConcurrentHttpPostCount = 0;  // 记录并发的 http post task 的数量
    private const int maxAllowedPostTaskCount = 20;  // todo: 未来修改为从参数配置

    public ThreadLocalLogger()
    {
        if (Logger.Instance == null)
        {
            throw new Exception("use Logger.Init() first");
        }
        this.Buffer = new BufferWrapper(Logger.Instance.LogBufferSize);
        flushTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(Logger.Instance.FlushIntervalMs));
        timerTask = Task.Run(TimerLoopAsync);
        if (Logger.Instance.JsonLineUrl != "")
        {
            httpClient = new HttpClient();
        }
    }

    // info MA0055: Do not use finalizer  => 析构函数调用 Dispose 是冗余且危险的
    // ~ThreadLocalLogger()
    // {
    //     Dispose();
    // }

    public void Dispose()
    {
        Buffer.Rented.Dispose();
        flushTimer.Dispose();
        timerTask.Dispose();
        httpClient?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 获取日志的缓冲区
    /// </summary>
    /// <returns></returns>
    internal ref RentedBuffer GetBuffer()
    {
        BufferWrapper w = Volatile.Read(ref this.Buffer);
        return ref w.Rented;
    }

    /// <summary>
    /// 对日志的缓冲区做轮换
    /// </summary>
    /// <returns>旧的缓冲区对象</returns>
    internal BufferWrapper NewAndGetOld()
    {
        // 上层会加锁，这个函数内不要加锁
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        BufferWrapper old = Volatile.Read(ref this.Buffer);
        var newObject = new BufferWrapper(Logger.Instance.LogBufferSize);
        Volatile.Write(ref this.Buffer, newObject);
        return old;
    }

    /// <summary>
    /// 决定要不要对日志缓冲区进行轮换
    /// </summary>
    /// <param name="buf"></param>
    internal void Flush(ref RentedBuffer buf)
    {
        System.Diagnostics.Debug.Assert(buf.Data != null);
        if (TestOutputCapture != null)  // 这部分代码仅仅是为了方便单元测试
        {
            var testWrapper = NewAndGetOld();
            try
            {
                TestOutputCapture(Encoding.UTF8.GetString(testWrapper.Rented.Data!, 0, testWrapper.Rented.Length));
            }
            finally
            {
                testWrapper.Rented.Dispose();
            }
            return;
        }
        if (buf.Length < buf.Data.Length - ReservedBufferLen)
        {
            return;
        }
        // 上层已经加锁了
        var wrapper = NewAndGetOld();
        // todo: UnsafeQueueUserWorkItem 可能更快 => 但是内部的函数对类有依赖，Task 会更适合的
        _ = Task.Run(async () =>
        {
            // ConfigureAwait: 恢复执行时直接在线程池的任意线程上继续，不切换上下文
            await writeLogAsync(wrapper).ConfigureAwait(false);
            wrapper = null;
        });
    }

    /// <summary>
    /// 进程退出时，把 buffer 中剩余的日志进行输出
    /// </summary>
    internal void Shutdown()
    {
        lock (locker)
        {
            ref RentedBuffer rented = ref GetBuffer();
            if (rented.Length == 0)
            {
                return;
            }
            WriteStdout(rented.AsSpan());
            rented.Dispose();
        }
    }

    private static readonly System.Net.Http.Headers.MediaTypeHeaderValue mediaType = new MediaTypeHeaderValue("application/json");

    private const int defaultVlogsServerTimeoutMs = 1000 * 20;  // 发送到 vlogs 服务器的时候，最大超时时间为 20s

    // 把日志通过 http jsonline 的方式发送给 VictoriaLogs 服务器
    private async Task<Error> writeJsonlineAsync(BufferWrapper wrapper)
    {
        System.Diagnostics.Debug.Assert(httpClient != null);
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        var (compressed, error) = ZstdCompressor.Compress(wrapper.Rented.Data.AsSpan(0, wrapper.Rented.Length));
        if (error.Err())
        {
            return error;
        }
        var sw = Stopwatch.StartNew();
        var cnt = Interlocked.Read(ref ConcurrentHttpPostCount);
        if (cnt >= maxAllowedPostTaskCount)
        {
            return QiWa.Common.Error.WithLoc(5, "maxAllowedPostTaskCount reached");
        }
        Interlocked.Increment(ref ConcurrentHttpPostCount);
        try
        {
            using var content = new ReadOnlyMemoryContent(new ReadOnlyMemory<byte>(compressed.Data!, 0, compressed.Length));
            content.Headers.ContentType = mediaType;
            content.Headers.ContentEncoding.Add("zstd");
            try
            {
                // see: https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.postasync?view=net-10.0#system-net-http-httpclient-postasync(system-uri-system-net-http-httpcontent)
                // todo: 这里应该使用 fire and forgot 的模型 => 上层使用了 fire and forgot 模型
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(defaultVlogsServerTimeoutMs));  // 初始化的时候允许更长的超时时间
                CancellationToken ct = cts.Token;
                using var response = await httpClient.PostAsync(Logger.Instance.JsonLineUrl, content, ct).ConfigureAwait(false);
                sw.Stop();
                if (!response.IsSuccessStatusCode)
                {
                    return QiWa.Common.Error.WithLoc(code: 1, message: $"response code={response.StatusCode}, url={Logger.Instance.JsonLineUrl}");
                }
                ThreadLocalLogger.Current.Info(
                    Field.String("message"u8, $"send to log server success, {wrapper.Rented.Length} bytes"),
                    Field.Int64("latency_us"u8, sw.Elapsed.Microseconds)
                );
                return default;
            }
            catch (HttpRequestException exHttp)
            {
                return QiWa.Common.Error.WithLoc(code: 2, message: $"[HttpRequestException] exception={exHttp.Message}, url={Logger.Instance.JsonLineUrl}");
            }
            catch (OperationCanceledException exTimeout)
            {
                return QiWa.Common.Error.WithLoc(code: 3, message: $"[OperationCanceledException] exception={exTimeout.Message}, url={Logger.Instance.JsonLineUrl}");
            }
            catch (Exception ex)
            {
                // todo: 这里曾经发生了无法抓到的异常
                // A Task's exception(s) were not observed either by Waiting on the Task or accessing its Exception property. 
                // As a result, the unobserved exception was rethrown by the finalizer thread. (One or more errors occurred.
                return QiWa.Common.Error.WithLoc(code: 4, message: $"unknown exception={ex.Message}, url={Logger.Instance.JsonLineUrl}");
            }
        }
        finally
        {
            compressed.Dispose();
            Interlocked.Decrement(ref ConcurrentHttpPostCount);
        }
    }

    private async Task writeLogAsync(BufferWrapper wrapper)
    {
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        try
        {
            if (Logger.Instance.JsonLineUrl != "")
            {
                var err = await writeJsonlineAsync(wrapper).ConfigureAwait(false);
                if (!err.Err())
                {
                    // 没有错误，说明已经成功发送到服务器端了
                    return;
                }
                Logger.LogDiagnosticsError(null,
                    $"writeJsonline fail: code={err.Code}, msg={err.Message}");
            }
            // 这部分代码仅用于单元测试
            var outputCapture = TestOutputCapture;
            if (outputCapture != null)
            {
                outputCapture(Encoding.UTF8.GetString(wrapper.Rented.Data!, 0, wrapper.Rented.Length));
                return;
            }
            WriteStdout(wrapper.Rented.Data.AsSpan(0, wrapper.Rented.Length));
        }
        finally
        {
            wrapper.Rented.Dispose();
        }
    }

    private async Task TimerLoopAsync()
    {
        System.Diagnostics.Debug.Assert(Logger.Instance != null);
        try
        {
            while (await flushTimer.WaitForNextTickAsync(Logger.Instance.LoggerToken.Token).ConfigureAwait(false))
            {
                // 检查退出信号，且等待定时器触发
                BufferWrapper? wrapper;
                lock (locker)
                {
                    // 在 buffer 交换期间，一定没有在写入日志
                    var rent = GetBuffer();
                    if (rent.Length == 0)
                    {
                        continue;
                    }
                    wrapper = NewAndGetOld();
                }
                _ = Task.Run(async () =>
                {
                    await writeLogAsync(wrapper).ConfigureAwait(false);
                    wrapper = null;
                });
            }
        }
        catch (OperationCanceledException err)
        {
            // Prompt intent: `make test` should not print false failure logs during normal logger shutdown.
            if (Logger.Instance == null || Logger.Instance.LoggerToken.IsCancellationRequested)
            {
                return;
            }
            var exceptionLocation = GetExceptionLocation(err);
            Logger.LogDiagnosticsError(err,
                $"TimerLoop canceled. IsCancellationRequested={Logger.Instance.LoggerToken.IsCancellationRequested}. ExceptionLocation={exceptionLocation}.");
        }
    }
}
