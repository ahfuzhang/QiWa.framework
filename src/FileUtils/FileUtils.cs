#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
namespace QiWa.FileUtils;

using QiWa.Common;

public static class FileUtils
{
    /// <summary>
    /// 如果一次性加载文件的所有内容到内存，所允许的支持的最大文件长度，100mb
    /// </summary>
    private const Int64 READ_ALL_ALLOWED_MAX_BYTES = 1024 * 1024 * 100L;
    private const int default_file_buffer_size = 64 * 1024;

    /// <summary>
    /// 一次性把一个文件的全部内容读到内存。最大支持 100mb
    /// </summary>
    /// <param name="inputPath"> 文件路径 </param>
    /// <returns>
    ///   RentedBuffer: 文件内容的数组。数组是从内存池借用的，使用完成后需要归还。
    ///   Error
    /// </returns>
    public static async Task<System.ValueTuple<RentedBuffer, Error>> ReadAllAndRentAync(string inputPath)
    {
        int totalRead = 0;
        try
        {
            using var stream = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: default_file_buffer_size,
                useAsync: true);
            if (stream.Length > READ_ALL_ALLOWED_MAX_BYTES)
            {
                return (default, Error.WithLoc(code: 1, message: $"Input file is too large: {stream.Length} bytes."));
            }
            int length = (int)stream.Length;
            if (length == 0)
            {
                return (default, Error.WithLoc(code: 2, message: "Loaded 0 bytes."));
            }
            RentedBuffer rent = new RentedBuffer(length);
            while (totalRead < length)
            {
                int read;
                try
                {
                    // 按本次提示词修复 CA1835，改用 Memory<byte> 重载，避免旧的数组偏移量重载告警。
                    read = await stream.ReadAsync(rent.Data!.AsMemory(totalRead, length - totalRead)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    rent.Dispose();
                    return (default, Error.WithLoc(code: 3, message: $"ReadAsync fail: {ex.Message}"));
                }
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }
            rent.Length = totalRead;
            if (totalRead != length)
            {
                rent.Dispose();
                return (default, Error.WithLoc(code: 3, message: $"not read all:{totalRead}/{length}" ));
            }
            return (rent, default);
        }
        catch (Exception ex)
        {
            return (default, Error.WithLoc(code: 4, message: $"Exception:{ex.Message}"));
        }
    }
}
