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
using QiWa.Mysql;
using Xunit;

[Trait("Category", "Integration")]
public class RealMysqlTests
{
    private const string ConnectionString =
        "Server=127.0.0.1;Port=3306;Database=flutter_admin;User ID=root;Password=root123;" +
        "CharSet=utf8mb4;Maximum Pool Size=100;Connection Reset=false;" +
        "Connection Timeout=15;Default Command Timeout=30;Keepalive=60;";

    // 测试专用唯一名称，避免与正式数据冲突
    private const string TestDataSourceName = "__qiwa_test_data_source__";

    // ── INSERT SQL ────────────────────────────────────────────────────────────

    private const string InsertSql =
        "INSERT INTO data_sources " +
        "(data_source_name, data_source_host, data_source_port, data_source_user, " +
        " data_source_pwd, data_source_db, charset, max_pool_size, connection_reset, " +
        " connection_time_out_seconds, command_timeout_seconds, options) " +
        "VALUES (@name, @host, @port, @user, @pwd, @db, @charset, @max_pool, @reset, @conn_timeout, @cmd_timeout, @options)";

    private static readonly Dictionary<string, MySqlDbType> InsertParams = new()
    {
        ["@name"]        = MySqlDbType.VarChar,
        ["@host"]        = MySqlDbType.VarChar,
        ["@port"]        = MySqlDbType.Int32,
        ["@user"]        = MySqlDbType.VarChar,
        ["@pwd"]         = MySqlDbType.VarChar,
        ["@db"]          = MySqlDbType.VarChar,
        ["@charset"]     = MySqlDbType.VarChar,
        ["@max_pool"]    = MySqlDbType.Int32,
        ["@reset"]       = MySqlDbType.Bool,
        ["@conn_timeout"]= MySqlDbType.Int32,
        ["@cmd_timeout"] = MySqlDbType.Int32,
        ["@options"]     = MySqlDbType.VarChar,
    };

    // ── SELECT SQL ────────────────────────────────────────────────────────────

    private const string SelectSql =
        "SELECT data_source_id, data_source_name, data_source_host, data_source_port " +
        "FROM data_sources WHERE data_source_name = @name";

    private static readonly Dictionary<string, MySqlDbType> SelectParams = new()
    {
        ["@name"] = MySqlDbType.VarChar,
    };

    // ── UPDATE SQL ────────────────────────────────────────────────────────────

    private const string UpdateSql =
        "UPDATE data_sources SET data_source_host = @host WHERE data_source_name = @name";

    private static readonly Dictionary<string, MySqlDbType> UpdateParams = new()
    {
        ["@host"] = MySqlDbType.VarChar,
        ["@name"] = MySqlDbType.VarChar,
    };

    // ── DELETE SQL ────────────────────────────────────────────────────────────

    private const string DeleteSql =
        "DELETE FROM data_sources WHERE data_source_name = @name";

    private static readonly Dictionary<string, MySqlDbType> DeleteParams = new()
    {
        ["@name"] = MySqlDbType.VarChar,
    };

    // ── COUNT SQL (用于验证行是否存在) ─────────────────────────────────────

    private const string CountSql =
        "SELECT COUNT(*) FROM data_sources WHERE data_source_name = @name";

    // ── 主测试：完整 CRUD 流程 ─────────────────────────────────────────────

    [Fact]
    public async Task CRUD_InsertQueryUpdateQueryDelete_DataSources()
    {
        var (pool, err) = await DbConnectionPool.CreateAsync(ConnectionString, limit: 3).ConfigureAwait(true);
        Assert.False(err.Err(), $"CreateAsync failed: {err.Message}");

        try
        {
            await RunCrudAsync(pool!).ConfigureAwait(true);
        }
        finally
        {
            // 确保测试数据无论成功失败都被清理
            await CleanupAsync(pool!).ConfigureAwait(true);
            pool!.Close();
        }
    }

    private static async Task RunCrudAsync(DbConnectionPool pool)
    {
        // ── Step 1: INSERT ────────────────────────────────────────────────────
        var (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err(), $"GetAsync failed: {getErr.Message}");
        using (conn)
        {
            var (rows, lastId, insErr) = await conn!.ExecuteNonQueryAsync(
                InsertSql, InsertParams,
                cmd =>
                {
                    cmd.SetParameterValue("@name",         TestDataSourceName);
                    cmd.SetParameterValue("@host",         "192.168.1.100");
                    cmd.SetParameterValue("@port",         3306);
                    cmd.SetParameterValue("@user",         "test_user");
                    cmd.SetParameterValue("@pwd",          "test_pwd");
                    cmd.SetParameterValue("@db",           "test_db");
                    cmd.SetParameterValue("@charset",      "utf8mb4");
                    cmd.SetParameterValue("@max_pool",     10);
                    cmd.SetParameterValue("@reset",        false);
                    cmd.SetParameterValue("@conn_timeout", 15);
                    cmd.SetParameterValue("@cmd_timeout",  30);
                    cmd.SetParameterValue("@options",      "");
                    return default;
                }).ConfigureAwait(true);

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
            var (rowCount, selErr) = await conn!.ExecuteReaderAsync(
                SelectSql, SelectParams,
                cmd => { cmd.SetParameterValue("@name", TestDataSourceName); return default; },
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
        }

        // ── Step 3: UPDATE ────────────────────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            var (rows, _, updErr) = await conn!.ExecuteNonQueryAsync(
                UpdateSql, UpdateParams,
                cmd =>
                {
                    cmd.SetParameterValue("@host", "10.0.0.1");
                    cmd.SetParameterValue("@name", TestDataSourceName);
                    return default;
                }).ConfigureAwait(true);

            Assert.False(updErr.Err(), $"UPDATE failed: {updErr.Message}");
            Assert.Equal(1, rows);
        }

        // ── Step 4: SELECT — 验证更新结果 ────────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            string? host = null;
            var (rowCount, selErr) = await conn!.ExecuteReaderAsync(
                SelectSql, SelectParams,
                cmd => { cmd.SetParameterValue("@name", TestDataSourceName); return default; },
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
            var (rows, _, delErr) = await conn!.ExecuteNonQueryAsync(
                DeleteSql, DeleteParams,
                cmd => { cmd.SetParameterValue("@name", TestDataSourceName); return default; }).ConfigureAwait(true);

            Assert.False(delErr.Err(), $"DELETE failed: {delErr.Message}");
            Assert.Equal(1, rows);
        }

        // ── Step 6: SELECT — 验证记录已删除 ──────────────────────────────────
        (conn, getErr) = await pool.GetAsync().ConfigureAwait(true);
        Assert.False(getErr.Err());
        using (conn)
        {
            var (count, finalErr) = await conn!.ExecuteScalarAsync(
                CountSql, SelectParams,
                cmd => { cmd.SetParameterValue("@name", TestDataSourceName); return default; }).ConfigureAwait(true);

            Assert.False(finalErr.Err(), $"SELECT after DELETE failed: {finalErr.Message}");
            Assert.Equal(0L, Convert.ToInt64(count));
        }
    }

    /// <summary>
    /// 确保测试数据被清理（幂等，可重复调用）。
    /// </summary>
    private static async Task CleanupAsync(DbConnectionPool pool)
    {
        var (conn, err) = await pool.GetAsync().ConfigureAwait(true);
        if (err.Err())
        {
            return;
        }
        using (conn)
        {
            await conn!.ExecuteNonQueryAsync(
                DeleteSql, DeleteParams,
                cmd => { cmd.SetParameterValue("@name", TestDataSourceName); return default; }).ConfigureAwait(true);
        }
    }
}
#endif
