namespace QiWa.Mysql;

/// <summary>
/// Abstracts row-by-row reading from a query result set.
/// Implemented by the real MySqlDataReader wrapper and by fakes for unit testing.
///
/// 列可以通过序号 (int ordinal) 或字段名 (string columnName) 两种方式寻址。
/// string 重载由接口默认实现，内部调用 <see cref="GetOrdinal"/> 再转发到对应的 int 重载，
/// 实现类只需实现 int 重载和 <see cref="GetOrdinal"/> 即可。
/// </summary>
public interface IRawReader : IAsyncDisposable
{
    /// <summary>Advances to the next row. Returns false when no more rows are available.</summary>
    Task<bool> ReadAsync(CancellationToken ct);

    /// <summary>Number of columns in the current result set.</summary>
    int FieldCount { get; }

    /// <summary>Returns the zero-based ordinal of the column with the given name.</summary>
    /// <exception cref="IndexOutOfRangeException">The column name is not found.</exception>
    int GetOrdinal(string columnName);

    // ── IsDBNull ────────────────────────────────────────────────────────────

    /// <summary>Returns true if the column at <paramref name="ordinal"/> is NULL.</summary>
    bool IsDBNull(int ordinal);

    /// <summary>Returns true if the column named <paramref name="columnName"/> is NULL.</summary>
    bool IsDBNull(string columnName) => IsDBNull(GetOrdinal(columnName));

    // ── GetValue ─────────────────────────────────────────────────────────────

    /// <summary>Returns the raw value of the column at <paramref name="ordinal"/>.</summary>
    object? GetValue(int ordinal);

    /// <summary>Returns the raw value of the column named <paramref name="columnName"/>.</summary>
    object? GetValue(string columnName) => GetValue(GetOrdinal(columnName));

    // ── GetBoolean ───────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="bool"/>.</summary>
    bool GetBoolean(int ordinal);

    /// <summary>Returns the column value as <see cref="bool"/>.</summary>
    bool GetBoolean(string columnName) => GetBoolean(GetOrdinal(columnName));

    // ── GetInt32 ─────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="int"/>.</summary>
    int GetInt32(int ordinal);

    /// <summary>Returns the column value as <see cref="int"/>.</summary>
    int GetInt32(string columnName) => GetInt32(GetOrdinal(columnName));

    // ── GetInt64 ─────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="long"/>.</summary>
    long GetInt64(int ordinal);

    /// <summary>Returns the column value as <see cref="long"/>.</summary>
    long GetInt64(string columnName) => GetInt64(GetOrdinal(columnName));
    // ── GetUInt64 ─────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="ulong"/>.</summary>
    ulong GetUInt64(int ordinal);

    /// <summary>Returns the column value as <see cref="ulong"/>.</summary>
    ulong GetUInt64(string columnName) => GetUInt64(GetOrdinal(columnName));

    // ── GetFloat ─────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="float"/>.</summary>
    float GetFloat(int ordinal);

    /// <summary>Returns the column value as <see cref="float"/>.</summary>
    float GetFloat(string columnName) => GetFloat(GetOrdinal(columnName));

    // ── GetDouble ────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="double"/>.</summary>
    double GetDouble(int ordinal);

    /// <summary>Returns the column value as <see cref="double"/>.</summary>
    double GetDouble(string columnName) => GetDouble(GetOrdinal(columnName));

    // ── GetDecimal ───────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="decimal"/>.</summary>
    decimal GetDecimal(int ordinal);

    /// <summary>Returns the column value as <see cref="decimal"/>.</summary>
    decimal GetDecimal(string columnName) => GetDecimal(GetOrdinal(columnName));

    // ── GetString ────────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="string"/>.</summary>
    string GetString(int ordinal);

    /// <summary>Returns the column value as <see cref="string"/>.</summary>
    string GetString(string columnName) => GetString(GetOrdinal(columnName));

    // ── GetDateTime ──────────────────────────────────────────────────────────

    /// <summary>Returns the column value as <see cref="DateTime"/>.</summary>
    DateTime GetDateTime(int ordinal);

    /// <summary>Returns the column value as <see cref="DateTime"/>.</summary>
    DateTime GetDateTime(string columnName) => GetDateTime(GetOrdinal(columnName));
}
