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

    /// <summary>
    /// Borrow a connection from the pool.
    /// If the pool is empty and below <c>limit</c>, a new connection is created.
    /// Otherwise the call waits until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async ValueTask<(DbConnection<TConn, TCmd, TReader>?, Error)> GetAsync(CancellationToken ct = default)
    {
        // Fast path: idle connection already available.
        do
        {
            if (!_channel.Reader.TryRead(out var conn))
            {
                break;
            }
            if (!conn.IsInUse())
            {
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
        var (newConn, err) = await DbConnection<TConn, TCmd, TReader>.OpenAsync(this, ct).ConfigureAwait(false);
        if (err.Err())
        {
            return (null, err);
        }
        Interlocked.Increment(ref _count);
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
}

/// <summary>
/// Convenience factory for production use with the real MySqlConnector types.
/// Equivalent to <c>DbConnectionPool&lt;MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper&gt;</c>.
/// </summary>
public static class DbConnectionPool
{
    /// <summary>
    /// 创建生产环境连接池（使用 MySqlConnector 自带对象的 wrapper）。
    /// </summary>
    public static Task<(DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper>?, Error)>
        CreateAsync(string connectionString, int limit, CancellationToken ct = default)
        => DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper>
            .CreateAsync(connectionString, limit, () => new MySqlConnectionWrapper(), ct);
}

/// <summary>
/// A pooled database connection that caches prepared statements keyed by SQL text.
/// Generic over <typeparamref name="TConn"/>, <typeparamref name="TCmd"/>, <typeparamref name="TReader"/>
/// so all operations are typed at compile time.
/// </summary>
public sealed class DbConnection<TConn, TCmd, TReader> : IDisposable
    where TConn : class, IRawConnection<TCmd, TReader>
    where TCmd : class, IRawCommand<TReader>
    where TReader : class, IRawReader
{
    private readonly DbConnectionPool<TConn, TCmd, TReader> _pool;
    private TConn _rawConn;
    private readonly Dictionary<string, TCmd> _preparedStatements = new();
    private long _inUse = 0;
    private bool _disableReuse;  // 当出现异常时，不再重用

    internal DbConnection(DbConnectionPool<TConn, TCmd, TReader> pool, TConn rawConn)
    {
        _pool = pool;
        _rawConn = rawConn;
    }

    private void Close()
    {
        foreach (var kv in _preparedStatements)
        {
            kv.Value.Dispose();
        }
        _preparedStatements.Clear();
        if (_rawConn != null)
        {
            // 彻底释放这个对象
#pragma warning disable MA0045
            _rawConn.Close();  // MA0045
#pragma warning restore MA0045
            _rawConn.Dispose();
            _rawConn = null!;
        }
    }

    /// <summary>
    /// Clears all cached prepared statements. Call this if you want to free up memory or if you know the SQL statements will no longer be used.
    /// </summary>
    public void ClearPreparedStatements()
    {
        foreach (var kv in _preparedStatements)
        {
            kv.Value.Dispose();
        }
        _preparedStatements.Clear();
    }

    internal void CloseAfterDone()
    {
        // 干完活后关闭
        if (!IsInUse())
        {
            Close();
            return;
        }
        // 当这个对象错误的长期执行时，在独立的 task 中守候它正常关闭
        _ = Task.Run(async () =>
        {
            while (IsInUse())
            {
                await Task.Delay(100).ConfigureAwait(true);
            }
            Close();
        });
    }

    /// <summary>当前连接对象是否正在使用之中</summary>
    public bool IsInUse() => Interlocked.Read(ref _inUse) == 1;

    // 初始化 Connection 对象
    internal static async ValueTask<(DbConnection<TConn, TCmd, TReader>?, Error)> OpenAsync(
        DbConnectionPool<TConn, TCmd, TReader> pool, CancellationToken ct)
    {
        var rawConn = pool._rawConnectionFactory();
        rawConn.ConnectionString = pool._connectionString;
#pragma warning disable CA2000
        var conn = new DbConnection<TConn, TCmd, TReader>(pool, rawConn);
#pragma warning restore CA2000
        try
        {
            await rawConn.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, Error.WithLoc(1, "[MySqlException]OpenAsync error: " + ex.Message));
        }
        catch (OperationCanceledException)
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, Error.WithLoc(2, "[OperationCanceledException]OpenAsync timeout"));
        }
        try
        {
            await rawConn.PingAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, Error.WithLoc(2, "[MySqlException]PingAsync error: " + ex.Message));
        }
        catch (OperationCanceledException)
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, Error.WithLoc(2, "[OperationCanceledException]PingAsync timeout"));
        }
        return (conn, default);
    }

    /// <summary>
    /// Returns this connection to the pool. Called automatically when used inside a using block.
    /// </summary>
    public void Dispose()
    {
        if (!_disableReuse)
        {
            _pool.Put(this);
            return;
        }
        CloseAfterDone();
    }

    // Returns the cached TCmd for sql, or prepares a new one.
    private async ValueTask<(TCmd?, Error)> GetOrPrepareAsync(
        string sql, SqlParam[] parameters, CancellationToken ct)
    {
        if (_preparedStatements.TryGetValue(sql, out var cached))
        {
            return (cached, default);
        }
        var cmd = _rawConn!.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd.AddParameter(p.Name, p.DataType);
            }
        }
        try
        {
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            cmd.Dispose();
            return (null, Error.WithLoc(1, $"[MySqlException]DbConnection.PrepareAsync: {ex.Message}"));
        }
        catch (OperationCanceledException)
        {
            cmd.Dispose();
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (null, Error.WithLoc(2, "[OperationCanceledException]cmd.PrepareAsync timeout"));
        }
        _preparedStatements[sql] = cmd;
        return (cmd, default);
    }

    /// <summary>
    /// Executes INSERT / UPDATE / DELETE and returns (affectedRows, lastInsertId, error).
    /// </summary>
    /// <param name="sql">The SQL statement; use named parameters like @name.</param>
    /// <param name="parameters">Parameters (name, type, value). Pass null or empty for no parameters.</param>
    /// <param name="ct">控制超时的 token</param>
    public async ValueTask<(int affectedRows, long lastInsertId, Error err)> ExecuteNonQueryAsync(
        string sql,
        SqlParam[] parameters,
        CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _inUse, 1);
        using var _ = new QiWa.Helper.ScopeGuard(() =>
        {
            Interlocked.Exchange(ref _inUse, 0);
        });
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(true);
        if (prepErr.Err())
        {
            return (0, 0, prepErr);
        }
        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd!.SetParameterValue(p.Name, p.Value);
            }
        }
        try
        {
            int rows = await cmd!.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return (rows, cmd.LastInsertedId, default);
        }
        catch (MySqlException ex)
        {
            return (0, 0, Error.WithLoc(1, $"[MySqlException]DbConnection.ExecuteNonQueryAsync: {ex.Message}"));
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (0, 0, Error.WithLoc(2, "[OperationCanceledException]cmd.ExecuteNonQueryAsync timeout"));
        }
    }

    /// <summary>删除某条语句的缓存</summary>
    /// <param name="sql"></param>
    /// <returns>true/false, 是否删除成功</returns>
    public bool RemoveCache(string sql)
    {
        if (!_preparedStatements.TryGetValue(sql, out var cached))
        {
            return false;
        }
        cached.Dispose();
        _preparedStatements.Remove(sql);
        return true;
    }

    /// <summary>
    /// Executes a scalar query (e.g. SELECT COUNT(*)) and returns the result as an object.
    /// </summary>
    public async ValueTask<(object? result, Error err)> ExecuteScalarAsync(
        string sql,
        SqlParam[] parameters,
        CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _inUse, 1);
        using var _ = new QiWa.Helper.ScopeGuard(() =>
        {
            Interlocked.Exchange(ref _inUse, 0);
        });
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(true);
        if (prepErr.Err())
        {
            return (null, prepErr);
        }
        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd!.SetParameterValue(p.Name, p.Value);
            }
        }
        try
        {
            var scalar = await cmd!.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return (scalar, default);
        }
        catch (MySqlException ex)
        {
            return (null, Error.WithLoc(1, $"[MySqlException]DbConnection.ExecuteScalarAsync: {ex.Message}"));
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (0, Error.WithLoc(2, "[OperationCanceledException]cmd.ExecuteScalarAsync timeout"));
        }
    }

    /// <summary>
    /// Executes a SELECT and feeds each row to <paramref name="eachRowFunc"/>.
    /// Returns (rowCount, error). If <paramref name="eachRowFunc"/> returns an error, iteration stops immediately.
    /// The callback receives the concrete <typeparamref name="TReader"/> directly — no interface cast needed.
    /// </summary>
    public async ValueTask<(long rowCount, Error err)> ExecuteReaderAsync(
        string sql,
        SqlParam[] parameters,
        Func<TReader, Error> eachRowFunc,
        CancellationToken ct = default)
    {
        if (eachRowFunc == null)
        {
            return (0, Error.WithLoc(65535, "param error: must set eachRowFunc"));
        }
        Interlocked.Exchange(ref _inUse, 1);
        using var _ = new QiWa.Helper.ScopeGuard(() =>
        {
            Interlocked.Exchange(ref _inUse, 0);
        });
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(true);
        if (prepErr.Err())
        {
            return (0, prepErr);
        }
        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd!.SetParameterValue(p.Name, p.Value);
            }
        }
        try
        {
            var reader = await cmd!.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                long rowCount = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(true))
                {
                    var rowErr = eachRowFunc(reader);
                    if (rowErr.Err())
                    {
                        return (rowCount, rowErr);
                    }
                    rowCount++;
                }
                return (rowCount, default);
            }
        }
        catch (MySqlException ex)
        {
            return (0, Error.WithLoc(1, $"[MySqlException]DbConnection.ExecuteReaderAsync: {ex.Message}"));
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (0, Error.WithLoc(2, "[OperationCanceledException]cmd.ExecuteReaderAsync timeout"));
        }
    }
}
