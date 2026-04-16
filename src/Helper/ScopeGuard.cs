#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.Helper;

/// <summary>
/// 这个类是为了提供类似 golang 中的 defer 语句的效果
/// 也可以在代码中连续写多个 `using () using () {}` 来代替
/// </summary>
/// <example>
/// ```csharp
/// // 退出作用域时，自动调用 Cleanup() 函数。本质上是 try...finally 的另一个写法
/// using (var _ = new ScopeGuard(() => Cleanup(obj))){
///     Step1();
///     Step2();
/// }
/// ```
/// </example>
public struct ScopeGuard : IDisposable
{
    private Action? _onDispose;

    public ScopeGuard(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose()
    {
        _onDispose?.Invoke();
        _onDispose = null;
    }
}
