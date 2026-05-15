#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.KestrelWrap;

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Microsoft.AspNetCore.Http;
using QiWa.Common;
using QiWa.Compress;
using QiWa.ConsoleLogger;
using QiWa.Metrics;

/// <summary>
/// 请求数据的编码类型
/// </summary>
public enum SerializeType
{
    Unknown = 0,
    JSON = 1,
    Protobuf = 2
}

/// <summary>
/// 压缩的类型
/// </summary>
public enum CompressType
{
    NotCompressed = 0,
    Gzip = 1,
    Zstd = 2
}

/// <summary>
/// 整个框架的基础的计数器
/// </summary>
public class Counters : QiWa.Metrics.MetricsBase, QiWa.Common.IResettable
{
    // 总请求数
    // 交给子类去累加
    [PrometheusMetric("http_request_total", "framework=\"QiWa\"")]
    public UInt64 HttpRequestTotal;

    // 错误：错误请求数
    // 交给子类去累加
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"bad request\"")]
    public UInt64 HttpBadRequestTotal;

    // 错误：初始化失败数
    //[PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"init error\"")]
    //public UInt64 InitErrorsTotal;

    // 错误: 读数据错误次数
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"red request error\"")]
    public UInt64 ReadRequestErrorsTotal;

    // 错误: 编码错误
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"encode error\"")]
    public UInt64 EncodeErrorsTotal;

    // 错误: 发送数据错误
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"send error\"")]
    public UInt64 SendErrorsTotal;

    // 错误: 找不到对应路径
    // 交给子类去累加
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"404\",err_type=\"not found error\"")]
    public UInt64 HttpNotFoundErrorsTotal;

    // 错误: json 解码错误
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"json decode error\"")]
    public UInt64 HttpJsonDecodeErrorsTotal;

    // 错误: protobuf 解码错误
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"protobuf decode error\"")]
    public UInt64 HttpProtobufDecodeErrorsTotal;

    // 错误: 未知编码格式错误
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"400\",err_type=\"format error\"")]
    public UInt64 HttpUnknownFormatErrorsTotal;

    //[PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"413\",err_type=\"content too large error\"")]
    //public UInt64 ContentTooLargeErrorsTotal;

    // 错误: 内部错误(未区分)
    [PrometheusMetric("errors_total", "framework=\"QiWa\",status_code=\"500\",err_type=\"internal error\"")]
    public UInt64 HttpInternalErrorsTotal;

    // BytesTotal: 原始的请求字节数
    [PrometheusMetric("http_raw_request_bytes_total", "framework=\"QiWa\"")]
    public UInt64 RawRequestBytesTotal;

    // BytesTotal: 解压缩后 zstd 请求字节数
    [PrometheusMetric("http_decompressed_request_bytes_total", "framework=\"QiWa\",compress_type=\"zstd\"")]
    public UInt64 ZstdDecompressedRequestBytesTotal;

    // BytesTotal: 解压缩后 gzip 请求字节数
    [PrometheusMetric("http_decompressed_request_bytes_total", "framework=\"QiWa\",compress_type=\"gzip\"")]
    public UInt64 GzipDecompressedRequestBytesTotal;

    // BytesTotal: 解压缩后的 json 请求字节数
    [PrometheusMetric("http_request_bytes_total", "framework=\"QiWa\",serialize_type=\"json\"")]
    public UInt64 JsonRequestBytesTotal;

    // BytesTotal: 解压缩后的 protobuf 请求字节数
    [PrometheusMetric("http_request_bytes_total", "framework=\"QiWa\",serialize_type=\"protobuf\"")]
    public UInt64 ProtobufRequestBytesTotal;

    // BytesTotal: 压缩前的 json 响应字节数
    [PrometheusMetric("http_response_bytes_total", "framework=\"QiWa\",serialize_type=\"json\"")]
    public UInt64 JsonResponseBytesTotal;

    // BytesTotal: 压缩前的 protobuf 响应字节数
    [PrometheusMetric("http_response_bytes_total", "framework=\"QiWa\",serialize_type=\"protobuf\"")]
    public UInt64 ProtobufResponseBytesTotal;

    [PrometheusMetric("compressed_http_response_bytes_total", "framework=\"QiWa\",compress_type=\"zstd\"")]
    public UInt64 ZstdResponseBytesTotal;

    [PrometheusMetric("compressed_http_response_bytes_total", "framework=\"QiWa\",compress_type=\"gzip\"")]
    public UInt64 GzipResponseBytesTotal;

    [PrometheusMetric("compressed_http_response_bytes_total", "framework=\"QiWa\",compress_type=\"no\"")]
    public UInt64 NotCompressedResponseBytesTotal;

    [PrometheusMetric("framework_latency_us", "framework=\"QiWa\"")]
    public LatencyHistogram Latency;

    public void Reset()
    {
        HttpRequestTotal = 0;
        HttpBadRequestTotal = 0;
        //InitErrorsTotal = 0;
        ReadRequestErrorsTotal = 0;
        EncodeErrorsTotal = 0;
        SendErrorsTotal = 0;
        HttpNotFoundErrorsTotal = 0;
        HttpJsonDecodeErrorsTotal = 0;
        HttpProtobufDecodeErrorsTotal = 0;
        HttpUnknownFormatErrorsTotal = 0;
        HttpInternalErrorsTotal = 0;
        RawRequestBytesTotal = 0;
        ZstdDecompressedRequestBytesTotal = 0;
        GzipDecompressedRequestBytesTotal = 0;
        JsonRequestBytesTotal = 0;
        ProtobufRequestBytesTotal = 0;
        JsonResponseBytesTotal = 0;
        ProtobufResponseBytesTotal = 0;
        ZstdResponseBytesTotal = 0;
        GzipResponseBytesTotal = 0;
        NotCompressedResponseBytesTotal = 0;
        Latency.Reset();
    }
}

/// <summary>
/// 注意：依赖日志库 ConsoleLogger，必须先初始化日志库
/// </summary>
public abstract class ContextBase
{
    /// <summary>
    /// 在代理服务器的场景，子类可能需要使用这个成员，获取原始的请求内容
    /// </summary>
    public RentedBuffer RawRequest = new RentedBuffer(ServerConfig.DefaultRequestSize);  // todo: 这个内存可能太大了
    private RentedBuffer RequestData = new RentedBuffer(ServerConfig.DefaultRequestSize);
    public HttpContext? HttpContext;
    public TaskLogger? L;
    public SerializeType SerializeType;
    public CompressType CompressType;
    public bool IsGrpc;
    public RentedBuffer ResponseBuffer = new RentedBuffer(ServerConfig.DefaultRequestSize);

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
            //dst.InitErrorsTotal += Interlocked.Read(ref c.InitErrorsTotal);
            dst.ReadRequestErrorsTotal += Interlocked.Read(ref c.ReadRequestErrorsTotal);
            dst.EncodeErrorsTotal += Interlocked.Read(ref c.EncodeErrorsTotal);
            dst.SendErrorsTotal += Interlocked.Read(ref c.SendErrorsTotal);
            dst.HttpNotFoundErrorsTotal += Interlocked.Read(ref c.HttpNotFoundErrorsTotal);
            dst.HttpJsonDecodeErrorsTotal += Interlocked.Read(ref c.HttpJsonDecodeErrorsTotal);
            dst.HttpProtobufDecodeErrorsTotal += Interlocked.Read(ref c.HttpProtobufDecodeErrorsTotal);
            dst.HttpUnknownFormatErrorsTotal += Interlocked.Read(ref c.HttpUnknownFormatErrorsTotal);
            dst.HttpInternalErrorsTotal += Interlocked.Read(ref c.HttpInternalErrorsTotal);
            dst.RawRequestBytesTotal += Interlocked.Read(ref c.RawRequestBytesTotal);
            dst.ZstdDecompressedRequestBytesTotal += Interlocked.Read(ref c.ZstdDecompressedRequestBytesTotal);
            dst.GzipDecompressedRequestBytesTotal += Interlocked.Read(ref c.GzipDecompressedRequestBytesTotal);
            dst.JsonRequestBytesTotal += Interlocked.Read(ref c.JsonRequestBytesTotal);
            dst.ProtobufRequestBytesTotal += Interlocked.Read(ref c.ProtobufRequestBytesTotal);
            dst.JsonResponseBytesTotal += Interlocked.Read(ref c.JsonResponseBytesTotal);
            dst.ProtobufResponseBytesTotal += Interlocked.Read(ref c.ProtobufResponseBytesTotal);
            dst.ZstdResponseBytesTotal += Interlocked.Read(ref c.ZstdResponseBytesTotal);
            dst.GzipResponseBytesTotal += Interlocked.Read(ref c.GzipResponseBytesTotal);
            dst.NotCompressedResponseBytesTotal += Interlocked.Read(ref c.NotCompressedResponseBytesTotal);
            dst.Latency.Sum(ref c.Latency);
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
        L = null;
        SerializeType = SerializeType.Unknown;
        CompressType = CompressType.NotCompressed;
        ResponseBuffer.Length = 0;
        IsGrpc = false;
    }

    public void Dispose()
    {
        Reset();
        RawRequest.Dispose();
        RequestData.Dispose();
        ResponseBuffer.Dispose();
        // 释放上下文资源，例如关闭连接、清理内存等
    }

    /// <summary>
    /// 对请求的参数进行基本的检查
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
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
        if (httpContext.Request.ContentType == null)
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "no ContentType");
        }
        // todo: 支持 application/octet-stream
        // todo: 支持 application/x-www-form-urlencoded
        if (!httpContext.Request.ContentType.StartsWith("application/json") &&
            !httpContext.Request.ContentType.StartsWith("application/protobuf"))
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "not support content type: " + httpContext.Request.ContentType);
        }
        // 如果存在 content-length
        if (httpContext.Request.ContentLength != null &&
            (httpContext.Request.ContentLength == 0 ||
            httpContext.Request.ContentLength > ServerConfig.MaxRequestSize))
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: $"Content-Length must be greater than 0 and less than {ServerConfig.MaxRequestSize} bytes");
        }
        return default;
    }

    public static Error GrpcValidate(HttpContext httpContext)
    {
        Debug.Assert(httpContext != null);
        Debug.Assert(httpContext.Request != null);
        // 验证请求数据是否合法，例如检查必填字段、字段格式等
        if (httpContext.Request.Method != HttpMethods.Post)
        {
            httpContext.Response.StatusCode = 405;  // Method Not Allowed
            return Error.WithLoc(code: 405, message: "Only POST method is allowed");
        }
        if (httpContext.Request.ContentType == null)
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "no ContentType");
        }
        if (!httpContext.Request.ContentType.StartsWith("application/grpc"))
        {
            httpContext.Response.StatusCode = 400;
            return Error.WithLoc(code: 400, message: "not support content type: " + httpContext.Request.ContentType);
        }
        // 如果存在 content-length
        if (httpContext.Request.ContentLength != null &&
            (httpContext.Request.ContentLength == 0 ||
            httpContext.Request.ContentLength > ServerConfig.MaxRequestSize))
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
        string contentType = this.HttpContext!.Request.ContentType!.ToString();
        if (contentType.StartsWith("application/json") || contentType.StartsWith("application/grpc+json"))
        {
            // 解析 JSON 数据到 Request 对象
            this.SerializeType = SerializeType.JSON;
            err = req.FromJSON(reqBytes);
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpJsonDecodeErrorsTotal);
                this.HttpContext!.Response.StatusCode = 400;
                return Error.WithLoc(code: 400, message: "Failed to parse JSON: " + err.Message);
            }
            Interlocked.Add(ref Counters.JsonRequestBytesTotal, (ulong)reqBytes.Length);
            return default;
        }
        if (contentType.StartsWith("application/protobuf") || contentType.StartsWith("application/grpc"))
        {
            this.SerializeType = SerializeType.Protobuf;
            err = req.FromProtobuf(reqBytes);
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpProtobufDecodeErrorsTotal);
                this.HttpContext!.Response.StatusCode = 400;
                return Error.WithLoc(code: 400, message: "Failed to parse Protobuf: " + err.Message);
            }
            Interlocked.Add(ref Counters.ProtobufRequestBytesTotal, (ulong)reqBytes.Length);
            return default;
        }
        this.SerializeType = SerializeType.Unknown;
        Interlocked.Increment(ref Counters.HttpUnknownFormatErrorsTotal);
        this.HttpContext!.Response.StatusCode = 400;
        return Error.WithLoc(code: 400, message: "Unsupported Content-Type: " + this.HttpContext!.Request.ContentType);
    }

    public (byte[]?, Error) Encode<TResponse>(ref readonly TResponse rsp) where TResponse : struct, QiWa.Common.IEncoder
    {
        Debug.Assert(ResponseBuffer.Length == 0);
        if (IsGrpc)
        {
            this.ResponseBuffer.Length = 5;  // 预留 grpc 的头长
        }
        switch (this.SerializeType)
        {
            case SerializeType.JSON:
                this.HttpContext!.Response.Headers.ContentType = IsGrpc ? "application/grpc+json" : "application/json";
                rsp.ToJSON(ref this.ResponseBuffer);
                Interlocked.Add(ref Counters.JsonResponseBytesTotal, (ulong)this.ResponseBuffer.Length);
                break;
            case SerializeType.Protobuf:
                this.HttpContext!.Response.Headers.ContentType = IsGrpc ? "application/grpc+proto" : "application/protobuf";
                rsp.ToProtobuf(ref this.ResponseBuffer);
                Interlocked.Add(ref Counters.ProtobufResponseBytesTotal, (ulong)this.ResponseBuffer.Length);
                break;
            default:
                throw new Exception("impossible error");
        }
        ReadOnlySpan<byte> data = IsGrpc ? this.ResponseBuffer.Data.AsSpan(5, this.ResponseBuffer.Length - 5) : this.ResponseBuffer.AsSpan();  // 序列化后的数据
        byte[]? responseBytes;
        string acceptEncoding = IsGrpc ? this.HttpContext!.Request.Headers["grpc-accept-encoding"].ToString() : this.HttpContext!.Request.Headers.AcceptEncoding.ToString();
        byte compressedFlag = 0;
        if (acceptEncoding.Contains("zstd"))
        {
            this.RequestData.Length = IsGrpc ? 5 : 0;  // 重用临时缓冲区
            Error err = QiWa.Compress.ZstdCompressor.Compress(ref this.RequestData, data);
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                return (null, err);
            }
            if (IsGrpc)
            {
                this.HttpContext!.Response.Headers["grpc-encoding"] = "zstd";
            }
            else
            {
                this.HttpContext!.Response.Headers.ContentEncoding = "zstd";
            }
            responseBytes = this.RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.ZstdResponseBytesTotal, (ulong)responseBytes.Length);
            compressedFlag = 1;
        }
        else if (acceptEncoding.Contains("gzip"))
        {
            var (compressed, err) = QiWa.Compress.GzipCompressor.Compress(data, reserve: IsGrpc ? 5 : 0);
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                return (null, err);
            }
            this.RequestData.Dispose();
            this.RequestData = compressed;
            if (IsGrpc)
            {
                this.HttpContext!.Response.Headers["grpc-encoding"] = "gzip";
            }
            else
            {
                this.HttpContext!.Response.Headers.ContentEncoding = "gzip";
            }
            responseBytes = this.RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.GzipResponseBytesTotal, (ulong)responseBytes.Length);
            compressedFlag = 1;
        }
        else
        {
            responseBytes = this.ResponseBuffer.AsSpan().ToArray();
            Interlocked.Add(ref Counters.NotCompressedResponseBytesTotal, (ulong)responseBytes.Length);
        }
        //
        if (IsGrpc)
        {
            // 填充 grpc 的头部
            UInt32 l = (UInt32)responseBytes.Length;
            responseBytes[4] = (byte)(l >> 24);
            responseBytes[3] = (byte)((l >> 16) & 0xFF);
            responseBytes[2] = (byte)((l >> 8) & 0xFF);
            responseBytes[1] = (byte)(l);
            responseBytes[0] = compressedFlag;
        }
        return (responseBytes, default);
    }

    public async Task<Error> ReadRequest()
    {
        Debug.Assert(RawRequest.Data != null);
        Debug.Assert(RawRequest.Length == 0);

        if (this.HttpContext!.Request.ContentLength.HasValue)
        {
            // Content-Length 已知：精确读取指定字节数
            int contentLength = (int)this.HttpContext.Request.ContentLength.Value;
            if (contentLength >= ServerConfig.MaxRequestSize)
            {
                //Interlocked.Increment(ref Counters.ContentTooLargeErrorsTotal);
                Interlocked.Increment(ref Counters.ReadRequestErrorsTotal);
                this.HttpContext.Response.StatusCode = 413;
                return Error.WithLoc(code: 413, message: "Content too large");
            }
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

                // todo: 删除，下面的代码是为了做实验
                //System.IO.Pipelines.PipeReader p = this.HttpContext.Request.BodyReader;
            }
            catch (EndOfStreamException ex)
            {
                Interlocked.Increment(ref Counters.ReadRequestErrorsTotal);
                this.HttpContext.Response.StatusCode = 400;
                return Error.WithLoc(code: 400, message: "Failed to read request body: " + ex.Message);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Counters.ReadRequestErrorsTotal);
                this.HttpContext.Response.StatusCode = 408;
                return Error.WithLoc(code: 408, message: "Request was canceled");
            }
            RawRequest.Length = contentLength;
            Interlocked.Add(ref Counters.RawRequestBytesTotal, (ulong)RawRequest.Length);
            return default;
            // todo: 提供一个注入层，便于做混淆层的实现
        }

        // 无 Content-Length：循环读取直到 EOF，适用于 HTTP/2 多 stream 场景
        const int chunkSize = 1024 * 4;

        int bytesRead;
        while (true)
        {
            if ((RawRequest.Data.Length - RawRequest.Length) < chunkSize)
            {
                RawRequest.Extend(chunkSize);
            }
            int toRead = RawRequest.Data.Length - RawRequest.Length;
            try
            {
                bytesRead = await this.HttpContext.Request.Body.ReadAsync(
                    RawRequest.Data.AsMemory(RawRequest.Length, toRead),
                    this.HttpContext.RequestAborted
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Counters.ReadRequestErrorsTotal);
                this.HttpContext.Response.StatusCode = 408;
                return Error.WithLoc(code: 408, message: "Request was canceled");
            }
            if (bytesRead == 0)
            {
                break;
            }
            RawRequest.Length += bytesRead;
            if (RawRequest.Length >= ServerConfig.MaxRequestSize)
            {
                Interlocked.Increment(ref Counters.ReadRequestErrorsTotal);
                this.HttpContext.Response.StatusCode = 413;
                return Error.WithLoc(code: 413, message: "Content too large");
            }
        }
        Interlocked.Add(ref Counters.RawRequestBytesTotal, (ulong)RawRequest.Length);
        return default;
    }

    public (byte[]?, Error) ParseGrpc()
    {
        if (RawRequest.Length <= 5)
        {
            Interlocked.Increment(ref Counters.HttpBadRequestTotal);
            this.HttpContext!.Response.StatusCode = 400;
            return (null, Error.WithLoc(400, "length too short"));
        }
        if (RawRequest.Data[0] != 0 && RawRequest.Data[0] != 1)
        {
            Interlocked.Increment(ref Counters.HttpBadRequestTotal);
            this.HttpContext!.Response.StatusCode = 400;
            return (null, Error.WithLoc(400, "compress type error"));
        }
        byte compressType = RawRequest.Data[0];
        var messageLength = BinaryPrimitives.ReadUInt32BigEndian(RawRequest.Data.AsSpan(1, 4));
        if (RawRequest.Length != 5 + (int)messageLength)
        {
            Interlocked.Increment(ref Counters.HttpBadRequestTotal);
            this.HttpContext!.Response.StatusCode = 400;
            return (null, Error.WithLoc(400, "grpc frame length mismatch"));
        }
        byte[] bytes = RawRequest.Data.AsSpan(5, (int)messageLength).ToArray();
        if (compressType == 0)
        {
            return (bytes, default);
        }
        // 进行解压缩操作
        var grpcEncoding = this.HttpContext!.Request.Headers["grpc-encoding"].ToString();
        if (grpcEncoding.Contains("zstd", StringComparison.CurrentCulture))
        {
            Debug.Assert(RequestData.Data != null);
            Debug.Assert(RequestData.Length == 0);
            // 重用 buffer，避免每次都 Rent
            Error err = ZstdCompressor.Uncompress(ref RequestData, bytes);
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress zstd body: " + err.Message));
            }
            bytes = RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.ZstdDecompressedRequestBytesTotal, (ulong)bytes.Length);
        }
        else if (grpcEncoding.Contains("gzip", StringComparison.CurrentCulture))
        {
            var (gzipBuf, gzipErr) = GzipCompressor.Uncompress(bytes);
            if (gzipErr.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress gzip body: " + gzipErr.Message));
            }
            RequestData.Dispose();
            RequestData = gzipBuf;
            bytes = RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.GzipDecompressedRequestBytesTotal, (ulong)bytes.Length);
        }
        else
        {
            Interlocked.Increment(ref Counters.HttpBadRequestTotal);
            this.HttpContext.Response.StatusCode = 400;
            return (null, Error.WithLoc(code: 400, message: "not supported compress type: " + grpcEncoding));
        }
        return (bytes, default);
    }

    public (byte[]?, Error) Decompress()
    {
        // 处理压缩: 读 Content-Encoding，支持 gzip 和 zstd 压缩格式
        var contentEncoding = this.HttpContext!.Request.Headers.ContentEncoding.ToString();
        byte[] reqBytes = RawRequest.AsSpan().ToArray();
        if (contentEncoding == "")
        {
            return (reqBytes, default);
        }
        if (contentEncoding.Contains("zstd", StringComparison.CurrentCulture))
        {
            Debug.Assert(RequestData.Data != null);
            Debug.Assert(RequestData.Length == 0);
            // 重用 buffer，避免每次都 Rent
            Error err = ZstdCompressor.Uncompress(ref RequestData, RawRequest.AsSpan());
            if (err.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress zstd body: " + err.Message));
            }
            reqBytes = RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.ZstdDecompressedRequestBytesTotal, (ulong)reqBytes.Length);
        }
        else if (contentEncoding.Contains("gzip", StringComparison.CurrentCulture))
        {
            var (gzipBuf, gzipErr) = GzipCompressor.Uncompress(RawRequest.AsSpan());
            if (gzipErr.Err())
            {
                Interlocked.Increment(ref Counters.HttpInternalErrorsTotal);
                this.HttpContext.Response.StatusCode = 400;
                return (null, Error.WithLoc(code: 400, message: "Failed to decompress gzip body: " + gzipErr.Message));
            }
            RequestData.Dispose();
            RequestData = gzipBuf;
            reqBytes = RequestData.AsSpan().ToArray();
            Interlocked.Add(ref Counters.GzipDecompressedRequestBytesTotal, (ulong)reqBytes.Length);
        }
        else
        {
            Interlocked.Increment(ref Counters.HttpBadRequestTotal);
            this.HttpContext.Response.StatusCode = 400;
            return (null, Error.WithLoc(code: 400, message: "not supported compress type: " + contentEncoding));
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
