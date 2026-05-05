#pragma warning disable CS1591
namespace Tests.KestrelWrap;

using QiWa.KestrelWrap;

/// <summary>
/// 为 ContextBase 测试补一个可配合 using 使用的轻量包装类型，方便自动释放缓冲区资源。
/// </summary>
internal sealed class ContextBaseTestContext : ContextBase, IDisposable
{
    public new void Dispose() => base.Dispose();
}
