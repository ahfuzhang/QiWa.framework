namespace QiWa.Mysql;

/// <summary>
/// Abstracts the lifecycle of a raw database connection.
/// Implemented by the real MySqlConnection wrapper and by fakes for unit testing.
/// </summary>
public interface IRawConnection : IDisposable
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
    IRawCommand CreateCommand();
}
