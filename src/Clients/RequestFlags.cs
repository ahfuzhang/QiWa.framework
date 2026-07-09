#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Clients;

/// <summary>
/// 以 bit 位的方式，允许用户配置发送请求时候的选项
/// </summary>
public enum RequestFlags : UInt64
{
    /// <summary>
    /// 无意义
    /// </summary>
    None = 0,

    /// <summary>
    /// 使用 JSON 格式编码
    /// </summary>
    UseJSON = 1,  // bit 0

    /// <summary>
    /// 使用 Protobuf 格式编码
    /// </summary>
    UseProtobuf = 2,  // bit 1

    /// <summary>
    /// 使用 zstd 压缩. zstd 优先于 gzip 压缩
    /// </summary>
    UseZstd = 4,  // bit 2

    /// <summary>
    /// 使用 gzip 压缩
    /// </summary>
    UseGzip = 8,  // bit 3

    /// <summary>
    /// 使用 GET 请求。没有这个 bit，则默认就是 POST 请求
    /// </summary>
    UseGet = 16,  // bit 4

}

public enum CompressType : int
{
    NotCompressed = 0,
    Zstd = 1,
    Gzip = 2,
}
