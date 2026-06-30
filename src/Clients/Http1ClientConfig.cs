namespace QiWa.Clients;

using System.Runtime.InteropServices;

/// <summary>
/// Provides HTTP/1-specific client configuration.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct Http1ClientConfig
{
    /// <summary>
    /// Defines the default maximum HTTP/1 connections per backend server and per CPU core.
    /// </summary>
    public const int DefaultMaxConnectionsPerServerPerCore = 200;

    /// <summary>
    /// Limits the maximum HTTP/1 connections to the same endpoint.
    /// </summary>
    public int MaxConnectionsPerServer;

    /// <summary>
    /// Creates the HTTP/1 defaults for the supplied CPU core count.
    /// </summary>
    /// <param name="cores">The CPU core count used to scale the connection limit.</param>
    /// <returns>The default HTTP/1 configuration.</returns>
    public static Http1ClientConfig New(int cores)
    {
        return new Http1ClientConfig
        {
            MaxConnectionsPerServer = DefaultMaxConnectionsPerServerPerCore * cores,
        };
    }
}
