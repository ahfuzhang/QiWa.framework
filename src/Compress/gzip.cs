using System.IO.Compression;
using QiWa.Common;

namespace QiWa.Compress;

/// <summary>
/// 提供 gzip 压缩和解压功能。
/// </summary>
public class GzipCompressor
{
    /// <summary>
    /// 解压缩后的数据最多允许是压缩数据的倍数，防止 gzip 炸弹攻击
    /// </summary>
    public const int MaxDecompressRatio = 20;

    /// <summary>
    /// 自定义只写 Stream，直接写入 RentedBuffer，避免中间拷贝
    /// </summary>
    private sealed class RentedBufferWriteStream : Stream
    {
        public RentedBuffer Buffer;

        public void Reset(int initialCapacity)
        {
            if (Buffer.Data == null)
            {
                Buffer = new RentedBuffer(initialCapacity);
                return;
            }
            if (Buffer.Data.Length < initialCapacity)
            {
                Buffer.Dispose();
                Buffer = new RentedBuffer(initialCapacity);
                return;
            }
            Buffer.Length = 0;
        }

        public void Reserve(int len)
        {
            Buffer.Length = len;
        }

        /// <summary>
        /// 将所有权转交给调用者，stream 不再持有该 buffer 的引用
        /// </summary>
        public Common.RentedBuffer TakeBuffer()
        {
            var result = Buffer;
            Buffer = default;
            return result;
        }

        public void DisposeBuffer() => Buffer.Dispose();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Buffer.Length;
        public override long Position { get => Buffer.Length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Buffer.Extend(count);
            buffer.AsSpan(offset, count).CopyTo(Buffer.Data.AsSpan(Buffer.Length));  // todo:  发生了拷贝，值得优化
            Buffer.Length += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Buffer.Extend(buffer.Length);
            buffer.CopyTo(Buffer.Data.AsSpan(Buffer.Length));
            Buffer.Length += buffer.Length;
        }
    }

    [ThreadStatic]
    private static RentedBufferWriteStream? _compressStream;
    [ThreadStatic]
    private static RentedBufferWriteStream? _decompressStream;

    /// <summary>
    /// 对数据进行 gzip 压缩
    /// </summary>
    /// <param name="input"></param>
    /// <param name="reserve"></param>
    /// <returns></returns>
    public static (RentedBuffer, Error) Compress(ReadOnlySpan<byte> input, int reserve = 0)
    {
        var stream = _compressStream ??= new RentedBufferWriteStream();
        stream.Reset(Math.Max(input.Length, 64));
        stream.Reserve(reserve);  // 在压缩 grpc 的返回的时候，可以用于预留 5 字节的 grpc 首部
        try
        {
            using var gzip = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            gzip.Write(input);
            gzip.Flush();
        }
        catch (System.Exception ex)
        {
            stream.DisposeBuffer();
            return (default, Error.WithLoc(code: 1, message: $"GZip Compress fail: {ex.Message}"));
        }
        return (stream.TakeBuffer(), default);
    }

    /// <summary>
    /// 对数据进行 gzip 解压。解压缩后的大小最多允许是压缩数据的 MaxDecompressRatio 倍，防止 gzip 炸弹攻击。
    /// </summary>
    /// <param name="compressed"></param>
    /// <returns></returns>
    public static (RentedBuffer, Error) Uncompress(ReadOnlySpan<byte> compressed)
    {
        var stream = _decompressStream ??= new RentedBufferWriteStream();
        // gzip 尾部最后 4 字节为 ISIZE（原始大小 mod 2^32，小端序）
        // 有效范围：gzip 最小合法帧为 18 字节（10字节头 + 2字节空块 + 4字节CRC + 4字节ISIZE）
        var initialSize = compressed.Length >= 18
            ? (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(compressed[^4..])
            : 0;
        if (initialSize / compressed.Length > MaxDecompressRatio)
        {
            return (default, Error.WithLoc(code: 3, message: $"GZip bomb detected: decompressed size exceeds {MaxDecompressRatio}x compressed size"));
        }
        stream.Reset(Math.Max(initialSize, 256));
        try
        {
            var inputMs = new MemoryStream(compressed.ToArray(), 0, compressed.Length, writable: false);
            using var gzip = new GZipStream(inputMs, CompressionMode.Decompress, leaveOpen: true);
            gzip.CopyTo(stream);
        }
        catch (InvalidDataException ex)
        {
            stream.DisposeBuffer();
            return (default, Error.WithLoc(code: 3, message: $"GZip bomb detected: {ex.Message}"));
        }
        catch (System.Exception ex)
        {
            stream.DisposeBuffer();
            return (default, Error.WithLoc(code: 2, message: $"GZip Uncompress fail: {ex.Message}"));
        }

        return (stream.TakeBuffer(), default);
    }
}
