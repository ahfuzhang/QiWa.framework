namespace QiWa.Clients;

using System.Runtime.InteropServices;

/// <summary>
/// Provides the complete default configuration for QiWa HTTP clients.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct HttpClientConfig
{
    /// <summary>
    /// Stores TCP-level client configuration.
    /// </summary>
    public TcpClientConfig Tcp;

    /// <summary>
    /// Stores HTTP handler configuration shared by every protocol version.
    /// </summary>
    public AllHttpConfig Http;

    /// <summary>
    /// Stores HTTP/1-specific client configuration.
    /// </summary>
    public Http1ClientConfig Http1;

    /// <summary>
    /// Stores HTTP/2-specific client configuration.
    /// </summary>
    public Http2ClientConfig Http2;

    /// <summary>
    /// Creates the complete client defaults so VS Code can load the solution through a successful design-time build.
    /// </summary>
    /// <param name="cores">The CPU core count used to scale protocol-specific connection limits.</param>
    /// <returns>The default HTTP client configuration.</returns>
    public static HttpClientConfig New(int cores)
    {
        return new HttpClientConfig
        {
            Tcp = TcpClientConfig.New(),
            Http = AllHttpConfig.New(),
            Http1 = Http1ClientConfig.New(cores),
            Http2 = Http2ClientConfig.New(cores),
        };
    }
}
