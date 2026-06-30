#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Clients;

using System.Net;
using QiWa.Common;
using QiWa.Compress;

/// <summary>
/// 以 bit 位的方式，允许用户配置发送请求时候的选项
/// </summary>
public enum RequestFlags : UInt64
{
    /// <summary>
    /// 无意义
    /// </summary>
    None = 0,

    /// <summary>
    /// 使用 JSON 格式编码
    /// </summary>
    UseJSON = 1,  // bit 0

    /// <summary>
    /// 使用 Protobuf 格式编码
    /// </summary>
    UseProtobuf = 2,  // bit 1

    /// <summary>
    /// 使用 zstd 压缩
    /// </summary>
    UseZstd = 4,  // bit 2

    /// <summary>
    /// 使用 gzip 压缩
    /// </summary>
    UseGzip = 8,  // bit 3

    /// <summary>
    /// 使用 post 请求
    /// </summary>
    UsePost = 16,  // bit 4

    /// <summary>
    /// 使用 GET 请求
    /// </summary>
    UseGet = 32,  // bit 5
}

/// <summary>
/// HttpClient 对象的错误码汇总
/// </summary>
public enum ClientErrorCode : uint
{
    Success = 0,
    ZstdCompressError = 1001,
    GzipCompressError = 1002,
    UnknownDataSerializeTypeError = 1003,
    HttpMethodNotSupportError = 1004,
    GrpcStatusError = 1005,
    BadGrpcResponseError = 1006,
    ZstdDecompressError = 1007,
    GzipDecompressError = 1008,
    CompressTypeNotSupportError = 1009,
    HttpRequestExceptionError = 1010,
    OperationCanceledExceptionError = 1011,
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

    private readonly SocketsHttpHandler handler;

    private readonly ClientProtocol protocol;

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
    /// <param name="baseAddress"></param>
    /// <param name="host"></param>
    public static HttpClientBase NewHttp1Client(ref HttpClientConfig cfg, Uri baseAddress, string host)
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
    public static HttpClientBase NewHttp2Client(ref HttpClientConfig cfg, Uri baseAddress, string host)
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
    public static HttpClientBase NewGrpcClient(ref HttpClientConfig cfg, Uri baseAddress, string host)
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

    // 发起 http1/http2 的请求
    public async Task<(RentedBuffer, Error)> HttpRequestAsync(string path, RentedBuffer reqBytes, UInt64 flags, CancellationToken cancellationToken)
    {
        if ((flags & (UInt64)(RequestFlags.UseZstd)) != 0)
        {
            var (compressed, err) = ZstdCompressor.Compress(reqBytes.AsSpan());
            if (err.Err())
            {
                return (default, Error.WithLoc((uint)ClientErrorCode.ZstdCompressError, $"ZstdCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            reqBytes = compressed;
        }
        else if ((flags & (UInt64)(RequestFlags.UseGzip)) != 0)
        {
            var (compressed, err) = GzipCompressor.Compress(reqBytes.AsSpan());
            if (err.Err())
            {
                return (default, Error.WithLoc((uint)ClientErrorCode.GzipCompressError, $"GzipCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            reqBytes = compressed;
        }
        var (output, ret) = await RequestAsync(path, reqBytes, flags, cancellationToken).ConfigureAwait(true);
        if (((flags & (UInt64)(RequestFlags.UseZstd)) != 0) || ((flags & (UInt64)(RequestFlags.UseGzip)) != 0))
        {
            // 压缩时，在函数内申请了非 GC 内存
            reqBytes.Dispose();  // 谁申请，谁负责释放
        }
        return (output, ret);
    }

    // 发起 grpc 的请求
    public async Task<(RentedBuffer, Error)> GrpcRequestAsync(string package, string service, string method, RentedBuffer reqBytes, UInt64 flags, CancellationToken cancellationToken)
    {
        string path = $"/{package}.{service}/{method}";
        flags |= (UInt64)(RequestFlags.UsePost);
        RentedBuffer dst;
        dst.Length = 5;
        Error err;
        if ((flags & (UInt64)(RequestFlags.UseZstd)) != 0)
        {
            dst = new(reqBytes.Length + 5);
            err = ZstdCompressor.Compress(ref dst, reqBytes.AsSpan());
            if (err.Err())
            {
                dst.Dispose();
                return (default, Error.WithLoc((uint)ClientErrorCode.ZstdCompressError, $"ZstdCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            dst.Data[0] = 1;
        }
        else if ((flags & (UInt64)(RequestFlags.UseGzip)) != 0)
        {
            (dst, err) = GzipCompressor.Compress(reqBytes.AsSpan(), 5);
            if (err.Err())
            {
                dst.Dispose();
                return (default, Error.WithLoc((uint)ClientErrorCode.GzipCompressError, $"GzipCompressor.Compress error: code={err.Code}, message={err.Message}"));
            }
            dst.Data[0] = 1;
        }
        else
        {
            dst = new(reqBytes.Length + 5);
            dst.Length = 5;
            dst.Append(reqBytes.AsSpan());  // 为了补充 grpc 的头部 5 字节，只能产生一次拷贝
            dst.Data[0] = 0;
        }
        UInt32 n = (UInt32)(dst.Length - 5);
        dst.Data[1] = (byte)(n >> 24);
        dst.Data[2] = (byte)((n >> 16) & 0xFF);
        dst.Data[3] = (byte)((n >> 8) & 0xFF);
        dst.Data[4] = (byte)(n & 0xFF);
        var (output, ret) = await this.RequestAsync(path, dst, flags, cancellationToken).ConfigureAwait(true);
        dst.Dispose();  // 必须释放非 GC 内存
        return (output, ret);
    }

    public (HttpRequestMessage?, Error) buildRequestMessage(string path, RentedBuffer reqBytes, UInt64 flags)
    {
        if (((flags & (UInt64)(RequestFlags.UseJSON)) == 0) && ((flags & (UInt64)(RequestFlags.UseProtobuf)) == 0))
        {
            return (null, Error.WithLoc((uint)ClientErrorCode.UnknownDataSerializeTypeError, "unknown data seriliaze type"));
        }
        HttpRequestMessage req = new()
        {
            RequestUri = new Uri(path, UriKind.Relative),
            Content = new ByteArrayContent(reqBytes.Data, 0, reqBytes.Length),
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
        // http method
        if ((flags & (UInt64)(RequestFlags.UsePost)) != 0)
        {
            req.Method = HttpMethod.Post;
        }
        else if ((flags & (UInt64)(RequestFlags.UseGet)) != 0)
        {
            req.Method = HttpMethod.Get;
        }
        else
        {
            req.Dispose();
            return (null, Error.WithLoc((uint)ClientErrorCode.HttpMethodNotSupportError, "http method not support"));
        }
        return (req, default);
    }

    internal static (RentedBuffer, Error) parseGrpcResponse(byte[] body, HttpResponseMessage rsp)
    {
        if (rsp.TrailingHeaders.Contains("grpc-status"))
        {
            string status = rsp.TrailingHeaders.GetValues("grpc-status").First();
            if (status != "0")
            {
                string msg = rsp.TrailingHeaders.GetValues("grpc-message").First();
                return (default, Error.WithLoc((uint)ClientErrorCode.GrpcStatusError, $"grpc-status={status}, grpc-message={msg}"));
            }
        }
        if (body.Length < 5)
        {
            return (default, Error.WithLoc((uint)ClientErrorCode.BadGrpcResponseError, $"bad grpc response: length={body.Length}"));
        }
        bool isCompressed = body[0] == 1;
        UInt32 len = ((UInt32)body[1]) << 24 |
            ((UInt32)body[2]) << 16 |
            ((UInt32)body[3]) << 8 |
            (UInt32)body[4];
        if (len + 5 != body.Length)
        {
            return (default, Error.WithLoc((uint)ClientErrorCode.BadGrpcResponseError, $"bad grpc response: length={body.Length}"));
        }
        body = body[5..];
        if (body.Length == 0)
        {
            return (default, default);
        }
        if (!isCompressed)
        {
            RentedBuffer output = new((int)len);
            output.Append(body.AsSpan());  // todo: 未来思考如何才能减少这里的拷贝
            // todo: 未来做成通过传入 Func 来反序列化
            return (output, default);
        }
        Error err;
        if (rsp.Headers.GetValues("grpc-encoding").Contains("zstd"))
        {
            RentedBuffer decompressed;
            (decompressed, err) = ZstdCompressor.Uncompress(body.AsSpan());
            if (err.Err())
            {
                return (default, Error.WithLoc((uint)ClientErrorCode.ZstdDecompressError, $"ZstdCompressor.Uncompress error: code={err.Code}, msg={err.Message}"));
            }
            return (decompressed, default);
        }
        else if (rsp.Headers.GetValues("grpc-encoding").Contains("gzip"))
        {
            RentedBuffer decompressed;
            (decompressed, err) = GzipCompressor.Uncompress(body.AsSpan());
            if (err.Err())
            {
                return (default, Error.WithLoc((uint)ClientErrorCode.GzipDecompressError, $"GzipCompressor.Uncompress error: code={err.Code}, msg={err.Message}"));
            }
            return (decompressed, default);
        }
        return (default, Error.WithLoc((uint)ClientErrorCode.CompressTypeNotSupportError, $"bad grpc compress type: {rsp.Headers.GetValues("grpc-encoding").ToString()}"));
    }

    // 假设上层已经压缩好了
    internal async Task<(RentedBuffer, Error)> RequestAsync(string path, RentedBuffer reqBytes, UInt64 flags, CancellationToken cancellationToken)
    {
        HttpRequestMessage? req;
        Error err;
        (req, err) = this.buildRequestMessage(path, reqBytes, flags);
        if (err.Err())
        {
            return (default, err);
        }
        using (req)
        {
            HttpResponseMessage? rsp = null;
            try
            {
                try
                {
                    rsp = await this.Client.SendAsync(req!, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(true);
                }
                catch (HttpRequestException ex)
                {
                    return (default, Error.WithLoc((uint)ClientErrorCode.HttpRequestExceptionError, "HttpRequestException:" + ex.Message));
                }
                catch (OperationCanceledException cancelEx)
                {
                    return (default, Error.WithLoc((uint)ClientErrorCode.OperationCanceledExceptionError, "OperationCanceledException:" + cancelEx.Message));
                }
                if (rsp.StatusCode != HttpStatusCode.OK)
                {
                    return (default, Error.WithLoc((uint)rsp.StatusCode, $"status code: {rsp.StatusCode}"));
                }
                byte[] body = await rsp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(true);
                if (this.protocol == ClientProtocol.Grpc)
                {
                    return parseGrpcResponse(body, rsp);
                }
                if (body.Length == 0)
                {
                    return (default, default);  // 没有 body, 返回空内容
                }
                if (rsp.Content.Headers.ContentEncoding.Contains("zstd"))
                {
                    RentedBuffer decompressed;
                    (decompressed, err) = ZstdCompressor.Uncompress(body.AsSpan());
                    if (err.Err())
                    {
                        return (default, Error.WithLoc((uint)ClientErrorCode.ZstdDecompressError, $"ZstdCompressor.Uncompress error: code={err.Code}, msg={err.Message}"));
                    }
                    return (decompressed, default);
                }
                if (rsp.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    RentedBuffer decompressed;
                    (decompressed, err) = GzipCompressor.Uncompress(body.AsSpan());
                    if (err.Err())
                    {
                        return (default, Error.WithLoc((uint)ClientErrorCode.GzipCompressError, $"GzipCompressor.Uncompress error: code={err.Code}, msg={err.Message}"));
                    }
                    return (decompressed, default);
                }
                RentedBuffer output = new(body.Length);
                output.Append(body);  // todo: 未来通过传入 Func 来直接反序列化，减少拷贝
                return (output, default);
            }
            finally
            {
                rsp?.Dispose();
            }
        }
    }
}
