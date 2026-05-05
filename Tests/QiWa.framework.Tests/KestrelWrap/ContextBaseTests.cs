// 意图：为 ContextBase 生成高覆盖率测试，覆盖计数器、校验、读包、解压、编解码、日志初始化，
//      并使用随机端口的 Kestrel 验证真实 chunked 请求读取路径，同时保证测试结束后释放所有资源。
#pragma warning disable CS1591
namespace Tests.KestrelWrap;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QiWa.Common;
using QiWa.Compress;
using QiWa.ConsoleLogger;
using QiWa.KestrelWrap;
using Xunit;

[Collection("ConsoleLogger")]
public class ContextBaseTests
{
    private static readonly FieldInfo RequestDataField = typeof(ContextBase)
        .GetField("RequestData", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ThreadLocalCountersField = typeof(ContextBase)
        .GetField("_threadLocal", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly FieldInfo TaskLoggerPrefixField = typeof(TaskLogger)
        .GetField("prefix", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void CountersReset_ClearsAllMetricFields()
    {
        var counters = new Counters();
        foreach (FieldInfo field in GetCounterFields())
        {
            field.SetValue(counters, 123UL);
        }

        counters.Reset();

        AssertAllCounterFieldsAreZero(counters);
    }

    [Fact]
    public void CountersReset_IsIdempotentAndAllowsReuse()
    {
        var counters = new Counters();
        ulong seed = 1;
        foreach (FieldInfo field in GetCounterFields())
        {
            field.SetValue(counters, seed++);
        }

        counters.Reset();
        AssertAllCounterFieldsAreZero(counters);

        counters.HttpRequestTotal = 11;
        counters.HttpInternalErrorsTotal = 22;
        counters.ZstdResponseBytesTotal = 33;

        counters.Reset();
        counters.Reset();

        AssertAllCounterFieldsAreZero(counters);
    }

    [Fact]
    public void SumCounters_AggregatesThreadLocalValuesAndExistingDestination()
    {
        ResetAllCounters();
        try
        {
            ContextBase.Counters.HttpRequestTotal = 2;
            ContextBase.Counters.JsonRequestBytesTotal = 3;

            var worker = new Thread(() =>
            {
                ContextBase.Counters.Reset();
                ContextBase.Counters.HttpRequestTotal = 5;
                ContextBase.Counters.GzipResponseBytesTotal = 7;
            });
            worker.Start();
            worker.Join();

            Counters snapshot = ContextBase.SumCounters(null);
            Assert.Equal(7UL, snapshot.HttpRequestTotal);
            Assert.Equal(3UL, snapshot.JsonRequestBytesTotal);
            Assert.Equal(7UL, snapshot.GzipResponseBytesTotal);

            var existing = new Counters { HttpRequestTotal = 11 };
            Counters appended = ContextBase.SumCounters(existing);
            Assert.Same(existing, appended);
            Assert.Equal(18UL, appended.HttpRequestTotal);
        }
        finally
        {
            ResetAllCounters();
        }
    }

    [Fact]
    public void Reset_ClearsStateAndReturnsLoggerToPool()
    {
        using var ctx = new ContextBaseTestContext();
        DefaultHttpContext httpContext = CreateHttpContext(bodyBytes: Utf8("abc"), contentLength: 3);

        ctx.InitFromHttp(httpContext);
        SetRentedBuffer(ref ctx.RawRequest, Utf8("abc"));
        ctx.SerializeType = SerializeType.JSON;
        ctx.CompressType = CompressType.Gzip;
        SetRentedBuffer(ref ctx.ResponseBuffer, Utf8("ok"));

        ctx.Reset();

        Assert.Equal(0, ctx.RawRequest.Length);
        Assert.Equal(0, ctx.ResponseBuffer.Length);
        Assert.Null(ctx.HttpContext);
        Assert.Null(ctx.L);
        Assert.Equal(SerializeType.Unknown, ctx.SerializeType);
        Assert.Equal(CompressType.NotCompressed, ctx.CompressType);
    }

    [Fact]
    public void Reset_WhenLoggerIsNull_ClearsPrivateRequestDataAndSupportsRepeatedCalls()
    {
        using var ctx = new ContextBaseTestContext();
        SetRentedBuffer(ref ctx.RawRequest, Utf8("raw"));
        SetPrivateRequestData(ctx, Utf8("req"));
        SetRentedBuffer(ref ctx.ResponseBuffer, Utf8("rsp"));
        ctx.HttpContext = CreateHttpContext(bodyBytes: Utf8("body"), contentLength: 4);
        ctx.SerializeType = SerializeType.Protobuf;
        ctx.CompressType = CompressType.Zstd;

        ctx.Reset();
        ctx.Reset();

        Assert.Equal(0, ctx.RawRequest.Length);
        Assert.Equal(0, GetPrivateRequestData(ctx).Length);
        Assert.Equal(0, ctx.ResponseBuffer.Length);
        Assert.Null(ctx.HttpContext);
        Assert.Null(ctx.L);
        Assert.Equal(SerializeType.Unknown, ctx.SerializeType);
        Assert.Equal(CompressType.NotCompressed, ctx.CompressType);
    }

    [Fact]
    public void Dispose_DisposesAllBuffers()
    {
        var ctx = new ContextBaseTestContext();

        ctx.Dispose();

        Assert.Null(ctx.RawRequest.Data);
        Assert.Null(ctx.ResponseBuffer.Data);
        Assert.Null(GetPrivateRequestData(ctx).Data);
    }

    [Fact]
    public void Validate_RejectsInvalidRequests()
    {
        var cases = new (DefaultHttpContext Context, int StatusCode, string Message)[]
        {
            (CreateHttpContext(method: "GET", bodyBytes: Utf8("x"), contentLength: 1), 405, "Only POST method is allowed"),
            (CreateHttpContext(contentType: null, bodyBytes: Utf8("x"), contentLength: 1), 400, "no ContentType"),
            (CreateHttpContext(contentType: "text/plain", bodyBytes: Utf8("x"), contentLength: 1), 400, "not support content type"),
            (CreateHttpContext(contentLength: null), 400, "Content-Length must be greater than 0"),
            (CreateHttpContext(contentLength: 0), 400, "Content-Length must be greater than 0"),
            (CreateHttpContext(contentLength: ServerConfig.MaxRequestSize + 1L), 400, "Content-Length must be greater than 0"),
        };

        foreach (var testCase in cases)
        {
            Error err = ContextBase.Validate(testCase.Context);
            Assert.True(err.Err());
            Assert.Equal((uint)testCase.StatusCode, err.Code);
            Assert.Equal(testCase.StatusCode, testCase.Context.Response.StatusCode);
            Assert.Contains(testCase.Message, err.Message);
        }
    }

    [Fact]
    public void Validate_AcceptsSupportedPostRequest()
    {
        DefaultHttpContext httpContext = CreateHttpContext(bodyBytes: Utf8("{}"), contentLength: 2);

        Error err = ContextBase.Validate(httpContext);

        Assert.False(err.Err());
    }

    [Fact]
    public void Decode_JsonSuccess_UpdatesRequestStateAndMetrics()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentType: "application/json"));
        var request = new TestRequestMessage();
        byte[] payload = Utf8("{\"id\":1}");

        Error err = ctx.Decode(payload, ref request);

        Assert.False(err.Err());
        Assert.Equal(SerializeType.JSON, ctx.SerializeType);
        Assert.Equal("json", request.LastDecoder);
        Assert.Equal(payload, request.LastPayload);
        Assert.Equal((ulong)payload.Length, SnapshotCounters().JsonRequestBytesTotal);
    }

    [Fact]
    public void Decode_JsonFailure_Returns400AndIncrementsErrorMetric()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentType: "application/json"));
        var request = new TestRequestMessage { FailJson = true };

        Error err = ctx.Decode(Utf8("{broken:true}"), ref request);

        Assert.True(err.Err());
        Assert.Equal(400U, err.Code);
        Assert.Equal(400, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().HttpJsonDecodeErrorsTotal);
    }

    [Fact]
    public void Decode_ProtobufSuccess_UpdatesRequestStateAndMetrics()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentType: "application/protobuf"));
        var request = new TestRequestMessage();
        byte[] payload = new byte[] { 1, 2, 3, 4 };

        Error err = ctx.Decode(payload, ref request);

        Assert.False(err.Err());
        Assert.Equal(SerializeType.Protobuf, ctx.SerializeType);
        Assert.Equal("protobuf", request.LastDecoder);
        Assert.Equal(payload, request.LastPayload);
        Assert.Equal((ulong)payload.Length, SnapshotCounters().ProtobufRequestBytesTotal);
    }

    [Fact]
    public void Decode_ProtobufFailure_Returns400AndIncrementsErrorMetric()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentType: "application/protobuf"));
        var request = new TestRequestMessage { FailProtobuf = true };

        Error err = ctx.Decode(new byte[] { 9, 8, 7 }, ref request);

        Assert.True(err.Err());
        Assert.Equal(400U, err.Code);
        Assert.Equal(400, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().HttpProtobufDecodeErrorsTotal);
    }

    [Fact]
    public void Decode_UnsupportedContentType_Returns400AndIncrementsErrorMetric()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentType: "application/xml"));
        var request = new TestRequestMessage();

        Error err = ctx.Decode(Utf8("<x/>"), ref request);

        Assert.True(err.Err());
        Assert.Equal(400U, err.Code);
        Assert.Equal(SerializeType.Unknown, ctx.SerializeType);
        Assert.Equal(400, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().HttpUnknownFormatErrorsTotal);
    }

    [Fact]
    public void Encode_WritesUncompressedJsonResponseAndMetrics()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(acceptEncoding: ""));
        ctx.SerializeType = SerializeType.JSON;
        var response = new TestResponseMessage
        {
            JsonPayload = Utf8("{\"ok\":true}"),
            ProtobufPayload = new byte[] { 1 },
        };

        (byte[]? bytes, Error err) = ctx.Encode(ref response);

        Assert.False(err.Err());
        Assert.Equal(response.JsonPayload, bytes);
        Assert.Equal("application/json", ctx.HttpContext!.Response.Headers.ContentType.ToString());
        Assert.Equal((ulong)response.JsonPayload.Length, SnapshotCounters().JsonResponseBytesTotal);
        Assert.Equal((ulong)response.JsonPayload.Length, SnapshotCounters().NotCompressedResponseBytesTotal);
    }

    [Fact]
    public void Encode_CompressesJsonResponseWithZstd()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(acceptEncoding: "zstd"));
        ctx.SerializeType = SerializeType.JSON;
        var response = new TestResponseMessage
        {
            JsonPayload = Utf8("{\"name\":\"qiwa\"}"),
            ProtobufPayload = new byte[] { 1 },
        };

        (byte[]? compressed, Error err) = ctx.Encode(ref response);
        (RentedBuffer uncompressed, Error unzipErr) = ZstdCompressor.Uncompress(compressed!);

        try
        {
            Assert.False(err.Err());
            Assert.False(unzipErr.Err());
            Assert.Equal("zstd", ctx.HttpContext!.Response.Headers.ContentEncoding.ToString());
            Assert.Equal(response.JsonPayload, uncompressed.AsSpan().ToArray());
            Assert.Equal((ulong)response.JsonPayload.Length, SnapshotCounters().JsonResponseBytesTotal);
            Assert.Equal((ulong)compressed!.Length, SnapshotCounters().ZstdResponseBytesTotal);
        }
        finally
        {
            uncompressed.Dispose();
        }
    }

    [Fact]
    public void Encode_CompressesProtobufResponseWithGzip()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(acceptEncoding: "gzip"));
        ctx.SerializeType = SerializeType.Protobuf;
        var response = new TestResponseMessage
        {
            JsonPayload = Utf8("{\"unused\":true}"),
            ProtobufPayload = new byte[] { 5, 6, 7, 8 },
        };

        (byte[]? compressed, Error err) = ctx.Encode(ref response);
        (RentedBuffer uncompressed, Error unzipErr) = GzipCompressor.Uncompress(compressed!);

        try
        {
            Assert.False(err.Err());
            Assert.False(unzipErr.Err());
            Assert.Equal("application/protobuf", ctx.HttpContext!.Response.Headers.ContentType.ToString());
            Assert.Equal("gzip", ctx.HttpContext.Response.Headers.ContentEncoding.ToString());
            Assert.Equal(response.ProtobufPayload, uncompressed.AsSpan().ToArray());
            Assert.Equal((ulong)response.ProtobufPayload.Length, SnapshotCounters().ProtobufResponseBytesTotal);
            Assert.Equal((ulong)compressed!.Length, SnapshotCounters().GzipResponseBytesTotal);
        }
        finally
        {
            uncompressed.Dispose();
        }
    }

    [Fact]
    public void Encode_UnknownSerializeType_Throws()
    {
        using var ctx = CreateInitializedContext(CreateHttpContext());
        var response = new TestResponseMessage
        {
            JsonPayload = Utf8("{}"),
            ProtobufPayload = new byte[] { 1 },
        };

        Exception ex = Assert.Throws<Exception>(() => ctx.Encode(ref response));

        Assert.Contains("impossible error", ex.Message);
    }

    [Fact]
    public async Task ReadRequest_WithContentLength_ReadsExactBody()
    {
        ResetAllCounters();
        byte[] payload = Utf8("known-body");
        using var ctx = CreateInitializedContext(CreateHttpContext(bodyBytes: payload, contentLength: payload.Length));

        Error err = await ctx.ReadRequest();

        Assert.False(err.Err());
        Assert.Equal(payload, ctx.RawRequest.AsSpan().ToArray());
        Assert.Equal((ulong)payload.Length, SnapshotCounters().RawRequestBytesTotal);
    }

    [Fact]
    public async Task ReadRequest_WithContentLength_Returns400OnPrematureEof()
    {
        ResetAllCounters();
        byte[] shortBody = Utf8("abc");
        using var ctx = CreateInitializedContext(CreateHttpContext(bodyBytes: shortBody, contentLength: 5));

        Error err = await ctx.ReadRequest();

        Assert.True(err.Err());
        Assert.Equal(400U, err.Code);
        Assert.Equal(400, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().ReadRequestErrorsTotal);
    }

    [Fact]
    public async Task ReadRequest_WithContentLength_Returns408OnCancellation()
    {
        ResetAllCounters();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ctx = CreateInitializedContext(CreateHttpContext(
            bodyStream: new ChunkedPayloadStream(Utf8("cancel"), 2, cancelReads: true),
            contentLength: 6,
            requestAborted: cts.Token));

        Error err = await ctx.ReadRequest();

        Assert.True(err.Err());
        Assert.Equal(408U, err.Code);
        Assert.Equal(408, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().ReadRequestErrorsTotal);
    }

    [Fact]
    public async Task ReadRequest_WithContentLength_Returns413WhenTooLarge()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(contentLength: ServerConfig.MaxRequestSize));

        Error err = await ctx.ReadRequest();

        Assert.True(err.Err());
        Assert.Equal(413U, err.Code);
        Assert.Equal(413, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().ReadRequestErrorsTotal);
    }

    [Fact]
    public async Task ReadRequest_WithoutContentLength_ReadsChunkedBodyAndExpandsBuffer()
    {
        ResetAllCounters();
        byte[] payload = Utf8("chunked-body");
        using var ctx = CreateInitializedContext(CreateHttpContext(
            bodyStream: new ChunkedPayloadStream(payload, chunkSize: 2),
            contentLength: null));
        ctx.RawRequest.Dispose();
        ctx.RawRequest = new RentedBuffer(1);

        Error err = await ctx.ReadRequest();

        Assert.False(err.Err());
        Assert.Equal(payload, ctx.RawRequest.AsSpan().ToArray());
        Assert.True(ctx.RawRequest.Data!.Length > 1);
        Assert.Equal((ulong)payload.Length, SnapshotCounters().RawRequestBytesTotal);
    }

    [Fact]
    public async Task ReadRequest_WithoutContentLength_Returns408OnCancellation()
    {
        ResetAllCounters();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var ctx = CreateInitializedContext(CreateHttpContext(
            bodyStream: new ChunkedPayloadStream(Utf8("cancel"), chunkSize: 2, cancelReads: true),
            contentLength: null,
            requestAborted: cts.Token));

        Error err = await ctx.ReadRequest();

        Assert.True(err.Err());
        Assert.Equal(408U, err.Code);
        Assert.Equal(408, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().ReadRequestErrorsTotal);
    }

    [Fact]
    public async Task ReadRequest_WithoutContentLength_Returns413WhenTooLarge()
    {
        ResetAllCounters();
        using var ctx = CreateInitializedContext(CreateHttpContext(
            bodyStream: new ChunkedPayloadStream(new byte[] { 1, 2, 3, 4 }, chunkSize: 8192, totalBytes: ServerConfig.MaxRequestSize),
            contentLength: null));

        Error err = await ctx.ReadRequest();

        Assert.True(err.Err());
        Assert.Equal(413U, err.Code);
        Assert.Equal(413, ctx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().ReadRequestErrorsTotal);
    }

    [Fact]
    public void Decompress_ReturnsRawPayloadWhenEncodingIsMissing()
    {
        using var ctx = CreateInitializedContext(CreateHttpContext());
        byte[] payload = Utf8("plain");
        SetRentedBuffer(ref ctx.RawRequest, payload);

        (byte[]? bytes, Error err) = ctx.Decompress();

        Assert.False(err.Err());
        Assert.Equal(payload, bytes);
    }

    [Fact]
    public void Decompress_HandlesGzipSuccessAndFailure()
    {
        ResetAllCounters();
        byte[] payload = Utf8("gzip-body");
        (RentedBuffer compressed, Error zipErr) = GzipCompressor.Compress(payload);
        Assert.False(zipErr.Err());

        try
        {
            using var successCtx = CreateInitializedContext(CreateHttpContext(contentEncoding: "gzip"));
            SetRentedBuffer(ref successCtx.RawRequest, compressed.AsSpan().ToArray());
            (byte[]? bytes, Error err) = successCtx.Decompress();
            Assert.False(err.Err());
            Assert.Equal(payload, bytes);
            Assert.Equal((ulong)payload.Length, SnapshotCounters().GzipDecompressedRequestBytesTotal);
        }
        finally
        {
            compressed.Dispose();
        }

        ResetAllCounters();
        using var failureCtx = CreateInitializedContext(CreateHttpContext(contentEncoding: "gzip"));
        SetRentedBuffer(ref failureCtx.RawRequest, new byte[] { 1, 2, 3, 4 });
        (byte[]? brokenBytes, Error brokenErr) = failureCtx.Decompress();
        Assert.Null(brokenBytes);
        Assert.True(brokenErr.Err());
        Assert.Equal(400U, brokenErr.Code);
        Assert.Equal(400, failureCtx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().HttpInternalErrorsTotal);
    }

    [Fact]
    public void Decompress_HandlesZstdSuccessAndFailure()
    {
        ResetAllCounters();
        byte[] payload = Utf8("zstd-body");
        (RentedBuffer compressed, Error zipErr) = ZstdCompressor.Compress(payload);
        Assert.False(zipErr.Err());

        try
        {
            using var successCtx = CreateInitializedContext(CreateHttpContext(contentEncoding: "zstd"));
            SetRentedBuffer(ref successCtx.RawRequest, compressed.AsSpan().ToArray());
            (byte[]? bytes, Error err) = successCtx.Decompress();
            Assert.False(err.Err());
            Assert.Equal(payload, bytes);
            Assert.Equal((ulong)payload.Length, SnapshotCounters().ZstdDecompressedRequestBytesTotal);
        }
        finally
        {
            compressed.Dispose();
        }

        ResetAllCounters();
        using var failureCtx = CreateInitializedContext(CreateHttpContext(contentEncoding: "zstd"));
        SetRentedBuffer(ref failureCtx.RawRequest, new byte[] { 0, 1, 2, 3 });
        (byte[]? brokenBytes, Error brokenErr) = failureCtx.Decompress();
        Assert.Null(brokenBytes);
        Assert.True(brokenErr.Err());
        Assert.Equal(400U, brokenErr.Code);
        Assert.Equal(400, failureCtx.HttpContext!.Response.StatusCode);
        Assert.Equal(1UL, SnapshotCounters().HttpInternalErrorsTotal);
    }

    [Theory]
    [InlineData("127.0.0.1", "client_ipv4")]
    [InlineData("::1", "client_ipv6")]
    public void InitFromHttp_CreatesLoggerPrefixForClientAddress(string ipText, string expectedField)
    {
        using var ctx = new ContextBaseTestContext();
        DefaultHttpContext httpContext = CreateHttpContext(remoteIpAddress: IPAddress.Parse(ipText));

        Error err = ctx.InitFromHttp(httpContext);

        Assert.False(err.Err());
        Assert.Same(httpContext, ctx.HttpContext);
        Assert.NotNull(ctx.L);
        string prefix = GetLoggerPrefix(ctx.L!);
        Assert.Contains("\"path\":\"/tests/context\"", prefix);
        Assert.Contains($"\"{expectedField}\"", prefix);
    }

    [Fact]
    public async Task ReadRequest_WithRealKestrelServer_HandlesChunkedBody()
    {
        byte[] payload = Utf8("real-kestrel-body");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var receivedBody = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using WebApplication app = builder.Build();

        app.MapPost("/read", async httpContext =>
        {
            using var ctx = new ContextBaseTestContext();
            ctx.InitFromHttp(httpContext);
            Error err = await ctx.ReadRequest().ConfigureAwait(false);
            httpContext.Response.Headers["X-Observed-Content-Length"] = httpContext.Request.ContentLength?.ToString() ?? "null";
            if (err.Err())
            {
                httpContext.Response.StatusCode = (int)err.Code;
                return;
            }

            string bodyText = Encoding.UTF8.GetString(ctx.RawRequest.AsSpan());
            receivedBody.TrySetResult(bodyText);
            await httpContext.Response.WriteAsync(bodyText).ConfigureAwait(false);
        });

        await app.StartAsync();
        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();

        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{address}/read");
            request.Content = new StreamContent(new ChunkedPayloadStream(payload, chunkSize: 3));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using HttpResponseMessage response = await client.SendAsync(request);
            string responseBody = await response.Content.ReadAsStringAsync();
            string observedContentLength = response.Headers.GetValues("X-Observed-Content-Length").Single();

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal("null", observedContentLength);
            Assert.Equal(Encoding.UTF8.GetString(payload), responseBody);
            Assert.Equal(Encoding.UTF8.GetString(payload), await receivedBody.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static DefaultHttpContext CreateHttpContext(
        string method = "POST",
        string? contentType = "application/json",
        byte[]? bodyBytes = null,
        Stream? bodyStream = null,
        long? contentLength = 1,
        string acceptEncoding = "",
        string contentEncoding = "",
        IPAddress? remoteIpAddress = null,
        CancellationToken requestAborted = default)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = "/tests/context";
        httpContext.Request.Protocol = "HTTP/1.1";
        httpContext.Request.ContentType = contentType;
        httpContext.Request.Body = bodyStream ?? new MemoryStream(bodyBytes ?? Array.Empty<byte>(), writable: false);
        httpContext.Request.ContentLength = contentLength;
        httpContext.RequestAborted = requestAborted;
        httpContext.Connection.RemoteIpAddress = remoteIpAddress ?? IPAddress.Loopback;
        httpContext.Response.Body = new MemoryStream();
        if (!string.IsNullOrEmpty(acceptEncoding))
        {
            httpContext.Request.Headers.AcceptEncoding = acceptEncoding;
        }
        if (!string.IsNullOrEmpty(contentEncoding))
        {
            httpContext.Request.Headers.ContentEncoding = contentEncoding;
        }
        return httpContext;
    }

    private static ContextBaseTestContext CreateInitializedContext(DefaultHttpContext httpContext)
    {
        var ctx = new ContextBaseTestContext();
        Error err = ctx.InitFromHttp(httpContext);
        Assert.False(err.Err());
        return ctx;
    }

    private static IEnumerable<FieldInfo> GetCounterFields()
        => typeof(Counters)
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(field => field.FieldType == typeof(ulong));

    private static void AssertAllCounterFieldsAreZero(Counters counters)
    {
        foreach (FieldInfo field in GetCounterFields())
        {
            Assert.Equal(0UL, (ulong)field.GetValue(counters)!);
        }
    }

    private static void ResetAllCounters()
    {
        var threadLocal = (ThreadLocal<Counters>)ThreadLocalCountersField.GetValue(null)!;
        foreach (Counters counter in threadLocal.Values)
        {
            counter.Reset();
        }
    }

    private static Counters SnapshotCounters() => ContextBase.SumCounters(new Counters());

    private static RentedBuffer GetPrivateRequestData(ContextBaseTestContext ctx)
        => (RentedBuffer)RequestDataField.GetValue(ctx)!;

    private static void SetPrivateRequestData(ContextBaseTestContext ctx, byte[] data)
    {
        RentedBuffer requestData = GetPrivateRequestData(ctx);
        requestData.Length = 0;
        requestData.Extend(data.Length);
        data.AsSpan().CopyTo(requestData.Data!.AsSpan(0, data.Length));
        requestData.Length = data.Length;
        RequestDataField.SetValue(ctx, requestData);
    }

    private static string GetLoggerPrefix(TaskLogger logger)
    {
        var prefix = (RentedBuffer)TaskLoggerPrefixField.GetValue(logger)!;
        return Encoding.UTF8.GetString(prefix.AsSpan());
    }

    private static void SetRentedBuffer(ref RentedBuffer buffer, byte[] data)
    {
        buffer.Length = 0;
        buffer.Extend(data.Length);
        data.AsSpan().CopyTo(buffer.Data!.AsSpan(0, data.Length));
        buffer.Length = data.Length;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
