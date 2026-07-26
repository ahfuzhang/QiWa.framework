#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Mysql;

using MySqlConnector;

using QiWa.Common;

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
    internal TConn _rawConn;
    private readonly Dictionary<string, TCmd> _preparedStatements = new();
    private long _inUse = 0;
    private bool _disableReuse;  // 当出现异常时，不再重用
    internal Int64 lastUseTimestamp = 0;

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
            return (null, Error.WithLoc((int)ErrorCodes.CreateConnectionMysqlExceptionError, "[MySqlException]OpenAsync error: " + ex.Message));
        }
        catch (OperationCanceledException)
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, Error.WithLoc((int)ErrorCodes.CreateConnectionTimeoutError, "[OperationCanceledException]OpenAsync timeout"));
        }
        catch (IOException exIO)
        {
            return (null, Error.WithLoc((int)ErrorCodes.CreateConnectionIOExceptionError, $"[IOException]OpenAsync io exception, message={exIO.Message}"));
        }
        catch (Exception exUnknown)
        {
            return (null, Error.WithLoc((int)ErrorCodes.CreateConnectionUnknownExceptionError, $"[Exception]OpenAsync exception, message={exUnknown.Message}"));
        }
        Error err = await PingAsync(rawConn, ct).ConfigureAwait(false);
        if (err.Err())
        {
            await rawConn.CloseAsync().ConfigureAwait(true);
            return (null, err);
        }
        conn.lastUseTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (conn, default);
    }

    internal long IdleSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.lastUseTimestamp;
    }

    internal static async ValueTask<Error> PingAsync(TConn conn, CancellationToken ct)
    {
        try
        {
            await conn.PingAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            return Error.WithLoc((int)ErrorCodes.PingMysqlExceptionError, "[MySqlException]PingAsync error: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Error.WithLoc((int)ErrorCodes.PingTimeoutError, "[OperationCanceledException]PingAsync timeout");
        }
        catch (System.IO.IOException exIO)
        {
            return Error.WithLoc((int)ErrorCodes.PingIOExceptionError, $"[System.IO.IOException], message={exIO.Message}");
        }
        catch (Exception exUnknown)
        {
            return Error.WithLoc((int)ErrorCodes.PingUnknownExceptionError, $"[Exception], message={exUnknown.Message}");
        }
        return default;
    }

    public ValueTask<Error> PingAsync(CancellationToken ct = default)
    {
        return PingAsync(_rawConn, ct);
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
            // AOT 版本在此处发生无法捕获的异常：Unable to write data to the transport connection: Broken pipe.
            await cmd.PrepareAsync(ct).ConfigureAwait(false);
        }
        catch (MySqlException ex)
        {
            cmd.Dispose();
            return (null, Error.WithLoc((int)ErrorCodes.PrepareMysqlExceptionError, $"[MySqlException]DbConnection.PrepareAsync: {ex.Message}"));
        }
        catch (OperationCanceledException)
        {
            cmd.Dispose();
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (null, Error.WithLoc((int)ErrorCodes.PrepareTimeoutError, "[OperationCanceledException]cmd.PrepareAsync timeout"));
        }
        catch (System.IO.IOException exIO)
        {
            cmd.Dispose();
            _disableReuse = true;  // 一个连接长期不用，就会出现 Broken pipe
            return (null, Error.WithLoc((int)ErrorCodes.PrepareIOExceptionError, $"[System.IO.IOException]Broken pipe, ex={exIO.Message}"));
        }
        catch (System.InvalidOperationException exInvalid)
        {
            cmd.Dispose();
            _disableReuse = true;
            return (null, Error.WithLoc((int)ErrorCodes.PrepareUnknownExceptionError, $"[InvalidOperationException]ex={exInvalid.Message}"));
        }
        catch (Exception exUnknown)
        {
            cmd.Dispose();
            _disableReuse = true;
            return (null, Error.WithLoc((int)ErrorCodes.PrepareUnknownExceptionError, $"[Exception]ex={exUnknown.Message}"));
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
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(false);
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
            return (0, 0,
                Error.WithLoc(
                    (int)ErrorCodes.ExecuteMySqlExceptionError,
                    $"[MySqlException]ExecuteNonQueryAsync: {ex.Message}"
                )
            );
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (0, 0, Error.WithLoc((int)ErrorCodes.ExecuteTimeoutError, "[OperationCanceledException]cmd.ExecuteNonQueryAsync timeout"));
        }
        catch (IOException exIO)
        {
            _disableReuse = true;
            return (0, 0, Error.WithLoc((int)ErrorCodes.ExecuteIOExceptionError, $"[IOException]cmd.ExecuteNonQueryAsync,ex={exIO.Message}"));
        }
        catch (Exception exUnknown)
        {
            _disableReuse = true;
            return (0, 0, Error.WithLoc((int)ErrorCodes.ExecuteUnknownExceptionError, $"[IOException]cmd.ExecuteNonQueryAsync,ex={exUnknown.Message}"));
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
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(false);  // ConfigureAwait(true); 时，在 AOT 版本发生无法捕获的异常
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
            return (null,
                Error.WithLoc(
                    (int)ErrorCodes.ExecuteMySqlExceptionError,
                    $"[MySqlException]ExecuteScalarAsync: {ex.Message}"
                )
            );
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (null, Error.WithLoc((int)ErrorCodes.ExecuteTimeoutError, "[OperationCanceledException]cmd.ExecuteScalarAsync timeout"));
        }
        catch (IOException exIO)
        {
            _disableReuse = true;
            return (null, Error.WithLoc((int)ErrorCodes.ExecuteIOExceptionError, $"[IOException]cmd.ExecuteScalarAsync,ex={exIO.Message}"));
        }
        catch (Exception exUnknown)
        {
            _disableReuse = true;
            return (null, Error.WithLoc((int)ErrorCodes.ExecuteUnknownExceptionError, $"[IOException]cmd.ExecuteScalarAsync,ex={exUnknown.Message}"));
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
        var (cmd, prepErr) = await GetOrPrepareAsync(sql, parameters, ct).ConfigureAwait(false);
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
            return (0,
                Error.WithLoc(
                    (int)ErrorCodes.ExecuteMySqlExceptionError,
                    $"[MySqlException]ExecuteReaderAsync: {ex.Message}"
                )
            );
        }
        catch (OperationCanceledException)
        {
            _disableReuse = true;  // 如果发生超时，则这条连接可能一直在使用之中，所以不再重用这个连接对象
            return (0, Error.WithLoc((int)ErrorCodes.ExecuteTimeoutError, "[OperationCanceledException]cmd.ExecuteReaderAsync timeout"));
        }
        catch (IOException exIO)
        {
            _disableReuse = true;
            return (0, Error.WithLoc((int)ErrorCodes.ExecuteIOExceptionError, $"[IOException]cmd.ExecuteReaderAsync,ex={exIO.Message}"));
        }
        catch (Exception exUnknown)
        {
            _disableReuse = true;
            return (0, Error.WithLoc((int)ErrorCodes.ExecuteUnknownExceptionError, $"[Exception]cmd.ExecuteReaderAsync,ex={exUnknown.Message}"));
        }
    }
}

