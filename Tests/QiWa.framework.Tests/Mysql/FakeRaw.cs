#pragma warning disable CS1591
namespace Tests.Mysql;

using MySqlConnector;
using QiWa.Mysql;

/// <summary>
/// Fake IRawConnection for unit tests. Allows injecting errors for each lifecycle operation.
/// </summary>
internal sealed class FakeRawConnection : IRawConnection<FakeRawCommand, FakeRawReader>
{
    /// <summary>Tracks whether the fake connection is currently open.</summary>
    private bool _isOpen;

    /// <summary>Exception thrown by OpenAsync, or null to succeed.</summary>
    public Exception? OpenException { get; set; }

    /// <summary>Exception thrown by PingAsync, or null to succeed.</summary>
    public Exception? PingException { get; set; }

    /// <summary>Exception thrown by ResetConnectionAsync, or null to succeed.</summary>
    public Exception? ResetConnectionException { get; set; }

    public string ConnectionString { get; set; } = "";
    public bool WasOpened { get; set; }
    public bool WasClosed { get; set; }
    public bool WasDisposed { get; set; }
    public bool WasReset { get; set; }

    /// <summary>The command returned by CreateCommand(); shared so tests can configure it.</summary>
    public FakeRawCommand Command { get; } = new FakeRawCommand();

    public Task OpenAsync(CancellationToken ct)
    {
        if (OpenException != null)
        {
            throw OpenException;
        }
        WasOpened = true;
        _isOpen = true;
        return Task.CompletedTask;
    }

    public Task PingAsync(CancellationToken ct)
    {
        if (PingException != null)
        {
            throw PingException;
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        WasClosed = true;
        _isOpen = false;
        return Task.CompletedTask;
    }

    public void Close()
    {
        WasClosed = true;
        _isOpen = false;
    }

    public FakeRawCommand CreateCommand() => Command;

    /// <summary>Reports the fake connection state required by connection-pool reuse tests.</summary>
    public bool IsOpen() => _isOpen;

    public ValueTask ResetConnectionAsync(CancellationToken cancellationToken)
    {
        if (ResetConnectionException != null)
        {
            throw ResetConnectionException;
        }
        WasReset = true;
        _isOpen = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        WasDisposed = true;
        _isOpen = false;
    }
}

/// <summary>
/// Fake IRawCommand for unit tests. Supports error injection and configurable return values.
/// </summary>
internal sealed class FakeRawCommand : IRawCommand<FakeRawReader>
{
    /// <summary>Exception thrown by PrepareAsync, or null to succeed.</summary>
    public Exception? PrepareException { get; set; }

    /// <summary>Exception thrown by ExecuteNonQueryAsync, or null to succeed.</summary>
    public Exception? NonQueryException { get; set; }

    /// <summary>Exception thrown by ExecuteScalarAsync, or null to succeed.</summary>
    public Exception? ScalarException { get; set; }

    /// <summary>Exception thrown by ExecuteReaderAsync, or null to succeed.</summary>
    public Exception? ReaderException { get; set; }

    /// <summary>Value returned by ExecuteNonQueryAsync.</summary>
    public int NonQueryResult { get; set; } = 1;

    /// <summary>Auto-increment ID returned after a non-query execution.</summary>
    public long LastInsertedIdValue { get; set; }

    /// <summary>Value returned by ExecuteScalarAsync.</summary>
    public object? ScalarResult { get; set; }

    /// <summary>Reader returned by ExecuteReaderAsync.</summary>
    public FakeRawReader Reader { get; set; } = new FakeRawReader(0);

    public string CommandText { get; set; } = "";
    public long LastInsertedId => LastInsertedIdValue;
    public bool WasPrepared { get; private set; }
    public bool WasDisposed { get; private set; }

    public readonly List<(string name, MySqlDbType type)> AddedParameters = new();
    public readonly Dictionary<string, object?> SetValues = new();

    public void AddParameter(string name, MySqlDbType type) => AddedParameters.Add((name, type));

    public void SetParameterValue(string name, object? value) => SetValues[name] = value;

    public Task PrepareAsync(CancellationToken ct)
    {
        if (PrepareException != null)
        {
            throw PrepareException;
        }
        WasPrepared = true;
        return Task.CompletedTask;
    }

    public Task<int> ExecuteNonQueryAsync(CancellationToken ct)
    {
        if (NonQueryException != null)
        {
            throw NonQueryException;
        }
        return Task.FromResult(NonQueryResult);
    }

    public Task<object?> ExecuteScalarAsync(CancellationToken ct)
    {
        if (ScalarException != null)
        {
            throw ScalarException;
        }
        return Task.FromResult(ScalarResult);
    }

    public Task<FakeRawReader> ExecuteReaderAsync(CancellationToken ct)
    {
        if (ReaderException != null)
        {
            throw ReaderException;
        }
        return Task.FromResult(Reader);
    }

    public void Dispose() => WasDisposed = true;
}

/// <summary>
/// Fake IRawReader for unit tests. Yields a fixed number of rows then stops.
/// </summary>
internal sealed class FakeRawReader : IRawReader
{
    private int _remaining;

    /// <param name="rowCount">How many times ReadAsync returns true before returning false.</param>
    public FakeRawReader(int rowCount) => _remaining = rowCount;

    public Task<bool> ReadAsync(CancellationToken ct)
        => Task.FromResult(_remaining-- > 0);

    public int FieldCount => 0;

    /// <summary>Always returns 0 — fake reader has no real schema.</summary>
    public int GetOrdinal(string columnName) => 0;

    public bool IsDBNull(int ordinal) => true;
    public object? GetValue(int ordinal) => null;
    public bool GetBoolean(int ordinal) => false;
    public int GetInt32(int ordinal) => 0;
    public long GetInt64(int ordinal) => 0L;
    public ulong GetUInt64(int ordinal) => 0UL;
    public float GetFloat(int ordinal) => 0f;
    public double GetDouble(int ordinal) => 0.0;
    public decimal GetDecimal(int ordinal) => 0m;
    public string GetString(int ordinal) => "";
    public DateTime GetDateTime(int ordinal) => default;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
