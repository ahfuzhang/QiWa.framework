namespace QiWa.Mysql;

/// <summary>
/// Abstracts the lifecycle of a raw database connection.
/// Generic over <typeparamref name="TCmd"/> and <typeparamref name="TReader"/> so concrete types
/// flow through without casting.
/// Implemented by the real MySqlConnection wrapper and by fakes for unit testing.
/// </summary>
public interface IRawConnection<TCmd, TReader> : IDisposable
    where TCmd : IRawCommand<TReader>
    where TReader : IRawReader
{
    /// <summary>Gets or sets the connection string.</summary>
    string ConnectionString { get; set; }

    /// <summary>Opens the connection asynchronously.</summary>
    Task OpenAsync(CancellationToken ct);

    /// <summary>Pings the server to verify the connection is alive.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Closes the connection asynchronously.</summary>
    Task CloseAsync();

    /// <summary>Closes the connection synchronously.</summary>
    void Close();

    /// <summary>Creates a new command for this connection.</summary>
    TCmd CreateCommand();

    /// <summary>
    /// 连接是否处于打开状态
    /// </summary>
    /// <returns></returns>
    bool IsOpen();

    /// <summary>
    /// Resets the connection's session state so it can be safely reused from a pool.
    /// see: github.com/mysql-net/MySqlConnector/src/MySqlConnector/MySqlConnection.cs
    /// </summary>
    ValueTask ResetConnectionAsync(CancellationToken cancellationToken);
}
