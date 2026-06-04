namespace QiWa.KestrelWrap;

using QiWa.Common;

/// <summary>
/// 要求实现 Run() 方法
/// </summary>
public interface IRunnable
{
    /// <summary>
    /// 实现 Run 方法
    /// </summary>
    /// <returns>Error 对象，运行时是否发生错误</returns>
    ValueTask<Error> RunAsync();
}
