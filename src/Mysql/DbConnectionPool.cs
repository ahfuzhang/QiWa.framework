namespace QiWa.Mysql;

using System.Threading.Channels;
using MySqlConnector;

using QiWa.Common;

/// <summary>
/// Reserved for future per-call options (trace flags, query hints, etc.).
/// </summary>
public readonly struct Options { }

/// <summary>
/// A fixed-size pool of <see cref="DbConnection"/> objects backed by a bounded channel.
/// Not a singleton — create one per logical database / role.
/// </summary>
public sealed class DbConnectionPool
{
    internal readonly string _connectionString;
    private readonly int _limit;
    private readonly Channel<DbConnection> _channel;
    private int _count;     // successfully created connections (in channel + in use)

    // Factory injected at construction time; defaults to a real MySqlConnection wrapper.
    // Overridden in unit tests to substitute a fake connection without a real MySQL server.
    internal readonly Func<IRawConnection> _rawConnectionFactory;

    private DbConnectionPool(string connectionString, int limit, Func<IRawConnection> rawConnectionFactory)
    {
        _connectionString = connectionString;
        _limit = limit;
        _rawConnectionFactory = rawConnectionFactory;
        _channel = Channel.CreateBounded<DbConnection>(new BoundedChannelOptions(limit)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// 销毁整个连接池
    /// </summary>
    public void Close()
    {
        _channel.Writer.Complete();
        while (_channel.Reader.TryRead(out var conn))
        {
            conn.CloseAfterDone();
        }
    }

    /// <summary>
    /// 创建一个连接池对象
    /// </summary>
    /// <param name="connectionString">连接字符串</param>
    /// <param name="limit">连接池最大数量</param>
    /// <param name="ct">控制访问超时的 token</param>
    /// <returns>
    /// * DbConnectionPool? 连接池对象
    /// * Error 错误对象
    /// </returns>
    /// <exception cref="Exception">不会抛出任何异常</exception>
    public static Task<(DbConnectionPool?, Error)> CreateAsync(string connectionString, int limit, CancellationToken ct = default)
        => CreateAsync(connectionString, limit, () => new MySqlConnectionWrapper(), ct);

    /// <summary>
    /// Internal overload used by unit tests to inject a fake connection factory.
    /// </summary>
    internal static async Task<(DbConnectionPool?, Error)> CreateAsync(
        string connectionString, int limit, Func<IRawConnection> rawConnectionFactory, CancellationToken ct = default)
    {
        var pool = new DbConnectionPool(connectionString, limit, rawConnectionFactory);
        var (conn, err) = await DbConnection.OpenAsync(pool, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Borrow a connection from the pool.
    /// If the pool is empty and below <c>limit</c>, a new connection is created.
    /// Otherwise the call waits until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async ValueTask<(DbConnection?, Error)> GetAsync(CancellationToken ct = default)
    {
        // Fast path: idle connection already available.
        do
        {
            if (!_channel.Reader.TryRead(out var conn))
            {
                // 队列为空，说明需要创建新的连接
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
            DbConnection conn;
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
        // 构造新对象
        var (newConn, err) = await DbConnection.OpenAsync(this, ct).ConfigureAwait(false);
        if (err.Err())
        {
            return (null, err);
        }
        Interlocked.Increment(ref _count);  // only increment on successful creation
        return (newConn, default);
    }

    // 每个 DbConnection 对象的 Dispose() 方法会调用 Put() 来放回连接池
    internal void Put(DbConnection conn)
    {
        if (!_channel.Writer.TryWrite(conn))
        {
            // 如果不小心创建数量超标，归还的时候关闭这个对象
            conn.CloseAfterDone();
        }
    }
}

/// <summary>
/// A pooled MySQL connection that caches prepared statements keyed by SQL text.
/// </summary>
public sealed class DbConnection : IDisposable
{
    private readonly DbConnectionPool _pool;
    private IRawConnection _rawConn;
    private readonly Dictionary<string, IRawCommand> _preparedStatements = new();
    private long _inUse = 0;
    private bool _disableReuse;  // 当出现异常时，不再重用

    internal DbConnection(DbConnectionPool pool, IRawConnection rawConn)
    {
        _pool = pool;
        _rawConn = rawConn;
    }

    private void Close()
    {
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
                await Task.Delay(100).ConfigureAwait(true);  // 100 毫秒检查一次，是否用完了
            }
            Close();
        });
    }

    /// <summary>
    /// 当前连接对象是否正在使用之中
    /// </summary>
    public bool IsInUse()
    {
        return Interlocked.Read(ref this._inUse) == 1;
    }

    // 初始化 Connection 对象
    internal static async ValueTask<(DbConnection?, Error)> OpenAsync(DbConnectionPool pool, CancellationToken ct)
    {
        var rawConn = pool._rawConnectionFactory();
        rawConn.ConnectionString = pool._connectionString;
#pragma warning disable CA2000
        var conn = new DbConnection(pool, rawConn);
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

    // Returns the cached IRawCommand for sql, or prepares a new one.
    private async ValueTask<(IRawCommand?, Error)> GetOrPrepareAsync(
        string sql, Dictionary<string, MySqlDbType>? parameters, CancellationToken ct)
    {
        // 直接获取缓存的语句
        if (_preparedStatements.TryGetValue(sql, out var cached))
        {
            return (cached, default);
        }
        var cmd = _rawConn!.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var kv in parameters)
            {
                cmd.AddParameter(kv.Key, kv.Value);
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
    /// <param name="parameters">Parameter name → MySQL type map used for prepare. Pass null for no parameters.</param>
    /// <param name="bindFunc">Closure that sets parameter values on the prepared command. Pass null for no parameters.</param>
    /// <param name="ct">控制超时的 token</param>
    public async ValueTask<(int affectedRows, long lastInsertId, Error err)> ExecuteNonQueryAsync(
        string sql,
        Dictionary<string, MySqlDbType>? parameters,
        Func<IRawCommand, Error>? bindFunc,
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
        if (bindFunc != null)
        {
            var bindErr = bindFunc(cmd!);
            if (bindErr.Err())
            {
                return (0, 0, bindErr);
            }
        }
        try
        {
            // 一旦有错误的编译语句，使用者需要使用 RemoveCache 来删除缓存。否则这条语句会一直报错
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

    /// <summary>
    /// 删除某条语句的缓存
    /// </summary>
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
        Dictionary<string, MySqlDbType>? parameters,
        Func<IRawCommand, Error>? bindFunc,
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
        if (bindFunc != null)
        {
            var bindErr = bindFunc(cmd!);
            if (bindErr.Err())
            {
                return (null, bindErr);
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
    /// </summary>
    public async ValueTask<(long rowCount, Error err)> ExecuteReaderAsync(
        string sql,
        Dictionary<string, MySqlDbType>? parameters,
        Func<IRawCommand, Error>? bindFunc,
        Func<IRawReader, Error> eachRowFunc,
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

        if (bindFunc != null)
        {
            var bindErr = bindFunc(cmd!);
            if (bindErr.Err())
            {
                return (0, bindErr);
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
