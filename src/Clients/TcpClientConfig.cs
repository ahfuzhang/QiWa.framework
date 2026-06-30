namespace QiWa.Clients;

using System.Runtime.InteropServices;

/// <summary>
/// Provides TCP-level configuration for HTTP client handlers.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct TcpClientConfig
{
    /// <summary>
    /// Defines the default maximum connection lifetime used to refresh DNS-backed connections.
    /// </summary>
    public static readonly TimeSpan DefaultPooledConnectionLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Defines the default idle timeout before an unused pooled connection is closed.
    /// </summary>
    public static readonly TimeSpan DefaultPooledConnectionIdleTimeout = TimeSpan.FromSeconds(2 * 60);

    /// <summary>
    /// Defines the default TCP connection establishment timeout.
    /// </summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Defines the maximum connection lifetime used to refresh DNS-backed connections.
    /// </summary>
    public TimeSpan PooledConnectionLifetime;

    /// <summary>
    /// Defines the idle timeout before an unused pooled connection is closed.
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout;

    /// <summary>
    /// Defines the TCP connection establishment timeout.
    /// </summary>
    public TimeSpan ConnectTimeout;

    /// <summary>
    /// Creates TCP defaults without using invalid const TimeSpan fields, matching the VS Code solution load fix request.
    /// </summary>
    /// <returns>The default TCP client configuration.</returns>
    public static TcpClientConfig New()
    {
        return new TcpClientConfig
        {
            PooledConnectionLifetime = DefaultPooledConnectionLifetime,
            PooledConnectionIdleTimeout = DefaultPooledConnectionIdleTimeout,
            ConnectTimeout = DefaultConnectTimeout,
        };
    }
}
