#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace examples;

using Microsoft.AspNetCore.Http;
using QiWa.Common;
using QiWa.ConsoleLogger;
using Microsoft.Extensions.ObjectPool;
using QiWa.KestrelWrap;

public struct DemoServerCounters
{
    public UInt64 PathHelloRequestTotal;
    public UInt64 PathHelloDecodeErrorsTotal;
    public UInt64 PathHelloExceptionsTotal;
    public UInt64 PathHelloLogicErrorsTotal;
}

public class DemoServer  // 这里是 service 的名字
{
    internal static readonly DefaultObjectPool<HelloContext> HelloContextPool = new DefaultObjectPool<HelloContext>(
        new ContextObjectPolicy<HelloContext>(),
        maximumRetained: ServerConfig.MaxCocurrentCount
    );

    [ThreadStatic]
    public static DemoServerCounters Counters;  // todo: 把自己注册到全局

    public static async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref ContextBase.Counters.HttpRequestTotal);
        Error err = ContextBase.Validate(context);
        if (err.Err())
        {
            // 打日志
            ThreadLocalLogger.Current.Warn(
                Field.String("path"u8, context.Request.Path.Value ?? ""),
                Field.String("method"u8, context.Request.Method),
                Field.String("protocol"u8, context.Request.Protocol),
                Field.String(
                    (context.Request.HttpContext.Connection.RemoteIpAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        ? "client_ipv6"u8 : "client_ipv4"u8,
                    context.Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
                Field.Int64("error_code"u8, err.Code),
                Field.String("message"u8, err.Message)
            );
            // todo: metrics 上报
            Interlocked.Increment(ref ContextBase.Counters.HttpBadRequestTotal);
            return;
        }
        // 判断请求路径
        byte[]? responseBytes;
        switch (context.Request.Path)
        {
            case "/service/Hello":  // 这里是每个 method 的路径
                {
                    Interlocked.Increment(ref Counters.PathHelloRequestTotal);
                    HelloContext ctx = HelloContextPool.Get();
                    using var _ = new QiWa.Helper.ScopeGuard(() =>
                    {
                        HelloContextPool.Return(ctx);
                        //todo: 上报处理时间
                    });
                    err = ctx.InitFromHttp(context);
                    if (err.Err())
                    {
                        // todo: 打日志
                        ThreadLocalLogger.Current.Warn(
                            Field.String("path"u8, context.Request.Path.Value ?? ""),
                            Field.String("method"u8, context.Request.Method),
                            Field.String("protocol"u8, context.Request.Protocol),
                            Field.String(
                                (context.Request.HttpContext.Connection.RemoteIpAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                                    ? "client_ipv6"u8 : "client_ipv4"u8,
                                context.Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
                            Field.Int64("error_code"u8, err.Code),
                            Field.String("message"u8, err.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref ContextBase.Counters.InitErrorsTotal);
                        return;
                    }
                    byte[]? reqRequest;
                    (reqRequest, err) = await ctx.ReadRequest().ConfigureAwait(true);
                    if (err.Err())
                    {
                        // todo: 打日志
                        ctx.L!.Warn(
                            Field.Int64("error_code"u8, err.Code),
                            Field.String("message"u8, err.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref ContextBase.Counters.InitErrorsTotal);
                        return;
                    }
                    // 解码
                    err = ctx.Decode<ReadonlyHelloRequest>(reqRequest!, ref ctx.Request);
                    if (err.Err())
                    {
                        // todo: 打日志
                        ctx.L!.Warn(
                            Field.Int64("error_code"u8, err.Code),
                            Field.String("message"u8, err.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref Counters.PathHelloDecodeErrorsTotal);
                        return;
                    }
                    // 调用业务
                    try
                    {
                        // todo: 加上计时
                        err = await ctx.Run().ConfigureAwait(true);  // todo: 这里要加异常处理
                    }
                    catch (Exception ex)
                    {
                        // todo: 打日志
                        ctx.L!.Warn(
                            Field.Int64("error_code"u8, 65535),
                            Field.String("message"u8, ex.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref Counters.PathHelloExceptionsTotal);
                        context.Response.StatusCode = 500;
                        return;
                    }
                    if (err.Err())
                    {
                        // todo: 打日志
                        ctx.L!.Warn(
                            Field.Int64("error_code"u8, err.Code),
                            Field.String("message"u8, err.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref Counters.PathHelloLogicErrorsTotal);
                        return;
                    }
                    // 响应
                    (responseBytes, err) = ctx.Encode<HelloResponse>(ref ctx.Response);
                    if (err.Err())
                    {
                        // todo: 打日志
                        ctx.L!.Warn(
                            Field.Int64("error_code"u8, err.Code),
                            Field.String("message"u8, err.Message)
                        );
                        // todo: 数据上报
                        Interlocked.Increment(ref ContextBase.Counters.EncodeErrorsTotal);
                        return;
                    }
                }
                break;
            default:
                // todo: 多个 service 如何处理?
                context.Response.StatusCode = 404;
                // todo: 打日志
                // todo: 数据上报  => 可以考虑使用 thread local
                Interlocked.Increment(ref ContextBase.Counters.HttpNotFoundErrorsTotal);
                return;
        }
        // 输出
        context.Response.StatusCode = 200;
        try
        {
            await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            ThreadLocalLogger.Current.Warn(
                Field.String("path"u8, context.Request.Path.Value ?? ""),
                Field.String("method"u8, context.Request.Method),
                Field.String("protocol"u8, context.Request.Protocol),
                Field.String(
                    (context.Request.HttpContext.Connection.RemoteIpAddress?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        ? "client_ipv6"u8 : "client_ipv4"u8,
                    context.Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""),
                Field.Int64("error_code"u8, 65535),
                Field.String("message"u8, ex.Message),
                Field.String("exception"u8, "OperationCanceledException")
            );
            Interlocked.Increment(ref ContextBase.Counters.SendErrorsTotal);
            // ThreadLocalLogger.Current.Warn(Field.String("path"u8, context.Request.Path.Value ?? ""),
            //     Field.String("error"u8, $"OperationCanceledException, Failed to write response: {ex.Message}"));
            return;
            //context.Response.StatusCode = 500;
        }
        // todo: 数据上报
        // todo: 打日志
        // todo: 拦截器调用
    }
}
