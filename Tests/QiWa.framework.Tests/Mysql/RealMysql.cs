// 意图：使用真实 MySQL 后端对 DbConnectionPool / DbConnection 进行集成测试。
// 要求 127.0.0.1:3306 上有 flutter_admin 数据库且 data_sources 表已存在。
// 测试流程：INSERT → 查询验证 → UPDATE → 查询验证 → DELETE → 查询验证已删除。
//
// 默认不参与编译。需要传入宏 INTEGRATION_TESTS 才会编译和运行：
//   dotnet test -p:DefineConstants=INTEGRATION_TESTS
#if INTEGRATION_TESTS
#pragma warning disable CS1591
namespace Tests.Mysql;

using MySqlConnector;
using QiWa.Common;
using QiWa.ConsoleLogger;
using QiWa.Mysql;
using Xunit;

using MysqlConnectionPool = QiWa.Mysql.DbConnectionPool<QiWa.Mysql.MySqlConnectionWrapper, QiWa.Mysql.MySqlCommandWrapper, QiWa.Mysql.MySqlReaderWrapper>;

[Trait("Category", "Integration")]
public class RealMysqlTests
{
    static RealMysqlTests()
    {
        // DbConnectionPool / DbConnection 内部使用 ThreadLocalLogger 输出调试日志，
        // 若未先初始化 Logger 会导致日志组件未就绪而报错，因此在测试类初始化时先调用 Logger.Init()。
        Logger.Init(LogLevel.Debug, 100);
    }

    private const string DefaultConnectionString =
        "Server=127.0.0.1;Port=3306;Database=flutter_admin;User ID=root;Password=root123;" +
        "CharSet=utf8mb4;Maximum Pool Size=100;Connection Reset=false;" +
        "Connection Timeout=15;Default Command Timeout=30;Keepalive=60;";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MYSQL_DSN") is { Length: > 0 } dsn ? dsn : DefaultConnectionString;

    // 测试专用唯一名称，避免与正式数据冲突
    private const string TestDataSourceName = "__qiwa_test_data_source__";

    // ── INSERT SQL ────────────────────────────────────────────────────────────

    private const string InsertSql =
        "INSERT INTO data_sources " +
        "(data_source_name, data_source_host, data_source_port, data_source_user, " +
        " data_source_pwd, data_source_db, charset, max_pool_size, connection_reset, " +
        " connection_time_out_seconds, command_timeout_seconds, options) " +
        "VALUES (@name, @host, @port, @user, @pwd, @db, @charset, @max_pool, @reset, @conn_timeout, @cmd_timeout, @options)";

    // ── SELECT SQL ────────────────────────────────────────────────────────────

    private const string SelectSql =
        "SELECT data_source_id, data_source_name, data_source_host, data_source_port " +
        "FROM data_sources WHERE data_source_name = @name";

    // ── UPDATE SQL ────────────────────────────────────────────────────────────

    private const string UpdateSql =
        "UPDATE data_sources SET data_source_host = @host WHERE data_source_name = @name";

    // ── DELETE SQL ────────────────────────────────────────────────────────────

    private const string DeleteSql =
        "DELETE FROM data_sources WHERE data_source_name = @name";

    // ── COUNT SQL (用于验证行是否存在) ─────────────────────────────────────

    private const string CountSql =
        "SELECT COUNT(*) FROM data_sources WHERE data_source_name = @name";

    // ── 主测试：完整 CRUD 流程 ─────────────────────────────────────────────
    //
    // 两轮测试：
    //   round 1 (simulateIdleDisconnect = false): happy path，与连接池刚建立时的正常流程一致。
    //   round 2 (simulateIdleDisconnect = true):  连接池建立后先睡眠 5 分钟，
    //            等待 MySQL 服务端因 wait_timeout 主动断开这些空闲 TCP 连接，
    //            再复用同一批连接对象执行 CRUD，从而触发“连接对象仍存活但底层 TCP
    //            已被服务端异常中断”场景下连接池的探测与自愈行为。
    //            注：需要目标 MySQL 的 wait_timeout 配置小于 5 分钟，此轮测试才有意义。

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CRUD_InsertQueryUpdateQueryDelete_DataSources(bool simulateIdleDisconnect)
    {
        var (pool, err) = await MysqlConnectionPool.CreateAsync(ConnectionString, limit: 3,
            () => new MySqlConnectionWrapper()).ConfigureAwait(true);
        Assert.False(err.Err(), $"CreateAsync failed: {err.Message}");

        try
        {
            if (simulateIdleDisconnect)
            {
                await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            }
            await RunCrudAsync(pool!).ConfigureAwait(true);
        }
        finally
        {
            // 确保测试数据无论成功失败都被清理
            await CleanupAsync(pool!).ConfigureAwait(true);
            pool!.Close();
        }
    }

    private static async Task RunCrudAsync(DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper> pool)
    {
        // ── Step 1: INSERT ────────────────────────────────────────────────────
        var (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err(), $"GetAsync failed: {getErr.Message}");
        using (conn)
        {
            // 验证连接存活探测正常工作
            var pingErr = await conn!.PingAsync().ConfigureAwait(true);
            Assert.False(pingErr.Err(), $"PingAsync failed: {pingErr.Message}");

            var (rows, lastId, insErr) = await conn!.ExecuteNonQueryAsync(InsertSql,
            [
                new SqlParam { Name = "@name",         DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
                new SqlParam { Name = "@host",         DataType = MySqlDbType.VarChar, Value = "192.168.1.100" },
                new SqlParam { Name = "@port",         DataType = MySqlDbType.Int32,   Value = 3306 },
                new SqlParam { Name = "@user",         DataType = MySqlDbType.VarChar, Value = "test_user" },
                new SqlParam { Name = "@pwd",          DataType = MySqlDbType.VarChar, Value = "test_pwd" },
                new SqlParam { Name = "@db",           DataType = MySqlDbType.VarChar, Value = "test_db" },
                new SqlParam { Name = "@charset",      DataType = MySqlDbType.VarChar, Value = "utf8mb4" },
                new SqlParam { Name = "@max_pool",     DataType = MySqlDbType.Int32,   Value = 10 },
                new SqlParam { Name = "@reset",        DataType = MySqlDbType.Bool,    Value = false },
                new SqlParam { Name = "@conn_timeout", DataType = MySqlDbType.Int32,   Value = 15 },
                new SqlParam { Name = "@cmd_timeout",  DataType = MySqlDbType.Int32,   Value = 30 },
                new SqlParam { Name = "@options",      DataType = MySqlDbType.VarChar, Value = "" },
            ]).ConfigureAwait(true);

            Assert.False(insErr.Err(), $"INSERT failed: {insErr.Message}");
            Assert.Equal(1, rows);
            Assert.True(lastId > 0, "lastInsertId should be > 0");
        }

        // ── Step 2: SELECT — 验证插入结果 ────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            string? host = null;
            long id = 0;
            var (rowCount, selErr) = await conn!.ExecuteReaderAsync(SelectSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ],
            reader =>
            {
                id   = reader.GetInt64("data_source_id");
                host = reader.GetString("data_source_host");
                return default;
            }).ConfigureAwait(true);

            Assert.False(selErr.Err(), $"SELECT after INSERT failed: {selErr.Message}");
            Assert.Equal(1, rowCount);
            Assert.Equal("192.168.1.100", host);
            Assert.True(id > 0);

            // 清空已缓存的 prepared statement，验证清空后同一条 SQL 仍可正常重新准备并执行
            conn.ClearPreparedStatements();

            host = null;
            var (rowCountAfterClear, selErrAfterClear) = await conn.ExecuteReaderAsync(SelectSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ],
            reader =>
            {
                host = reader.GetString("data_source_host");
                return default;
            }).ConfigureAwait(true);

            Assert.False(selErrAfterClear.Err(), $"SELECT after ClearPreparedStatements failed: {selErrAfterClear.Message}");
            Assert.Equal(1, rowCountAfterClear);
            Assert.Equal("192.168.1.100", host);
        }

        // ── Step 3: UPDATE ────────────────────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            var (rows, _, updErr) = await conn!.ExecuteNonQueryAsync(UpdateSql,
            [
                new SqlParam { Name = "@host", DataType = MySqlDbType.VarChar, Value = "10.0.0.1" },
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ]).ConfigureAwait(true);

            Assert.False(updErr.Err(), $"UPDATE failed: {updErr.Message}");
            Assert.Equal(1, rows);
        }

        // ── Step 4: SELECT — 验证更新结果 ────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            string? host = null;
            var (rowCount, selErr) = await conn!.ExecuteReaderAsync(SelectSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ],
            reader =>
            {
                host = reader.GetString("data_source_host");
                return default;
            }).ConfigureAwait(true);

            Assert.False(selErr.Err(), $"SELECT after UPDATE failed: {selErr.Message}");
            Assert.Equal(1, rowCount);
            Assert.Equal("10.0.0.1", host);
        }

        // ── Step 5: DELETE ────────────────────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            var (rows, _, delErr) = await conn!.ExecuteNonQueryAsync(DeleteSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ]).ConfigureAwait(true);

            Assert.False(delErr.Err(), $"DELETE failed: {delErr.Message}");
            Assert.Equal(1, rows);
        }

        // ── Step 6: SELECT — 验证记录已删除 ──────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            var (count, finalErr) = await conn!.ExecuteScalarAsync(CountSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ]).ConfigureAwait(true);

            Assert.False(finalErr.Err(), $"SELECT after DELETE failed: {finalErr.Message}");
            Assert.Equal(0L, Convert.ToInt64(count));
        }
    }

    /// <summary>
    /// 确保测试数据被清理（幂等，可重复调用）。
    /// </summary>
    private static async Task CleanupAsync(DbConnectionPool<MySqlConnectionWrapper, MySqlCommandWrapper, MySqlReaderWrapper> pool)
    {
        var (conn, err) = await pool.GetAsync().ConfigureAwait(true);
        if (err.Err())
        {
            return;
        }
        using (conn)
        {
            await conn!.ExecuteNonQueryAsync(DeleteSql,
            [
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = TestDataSourceName },
            ]).ConfigureAwait(true);
        }
    }
}
#endif
