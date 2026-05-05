#pragma warning disable CS1591
namespace Tests.KestrelWrap;

using QiWa.Common;

/// <summary>
/// 为 ContextBase.Decode 测试准备的假请求对象，用来记录走到哪种解码分支并按需返回错误。
/// </summary>
internal struct TestRequestMessage : IDecoder
{
    public bool FailJson;
    public bool FailProtobuf;
    public string LastDecoder;
    public byte[] LastPayload;

    public Error FromProtobuf(ReadOnlySpan<byte> binary)
    {
        LastDecoder = "protobuf";
        LastPayload = binary.ToArray();
        if (FailProtobuf)
        {
            return Error.WithLoc(code: 902, message: "protobuf decode fail");
        }
        return default;
    }

    public Error FromJSON(ReadOnlySpan<byte> text)
    {
        LastDecoder = "json";
        LastPayload = text.ToArray();
        if (FailJson)
        {
            return Error.WithLoc(code: 901, message: "json decode fail");
        }
        return default;
    }
}
