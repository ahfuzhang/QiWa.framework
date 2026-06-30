namespace QiWa.Clients;

using System.Runtime.InteropServices;

/// <summary>
/// Provides default configuration shared by all HTTP protocol versions.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct AllHttpConfig
{
    /// <summary>
    /// Controls whether HTTP handlers store and send cookies automatically.
    /// </summary>
    public bool UseCookies;

    /// <summary>
    /// Controls whether HTTP handlers follow redirect responses automatically.
    /// </summary>
    public bool AllowAutoRedirect;

    /// <summary>
    /// Defines how many response body bytes the handler may discard when a response is not fully consumed.
    /// </summary>
    public int MaxResponseDrainSize;

    /// <summary>
    /// Defines how long the handler may spend draining an unused response body.
    /// </summary>
    public TimeSpan ResponseDrainTimeout;

    /// <summary>
    /// Defines the maximum response header size in kilobytes.
    /// </summary>
    public int MaxResponseHeadersLengthKb;

    /// <summary>
    /// 访问超时时间
    /// </summary>
    public TimeSpan Timeout;

    /// <summary>
    /// 默认超时时间：10s
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1000 * 10);

    /// <summary>
    /// 允许的最大的响应的长度
    /// </summary>
    public int MaxResponseContentBufferSize;

    /// <summary>
    /// 默认最大响应长度 10MB
    /// </summary>
    public const int DefaultMaxResponseContentBufferSize = 1024 * 1024 * 100;

    /// <summary>
    /// Creates the shared HTTP defaults expected by the QiWa HTTP client wrapper.
    /// </summary>
    /// <returns>The default shared HTTP configuration.</returns>
    public static AllHttpConfig New()
    {
        return new AllHttpConfig
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            MaxResponseDrainSize = 0,
            ResponseDrainTimeout = TimeSpan.FromMilliseconds(500),
            MaxResponseHeadersLengthKb = 1024 * 100,
            Timeout = DefaultTimeout,
            MaxResponseContentBufferSize = DefaultMaxResponseContentBufferSize,
        };
    }
}
