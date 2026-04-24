#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.KestrelWrap;

using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Http;
using QiWa.Common;
using QiWa.Compress;
using QiWa.ConsoleLogger;

public enum RequestContentType
{
    Unknown = 0,
    JSON = 1,
    Protobuf = 2
}

public class Counters : QiWa.Common.IResettable
{
    public UInt64 HttpRequestTotal;
    public UInt64 HttpBadRequestTotal;
    public UInt64 InitErrorsTotal;
    public UInt64 ReadRequestErrorsTotal;
    public UInt64 EncodeErrorsTotal;
    public UInt64 SendErrorsTotal;
    public UInt64 HttpNotFoundErrorsTotal;

    public void Reset()
    {
        HttpRequestTotal = 0;
        HttpBadRequestTotal = 0;
        InitErrorsTotal = 0;
        ReadRequestErrorsTotal = 0;
        EncodeErrorsTotal = 0;
        SendErrorsTotal = 0;
        HttpNotFoundErrorsTotal = 0;
    }
}

/// <summary>
/// 注意：依赖日志库 ConsoleLogger，必须先初始化日志库
/// </summary>
public abstract class ContextBase
{
    public RentedBuffer RawRequest = new RentedBuffer(ServerConfig.DefaultRequestSize);  // todo: 这个内存可能太大了
    public RentedBuffer RequestData = new RentedBuffer(ServerConfig.DefaultRequestSize);
    public HttpContext? HttpContext;
    public TaskLogger? L;
    public RequestContentType ContentType;

    // ThreadLocal
    internal static readonly ThreadLocal<Counters> _threadLocal =
        new ThreadLocal<Counters>(() => new Counters(), trackAllValues: true);
    public static Counters Counters => _threadLocal.Value!;

    /// <summary>
    /// 累加所有线程上的 Counter
    /// </summary>
    /// <param name="dst"></param>
    /// <returns></returns>
    public static Counters SumCounters(Counters? dst)
    {
        if (dst == null)
        {
            dst = new Counters();
        }
        foreach (Counters c in _threadLocal.Values)
        {
            dst.HttpRequestTotal += Interlocked.Read(ref c.HttpRequestTotal);
            dst.HttpBadRequestTotal += Interlocked.Read(ref c.HttpBadRequestTotal);
            dst.InitErrorsTotal += Interlocked.Read(ref c.InitErrorsTotal);
            dst.ReadRequestErrorsTotal += Interlocked.Read(ref c.ReadRequestErrorsTotal);
            dst.EncodeErrorsTotal += Interlocked.Read(ref c.EncodeErrorsTotal);
            dst.SendErrorsTotal += Interlocked.Read(ref c.SendErrorsTotal);
            dst.HttpNotFoundErrorsTotal += Interlocked.Read(ref c.HttpNotFoundErrorsTotal);
        }
        return dst;
    }

    public void Reset()
    {
        // 重置上下文数据，例如清空字段、重置状态等
        RawRequest.Length = 0;
        RequestData.Length = 0;
        this.HttpContext = null;
        if (L != null)
        {
            Logger.Return(L!);
        }
        L = null;  // 没必要设置为 null，放回对象池后，肯定不再使用
    }

    public void Dispose()
    {
        Reset();
        RawRequest.Dispose();
        RequestData.Dispose();
        // 释放上下文资源，例如关闭连接、清理内存等
    }

    public static Error Validate(HttpContext httpContext)
    {
        Debug.Assert(httpContext != null);
        Debug.Assert(httpContext.Request != null);
        // 验证请求数据是否合法，例如检查必填字段、字段格式等
        if (httpContext.Request.Method != HttpMethods.Post)  // todo: 某些接口可能允许 GET 请求
        {
            // 这一版只支持 post
            httpContext.Response.StatusCode = 405;  // Method Not Allowed
            return Error.WithLoc(code: 405, message: "Only POST method is allowed");
        }
        if (httpContext.Request.ContentType != "application/json" && httpContext.Request.ContentType != "application/protobuf")
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "not support content type: " + httpContext.Request.ContentType);
        }
        if (httpContext.Request.ContentLength == null ||
            httpContext.Request.ContentLength == 0 ||
            httpContext.Request.ContentLength > ServerConfig.MaxRequestSize)
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: $"Content-Length must be greater than 0 and less than {ServerConfig.MaxRequestSize} bytes");
        }
        return default;
    }

    public Error Decode<TRequest>(byte[] reqBytes, ref TRequest req) where TRequest : struct, QiWa.Common.IDecoder
    {
        // 下面进行数据反序列化
        Error err;
        if (this.HttpContext!.Request.ContentType?.StartsWith("application/protobuf") == true)
        {
            err = req.FromProtobuf(reqBytes);
            if (err.Err())
            {
                this.HttpContext!.Response.StatusCode = 400;
                return Error.WithLoc(code: 400, message: "Failed to parse Protobuf: " + err.Message);
            }
            this.ContentType = RequestContentType.Protobuf;
        }
        else if (this.HttpContext!.Request.ContentType?.StartsWith("application/json") == true)
        {
            // 解析 JSON 数据到 Request 对象
            err = req.FromJSON(reqBytes);
            if (err.Err())
            {
                this.HttpContext!.Response.StatusCode = 400;
                return Error.WithLoc(code: 400, message: "Failed to parse JSON: " + err.Message);
            }
            this.ContentType = RequestContentType.JSON;
        }
        else
        {
            this.ContentType = RequestContentType.Unknown;
            this.HttpContext!.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "Unsupported Content-Type: " + this.HttpContext!.Request.ContentType);
        }
        return default;
    }

    public (byte[]?, Error) Encode<TResponse>(ref readonly TResponse rsp) where TResponse : struct, QiWa.Common.IEncoder
    {
        RawRequest.Length = 0;
        switch (this.ContentType)
        {
            case RequestContentType.JSON:
                this.HttpContext!.Response.Headers.ContentType = "application/json";
                rsp.ToJSON(ref this.RawRequest);
                break;
            case RequestContentType.Protobuf:
                this.HttpContext!.Response.Headers.ContentType = "application/protobuf";
                rsp.ToProtobuf(ref this.RawRequest);
                break;
        }
        byte[]? responseBytes;
        string acceptEncoding = this.HttpContext!.Request.Headers.AcceptEncoding.ToString();
        if (acceptEncoding.Contains("zstd"))
        {
            this.RequestData.Length = 0;
            Error err = QiWa.Compress.ZstdCompressor.Compress(ref this.RequestData, this.RawRequest.AsSpan());
            if (err.Err())
            {
                return (null, err);
            }
            this.HttpContext!.Response.Headers.ContentEncoding = "zstd";
            responseBytes = this.RequestData.AsSpan().ToArray();
        }
        else if (acceptEncoding.Contains("gzip"))
        {
            this.RequestData.Length = 0;
            var (compressed, err) = QiWa.Compress.GzipCompressor.Compress(this.RawRequest.AsSpan());
            if (err.Err())
            {
                return (null, err);
            }
            this.RequestData.Dispose();
            this.RequestData = compressed;
            this.HttpContext!.Response.Headers.ContentEncoding = "gzip";
            responseBytes = this.RequestData.AsSpan().ToArray();
        }
        else
        {
            responseBytes = this.RawRequest.AsSpan().ToArray();
        }
        return (responseBytes, default);
    }

    public async Task<(byte[]?, Error)> ReadRequest()
    {
        // 解析请求
        Debug.Assert(RawRequest.Data != null);
        Debug.Assert(RawRequest.Length == 0);
        int contentLength = (int)this.HttpContext!.Request!.ContentLength!.Value;
        RawRequest.Extend(contentLength);
        try
        {
            /*
            // 在全局初始化的地方设置接收超时的时间。此处无法设置接收超时的时间。
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            });
            */
            // ConfigureAwait(false) 不必切换回原来的上下文，可以提高性能
            await this.HttpContext.Request.Body.ReadExactlyAsync(RawRequest.Data, 0, contentLength, this.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            this.HttpContext.Response.StatusCode = 400;
            return (null, Error.WithLoc(code: 400, message: "Failed to read request body: " + ex.Message));
        }
        catch (OperationCanceledException)
        {
            this.HttpContext.Response.StatusCode = 408;
            return (null, Error.WithLoc(code: 408, message: "Request was canceled"));
        }
        RawRequest.Length = contentLength;
        // 处理压缩: 读 Content-Encoding，支持 gzip 和 zstd 压缩格式
        var contentEncoding = this.HttpContext.Request.Headers.ContentEncoding.ToString();
        byte[] reqBytes = RawRequest.AsSpan().ToArray();
        if (contentEncoding.Contains("zstd", StringComparison.CurrentCulture))
        {
            Debug.Assert(RequestData.Data != null);
            Debug.Assert(RequestData.Length == 0);
            // 重用 buffer，避免每次都 Rent
            Error err = ZstdCompressor.Uncompress(ref RequestData, RawRequest.AsSpan());
            if (err.Err())
            {
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress zstd body: " + err.Message));
            }
            reqBytes = RequestData.AsSpan().ToArray();
            // todo: 压缩和解压缩的数据进行上报
        }
        else if (contentEncoding.Contains("gzip", StringComparison.CurrentCulture))
        {
            var (gzipBuf, gzipErr) = GzipCompressor.Uncompress(RawRequest.AsSpan());
            if (gzipErr.Err())
            {
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress gzip body: " + gzipErr.Message));
            }
            RequestData.Dispose();
            RequestData = gzipBuf;
            reqBytes = RequestData.AsSpan().ToArray();
        }
        return (reqBytes, default);
    }

    public Error InitFromHttp(HttpContext httpContext)
    {
        this.HttpContext = httpContext;
        // 初始化 logger
        var tempLogger = Logger.Get();
        L = tempLogger.WithFields(
            Field.String("path"u8, httpContext.Request.Path.Value ?? ""),
            Field.String("method"u8, httpContext.Request.Method),
            Field.String("protocol"u8, httpContext.Request.Protocol),
            Field.String(
                (httpContext.Request.HttpContext.Connection.RemoteIpAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    ? "client_ipv6"u8 : "client_ipv4"u8,
                httpContext.Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "")
        // todo: request id, client ip
        );
        Logger.Return(tempLogger);
        // todo: metrics 上报
        // todo: tracing 相关的工作
        return default;
    }
}
