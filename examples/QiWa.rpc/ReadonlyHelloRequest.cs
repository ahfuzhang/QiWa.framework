#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

namespace examples;

using QiWa.Common;

// 请求格式
public struct ReadonlyHelloRequest : QiWa.Common.IResettable, QiWa.Common.IDecoder
{
    public Error FromProtobuf(ReadOnlySpan<byte> binary)
    {
        return default;
    }

    public Error FromJSON(ReadOnlySpan<byte> text)
    {
        return default;
    }

    public void Reset()
    {

    }
}
