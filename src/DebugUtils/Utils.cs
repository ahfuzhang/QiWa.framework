namespace QiWa.DebugUtils;

using System.Diagnostics;

/// <summary>
/// 提供一些调试相关的工具函数，比如获取异常发生的位置等
/// </summary>
public static class DebugUtils
{
    /// <summary>
    /// 获取异常发生的位置，返回文件名和行号等信息。如果无法获取到位置信息，则返回空字符串。
    /// </summary>
    /// <param name="err"></param>
    /// <returns></returns>
    public static string GetExceptionLocation(Exception err)
    {
        if (err == null)
        {
            return string.Empty;
        }
        var trace = new StackTrace(err, true);
        var frames = trace.GetFrames();
        if (frames == null || frames.Length == 0)
        {
            return string.Empty;
        }
        foreach (var frame in frames)
        {
            var file = frame.GetFileName();
            var line = frame.GetFileLineNumber();
            if (!string.IsNullOrEmpty(file) && line > 0)
            {
                return $"file={file}, line={line}";
            }
        }
        return string.Empty;
    }
}
