// 意图：对 DbConnectionPool 和 DbConnection 进行单元测试，覆盖连接池管理、错误处理、并发等所有分支。
// 通过注入 FakeRawConnection 替代真实 MySQL 连接，无需 MySQL 服务器即可运行。
#pragma warning disable CS1591
namespace Tests.Mysql;

using System.Reflection;
using MySqlConnector;
using QiWa.Common;
using QiWa.Mysql;
using Xunit;

// Type aliases to avoid repeating the three type arguments throughout the file.
using TestPool = QiWa.Mysql.DbConnectionPool<
    Tests.Mysql.FakeRawConnection,
    Tests.Mysql.FakeRawCommand,
    Tests.Mysql.FakeRawReader>;
using TestConn = QiWa.Mysql.DbConnection<
    Tests.Mysql.FakeRawConnection,
    Tests.Mysql.FakeRawCommand,
    Tests.Mysql.FakeRawReader>;

public class DbConnectionPoolTests
{
    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// MySqlConnector 2.6.1 中 MySqlException 的所有构造函数均为 internal，通过反射创建实例。
    /// 使用单参数 internal ctor: MySqlException(string message)
    /// </summary>
    private static MySqlException MakeMySqlException(string message)
    {
        var ctor = typeof(MySqlException)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c =>
            {
                var p = c.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(string);
            });
        return (MySqlException)ctor.Invoke(new object[] { message });
    }

    /// <summary>
    /// 创建一个已成功初始化的连接池，factory 固定返回一个新的 FakeRawConnection。
    /// </summary>
    private static async Task<(TestPool pool, FakeRawConnection firstConn)> MakePoolAsync(int limit = 3)
    {
        var fake = new FakeRawConnection();
        var (pool, err) = await TestPool.CreateAsync("server=fake", limit, () => fake).ConfigureAwait(true);
        Assert.False(err.Err(), err.Message);
        return (pool!, fake);
    }

    /// <summary>
    /// 通过内部构造函数直接创建 DbConnection（用于注入测试）。
    /// </summary>
    private static TestConn CreateDbConnection(TestPool pool, FakeRawConnection rawConn)
    {
        var ctor = typeof(TestConn).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(TestPool), typeof(FakeRawConnection) },
            null)!;
        return (TestConn)ctor.Invoke(new object[] { pool, rawConn });
    }

    // ── DbConnectionPool.CreateAsync ─────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Success_ReturnsPoolWithNoError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        Assert.NotNull(pool);
        pool.Close();
    }

    [Fact]
    public async Task CreateAsync_OpenFails_WithMySqlException_ReturnsError()
    {
        var fake = new FakeRawConnection
        {
            OpenException = MakeMySqlException("connection refused"),
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 3, () => fake).ConfigureAwait(true);
        Assert.Null(pool);
        Assert.True(err.Err());
        Assert.Contains("MySqlException", err.Message);
        Assert.Contains("connection refused", err.Message);
        Assert.True(fake.WasClosed);
    }

    [Fact]
    public async Task CreateAsync_OpenFails_WithCancellation_ReturnsError()
    {
        var fake = new FakeRawConnection
        {
            OpenException = new OperationCanceledException("timeout"),
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 3, () => fake).ConfigureAwait(true);
        Assert.Null(pool);
        Assert.True(err.Err());
        Assert.Contains("OperationCanceledException", err.Message);
        Assert.True(fake.WasClosed);
    }

    [Fact]
    public async Task CreateAsync_PingFails_WithMySqlException_ReturnsError()
    {
        var fake = new FakeRawConnection
        {
            PingException = MakeMySqlException("ping failed"),
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 3, () => fake).ConfigureAwait(true);
        Assert.Null(pool);
        Assert.True(err.Err());
        Assert.Contains("PingAsync", err.Message);
        Assert.True(fake.WasClosed);
    }

    [Fact]
    public async Task CreateAsync_PingFails_WithCancellation_ReturnsError()
    {
        var fake = new FakeRawConnection
        {
            PingException = new OperationCanceledException("ping timeout"),
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 3, () => fake).ConfigureAwait(true);
        Assert.Null(pool);
        Assert.True(err.Err());
        Assert.Contains("PingAsync", err.Message);
        Assert.True(fake.WasClosed);
    }

    // ── DbConnectionPool.Close ───────────────────────────────────────────────

    [Fact]
    public async Task Close_DrainsPendingConnectionsFromChannel()
    {
        var (pool, fake) = await MakePoolAsync(limit: 3).ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);
        conn!.Dispose();  // returns connection to channel
        pool.Close();
        Assert.True(fake.WasOpened);
    }

    // ── DbConnectionPool.GetAsync — fast path ────────────────────────────────

    [Fact]
    public async Task GetAsync_FastPath_ReturnsIdleConnectionFromChannel()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, err) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(err.Err());
        Assert.NotNull(conn);
        conn!.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task GetAsync_ReusesConnectionOnSecondCall()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn1, _) = await pool.GetAsync().ConfigureAwait(true);
        conn1!.Dispose();  // returns to pool
        var (conn2, err) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(err.Err());
        Assert.NotNull(conn2);
        Assert.Same(conn1, conn2);  // same object reused
        conn2!.Dispose();
        pool.Close();
    }

    // ── DbConnectionPool.GetAsync — grow pool ────────────────────────────────

    [Fact]
    public async Task GetAsync_GrowsPool_WhenBelowLimit()
    {
        int factoryCalls = 0;
        Func<FakeRawConnection> factory = () =>
        {
            factoryCalls++;
            return new FakeRawConnection();
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 5, factory).ConfigureAwait(true);
        Assert.False(err.Err());

        // Fast path: take the existing connection out of the channel
        var (conn1, _) = await pool!.GetAsync().ConfigureAwait(true);

        // Channel empty + below limit → creates a new connection
        var (conn2, err2) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(err2.Err());
        Assert.NotNull(conn2);
        Assert.True(factoryCalls >= 2);

        conn1!.Dispose();
        conn2!.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task GetAsync_GrowPool_OpenFails_ReturnsError()
    {
        bool firstCall = true;
        Func<FakeRawConnection> factory = () =>
        {
            if (firstCall)
            {
                firstCall = false;
                return new FakeRawConnection();
            }
            return new FakeRawConnection { OpenException = MakeMySqlException("fail on second") };
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 5, factory).ConfigureAwait(true);
        Assert.False(err.Err());

        var (conn1, _) = await pool!.GetAsync().ConfigureAwait(true);  // fast path

        // Second GetAsync → creates new connection → fails
        var (conn2, err2) = await pool.GetAsync().ConfigureAwait(true);
        Assert.True(err2.Err());
        Assert.Null(conn2);

        conn1!.Dispose();
        pool.Close();
    }

    // ── DbConnectionPool.GetAsync — at limit ─────────────────────────────────

    [Fact]
    public async Task GetAsync_AtLimit_TimesOut_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync(limit: 1).ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);  // 取走唯一连接

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var (conn2, err) = await pool.GetAsync(cts.Token).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Null(conn2);
        Assert.Contains("timed out", err.Message);

        conn!.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task GetAsync_AtLimit_ReturnsConnectionWhenReleased()
    {
        var (pool, _) = await MakePoolAsync(limit: 1).ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);  // 取走唯一连接

        // Release it after a short delay
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(30).ConfigureAwait(false);
            conn!.Dispose();
        });

        var (conn2, err) = await pool.GetAsync().ConfigureAwait(true);
        await releaseTask.ConfigureAwait(true);

        Assert.False(err.Err());
        Assert.NotNull(conn2);
        conn2!.Dispose();
        pool.Close();
    }

    // ── DbConnectionPool.GetAsync — in-use connection in channel ─────────────

    [Fact]
    public async Task GetAsync_SkipsInUseConnection_AndCreatesNew()
    {
        int factoryCalls = 0;
        Func<FakeRawConnection> factory = () =>
        {
            factoryCalls++;
            return new FakeRawConnection();
        };
        var (pool, err) = await TestPool.CreateAsync("server=fake", 5, factory).ConfigureAwait(true);
        Assert.False(err.Err());

        var (conn, _) = await pool!.GetAsync().ConfigureAwait(true);

        // Mark conn as "in use" via reflection, then put it back directly
        var inUseField = typeof(TestConn).GetField("_inUse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inUseField.SetValue(conn, 1L);
        pool.Put(conn!);  // push the in-use connection back into the channel

        // GetAsync should skip the in-use connection and create a new one
        var (conn2, err2) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(err2.Err());
        Assert.NotNull(conn2);
        Assert.True(factoryCalls >= 2);

        // Unblock so CloseAfterDone background task can finish
        inUseField.SetValue(conn, 0L);
        conn2!.Dispose();
        pool.Close();
    }

    // ── DbConnectionPool.Put — overflow ──────────────────────────────────────

    [Fact]
    public async Task Put_ClosesConnection_WhenChannelFull()
    {
        var (pool, _) = await MakePoolAsync(limit: 1).ConfigureAwait(true);

        // Take the connection out to empty the channel
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);
        // Put it back so channel is full again (1 slot, now taken)
        conn!.Dispose();

        // Create an extra connection and try to Put it (channel full → overflow)
        var extraFake = new FakeRawConnection();
        var extraConn = CreateDbConnection(pool, extraFake);
        pool.Put(extraConn);

        Assert.True(extraFake.WasClosed);
        pool.Close();
    }

    // ── DbConnection.IsInUse ─────────────────────────────────────────────────

    [Fact]
    public async Task IsInUse_ReturnsFalse_Initially()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(conn!.IsInUse());
        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task IsInUse_ReturnsTrue_WhenFlagSet()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var inUseField = typeof(TestConn).GetField("_inUse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inUseField.SetValue(conn, 1L);
        Assert.True(conn!.IsInUse());

        inUseField.SetValue(conn, 0L);
        conn.Dispose();
        pool.Close();
    }

    // ── DbConnection.Dispose — disableReuse path ─────────────────────────────

    [Fact]
    public async Task Dispose_WithDisableReuse_ClosesConnectionInsteadOfReturning()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var field = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(conn, true);

        conn!.Dispose();  // should call CloseAfterDone instead of Put

        // A subsequent GetAsync can succeed (creates a new connection)
        var (conn2, err2) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(err2.Err());
        conn2!.Dispose();
        pool.Close();
    }

    // ── DbConnection.CloseAfterDone — in-use background path ─────────────────

    [Fact]
    public async Task CloseAfterDone_WhenInUse_WaitsAndThenCloses()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var inUseField = typeof(TestConn).GetField("_inUse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inUseField.SetValue(conn, 1L);  // simulate "in use"

        conn!.CloseAfterDone();  // starts background task

        // Release the flag after a short delay so the background task can complete
        await Task.Delay(50).ConfigureAwait(true);
        inUseField.SetValue(conn, 0L);
        await Task.Delay(300).ConfigureAwait(true);  // let background task finish

        pool.Close();
    }

    // ── DbConnection.RemoveCache ──────────────────────────────────────────────

    [Fact]
    public async Task RemoveCache_ReturnsFalse_WhenSqlNotCached()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        bool removed = conn!.RemoveCache("SELECT 1");
        Assert.False(removed);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task RemoveCache_ReturnsTrue_AndDisposesCommand_WhenFound()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        // Execute a query first to populate the cache
        await conn!.ExecuteNonQueryAsync("INSERT INTO t VALUES(1)", []).ConfigureAwait(true);

        bool removed = conn.RemoveCache("INSERT INTO t VALUES(1)");
        Assert.True(removed);

        // Second call should return false (already removed)
        bool removed2 = conn.RemoveCache("INSERT INTO t VALUES(1)");
        Assert.False(removed2);

        conn.Dispose();
        pool.Close();
    }

    // ── DbConnection.ExecuteNonQueryAsync ────────────────────────────────────

    [Fact]
    public async Task ExecuteNonQueryAsync_Success_ReturnsRowsAndLastInsertId()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.NonQueryResult = 5;
        fakeConn.Command.LastInsertedIdValue = 42;

        var (rows, lastId, err) = await conn!.ExecuteNonQueryAsync("INSERT INTO t VALUES(@v)", []).ConfigureAwait(true);
        Assert.False(err.Err());
        Assert.Equal(5, rows);
        Assert.Equal(42, lastId);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_WithParameters_SetsValuesOnCommand()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.NonQueryResult = 1;

        var parameters = new SqlParam[]
        {
            new SqlParam { Name = "@id", DataType = MySqlDbType.Int32, Value = 7 },
        };
        var (rows, _, err) = await conn!.ExecuteNonQueryAsync(
            "UPDATE t SET v=1 WHERE id=@id",
            parameters).ConfigureAwait(true);

        Assert.False(err.Err());
        Assert.Equal(1, rows);
        Assert.True(fakeConn.Command.SetValues.ContainsKey("@id"));
        Assert.Equal(7, fakeConn.Command.SetValues["@id"]);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_PrepareThrowsMySqlException_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.PrepareException = MakeMySqlException("syntax error");

        var (_, _, err) = await conn!.ExecuteNonQueryAsync("BAD SQL", []).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("PrepareAsync", err.Message);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_PrepareThrowsCancellation_SetsDisableReuse()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.PrepareException = new OperationCanceledException();

        var (_, _, err) = await conn!.ExecuteNonQueryAsync("SELECT 1", []).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("PrepareAsync", err.Message);

        var disableField = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)disableField.GetValue(conn)!);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ExecuteThrowsMySqlException_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.NonQueryException = MakeMySqlException("deadlock");

        var (_, _, err) = await conn!.ExecuteNonQueryAsync("DELETE FROM t", []).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("ExecuteNonQueryAsync", err.Message);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ExecuteThrowsCancellation_SetsDisableReuse()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.NonQueryException = new OperationCanceledException();

        var (_, _, err) = await conn!.ExecuteNonQueryAsync("DELETE FROM t", []).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("ExecuteNonQueryAsync", err.Message);

        var disableField = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)disableField.GetValue(conn)!);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_SecondCallUsesCache_SkipsPrepare()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;

        // First call: prepare + execute
        await conn!.ExecuteNonQueryAsync("INSERT INTO t VALUES(1)", []).ConfigureAwait(true);
        Assert.True(fakeConn.Command.WasPrepared);

        // Second call with same SQL: inject prepare error — should hit cache and not call Prepare
        fakeConn.Command.PrepareException = MakeMySqlException("should not be called");
        var (_, _, err) = await conn.ExecuteNonQueryAsync("INSERT INTO t VALUES(1)", []).ConfigureAwait(true);
        Assert.False(err.Err());  // no error because prepare was not called (cache hit)

        conn.Dispose();
        pool.Close();
    }

    // ── DbConnection.ExecuteScalarAsync ──────────────────────────────────────

    [Fact]
    public async Task ExecuteScalarAsync_Success_ReturnsResult()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.ScalarResult = 42L;

        var (result, err) = await conn!.ExecuteScalarAsync("SELECT COUNT(*) FROM t", []).ConfigureAwait(true);
        Assert.False(err.Err());
        Assert.Equal(42L, result);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteScalarAsync_WithParameters_SetsValuesOnCommand()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;

        var parameters = new SqlParam[]
        {
            new SqlParam { Name = "@id", DataType = MySqlDbType.Int32, Value = 99 },
        };
        var (_, err) = await conn!.ExecuteScalarAsync(
            "SELECT * FROM t WHERE id=@id",
            parameters).ConfigureAwait(true);

        Assert.False(err.Err());
        Assert.True(fakeConn.Command.SetValues.ContainsKey("@id"));
        Assert.Equal(99, fakeConn.Command.SetValues["@id"]);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteScalarAsync_ThrowsMySqlException_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.ScalarException = MakeMySqlException("scalar fail");

        var (_, err) = await conn!.ExecuteScalarAsync("SELECT 1", []).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("ExecuteScalarAsync", err.Message);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteScalarAsync_ThrowsCancellation_SetsDisableReuse()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.ScalarException = new OperationCanceledException();

        var (_, err) = await conn!.ExecuteScalarAsync("SELECT 1", []).ConfigureAwait(true);
        Assert.True(err.Err());

        var disableField = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)disableField.GetValue(conn)!);

        conn.Dispose();
        pool.Close();
    }

    // ── DbConnection.ExecuteReaderAsync ──────────────────────────────────────

    [Fact]
    public async Task ExecuteReaderAsync_NullEachRowFunc_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var (count, err) = await conn!.ExecuteReaderAsync("SELECT 1", [], null!).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Equal(0, count);
        Assert.Contains("must set eachRowFunc", err.Message);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_Success_CallsEachRowFuncForEveryRow()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.Reader = new FakeRawReader(3);  // 3 rows

        int rowsSeen = 0;
        var (count, err) = await conn!.ExecuteReaderAsync(
            "SELECT * FROM t",
            [],
            (FakeRawReader _) => { rowsSeen++; return default; }).ConfigureAwait(true);

        Assert.False(err.Err());
        Assert.Equal(3, count);
        Assert.Equal(3, rowsSeen);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_ZeroRows_ReturnsZeroCount()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.Reader = new FakeRawReader(0);

        var (count, err) = await conn!.ExecuteReaderAsync("SELECT * FROM t", [], (FakeRawReader _) => default).ConfigureAwait(true);
        Assert.False(err.Err());
        Assert.Equal(0, count);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_EachRowFuncReturnsError_StopsIteration()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.Reader = new FakeRawReader(5);

        int calls = 0;
        var (count, err) = await conn!.ExecuteReaderAsync(
            "SELECT * FROM t",
            [],
            (FakeRawReader _) =>
            {
                calls++;
                return calls == 2 ? Error.WithLoc(77, "stop on row 2") : default;
            }).ConfigureAwait(true);

        Assert.True(err.Err());
        Assert.Equal(77u, err.Code);
        Assert.Equal(1, count);  // 1 row counted before error
        Assert.Equal(2, calls);  // called twice before stopping

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_WithParameters_SetsValuesOnCommand()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;

        var parameters = new SqlParam[]
        {
            new SqlParam { Name = "@id", DataType = MySqlDbType.Int32, Value = 5 },
        };
        var (_, err) = await conn!.ExecuteReaderAsync(
            "SELECT * FROM t WHERE id=@id",
            parameters,
            (FakeRawReader _) => default).ConfigureAwait(true);

        Assert.False(err.Err());
        Assert.True(fakeConn.Command.SetValues.ContainsKey("@id"));
        Assert.Equal(5, fakeConn.Command.SetValues["@id"]);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_ThrowsMySqlException_ReturnsError()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.ReaderException = MakeMySqlException("reader fail");

        var (_, err) = await conn!.ExecuteReaderAsync("SELECT 1", [], (FakeRawReader _) => default).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("ExecuteReaderAsync", err.Message);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_ThrowsCancellation_SetsDisableReuse()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.ReaderException = new OperationCanceledException();

        var (_, err) = await conn!.ExecuteReaderAsync("SELECT 1", [], (FakeRawReader _) => default).ConfigureAwait(true);
        Assert.True(err.Err());

        var disableField = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)disableField.GetValue(conn)!);

        conn.Dispose();
        pool.Close();
    }

    [Fact]
    public async Task ExecuteReaderAsync_PrepareThrowsCancellation_SetsDisableReuse()
    {
        var (pool, _) = await MakePoolAsync().ConfigureAwait(true);
        var (conn, _) = await pool.GetAsync().ConfigureAwait(true);

        var rawField = typeof(TestConn).GetField("_rawConn", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var fakeConn = (FakeRawConnection)rawField.GetValue(conn)!;
        fakeConn.Command.PrepareException = new OperationCanceledException();

        var (_, err) = await conn!.ExecuteReaderAsync("SELECT SLOW", [], (FakeRawReader _) => default).ConfigureAwait(true);
        Assert.True(err.Err());
        Assert.Contains("PrepareAsync", err.Message);

        var disableField = typeof(TestConn).GetField("_disableReuse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)disableField.GetValue(conn)!);

        conn.Dispose();
        pool.Close();
    }

    // ── FakeRawReader interface coverage ─────────────────────────────────────

    [Fact]
    public void FakeRawReader_InterfaceMethods_ReturnDefaults()
    {
        var reader = new FakeRawReader(0);
        Assert.Equal(0, reader.FieldCount);
        Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetValue(0));
        Assert.False(reader.GetBoolean(0));
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(0UL, reader.GetUInt64(0));
        Assert.Equal(0f, reader.GetFloat(0));
        Assert.Equal(0.0, reader.GetDouble(0));
        Assert.Equal(0m, reader.GetDecimal(0));
        Assert.Equal("", reader.GetString(0));
        Assert.Equal(default, reader.GetDateTime(0));
    }
}
