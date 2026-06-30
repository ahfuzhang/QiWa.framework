namespace QiWa.Mysql;

using MySqlConnector;

/// <summary>
/// Wraps a real <see cref="MySqlConnection"/> to satisfy <see cref="IRawConnection"/>.
/// This is the production implementation; tests substitute a fake.
/// </summary>
internal sealed class MySqlConnectionWrapper : IRawConnection
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
    public IRawCommand CreateCommand() => new MySqlCommandWrapper(_conn.CreateCommand());

    /// <inheritdoc/>
    public void Dispose() => _conn.Dispose();
}

/// <summary>
/// Wraps a real <see cref="MySqlCommand"/> to satisfy <see cref="IRawCommand"/>.
/// </summary>
internal sealed class MySqlCommandWrapper : IRawCommand
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
    public Task PrepareAsync(CancellationToken ct)
        => _cmd.PrepareAsync(ct);

    /// <inheritdoc/>
    public Task<int> ExecuteNonQueryAsync(CancellationToken ct)
        => _cmd.ExecuteNonQueryAsync(ct);

    /// <inheritdoc/>
    public Task<object?> ExecuteScalarAsync(CancellationToken ct)
        => _cmd.ExecuteScalarAsync(ct);

    /// <inheritdoc/>
    public async Task<IRawReader> ExecuteReaderAsync(CancellationToken ct)
    {
        var reader = await _cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return new MySqlReaderWrapper(reader);
    }

    /// <inheritdoc/>
    public void Dispose() => _cmd.Dispose();
}

/// <summary>
/// Wraps a real <see cref="MySqlDataReader"/> to satisfy <see cref="IRawReader"/>.
/// </summary>
internal sealed class MySqlReaderWrapper : IRawReader
{
    private readonly MySqlDataReader _reader;

    internal MySqlReaderWrapper(MySqlDataReader reader) => _reader = reader;

    /// <inheritdoc/>
    public Task<bool> ReadAsync(CancellationToken ct) => _reader.ReadAsync(ct);

    /// <inheritdoc/>
    public int FieldCount => _reader.FieldCount;

    /// <inheritdoc/>
    public int GetOrdinal(string columnName) => _reader.GetOrdinal(columnName);

    /// <inheritdoc/>
    public bool IsDBNull(int ordinal) => _reader.IsDBNull(ordinal);

    /// <inheritdoc/>
    public object? GetValue(int ordinal) => _reader.GetValue(ordinal);

    /// <inheritdoc/>
    public bool GetBoolean(int ordinal) => _reader.GetBoolean(ordinal);

    /// <inheritdoc/>
    public int GetInt32(int ordinal) => _reader.GetInt32(ordinal);

    /// <inheritdoc/>
    public long GetInt64(int ordinal) => _reader.GetInt64(ordinal);

    /// <inheritdoc/>
    public float GetFloat(int ordinal) => _reader.GetFloat(ordinal);

    /// <inheritdoc/>
    public double GetDouble(int ordinal) => _reader.GetDouble(ordinal);

    /// <inheritdoc/>
    public decimal GetDecimal(int ordinal) => _reader.GetDecimal(ordinal);

    /// <inheritdoc/>
    public string GetString(int ordinal) => _reader.GetString(ordinal);

    /// <inheritdoc/>
    public DateTime GetDateTime(int ordinal) => _reader.GetDateTime(ordinal);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _reader.DisposeAsync();
}
