#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Clients;

/// <summary>
/// HttpClient 对象的错误码汇总
/// </summary>
public enum ClientErrorCode : uint
{
    Success = 0,
    ZstdCompressError = 1001,
    GzipCompressError = 1002,
    UnknownDataSerializeTypeError = 1003,
    HttpMethodNotSupportError = 1004,
    GrpcStatusError = 1005,
    BadGrpcResponseError = 1006,
    ZstdDecompressError = 1007,
    GzipDecompressError = 1008,
    CompressTypeNotSupportError = 1009,
    HttpRequestExceptionError = 1010,
    OperationCanceledExceptionError = 1011,
    ParamError = 1012,
}
