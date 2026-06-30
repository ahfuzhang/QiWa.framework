namespace QiWa.Clients;

using System.Runtime.InteropServices;

/// <summary>
/// Provides HTTP/2-specific client configuration.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct Http2ClientConfig
{
    /// <summary>
    /// Controls whether the handler opens extra HTTP/2 connections when one connection cannot provide enough concurrency.
    /// </summary>
    public bool EnableMultipleHttp2Connections;

    /// <summary>
    /// Defines the interval between HTTP/2 keep-alive ping frames.
    /// </summary>
    public TimeSpan KeepAlivePingDelay;

    /// <summary>
    /// Defines how long the handler waits for an HTTP/2 keep-alive ping response.
    /// </summary>
    public TimeSpan KeepAlivePingTimeout;

    /// <summary>
    /// Defines when HTTP/2 keep-alive pings are sent.
    /// </summary>
    public HttpKeepAlivePingPolicy KeepAlivePingPolicy;

    /// <summary>
    /// Defines the initial HTTP/2 stream flow-control window size in bytes.
    /// </summary>
    public int InitialHttp2StreamWindowSize;

    /// <summary>
    /// Defines the default maximum HTTP/2 connections per CPU core.
    /// </summary>
    public const int DefaultMaxConnectionsPerServerPerCore = 2;

    /// <summary>
    /// Limits the maximum HTTP/2 connections to the same endpoint.
    /// </summary>
    public int MaxConnectionsPerServer;

    /// <summary>
    /// Creates the HTTP/2 defaults for the supplied CPU core count.
    /// </summary>
    /// <param name="cores">The CPU core count used to scale the connection limit.</param>
    /// <returns>The default HTTP/2 configuration.</returns>
    public static Http2ClientConfig New(int cores)
    {
        return new Http2ClientConfig
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromMilliseconds(1000 * 2),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            InitialHttp2StreamWindowSize = 1024 * 1024 * 10,
            MaxConnectionsPerServer = DefaultMaxConnectionsPerServerPerCore * cores,
        };
    }
}
