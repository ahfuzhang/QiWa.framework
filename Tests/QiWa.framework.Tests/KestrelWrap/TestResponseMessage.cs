#pragma warning disable CS1591
namespace Tests.KestrelWrap;

using QiWa.Common;

/// <summary>
/// 为 ContextBase.Encode 测试准备的假响应对象，分别输出 JSON 和 Protobuf 的固定字节序列。
/// </summary>
internal struct TestResponseMessage : IEncoder
{
    public byte[] JsonPayload;
    public byte[] ProtobufPayload;

    public int ProtobufSize() => ProtobufPayload.Length;

    public Error ToProtobuf(ref RentedBuffer buf) => buf.Append(ProtobufPayload);

    public void ToJSON(ref RentedBuffer buf)
    {
        buf.Append(JsonPayload);
    }
}
