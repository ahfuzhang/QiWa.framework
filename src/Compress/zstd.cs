namespace QiWa.Compress;

using QiWa.Common;

/// <summary>
/// 提供 zstd 压缩和解压功能，基于 ZstdSharp 库实现。
/// </summary>
public class ZstdCompressor
{
    [ThreadStatic]
    private static ZstdSharp.Compressor? _compressor;
    [ThreadStatic]
    private static ZstdSharp.Decompressor? _decompressor;

    /// <summary>
    /// 对数据进行 zstd 压缩
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static (RentedBuffer, Error) Compress(ReadOnlySpan<byte> input)
    {
        var compressor = _compressor ??= new ZstdSharp.Compressor();
        int bound = ZstdSharp.Compressor.GetCompressBound(input.Length);
        RentedBuffer dst = new RentedBuffer(bound);
        bool success = compressor.TryWrap(input, dst.Data.AsSpan(), out int written);
        if (!success || written <= 0)
        {
            // 当 input 的长度为 0 时，仍然也会出现 9 字节的 zstd 空帧。因此压缩总是会成功。
            dst.Dispose();
            return (default, Error.WithLoc(code: 1, message: $"compressor.TryWrap fail, written={written}"));
        }
        dst.Length = written;
        return (dst, default);
    }

    /// <summary>
    /// 对数据进行 zstd 解压
    /// </summary>
    /// <param name="compressed"></param>
    /// <returns></returns>
    public static (RentedBuffer, Error) Uncompress(ReadOnlySpan<byte> compressed)
    {
        var decompressor = _decompressor ??= new ZstdSharp.Decompressor();
        ulong size;
        try
        {
            size = ZstdSharp.Decompressor.GetDecompressedSize(compressed);
        }
        catch (System.Exception ex)
        {
            return (default, Error.WithLoc(code: 3, message: $"GetDecompressedSize fail: {ex.Message}"));
        }
        RentedBuffer dst = new RentedBuffer((int)size);
        bool success;
        int written;
        try
        {
            success = decompressor.TryUnwrap(compressed, dst.Data.AsSpan(), out written);
        }
        catch (System.Exception ex)
        {
            dst.Dispose();
            return (default, Error.WithLoc(code: 4, message: $"decompressor.TryUnwrap fail: {ex.Message}"));
        }
        if (!success)
        {
            dst.Dispose();
            return (default, Error.WithLoc(code: 4, message: "decompressor.TryUnwrap fail"));
        }
        dst.Length = written;
        return (dst, default);
    }
}
