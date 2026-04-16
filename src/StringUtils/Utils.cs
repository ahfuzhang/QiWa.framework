#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.StringUtils;

using QiWa.Common;

public static class StringUtils
{
    /// <summary>
    /// 解析 buffer 大小字符串，支持 k/kb/m/mb/g/gb/t/tb/p/pb 后缀（不区分大小写）。
    /// 最大值为 1G。解析失败时返回非零 <see cref="Error"/>，不抛出异常。
    /// </summary>
    /// <param name="size">大小字符串，如 "128k"、"4mb" 等。</param>
    /// <returns>(解析结果字节数，错误信息)；出错时字节数为 0。</returns>
    public static (long, Error) ParseBufferSize(string size)
    {
        const long maxSize = 1024L * 1024 * 1024; // 1G
        size = size.Trim().ToLowerInvariant();
        long multiplier = 1;
        string numPart = size;

        if (size.EndsWith("pb")) { multiplier = 1024L * 1024 * 1024 * 1024 * 1024; numPart = size[..^2]; }
        else if (size.EndsWith('p')) { multiplier = 1024L * 1024 * 1024 * 1024 * 1024; numPart = size[..^1]; }
        else if (size.EndsWith("tb")) { multiplier = 1024L * 1024 * 1024 * 1024; numPart = size[..^2]; }
        else if (size.EndsWith('t')) { multiplier = 1024L * 1024 * 1024 * 1024; numPart = size[..^1]; }
        else if (size.EndsWith("gb")) { multiplier = 1024L * 1024 * 1024; numPart = size[..^2]; }
        else if (size.EndsWith('g')) { multiplier = 1024L * 1024 * 1024; numPart = size[..^1]; }
        else if (size.EndsWith("mb")) { multiplier = 1024L * 1024; numPart = size[..^2]; }
        else if (size.EndsWith('m')) { multiplier = 1024L * 1024; numPart = size[..^1]; }
        else if (size.EndsWith("kb")) { multiplier = 1024L; numPart = size[..^2]; }
        else if (size.EndsWith('k')) { multiplier = 1024L; numPart = size[..^1]; }

        if (!long.TryParse(numPart, out long num)){
            return (0, Error.WithLoc(1, $"Invalid buffer size format: {size}"));
        }
        var result = num * multiplier;
        return (result > maxSize ? maxSize : result, default);
    }
}
