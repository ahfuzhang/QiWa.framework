#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace ClientsTests;

using QiWa.Clients;
using QiWa.Common;
using Microsoft.Extensions.ObjectPool;
using Xunit;

// 实现一个客户端对象的例子程序

public struct GetExampleRequest : QiWa.Common.IResettable
{
    public int Field1;

    public void Reset()
    {
        Field1 = 0;
    }

    public Error ToJSON(ref RentedBuffer dst)
    {
        return default;
    }
}

public struct GetExampleResponse : QiWa.Common.IResettable
{
    public Int32 Code;
    public string Message;

    public void Reset()
    {
        Code = 0;
        Message = string.Empty;
    }

    public Error FromJSON(ReadOnlySpan<byte> src)
    {
        return default;
    }
}

public class ExampleClientContext : HttpClientContextBase, QiWa.Common.IResettable
{
    public static new readonly string ApiPath = "/api/v1/example";
    public static new readonly string Namespace = "generated";
    public static new readonly string Service = "ExampleService";
    public static new readonly string Method = "GetExample";

    public GetExampleRequest Request;
    public GetExampleResponse Response;

    public ExampleClientContext() : base(ApiPath, Namespace, Service, Method)
    {
    }

    public new void Reset()
    {
        Request.Reset();
        Response.Reset();
        base.Reset();
    }

    public async ValueTask<Error> GetExampleAsync(HttpClientBase client, CancellationToken timeout)
    {
        // client 对象已经决定了 protocol 是什么
        Error err;
        UInt64 flags = 0;
        //using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        //CancellationToken timeout = cts.Token;
        err = await client.RequestAsync(
            this,
            (RentedBuffer dst) =>
            {
                Error err1 = this.Request.ToJSON(ref dst);
                return (dst, err1);
            },
            (byte[] rspBytes, string contentType) =>
            {
                return this.Response.FromJSON(rspBytes.AsSpan());
            },
            flags,
            null,
            timeout
        ).ConfigureAwait(true);
        return err;
    }
}

// public class ContextObjectPolicy<ContextType>
//     : PooledObjectPolicy<ContextType>
//     where ContextType : QiWa.Common.IResettable, new()
// {
//     public override ContextType Create()
//         => new ContextType();

//     public override bool Return(ContextType ctx)
//     {
//         ctx.Reset();
//         return true; // true = 放回池
//     }
// }

public class GlobalClients
{
    public HttpClientBase http1;
    public HttpClientBase http2;
    public HttpClientBase grpc;

    public const int MaxConcurrentCount = 10000;

    // 对象池
    internal static readonly DefaultObjectPool<ExampleClientContext> ExampleClientContextPool = new DefaultObjectPool<ExampleClientContext>(
        new QiWa.KestrelWrap.ContextObjectPolicy<ExampleClientContext>(),
        maximumRetained: MaxConcurrentCount
    );

    public static GlobalClients Singleton = new GlobalClients();

    // 构造全局的客户端
    public GlobalClients()
    {
        HttpClientConfig cfg = HttpClientConfig.New(4);
        Uri target = new Uri("http://192.168.3.69:8079/flutter_admin_site/");
        http1 = HttpClientBase.NewHttp1Client(in cfg, target, "");
        http2 = HttpClientBase.NewHttp2Client(in cfg, target, "");
        Uri grpcTarget = new Uri("http://192.168.3.69:8079");
        grpc = HttpClientBase.NewGrpcClient(in cfg, grpcTarget, "");
    }

    public async ValueTask<Error> GetExample()
    {
        ExampleClientContext ctx = ExampleClientContextPool.Get();
        using var _ = new QiWa.Helper.ScopeGuard(() =>
        {
            ExampleClientContextPool.Return(ctx);
            //todo: 上报处理时间
        });
        ctx.Request.Field1 = 123;
        Error err = await ctx.GetExampleAsync(this.http1, default).ConfigureAwait(true);
        if (err.Err())
        {
            return err;
        }
        // todo:
        if (ctx.Response.Code!=0)
        {
            return Error.WithLoc(1, $"biz logic error: code={ctx.Response.Code}");
        }
        return default;
    }
}
