#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Mysql;

using System.Threading.Channels;
using MySqlConnector;

using QiWa.Common;

// todo: 压测测试
// todo: 资源泄露的测试
// todo: 并发测试

/// <summary>
/// Reserved for future per-call options (trace flags, query hints, etc.).
/// </summary>
public readonly struct Options { }

public sealed record SqlParam
{
    public required string Name;
    public MySqlDbType DataType;
    public object? Value;
}

/// <summary>
/// A fixed-size pool of database connections backed by a bounded channel.
/// Generic over the concrete connection, command, and reader types so callers work with
/// compile-time-known types rather than interface references.
///
/// Production: <c>DbConnectionPool&lt;MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper&gt;</c>
/// Tests:      <c>DbConnectionPool&lt;FakeRawConnection, FakeRawCommand, FakeRawReader&gt;</c>
///
/// Note: MySqlConnection / MySqlCommand / MySqlDataReader cannot be used directly as type arguments
/// because C# requires explicit interface declaration (no structural/duck typing).
/// The wrapper classes serve as thin adapters.
/// </summary>
public sealed class DbConnectionPool<TConn, TCmd, TReader>
    where TConn : class, IRawConnection<TCmd, TReader>
    where TCmd : class, IRawCommand<TReader>
    where TReader : class, IRawReader
{
    internal readonly string _connectionString;
    private readonly int _limit;
    private readonly Channel<DbConnection<TConn, TCmd, TReader>> _channel;
    private int _count;

    internal readonly Func<TConn> _rawConnectionFactory;

    private DbConnectionPool(string connectionString, int limit, Func<TConn> rawConnectionFactory)
    {
        _connectionString = connectionString;
        _limit = limit;
        _rawConnectionFactory = rawConnectionFactory;
        _channel = Channel.CreateBounded<DbConnection<TConn, TCmd, TReader>>(new BoundedChannelOptions(limit)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>销毁整个连接池</summary>
    public void Close()
    {
        _channel.Writer.Complete();
        while (_channel.Reader.TryRead(out var conn))
        {
            conn.CloseAfterDone();
        }
    }

    /// <summary>
    /// 创建一个连接池对象。
    /// </summary>
    /// <param name="connectionString">连接字符串</param>
    /// <param name="limit">连接池最大数量</param>
    /// <param name="rawConnectionFactory">创建 <typeparamref name="TConn"/> 实例的工厂函数</param>
    /// <param name="ct">控制访问超时的 token</param>
#pragma warning disable CA1000
    public static async Task<(DbConnectionPool<TConn, TCmd, TReader>?, Error)> CreateAsync(
        string connectionString, int limit, Func<TConn> rawConnectionFactory, CancellationToken ct = default)
    {
        var pool = new DbConnectionPool<TConn, TCmd, TReader>(connectionString, limit, rawConnectionFactory);
        var (conn, err) = await DbConnection<TConn, TCmd, TReader>.OpenAsync(pool, ct).ConfigureAwait(false);
        if (err.Err())
        {
            pool.Close();
            return (null, err);
        }
        if (!pool._channel.Writer.TryWrite(conn!))
        {
            throw new Exception("impossible error");
        }
        return (pool, default);
    }
#pragma warning restore CA1000

    internal const int maxIdleSeconds = 60;  // 超过一段时间没有使用的连接，要探测一下是否 tcp 连接还有效

    /// <summary>
    /// Borrow a connection from the pool.
    /// If the pool is empty and below <c>limit</c>, a new connection is created.
    /// Otherwise the call waits until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async ValueTask<(DbConnection<TConn, TCmd, TReader>?, Error)> GetAsync(CancellationToken ct = default)
    {
        // Fast path: idle connection already available.
        Error err;
        do
        {
            if (!_channel.Reader.TryRead(out var conn))
            {
                break;
            }
            if (!conn.IsInUse())
            {
                Int64 now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now - conn.lastUseTimestamp > maxIdleSeconds)
                {
                    // AOT 版本总是发生崩溃，因为 IDle 的连接一定不能再使用了。
                    // 怀疑是 AOT + MysqlConnector 共同引发的问题
                    // 该问题修复起来太困难了，因此直接丢失 Idel 的连接。

                    /*
                    err = await conn.PingAsync(ct).ConfigureAwait(false);
                    if (err.Err())
                    {
                        conn.CloseAfterDone();
                        Interlocked.Decrement(ref _count);
                        // todo: 如何更好的打日志
                        Console.WriteLine($"{{\"code\":{err.Code},\"message\":\"Idle and ping fail, {err.Message}\"}}");
                        continue;
                    }
                    Console.WriteLine($"{{\"message\":\"ping success, ts={conn.lastUseTimestamp}\"}}");
                    */
                    conn.CloseAfterDone();
                    continue;
                    // 为了解决崩溃问题：只要连接变成 Idle，就丢弃
                }
                conn.lastUseTimestamp = now;  // 更新最后使用时间，便于判断 Idle 太长时间的连接
                return (conn, default);
            }
            // 如果某个 conn 还在干活，应该丢弃这个 conn
            conn.CloseAfterDone();
            Interlocked.Decrement(ref _count);
        } while (true);

        // Try to grow the pool: claim a creation slot first, create only if under limit.
        int count = Interlocked.Increment(ref _count);
        if (count >= _limit)
        {
            DbConnection<TConn, TCmd, TReader> conn;
            try
            {
                conn = await _channel.Reader.ReadAsync(ct).ConfigureAwait(true);  // 阻塞等待，直至超时
            }
            catch (OperationCanceledException)
            {
                return (null, Error.WithLoc(1, "[OperationCanceledException]DbConnectionPool.GetAsync: timed out waiting for a free connection"));
            }
            if (!conn.IsInUse())
            {
                return (conn, default);
            }
            conn.CloseAfterDone();
            Interlocked.Decrement(ref _count);
        }

        // 如果因为并发，创建了超过 limit 的对象，则多余的对象在 Put() 时会被释放掉
        var (newConn, err1) = await DbConnection<TConn, TCmd, TReader>.OpenAsync(this, ct).ConfigureAwait(false);
        if (err1.Err())
        {
            return (null, err1);
        }
        Interlocked.Increment(ref _count);
        Console.WriteLine("{\"message\":\"create a new connection\"}");
        return (newConn, default);
    }

    // 每个 DbConnection 对象的 Dispose() 方法会调用 Put() 来放回连接池
    internal void Put(DbConnection<TConn, TCmd, TReader> conn)
    {
        if (!_channel.Writer.TryWrite(conn))
        {
            // 如果不小心创建数量超标，归还的时候关闭这个对象
            conn.CloseAfterDone();
        }
    }

    // public static Task<(DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper>?, Error)>
    //     CreateAsync(string connectionString, int limit, CancellationToken ct = default)
    //     => DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper>
    //         .CreateAsync(connectionString, limit, () => new MySqlConnectionWrapper(), ct);
}
