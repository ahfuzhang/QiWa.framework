namespace QiWa.Mysql;

using MySqlConnector;

/// <summary>
/// Wraps a real <see cref="MySqlConnection"/> to satisfy <see cref="IRawConnection{TCmd,TReader}"/>.
/// This is the production implementation; tests substitute a fake.
/// </summary>
public sealed class MySqlConnectionWrapper : IRawConnection<MySqlCommandWrapper, MySqlReaderWrapper>
{
    private readonly MySqlConnection _conn = new();

    /// <inheritdoc/>
    public string ConnectionString
    {
        get => _conn.ConnectionString;
        set => _conn.ConnectionString = value;
    }

    /// <inheritdoc/>
    public Task OpenAsync(CancellationToken ct) => _conn.OpenAsync(ct);

    /// <inheritdoc/>
    public Task PingAsync(CancellationToken ct) => _conn.PingAsync(ct);

    /// <inheritdoc/>
    public Task CloseAsync() => _conn.CloseAsync();

    /// <inheritdoc/>
    public void Close() => _conn.Close();

    /// <inheritdoc/>
    public MySqlCommandWrapper CreateCommand() => new MySqlCommandWrapper(_conn.CreateCommand());

    /// <inheritdoc/>
    public void Dispose() => _conn.Dispose();

    public bool IsOpen()
    {
        return _conn.State == System.Data.ConnectionState.Open;
    }

    // public System.Data.ConnectionState State()
    // {
    //     return _conn.State;
    // }
}

/// <summary>
/// Wraps a real <see cref="MySqlCommand"/> to satisfy <see cref="IRawCommand{TReader}"/>.
/// </summary>
public sealed class MySqlCommandWrapper : IRawCommand<MySqlReaderWrapper>
{
    private readonly MySqlCommand _cmd;

    internal MySqlCommandWrapper(MySqlCommand cmd) => _cmd = cmd;

    /// <inheritdoc/>
    public string CommandText
    {
        get => _cmd.CommandText;
        set => _cmd.CommandText = value;
    }

    /// <inheritdoc/>
    public long LastInsertedId => _cmd.LastInsertedId;

    /// <inheritdoc/>
    public void AddParameter(string name, MySqlDbType type) => _cmd.Parameters.Add(name, type);

    /// <inheritdoc/>
    public void SetParameterValue(string name, object? value) => _cmd.Parameters[name].Value = value;

    /// <inheritdoc/>
    public Task PrepareAsync(CancellationToken ct) => _cmd.PrepareAsync(ct);

    /// <inheritdoc/>
    public Task<int> ExecuteNonQueryAsync(CancellationToken ct) => _cmd.ExecuteNonQueryAsync(ct);

    /// <inheritdoc/>
    public Task<object?> ExecuteScalarAsync(CancellationToken ct) => _cmd.ExecuteScalarAsync(ct);

    /// <inheritdoc/>
    public async Task<MySqlReaderWrapper> ExecuteReaderAsync(CancellationToken ct)
    {
        var reader = await _cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return new MySqlReaderWrapper(reader);
    }

    /// <inheritdoc/>
    public void Dispose() => _cmd.Dispose();
}

/// <summary>
/// Wraps a real <see cref="MySqlDataReader"/> to satisfy <see cref="IRawReader"/>.
/// String-column overloads are provided as concrete methods so they are accessible
/// without casting to <see cref="IRawReader"/> (C# default interface methods are not
/// promoted to the implementing class automatically).
/// </summary>
public sealed class MySqlReaderWrapper : IRawReader
{
    private readonly MySqlDataReader _reader;

    internal MySqlReaderWrapper(MySqlDataReader reader) => _reader = reader;

    /// <inheritdoc/>
    public Task<bool> ReadAsync(CancellationToken ct) => _reader.ReadAsync(ct);

    /// <inheritdoc/>
    public int FieldCount => _reader.FieldCount;

    /// <inheritdoc/>
    public int GetOrdinal(string columnName) => _reader.GetOrdinal(columnName);

    // ── IsDBNull ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);
    /// <inheritdoc/>
    public bool IsDBNull(string columnName) => IsDBNull(GetOrdinal(columnName));

    // ── GetValue ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public object? GetValue(int ordinal) => _reader.GetValue(ordinal);
    /// <inheritdoc/>
    public object? GetValue(string columnName) => GetValue(GetOrdinal(columnName));

    // ── GetBoolean ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);
    /// <inheritdoc/>
    public bool GetBoolean(string columnName) => GetBoolean(GetOrdinal(columnName));

    // ── GetInt32 ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public int GetInt32(int ordinal) => _reader.GetInt32(ordinal);
    /// <inheritdoc/>
    public int GetInt32(string columnName) => GetInt32(GetOrdinal(columnName));

    // ── GetInt64 ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public long GetInt64(int ordinal) => _reader.GetInt64(ordinal);
    /// <inheritdoc/>
    public long GetInt64(string columnName) => GetInt64(GetOrdinal(columnName));

    // ── GetUInt64 ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public ulong GetUInt64(int ordinal) => _reader.GetUInt64(ordinal);
    /// <inheritdoc/>
    public ulong GetUInt64(string columnName) => GetUInt64(GetOrdinal(columnName));

    // ── GetFloat ──────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public float GetFloat(int ordinal) => _reader.GetFloat(ordinal);
    /// <inheritdoc/>
    public float GetFloat(string columnName) => GetFloat(GetOrdinal(columnName));

    // ── GetDouble ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public double GetDouble(int ordinal) => _reader.GetDouble(ordinal);
    /// <inheritdoc/>
    public double GetDouble(string columnName) => GetDouble(GetOrdinal(columnName));

    // ── GetDecimal ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);
    /// <inheritdoc/>
    public decimal GetDecimal(string columnName) => GetDecimal(GetOrdinal(columnName));

    // ── GetString ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string GetString(int ordinal) => _reader.GetString(ordinal);
    /// <inheritdoc/>
    public string GetString(string columnName) => GetString(GetOrdinal(columnName));

    // ── GetDateTime ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);
    /// <inheritdoc/>
    public DateTime GetDateTime(string columnName) => GetDateTime(GetOrdinal(columnName));

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _reader.DisposeAsync();
}
