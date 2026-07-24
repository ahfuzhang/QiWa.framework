#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Clients;

using System.Net;
using QiWa.Common;
using QiWa.Compress;

/// <summary>
/// 每个 method 对应着一个 client context 对象
/// </summary>
public class HttpClientContextBase : QiWa.Common.IResettable
{
    public const int GrpcHeaderSize = 5;  // grpc 的请求头部 5 字节
    public const int DefaultRequestSize = 1024 * 1;
    public const int DefaultResponseSize = 1024 * 4;

    // 提供客户端 context 的基类，提供几个基本的存储对象，便于重用和 0 alloc
    public RentedBuffer EncodedReqBuffer = new(DefaultRequestSize);
    public RentedBuffer CompressedReqBuffer = new(DefaultRequestSize);  // 用于压缩请求数据
    public RentedBuffer GrpcReqBuffer = new(DefaultRequestSize);
    public RentedBuffer ResponseBuffer = new(DefaultResponseSize);
    public RentedBuffer DecompressedRspBuffer = new(DefaultResponseSize * 2);
    public readonly Uri RelativePath;
    public readonly Uri? GrpcPath;
    public readonly string ApiPath = "";
    public readonly string Namespace = "";
    public readonly string Service = "";
    public readonly string Method = "";

    public HttpClientContextBase(string path, string packageName = "", string service = "", string method = "")
    {
        ApiPath = path;
        RelativePath = new Uri(path, UriKind.Relative);
        Namespace = packageName;
        Service = service;
        Method = method;
        if (!string.IsNullOrEmpty(Namespace) && !string.IsNullOrEmpty(Service) && !string.IsNullOrEmpty(Method))
        {
            string s = $"/{Namespace}.{Service}/{Method}";
            GrpcPath = new Uri(s, UriKind.Absolute);
        }
    }

    public void Reset()
    {
        EncodedReqBuffer.Length = 0;
        CompressedReqBuffer.Length = 0;
        GrpcReqBuffer.Length = 0;
        ResponseBuffer.Length = 0;
        DecompressedRspBuffer.Length = 0;
    }

    public Error Encode(Func<RentedBuffer, (RentedBuffer, Error)> encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        var (dst, err) = encoder(this.EncodedReqBuffer);
        if (err.Err())
        {
            // todo: 打日志，上报 metrics
            return err;
        }
        EncodedReqBuffer = dst;
        /*
          todo:
          注意！！！此处，使用者有可能故意构造一个内存泄露 —— 在函数内不使用输入的 RentedBuffer 对象，而是自己 new() 一个新的。
        */
        return default;
    }

    // 对请求 body 进行压缩
    public (byte[]?, Error) CompressRequest(byte[] reqBytes, UInt64 flags, bool isGrpc)
    {
        ArgumentNullException.ThrowIfNull(reqBytes);
        if ((flags & (UInt64)(RequestFlags.UseZstd)) != 0)
        {
            CompressedReqBuffer.Extend(reqBytes.Length + GrpcHeaderSize);  // grpc 的请求头部 5 字节
            if (isGrpc)
            {
                CompressedReqBuffer.Length = GrpcHeaderSize;
                CompressedReqBuffer.Data[0] = 1;  // 压缩标志
            }
            var err = ZstdCompressor.Compress(ref CompressedReqBuffer, reqBytes.AsSpan());
            if (err.Err())
            {
                return (null, Error.WithLoc((uint)ClientErrorCode.ZstdCompressError, $"ZstdCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            if (isGrpc)
            {
                UInt32 n = (UInt32)(CompressedReqBuffer.Length - GrpcHeaderSize);
                CompressedReqBuffer.Data[1] = (byte)(n >> 24);
                CompressedReqBuffer.Data[2] = (byte)((n >> 16) & 0xFF);
                CompressedReqBuffer.Data[3] = (byte)((n >> 8) & 0xFF);
                CompressedReqBuffer.Data[4] = (byte)(n & 0xFF);
            }
            return (CompressedReqBuffer.Data[..CompressedReqBuffer.Length], default);
        }
        // gzip
        if ((flags & (UInt64)(RequestFlags.UseGzip)) != 0)
        {
            CompressedReqBuffer.Extend(reqBytes.Length + GrpcHeaderSize);  // grpc 的请求头部 5 字节
            if (isGrpc)
            {
                CompressedReqBuffer.Length = GrpcHeaderSize;
                CompressedReqBuffer.Data[0] = 1;  // 压缩标志
            }
            var err = GzipCompressor.Compress(ref CompressedReqBuffer, reqBytes.AsSpan());
            if (err.Err())
            {
                return (null, Error.WithLoc((uint)ClientErrorCode.GzipCompressError, $"GzipCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            if (isGrpc)
            {
                UInt32 n = (UInt32)(CompressedReqBuffer.Length - GrpcHeaderSize);
                CompressedReqBuffer.Data[1] = (byte)(n >> 24);
                CompressedReqBuffer.Data[2] = (byte)((n >> 16) & 0xFF);
                CompressedReqBuffer.Data[3] = (byte)((n >> 8) & 0xFF);
                CompressedReqBuffer.Data[4] = (byte)(n & 0xFF);
            }
            return (CompressedReqBuffer.Data[..CompressedReqBuffer.Length], default);
        }
        // 不压缩
        if (isGrpc)
        {
            GrpcReqBuffer.Extend(reqBytes.Length + GrpcHeaderSize);
            GrpcReqBuffer.Data[0] = 0;
            UInt32 n = (UInt32)(reqBytes.Length);
            GrpcReqBuffer.Data[1] = (byte)(n >> 24);
            GrpcReqBuffer.Data[2] = (byte)((n >> 16) & 0xFF);
            GrpcReqBuffer.Data[3] = (byte)((n >> 8) & 0xFF);
            GrpcReqBuffer.Data[4] = (byte)(n & 0xFF);
            GrpcReqBuffer.Length = 5;
            GrpcReqBuffer.Append(reqBytes.AsSpan());  // grpc 的情况只能拷贝
            return (GrpcReqBuffer.Data[..GrpcReqBuffer.Length], default);
        }
        return (reqBytes, default);
    }

    // 用于读取 http body
    public async Task<Error> ReadResponseAsync(System.IO.Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int length = (int)stream.Length;
        ResponseBuffer.Extend(length);
        int totalRead = 0;
        while (totalRead < length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(ResponseBuffer.Data.AsMemory(totalRead, ResponseBuffer.Data.Length - totalRead), cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return Error.WithLoc((uint)ClientErrorCode.OperationCanceledExceptionError, "OperationCanceledException: read body timeout");
            }
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        ResponseBuffer.Length = totalRead;
        return default;
    }

    public (byte[]?, Error) Decompress(byte[] body, CompressType ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (ct == CompressType.Zstd)
        {
            DecompressedRspBuffer.Extend(body.Length * 2);  // 假定压缩比达到 50%
            Error err = ZstdCompressor.Uncompress(ref DecompressedRspBuffer, body.AsSpan());
            if (err.Err())
            {
                return (null, Error.WithLoc((uint)ClientErrorCode.ZstdDecompressError, $"code={err.Code}, message={err.Message}"));
            }
            return (DecompressedRspBuffer.Data[..DecompressedRspBuffer.Length], default);
        }
        if (ct == CompressType.Gzip)
        {
            DecompressedRspBuffer.Extend(body.Length * 2);  // 假定压缩比达到 50%
            Error err = GzipCompressor.Uncompress(ref DecompressedRspBuffer, body.AsSpan());
            if (err.Err())
            {
                return (null, Error.WithLoc((uint)ClientErrorCode.GzipDecompressError, $"code={err.Code}, message={err.Message}"));
            }
            return (DecompressedRspBuffer.Data[..DecompressedRspBuffer.Length], default);
        }
        return (null, Error.WithLoc((uint)ClientErrorCode.CompressTypeNotSupportError, "unknown compress type"));
    }

    public (byte[]?, Error) GrpcDecompress(CompressType ct)
    {
        byte[] body = this.ResponseBuffer.Data[..ResponseBuffer.Length];
        bool isCompressed = body[0] == 1;
        UInt32 len = ((UInt32)body[1]) << 24 |
            ((UInt32)body[2]) << 16 |
            ((UInt32)body[3]) << 8 |
            (UInt32)body[4];
        if (len + 5 != body.Length)
        {
            return (null, Error.WithLoc((uint)ClientErrorCode.BadGrpcResponseError, $"bad grpc response: length={body.Length}"));
        }
        body = body[5..];
        if (body.Length == 0)
        {
            return (body, default);
        }
        if (!isCompressed)
        {
            return (body, default);
        }
        return Decompress(body, ct);
    }
}

/// <summary>
/// Provides the base implementation for HTTP client wrappers.
/// </summary>
public class HttpClientBase : IHttpClient, IDisposable
{
    /// <summary>
    /// Stores the configured HTTP client instance used by subclasses.
    /// </summary>
    public HttpClient Client;

    private readonly SocketsHttpHandler handler;  // 每个 client 对象都有自己的 socket 选项

    private readonly ClientProtocol protocol;  // 协议类型

    /// <summary>
    /// Tracks whether managed resources have already been released.
    /// </summary>
    private bool disposed;

    internal HttpClientBase(SocketsHttpHandler handler, HttpClient client, ClientProtocol protocol)
    {
        this.handler = handler;
        this.Client = client;
        this.protocol = protocol;
    }

    /// <summary>
    /// Initializes an HTTP/1 client from the QiWa HTTP client configuration.
    /// </summary>
    /// <param name="cfg">The HTTP client configuration used to create the underlying handler.</param>
    /// <param name="baseAddress">可以使用 ip:port 或者域名</param>
    /// <param name="host">当使用 ip:port 的格式时，可以通过提供 host 来配置正确的 Host header</param>
    public static HttpClientBase NewHttp1Client(in HttpClientConfig cfg, Uri baseAddress, string host)
    {
        SocketsHttpHandler socketHandler = new()
        {
            // tcp options
            PooledConnectionLifetime = cfg.Tcp.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = cfg.Tcp.PooledConnectionIdleTimeout,
            ConnectTimeout = cfg.Tcp.ConnectTimeout,
            // all http options
            UseCookies = cfg.Http.UseCookies,
            AllowAutoRedirect = cfg.Http.AllowAutoRedirect,
            MaxResponseDrainSize = cfg.Http.MaxResponseDrainSize,
            ResponseDrainTimeout = cfg.Http.ResponseDrainTimeout,
            MaxResponseHeadersLength = cfg.Http.MaxResponseHeadersLengthKb,
            // http1 options
            MaxConnectionsPerServer = cfg.Http1.MaxConnectionsPerServer,
        };
        HttpClient client = new HttpClient(socketHandler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = cfg.Http.Timeout,
            MaxResponseContentBufferSize = cfg.Http.MaxResponseContentBufferSize,
            BaseAddress = baseAddress,
        };
        if (!string.IsNullOrEmpty(host))
        {
            client.DefaultRequestHeaders.Host = host;
        }
        client.DefaultRequestHeaders.Add("Accept-Encoding", "zstd,gzip");
        return new HttpClientBase(socketHandler, client, ClientProtocol.Http1);
    }

    // 构造 http2 的客户端
    public static HttpClientBase NewHttp2Client(in HttpClientConfig cfg, Uri baseAddress, string host)
    {
        SocketsHttpHandler socketHandler = new()
        {
            // tcp options
            PooledConnectionLifetime = cfg.Tcp.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = cfg.Tcp.PooledConnectionIdleTimeout,
            ConnectTimeout = cfg.Tcp.ConnectTimeout,
            // all http options
            UseCookies = cfg.Http.UseCookies,
            AllowAutoRedirect = cfg.Http.AllowAutoRedirect,
            MaxResponseDrainSize = cfg.Http.MaxResponseDrainSize,
            ResponseDrainTimeout = cfg.Http.ResponseDrainTimeout,
            MaxResponseHeadersLength = cfg.Http.MaxResponseHeadersLengthKb,
            // http2 options
            EnableMultipleHttp2Connections = cfg.Http2.EnableMultipleHttp2Connections,
            KeepAlivePingDelay = cfg.Http2.KeepAlivePingDelay,
            KeepAlivePingTimeout = cfg.Http2.KeepAlivePingTimeout,
            KeepAlivePingPolicy = cfg.Http2.KeepAlivePingPolicy,
            InitialHttp2StreamWindowSize = cfg.Http2.InitialHttp2StreamWindowSize,
            MaxConnectionsPerServer = cfg.Http2.MaxConnectionsPerServer,
        };
        HttpClient client = new HttpClient(socketHandler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = cfg.Http.Timeout,
            MaxResponseContentBufferSize = cfg.Http.MaxResponseContentBufferSize,
            BaseAddress = baseAddress,
        };
        if (!string.IsNullOrEmpty(host))
        {
            client.DefaultRequestHeaders.Host = host;
        }
        client.DefaultRequestHeaders.Add("Accept-Encoding", "zstd,gzip");
        return new HttpClientBase(socketHandler, client, ClientProtocol.Http2);
    }

    // 构造 grpc 的客户端
    public static HttpClientBase NewGrpcClient(in HttpClientConfig cfg, Uri baseAddress, string host)
    {
        SocketsHttpHandler socketHandler = new()
        {
            // tcp options
            PooledConnectionLifetime = cfg.Tcp.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = cfg.Tcp.PooledConnectionIdleTimeout,
            ConnectTimeout = cfg.Tcp.ConnectTimeout,
            // all http options
            UseCookies = cfg.Http.UseCookies,
            AllowAutoRedirect = cfg.Http.AllowAutoRedirect,
            MaxResponseDrainSize = cfg.Http.MaxResponseDrainSize,
            ResponseDrainTimeout = cfg.Http.ResponseDrainTimeout,
            MaxResponseHeadersLength = cfg.Http.MaxResponseHeadersLengthKb,
            // http2 options
            EnableMultipleHttp2Connections = cfg.Http2.EnableMultipleHttp2Connections,
            KeepAlivePingDelay = cfg.Http2.KeepAlivePingDelay,
            KeepAlivePingTimeout = cfg.Http2.KeepAlivePingTimeout,
            KeepAlivePingPolicy = cfg.Http2.KeepAlivePingPolicy,
            InitialHttp2StreamWindowSize = cfg.Http2.InitialHttp2StreamWindowSize,
            MaxConnectionsPerServer = cfg.Http2.MaxConnectionsPerServer,
        };
        HttpClient client = new HttpClient(socketHandler, disposeHandler: false)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = cfg.Http.Timeout,
            MaxResponseContentBufferSize = cfg.Http.MaxResponseContentBufferSize,
            BaseAddress = baseAddress,
        };
        if (!string.IsNullOrEmpty(host))
        {
            client.DefaultRequestHeaders.Host = host;
        }
        client.DefaultRequestHeaders.Add("Accept-Encoding", "zstd,gzip");
        return new HttpClientBase(socketHandler, client, ClientProtocol.Grpc);
    }

    /// <summary>
    /// Releases the underlying HTTP client and its handler.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases managed resources owned by this base client.
    /// </summary>
    /// <param name="disposing">True when managed resources should be released.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposed)
        {
            return;
        }

        if (disposing)
        {
            Client.Dispose();
            handler.Dispose();
        }

        disposed = true;
    }

    internal static readonly System.Net.Http.Headers.MediaTypeHeaderValue ContentTypeOfProtobuf = new("application/protobuf");
    internal static readonly System.Net.Http.Headers.MediaTypeHeaderValue ContentTypeOfJSON = new("application/json");
    internal static readonly System.Net.Http.Headers.MediaTypeHeaderValue ContentTypeOfGrpc = new("application/grpc");
    internal static readonly System.Net.Http.Headers.MediaTypeHeaderValue ContentTypeOfGrpcJSON = new("application/grpc+json");

    // 发送 POST 请求
    internal async Task<(HttpResponseMessage?, Error)> sendAsync(HttpClientContextBase ctx, byte[] reqBytes, UInt64 flags, Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        HttpRequestMessage? req;
        Error err;
        bool isGrpc = this.protocol == ClientProtocol.Grpc;
        (req, err) = this.buildRequestMessage(reqBytes!, flags, isGrpc ? ctx.GrpcPath! : ctx.RelativePath);
        if (err.Err())
        {
            return (null, err);
        }
        if (headers != null && headers.Count > 0)
        {
            foreach (var item in headers)
            {
                req!.Headers.Add(item.Key, item.Value);
            }
        }
        using (req)
        {
            HttpResponseMessage? rsp = null;
            try
            {
                // todo: 计时，metrics 上报
                rsp = await this.Client.SendAsync(req!, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(true);
                if (rsp!.StatusCode != HttpStatusCode.OK)
                {
                    rsp.Dispose();
                    err = Error.WithLoc((uint)rsp.StatusCode, $"status code: {rsp.StatusCode}");
                    return (null, err);
                }
                return (rsp, default);
            }
            catch (HttpRequestException ex)
            {
                err = Error.WithLoc((uint)ClientErrorCode.HttpRequestExceptionError, "HttpRequestException:" + ex.Message);
            }
            catch (OperationCanceledException cancelEx)
            {
                err = Error.WithLoc((uint)ClientErrorCode.OperationCanceledExceptionError, "OperationCanceledException:" + cancelEx.Message);
            }
            return (null, err);
        }
    }

    // 发送 GET 请求
    internal async Task<(HttpResponseMessage?, Error)> getAsync(Uri path, Dictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        HttpResponseMessage? rsp;
        Error err;
        using HttpRequestMessage req = new()
        {
            RequestUri = path,
            Method = HttpMethod.Get,
        };
        if (headers != null && headers.Count > 0)
        {
            foreach (var item in headers)
            {
                req.Headers.Add(item.Key, item.Value);
            }
        }
        try
        {
            // todo: 计时，metrics 上报
            rsp = await this.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(true);
            if (rsp!.StatusCode != HttpStatusCode.OK)
            {
                rsp.Dispose();
                err = Error.WithLoc((uint)rsp.StatusCode, $"status code: {rsp.StatusCode}");
                return (null, err);
            }
            return (rsp, default);
        }
        catch (HttpRequestException ex)
        {
            err = Error.WithLoc((uint)ClientErrorCode.HttpRequestExceptionError, "HttpRequestException:" + ex.Message);
        }
        catch (OperationCanceledException cancelEx)
        {
            err = Error.WithLoc((uint)ClientErrorCode.OperationCanceledExceptionError, "OperationCanceledException:" + cancelEx.Message);
        }
        return (null, err);
    }

    /// <summary>
    /// 发出请求
    /// </summary>
    /// <param name="ctx"> client context 对象</param>
    /// <param name="encoder">编码器</param>
    /// <param name="decoder">解码器</param>
    /// <param name="flags">标志, bit flags</param>
    /// <param name="headers">用户自定 header</param>
    /// <param name="cancellationToken">超时控制</param>
    /// <returns></returns>
    public async Task<Error> RequestAsync(
        HttpClientContextBase ctx,
        Func<RentedBuffer, (RentedBuffer, Error)> encoder,  // 编码器
        Func<byte[], string, Error> decoder,  // 解码器
        UInt64 flags,
        Dictionary<string, string>? headers,  // 自定义 header eg: application/octet-stream 的支持
        CancellationToken cancellationToken
        )
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(decoder);
        bool isGrpc = this.protocol == ClientProtocol.Grpc;
        bool useGet = (flags & (UInt64)(RequestFlags.UseGet)) != 0;
        if (useGet && useGet)
        {
            return Error.WithLoc((uint)ClientErrorCode.ParamError, "GRPC must use post");
        }
        HttpResponseMessage? rsp;
        Error err;
        if (useGet)
        {
            (rsp, err) = await getAsync(ctx.RelativePath, headers, cancellationToken).ConfigureAwait(true);
        }
        else
        {
            err = ctx.Encode(encoder);
            if (err.Err())
            {
                return err;
            }
            byte[]? reqBytes;
            (reqBytes, err) = ctx.CompressRequest(ctx.EncodedReqBuffer.Data[..ctx.EncodedReqBuffer.Length], flags, isGrpc);
            if (err.Err())
            {
                return err;
            }
            (rsp, err) = await sendAsync(ctx, reqBytes!, flags, headers, cancellationToken).ConfigureAwait(true);
        }
        if (err.Err())
        {
            return err;
        }
        using var _ = rsp;
        // read body
        var stream = await rsp!.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        long bodyLength = stream.Length;
        err = await ctx.ReadResponseAsync(stream, cancellationToken).ConfigureAwait(true);
        if (err.Err())
        {
            return err;
        }
        byte[]? rspBytes;
        if (this.protocol == ClientProtocol.Grpc)
        {
            (rspBytes, err) = parseGrpcResponse(ctx, bodyLength, rsp);
            if (err.Err())
            {
                return err;
            }
            return decoder(rspBytes!, rsp.Content.Headers.ContentType?.MediaType ?? "");
        }
        // http 协议
        CompressType ct = CompressType.NotCompressed;
        if (rsp.Content.Headers.ContentEncoding.Contains("zstd"))
        {
            ct = CompressType.Zstd;
        }
        else if (rsp.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            ct = CompressType.Gzip;
        }
        else if (rsp.Content.Headers.ContentEncoding.Count > 0)
        {
            err = Error.WithLoc((uint)ClientErrorCode.CompressTypeNotSupportError, $"unknown comress type: {rsp.Content.Headers.ContentEncoding}");
            return err;
        }
        (rspBytes, err) = ctx.Decompress(ctx.ResponseBuffer.Data[..ctx.ResponseBuffer.Length], ct);
        if (err.Err())
        {
            return err;
        }
        return decoder(rspBytes!, rsp.Content.Headers.ContentType?.MediaType ?? "");
    }

    public (HttpRequestMessage?, Error) buildRequestMessage(byte[] reqBytes, UInt64 flags, Uri path)
    {
        if (((flags & (UInt64)(RequestFlags.UseJSON)) == 0) && ((flags & (UInt64)(RequestFlags.UseProtobuf)) == 0))
        {
            return (null, Error.WithLoc((uint)ClientErrorCode.UnknownDataSerializeTypeError, "unknown data seriliaze type"));
        }
        HttpRequestMessage req = new()
        {
            RequestUri = path,
            Content = new ByteArrayContent(reqBytes),
            Method = HttpMethod.Post,
        };
        // 数据序列化方式
        if (this.protocol == ClientProtocol.Grpc)
        {
            if ((flags & (UInt64)(RequestFlags.UseJSON)) != 0)
            {
                req.Content.Headers.ContentType = ContentTypeOfGrpcJSON;
            }
            else if ((flags & (UInt64)(RequestFlags.UseProtobuf)) != 0)
            {
                req.Content.Headers.ContentType = ContentTypeOfGrpc;
            }
            req.Content.Headers.Add("grpc-accept-encoding", "zstd,gzip");
        }
        else
        {
            if ((flags & (UInt64)(RequestFlags.UseJSON)) != 0)
            {
                req.Content.Headers.ContentType = ContentTypeOfJSON;
            }
            else if ((flags & (UInt64)(RequestFlags.UseProtobuf)) != 0)
            {
                req.Content.Headers.ContentType = ContentTypeOfProtobuf;
            }
        }
        // 压缩方式
        if ((flags & (UInt64)(RequestFlags.UseZstd)) != 0)
        {
            req.Content.Headers.ContentEncoding.Add("zstd");
            if (this.protocol == ClientProtocol.Grpc)
            {
                req.Content.Headers.Add("grpc-encoding", "zstd");
            }
        }
        else if ((flags & (UInt64)(RequestFlags.UseGzip)) != 0)
        {
            req.Content.Headers.ContentEncoding.Add("gzip");
            if (this.protocol == ClientProtocol.Grpc)
            {
                req.Content.Headers.Add("grpc-encoding", "gzip");
            }
        }
        return (req, default);
    }

    internal static (byte[]?, Error) parseGrpcResponse(HttpClientContextBase ctx, long bodyLength, HttpResponseMessage rsp)
    {
        Error err;
        if (rsp.TrailingHeaders.Contains("grpc-status"))
        {
            string status = rsp.TrailingHeaders.GetValues("grpc-status").First();
            if (status != "0")
            {
                string msg = rsp.TrailingHeaders.GetValues("grpc-message").First();
                err = Error.WithLoc((uint)ClientErrorCode.GrpcStatusError, $"grpc-status={status}, grpc-message={msg}");
                return (null, err);
            }
        }
        if (bodyLength < 5)
        {
            err = Error.WithLoc((uint)ClientErrorCode.BadGrpcResponseError, $"bad grpc response: length={bodyLength}");
            return (null, err);
        }
        CompressType ct = CompressType.NotCompressed;
        if (rsp.Headers.GetValues("grpc-encoding").Contains("zstd"))
        {
            ct = CompressType.Zstd;
        }
        else if (rsp.Headers.GetValues("grpc-encoding").Contains("gzip"))
        {
            ct = CompressType.Gzip;
        }
        else if (rsp.Headers.GetValues("grpc-encoding").Any())
        {
            err = Error.WithLoc((uint)ClientErrorCode.BadGrpcResponseError, $"bad grpc response: length={bodyLength}");
            return (null, err);
        }
        byte[]? rspBytes;
        (rspBytes, err) = ctx.GrpcDecompress(ct);
        if (err.Err())
        {
            return (null, err);
        }
        return (rspBytes, default);
    }
}
