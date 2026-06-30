namespace QiWa.Clients;

/// <summary>
/// Lists the client protocol variants supported by the HTTP client wrapper.
/// </summary>
public enum ClientProtocol
{
    /// <summary>
    /// Indicates that no protocol has been selected.
    /// </summary>
    NotUse = 0,

    /// <summary>
    /// Indicates an HTTP/1 client.
    /// </summary>
    Http1 = 1,

    /// <summary>
    /// Indicates an HTTP/2 client.
    /// </summary>
    Http2 = 2,

    /// <summary>
    /// Indicates a gRPC client.
    /// </summary>
    Grpc = 3
}
