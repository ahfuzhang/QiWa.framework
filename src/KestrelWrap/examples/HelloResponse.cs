#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace examples;

using QiWa.Common;

// 响应格式
public struct HelloResponse : QiWa.Common.IResettable, QiWa.Common.IEncoder
{
    public void Reset()
    {

    }

    public int ProtobufSize()
    {
        return 0;
    }

    public Error ToProtobuf(ref RentedBuffer buf)
    {
        return default;
    }

    public void ToJSON(ref RentedBuffer buf)
    {

    }
}
