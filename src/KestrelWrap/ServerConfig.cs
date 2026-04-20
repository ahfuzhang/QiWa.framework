#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace QiWa.KestrelWrap;

/// <summary>
/// 服务器配置
/// 可以在编译期就决定最适合当前业务的服务器规格。
/// 如果需要定制化的服务器配置，可以在编译期修改这个结构体。
/// </summary>
public struct ServerConfig
{
    public const int MaxRequestSize = 1024 * 1024 * 1; // 1MB
    public const int DefaultRequestSize = 1024 * 4;
    public const int MaxCocurrentCount = 10000 * 8;  // 假定支持的最大并发数。8 核，每核 10000/s
}
