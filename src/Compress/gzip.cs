using System.Buffers;
using System.Buffers.Binary;
using LibDeflate;
using QiWa.Common;

namespace QiWa.Compress;

/// <summary>
/// Provides whole-buffer gzip compression and decompression through LibDeflate.NET.
/// </summary>
/// <remarks>
/// Intent from the prompt: avoid the GZipStream write-path crash recorded in
/// <c>崩溃信息.txt</c>, while minimizing allocations and copies for fixed-size blocks.
/// </remarks>
public static class GzipCompressor
{
    /// <summary>
    /// Limits decompressed data to this multiple of the compressed input, preventing gzip bombs.
    /// </summary>
    public const int MaxDecompressRatio = 20;

    /// <summary>
    /// Reuses a native compressor per thread so normal calls do not allocate managed or native compressor objects.
    /// </summary>
    [ThreadStatic]
    private static LibDeflate.GzipCompressor? _compressor;

    /// <summary>
    /// Reuses a native decompressor per thread so normal calls do not allocate managed or native decompressor objects.
    /// </summary>
    [ThreadStatic]
    private static GzipDecompressor? _decompressor;

    /// <summary>
    /// Compresses one complete input block into a gzip frame.
    /// </summary>
    /// <param name="input">The uncompressed block.</param>
    /// <param name="reserve">Bytes retained at the beginning for a caller-owned framing header, such as gRPC.</param>
    /// <returns>A rented buffer containing the reserved prefix followed by the gzip frame, or an error.</returns>
    public static (RentedBuffer, Error) Compress(ReadOnlySpan<byte> input, int reserve = 0)
    {
        if (reserve < 0)
        {
            return (default, Error.WithLoc(code: 1, message: "GZip Compress fail: reserve cannot be negative"));
        }

        try
        {
            LibDeflate.GzipCompressor compressor = _compressor ??= new LibDeflate.GzipCompressor(compressionLevel: 6);
            int compressedBound = compressor.GetBound(input.Length);
            RentedBuffer destination = new(checked(reserve + compressedBound));
            int written = compressor.Compress(input, destination.Data.AsSpan(reserve, compressedBound));
            if (written <= 0)
            {
                destination.Dispose();
                return (default, Error.WithLoc(code: 1, message: "GZip Compress fail: LibDeflate returned no output"));
            }

            destination.Length = reserve + written;
            return (destination, default);
        }
        catch (Exception ex)
        {
            return (default, Error.WithLoc(code: 1, message: $"GZip Compress fail: {ex.Message}"));
        }
    }

    /// <summary>
    /// Compresses one complete input block and appends its gzip frame to an existing rented buffer.
    /// </summary>
    /// <param name="dst">The destination buffer whose existing contents are retained.</param>
    /// <param name="input">The uncompressed block.</param>
    /// <returns>An error when compression cannot produce a complete gzip frame.</returns>
    public static Error Compress(ref RentedBuffer dst, ReadOnlySpan<byte> input)
    {
        try
        {
            LibDeflate.GzipCompressor compressor = _compressor ??= new LibDeflate.GzipCompressor(compressionLevel: 6);
            int compressedBound = compressor.GetBound(input.Length);
            dst.Extend(compressedBound);
            int written = compressor.Compress(input, dst.Data.AsSpan(dst.Length, compressedBound));
            if (written <= 0)
            {
                return Error.WithLoc(code: 1, message: "GZip Compress fail: LibDeflate returned no output");
            }

            dst.Length += written;
            return default;
        }
        catch (Exception ex)
        {
            return Error.WithLoc(code: 1, message: $"GZip Compress fail: {ex.Message}");
        }
    }
    /// <summary>
    /// Decompresses one complete gzip block using its ISIZE trailer as the exact destination size.
    /// </summary>
    /// <param name="compressed">The complete gzip frame.</param>
    /// <returns>A rented buffer containing the uncompressed block, or an error.</returns>
    public static (RentedBuffer, Error) Uncompress(ReadOnlySpan<byte> compressed)
    {
        Error sizeError = GetUncompressedSize(compressed, out int uncompressedSize);
        if (sizeError.Err())
        {
            return (default, sizeError);
        }

        RentedBuffer destination = new(uncompressedSize);
        try
        {
            GzipDecompressor decompressor = _decompressor ??= new GzipDecompressor();
            OperationStatus status = decompressor.Decompress(compressed, destination.Data.AsSpan(0, uncompressedSize), out int written, out int bytesRead);
            if (status != OperationStatus.Done || written != uncompressedSize || bytesRead != compressed.Length)
            {
                destination.Dispose();
                return (default, InvalidGzip($"LibDeflate status={status}, written={written}, consumed={bytesRead}"));
            }

            destination.Length = written;
            return (destination, default);
        }
        catch (Exception ex)
        {
            destination.Dispose();
            return (default, InvalidGzip(ex.Message));
        }
    }

    /// <summary>
    /// Validates a complete gzip frame and obtains the exact uncompressed block size from its ISIZE trailer.
    /// </summary>
    /// <param name="compressed">The gzip frame to validate.</param>
    /// <param name="uncompressedSize">The validated destination size.</param>
    /// <returns>An error when the frame is too short, unsafe, or exceeds supported block sizes.</returns>
    private static Error GetUncompressedSize(ReadOnlySpan<byte> compressed, out int uncompressedSize)
    {
        uncompressedSize = 0;
        if (compressed.Length < 18)
        {
            return InvalidGzip("frame is shorter than the 18-byte gzip minimum");
        }

        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(compressed[^4..]);
        if (declaredSize > int.MaxValue)
        {
            return InvalidGzip("declared size exceeds the supported block size");
        }

        uncompressedSize = (int)declaredSize;
        return uncompressedSize / compressed.Length > MaxDecompressRatio
            ? Error.WithLoc(code: 3, message: $"GZip bomb detected: decompressed size exceeds {MaxDecompressRatio}x compressed size")
            : default;
    }

    /// <summary>
    /// Decompresses one complete gzip block and appends its contents to an existing rented buffer.
    /// </summary>
    /// <param name="dst">The destination buffer whose existing contents are retained.</param>
    /// <param name="compressed">The complete gzip frame.</param>
    /// <returns>An error when the gzip frame is invalid, unsafe, or cannot be fully decoded.</returns>
    public static Error Uncompress(ref RentedBuffer dst, ReadOnlySpan<byte> compressed)
    {
        Error sizeError = GetUncompressedSize(compressed, out int uncompressedSize);
        if (sizeError.Err())
        {
            return sizeError;
        }

        try
        {
            GzipDecompressor decompressor = _decompressor ??= new GzipDecompressor();
            dst.Extend(uncompressedSize);
            OperationStatus status = decompressor.Decompress(compressed, dst.Data.AsSpan(dst.Length, uncompressedSize), out int written, out int bytesRead);
            if (status != OperationStatus.Done || written != uncompressedSize || bytesRead != compressed.Length)
            {
                return InvalidGzip($"LibDeflate status={status}, written={written}, consumed={bytesRead}");
            }

            dst.Length += written;
            return default;
        }
        catch (Exception ex)
        {
            return InvalidGzip(ex.Message);
        }
    }
    /// <summary>
    /// Creates the established gzip-data error shape used by current callers.
    /// </summary>
    /// <param name="detail">The validation or decoder failure detail.</param>
    /// <returns>An error whose code identifies invalid or unsafe gzip data.</returns>
    private static Error InvalidGzip(string detail)
    {
        return Error.WithLoc(code: 3, message: $"GZip bomb detected: {detail}");
    }
}
