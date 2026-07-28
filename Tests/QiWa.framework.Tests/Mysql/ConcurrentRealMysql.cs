// 意图：使用真实 MySQL 后端，对 DbConnectionPool 在高并发场景下的行为进行压力测试。
// 要求 127.0.0.1:3306 上有 flutter_admin 数据库且 data_sources 表已存在。
// 验证点：
//   1. 连接池最大连接数限制为 100，200 个并发协程同时随机进行 CRUD 操作时，
//      池内实际创建的连接数（_count）不会超过这个上限。
//   2. 第二轮测试先睡眠 5 分钟，等待 MySQL 服务端因 wait_timeout 主动断开这批
//      空闲连接的底层 TCP，再复用同一个连接池继续并发 CRUD，验证连接池能够
//      正确探测失效连接、丢弃并重建，而不会崩溃或超出连接数上限。
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
public class ConcurrentRealMysqlTests
{
    static ConcurrentRealMysqlTests()
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

    // 连接池允许创建的最大连接数
    private const int PoolLimit = 100;

    // 同时发起 CRUD 操作的并发协程数量
    private const int ConcurrentWorkers = 200;

    // 测试专用数据行名称前缀，各并发协程用各自唯一的名称，避免互相踩踏
    private const string TestDataSourceNamePrefix = "__qiwa_concurrent_test_";

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

    // ── 主测试：200 并发 CRUD + 连接池上限 + 空闲断连自愈 ───────────────────
    //
    // 两轮测试：
    //   round 1：连接池刚建立，200 个协程并发随机执行 CRUD，验证结果正确
    //            且连接池内连接数量始终不超过 PoolLimit。
    //   round 2：round 1 结束后先睡眠 5 分钟，等待 MySQL 服务端因 wait_timeout
    //            主动断开这批空闲 TCP 连接，再复用同一个连接池发起同样规模的
    //            并发 CRUD，验证连接池能探测到失效连接并自愈。
    //            注：需要目标 MySQL 的 wait_timeout 配置小于 5 分钟，此轮测试才有意义。
    [Fact]
    public async Task ConcurrentCrud_200Workers_RespectsPoolLimitAndSurvivesIdleDisconnect()
    {
        var (pool, err) = await MysqlConnectionPool.CreateAsync(ConnectionString, limit: PoolLimit,
            () => new MySqlConnectionWrapper()).ConfigureAwait(true);
        Assert.False(err.Err(), $"CreateAsync failed: {err.Message}");

        try
        {
            await RunConcurrentRoundAsync(pool!, round: 1).ConfigureAwait(true);

            await Task.Delay(TimeSpan.FromMinutes(5)).ConfigureAwait(true);
            await RunConcurrentRoundAsync(pool!, round: 2).ConfigureAwait(true);
        }
        finally
        {
            await CleanupAllAsync(pool!).ConfigureAwait(true);
            pool!.Close();
        }
    }

    // 一轮压测：并发跑完全部 worker 后，检查连接池的连接数量是否仍在预期范围内
    private static async Task RunConcurrentRoundAsync(MysqlConnectionPool pool, int round)
    {
        var tasks = new Task[ConcurrentWorkers];
        for (int i = 0; i < ConcurrentWorkers; i++)
        {
            int workerId = i;
            tasks[workerId] = RunWorkerCrudAsync(pool, round, workerId);
        }
        await Task.WhenAll(tasks).ConfigureAwait(true);

        long count = pool.Count;
        Assert.True(count >= 0 && count <= PoolLimit,
            $"round {round}: pool.Count={count} out of expected range [0, {PoolLimit}]");
    }

    // 单个协程的随机 CRUD 流程：INSERT -> 若干次随机 UPDATE(每次都用 SELECT 验证) -> DELETE -> SELECT 验证已删除
    private static async Task RunWorkerCrudAsync(MysqlConnectionPool pool, int round, int workerId)
    {
        string name = $"{TestDataSourceNamePrefix}r{round}_w{workerId}__";
        var rnd = new Random(unchecked((round * 100000) + workerId));

        // 随机错开各协程首次获取连接的时间点，制造更真实的并发抢占场景
        await Task.Delay(rnd.Next(0, 50)).ConfigureAwait(true);

        try
        {
            // ── INSERT ────────────────────────────────────────────────────────
            var (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
            Assert.False(getErr.Err(), $"[w{workerId}] GetAsync(insert) failed: {getErr.Message}");
            Assert.True(pool.Count <= PoolLimit, $"[w{workerId}] pool.Count={pool.Count} exceeds PoolLimit after insert-Get");

            string host = $"192.168.{rnd.Next(0, 256)}.{rnd.Next(1, 255)}";
            using (conn)
            {
                var (rows, lastId, insErr) = await conn!.ExecuteNonQueryAsync(InsertSql,
                [
                    new SqlParam { Name = "@name",         DataType = MySqlDbType.VarChar, Value = name },
                    new SqlParam { Name = "@host",         DataType = MySqlDbType.VarChar, Value = host },
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

                Assert.False(insErr.Err(), $"[w{workerId}] INSERT failed: {insErr.Message}");
                Assert.Equal(1, rows);
                Assert.True(lastId > 0, $"[w{workerId}] lastInsertId should be > 0");
            }

            // ── 随机次数的 UPDATE，每次都用 SELECT 验证结果是否符合预期 ─────────
            int updateTimes = rnd.Next(0, 3);
            for (int u = 0; u < updateTimes; u++)
            {
                host = $"10.{rnd.Next(0, 256)}.{rnd.Next(0, 256)}.{rnd.Next(1, 255)}";

                (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
                Assert.False(getErr.Err(), $"[w{workerId}] GetAsync(update#{u}) failed: {getErr.Message}");
                Assert.True(pool.Count <= PoolLimit, $"[w{workerId}] pool.Count={pool.Count} exceeds PoolLimit after update-Get");
                using (conn)
                {
                    var (rows, _, updErr) = await conn!.ExecuteNonQueryAsync(UpdateSql,
                    [
                        new SqlParam { Name = "@host", DataType = MySqlDbType.VarChar, Value = host },
                        new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = name },
                    ]).ConfigureAwait(true);

                    Assert.False(updErr.Err(), $"[w{workerId}] UPDATE#{u} failed: {updErr.Message}");
                    Assert.Equal(1, rows);
                }

                (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
                Assert.False(getErr.Err(), $"[w{workerId}] GetAsync(select-after-update#{u}) failed: {getErr.Message}");
                Assert.True(pool.Count <= PoolLimit, $"[w{workerId}] pool.Count={pool.Count} exceeds PoolLimit after select-Get");
                using (conn)
                {
                    string? selectedHost = null;
                    var (rowCount, selErr) = await conn!.ExecuteReaderAsync(SelectSql,
                    [
                        new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = name },
                    ],
                    reader =>
                    {
                        selectedHost = reader.GetString("data_source_host");
                        return default;
                    }).ConfigureAwait(true);

                    Assert.False(selErr.Err(), $"[w{workerId}] SELECT after UPDATE#{u} failed: {selErr.Message}");
                    Assert.Equal(1, rowCount);
                    Assert.Equal(host, selectedHost);
                }
            }

            // ── DELETE ────────────────────────────────────────────────────────
            (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
            Assert.False(getErr.Err(), $"[w{workerId}] GetAsync(delete) failed: {getErr.Message}");
            Assert.True(pool.Count <= PoolLimit, $"[w{workerId}] pool.Count={pool.Count} exceeds PoolLimit after delete-Get");
            using (conn)
            {
                var (rows, _, delErr) = await conn!.ExecuteNonQueryAsync(DeleteSql,
                [
                    new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = name },
                ]).ConfigureAwait(true);

                Assert.False(delErr.Err(), $"[w{workerId}] DELETE failed: {delErr.Message}");
                Assert.Equal(1, rows);
            }

            // ── SELECT — 验证记录已删除 ───────────────────────────────────────
            (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
            Assert.False(getErr.Err(), $"[w{workerId}] GetAsync(verify-delete) failed: {getErr.Message}");
            Assert.True(pool.Count <= PoolLimit, $"[w{workerId}] pool.Count={pool.Count} exceeds PoolLimit after verify-delete-Get");
            using (conn)
            {
                var (count, finalErr) = await conn!.ExecuteScalarAsync(CountSql,
                [
                    new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = name },
                ]).ConfigureAwait(true);

                Assert.False(finalErr.Err(), $"[w{workerId}] SELECT after DELETE failed: {finalErr.Message}");
                Assert.Equal(0L, Convert.ToInt64(count));
            }
        }
        finally
        {
            // 确保测试数据无论成功失败都被清理，避免污染下一轮/下一次运行
            await CleanupOneAsync(pool, name).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 清理单个 worker 产生的测试数据（幂等，可重复调用）。
    /// </summary>
    private static async Task CleanupOneAsync(MysqlConnectionPool pool, string name)
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
                new SqlParam { Name = "@name", DataType = MySqlDbType.VarChar, Value = name },
            ]).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 兜底清理：按前缀删除所有本测试可能残留的数据（幂等，可重复调用）。
    /// </summary>
    private static async Task CleanupAllAsync(MysqlConnectionPool pool)
    {
        var (conn, err) = await pool.GetAsync().ConfigureAwait(true);
        if (err.Err())
        {
            return;
        }
        using (conn)
        {
            await conn!.ExecuteNonQueryAsync(
                "DELETE FROM data_sources WHERE data_source_name LIKE @prefix",
            [
                new SqlParam { Name = "@prefix", DataType = MySqlDbType.VarChar, Value = TestDataSourceNamePrefix + "%" },
            ]).ConfigureAwait(true);
        }
    }
}
#endif
