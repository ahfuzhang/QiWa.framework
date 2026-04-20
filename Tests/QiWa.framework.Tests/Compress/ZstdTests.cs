using QiWa.Common;
using QiWa.Compress;
using Xunit;

namespace CompressTests;

public class ZstdTests
{
    public struct TestCase
    {
        public string Name;
        public byte[] Input;
        public bool IsCompressedInput; // If true, Uncompress directly. If false, Compress then Uncompress.
        public bool ExpectCompressError;
        public bool ExpectUncompressError;
        public uint ExpectedErrorCode;
        public string ExpectedErrorMsgContains;
    }

    [Fact]
    public void TestZstd_TableDriven()
    {
        var random = new Random(42);
        var largeData = new byte[1024 * 1024]; // 1MB
        random.NextBytes(largeData);

        // Construct corrupted data with Checksum enabled (to force TryUnwrap fail)
        using var compressorWithChecksum = new ZstdSharp.Compressor();
        compressorWithChecksum.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        var srcSpan = System.Text.Encoding.UTF8.GetBytes("hello checksum");
        var boundChecks = ZstdSharp.Compressor.GetCompressBound(srcSpan.Length);
        var dstChecks = new byte[boundChecks];
        compressorWithChecksum.TryWrap(srcSpan, dstChecks, out int writtenChecks);
        var corruptBody = dstChecks.Take(writtenChecks).ToArray();

        // Corrupt the body (middle byte, to avoid header damage)
        if (corruptBody.Length > 10)
        {
            corruptBody[corruptBody.Length / 2] ^= 0xFF;
        }

        var testCases = new List<TestCase> {
            new TestCase {
                Name = "Normal String",
                Input = System.Text.Encoding.UTF8.GetBytes("Hello, World!"),
                IsCompressedInput = false
            },
            new TestCase {
                Name = "Empty Input",
                Input = Array.Empty<byte>(),
                IsCompressedInput = false
            },
            new TestCase {
                Name = "Large Random Data",
                Input = largeData,
                IsCompressedInput = false
            },
            new TestCase {
                Name = "Garbage Data for Uncompress (Unknown size)",
                Input = new byte[] { 0x01, 0x02, 0x03, 0x04 }, // Not a valid Zstd frame
                IsCompressedInput = true,
                ExpectUncompressError = true,
                ExpectedErrorCode = 3, // Code = 3, Message = "Unknown decompressed size"
                ExpectedErrorMsgContains = "GetDecompressedSize fail"
            },
            new TestCase {
                Name = "Corrupt Body (TryUnwrap fail)",
                Input = corruptBody,
                IsCompressedInput = true,
                ExpectUncompressError = true,
                ExpectedErrorCode = 4, // Code = 4, Message = "decompressor.TryUnwrap fail"
                ExpectedErrorMsgContains = "decompressor.TryUnwrap fail"
            },
            new TestCase {
                Name = "Truncated Body",
                Input = System.Text.Encoding.UTF8.GetBytes("truncated"), // Placeholder
                IsCompressedInput = true,
                ExpectUncompressError = true,
                // If GetDecompressedSize fails, it's code 3. If it succeeds but Unwrap fails, code 4.
                // Truncation usually causes Code 3 if frame structure is broken.
                ExpectedErrorCode = 3,
                ExpectedErrorMsgContains = "GetDecompressedSize fail"
            },
            new TestCase {
                Name = "Lying Header (Content Size < Actual)",
                Input = System.Text.Encoding.UTF8.GetBytes("lying"), // Placeholder
                IsCompressedInput = true,
                ExpectUncompressError = true,
                ExpectedErrorCode = 4,
                ExpectedErrorMsgContains = "decompressor.TryUnwrap fail"
            }
        };

        // Prepare dynamic test data for placeholders
        var indexTrunc = testCases.FindIndex(t => t.Name == "Truncated Body");
        if (indexTrunc >= 0)
        {
            var valid = ZstdCompressor.Compress(System.Text.Encoding.UTF8.GetBytes("Hello World with enough length to have body")).Item1;
            var validBytes = valid.AsSpan().ToArray();
            valid.Dispose();
            // Truncate last byte
            if (validBytes.Length > 0)
            {
                var trunc = validBytes.Take(validBytes.Length - 1).ToArray();
                var tc = testCases[indexTrunc];
                tc.Input = trunc;
                testCases[indexTrunc] = tc;
            }
        }

        var indexLying = testCases.FindIndex(t => t.Name == "Lying Header (Content Size < Actual)");
        if (indexLying >= 0)
        {
            // Create valid frame with known size (e.g. 100 'A's)
            byte[] original = new byte[100];
            for (int i = 0; i < 100; i++)
            {
                original[i] = 65;
            }

            var valid = ZstdCompressor.Compress(original).Item1;
            var validBytes = valid.AsSpan().ToArray();
            valid.Dispose();

            // Heuristic: Search for the size byte (100 = 0x64) in the header (first 20 bytes) and change it to 1.
            // Zstd stores Frame_Content_Size.
            // For 100 bytes, it fits in 1 byte or 2 bytes depending on encoding.
            // If we find 0x64, change to 0x01.
            for (int i = 4; i < Math.Min(validBytes.Length, 20); i++)
            {
                if (validBytes[i] == 100)
                {
                    validBytes[i] = 1; // Claim size is 1
                    break;
                }
            }

            var tc = testCases[indexLying];
            tc.Input = validBytes;
            testCases[indexLying] = tc;
        }

        foreach (var tc in testCases)
        {
            RunTestCase(tc);
        }
    }

    [Fact]
    public void TestZstd_Concurrency()
    {
        int threadCount = 8;
        int iterationsPerThread = 100;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, threadCount, i =>
        {
            var random = new Random(i * 997); // Different seed per thread/iteration
            for (int j = 0; j < iterationsPerThread; j++)
            {
                try
                {
                    // 1. Generate random data
                    byte[] data = new byte[random.Next(100, 10240)];
                    random.NextBytes(data);

                    // 2. Compress
                    var (compressed, errComp) = ZstdCompressor.Compress(data);
                    if (errComp.Err())
                    {
                        errors.Add($"Thread {i} Iter {j} Compress Error: {errComp.Message}");
                        compressed.Dispose();
                        continue;
                    }

                    // 3. Uncompress
                    var (uncompressed, errUn) = ZstdCompressor.Uncompress(compressed.AsSpan());
                    if (errUn.Err())
                    {
                        errors.Add($"Thread {i} Iter {j} Uncompress Error: {errUn.Message}");
                    }
                    else
                    {
                        // 4. Verify
                        var resultBytes = uncompressed.AsSpan().ToArray();
                        if (!data.SequenceEqual(resultBytes))
                        {
                            errors.Add($"Thread {i} Iter {j} Data Mismatch");
                        }
                    }

                    compressed.Dispose();
                    uncompressed.Dispose();

                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {i} Iter {j} Exception: {ex}");
                }
            }
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void TestZstd_TryWrapFails_WithEmptyInputAndEmptyDestination()
    {
        // Prompt intent: only change tests, construct a zero-length input to model the failure guarded by ZstdCompressor.Compress.
        using var compressor = new ZstdSharp.Compressor();
        byte[] input = Array.Empty<byte>();
        byte[] destination = Array.Empty<byte>();

        bool success = compressor.TryWrap(input, destination, out int written);

        Assert.False(success);
        Assert.Equal(0, written);
    }

    // ---- Tests for Compress(ref RentedBuffer dst, ReadOnlySpan<byte> input) overload ----

    /// <summary>
    /// 意图：验证 Compress(ref RentedBuffer, ...) 对普通字符串压缩后可成功解压，且数据完整。
    /// </summary>
    [Fact]
    public void TestZstd_CompressRef_NormalString_RoundTrip()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("Hello, World! This is a test of Zstd ref overload.");
        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Compress(ref dst, input);
            Assert.False(err.Err(), $"Compress error: {err.Message}");
            Assert.True(dst.Length > 0, "Compressed length should be > 0");

            var (uncompressed, errUn) = ZstdCompressor.Uncompress(dst.AsSpan());
            Assert.False(errUn.Err(), $"Uncompress error: {errUn.Message}");
            Assert.True(input.SequenceEqual(uncompressed.AsSpan().ToArray()), "Roundtrip data mismatch");
            uncompressed.Dispose();
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证空输入时仍产生非空的 zstd 空帧（约 9 字节），压缩不报错。
    /// 注释中已说明：当 input 的长度为 0 时，仍然也会出现 9 字节的 zstd 空帧。
    /// </summary>
    [Fact]
    public void TestZstd_CompressRef_EmptyInput_ProducesNonEmptyFrame()
    {
        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Compress(ref dst, Array.Empty<byte>());
            Assert.False(err.Err(), $"Compress empty input error: {err.Message}");
            Assert.True(dst.Length > 0, "Zstd empty input should produce a non-empty empty frame (~9 bytes)");
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证 ref 重载压缩 1MB 随机数据后可完整解压，覆盖大数据场景。
    /// </summary>
    [Fact]
    public void TestZstd_CompressRef_LargeData_RoundTrip()
    {
        var random = new Random(123);
        var largeData = new byte[1024 * 1024];
        random.NextBytes(largeData);

        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Compress(ref dst, largeData);
            Assert.False(err.Err(), $"Compress large data error: {err.Message}");
            Assert.True(dst.Length > 0);

            var (uncompressed, errUn) = ZstdCompressor.Uncompress(dst.AsSpan());
            Assert.False(errUn.Err(), $"Uncompress large data error: {errUn.Message}");
            Assert.Equal(largeData.Length, uncompressed.Length);
            Assert.True(largeData.SequenceEqual(uncompressed.AsSpan().ToArray()), "Large data roundtrip mismatch");
            uncompressed.Dispose();
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证多次调用 Compress(ref dst, ...) 将数据依次追加在缓冲区中，
    ///       记录每块的起止偏移后，可从对应偏移量独立解压，数据正确。
    /// 这覆盖了 dst.Length += written 追加语义。
    /// </summary>
    [Fact]
    public void TestZstd_CompressRef_SequentialAppend_EachChunkDecompressable()
    {
        var chunk1 = System.Text.Encoding.UTF8.GetBytes("first chunk: Hello Zstd");
        var chunk2 = System.Text.Encoding.UTF8.GetBytes("second chunk: appended after first, a bit longer");

        var dst = new RentedBuffer(0);
        try
        {
            // 压缩第一块，记录压缩后偏移
            var err1 = ZstdCompressor.Compress(ref dst, chunk1);
            Assert.False(err1.Err(), $"First compress error: {err1.Message}");
            int endOfChunk1 = dst.Length;
            Assert.True(endOfChunk1 > 0);

            // 压缩第二块，追加在第一块后
            var err2 = ZstdCompressor.Compress(ref dst, chunk2);
            Assert.False(err2.Err(), $"Second compress error: {err2.Message}");
            int endOfChunk2 = dst.Length;
            Assert.True(endOfChunk2 > endOfChunk1);

            // 解压第一块并验证
            var (decomp1, errUn1) = ZstdCompressor.Uncompress(dst.AsSpan()[..endOfChunk1]);
            Assert.False(errUn1.Err(), $"Decompress chunk1 error: {errUn1.Message}");
            Assert.True(chunk1.SequenceEqual(decomp1.AsSpan().ToArray()), "Chunk1 data mismatch");
            decomp1.Dispose();

            // 解压第二块并验证
            var (decomp2, errUn2) = ZstdCompressor.Uncompress(dst.AsSpan()[endOfChunk1..endOfChunk2]);
            Assert.False(errUn2.Err(), $"Decompress chunk2 error: {errUn2.Message}");
            Assert.True(chunk2.SequenceEqual(decomp2.AsSpan().ToArray()), "Chunk2 data mismatch");
            decomp2.Dispose();
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证 Compress(ref RentedBuffer, ...) 在多线程并发下，
    ///       通过 [ThreadStatic] 独立使用压缩器，不出现数据竞争或错误。
    /// </summary>
    [Fact]
    public void TestZstd_CompressRef_Concurrency()
    {
        int threadCount = 8;
        int iterationsPerThread = 50;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, threadCount, i =>
        {
            var random = new Random(i * 1009);
            for (int j = 0; j < iterationsPerThread; j++)
            {
                var dst = new RentedBuffer(0);
                try
                {
                    byte[] data = new byte[random.Next(100, 8192)];
                    random.NextBytes(data);

                    var err = ZstdCompressor.Compress(ref dst, data);
                    if (err.Err())
                    {
                        errors.Add($"Thread {i} Iter {j} Compress Error: {err.Message}");
                        continue;
                    }

                    var (uncompressed, errUn) = ZstdCompressor.Uncompress(dst.AsSpan());
                    if (errUn.Err())
                    {
                        errors.Add($"Thread {i} Iter {j} Uncompress Error: {errUn.Message}");
                    }
                    else
                    {
                        if (!data.SequenceEqual(uncompressed.AsSpan().ToArray()))
                        {
                            errors.Add($"Thread {i} Iter {j} Data Mismatch");
                        }
                        uncompressed.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {i} Iter {j} Exception: {ex}");
                }
                finally
                {
                    dst.Dispose();
                }
            }
        });

        Assert.Empty(errors);
    }

    // ---- Tests for Uncompress(ref RentedBuffer dst, ReadOnlySpan<byte> compressed) overload ----

    /// <summary>
    /// 意图：验证 Uncompress(ref RentedBuffer, ...) 对正常压缩数据可成功解压，
    ///       dst.Length 等于原始数据长度，数据内容完整。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_NormalString_RoundTrip()
    {
        var input = System.Text.Encoding.UTF8.GetBytes("Hello, World! This is a test of Zstd Uncompress ref overload.");
        var (compressed, errComp) = ZstdCompressor.Compress(input);
        Assert.False(errComp.Err(), $"Compress error: {errComp.Message}");
        var compressedBytes = compressed.AsSpan().ToArray();
        compressed.Dispose();

        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Uncompress(ref dst, compressedBytes);
            Assert.False(err.Err(), $"Uncompress error: {err.Message}");
            Assert.Equal(input.Length, dst.Length);
            Assert.True(input.SequenceEqual(dst.AsSpan().ToArray()), "Roundtrip data mismatch");
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证解压 zstd 空帧（压缩空输入产生的约 9 字节帧）后 dst.Length = 0，无报错。
    /// 覆盖 GetDecompressedSize 返回 0、TryUnwrap 写出 0 字节的正常路径。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_EmptyFrame_ProducesEmptyData()
    {
        var (compressed, errComp) = ZstdCompressor.Compress(Array.Empty<byte>());
        Assert.False(errComp.Err(), $"Compress empty error: {errComp.Message}");
        var compressedBytes = compressed.AsSpan().ToArray();
        compressed.Dispose();
        Assert.True(compressedBytes.Length > 0, "zstd empty frame itself should be non-empty (~9 bytes)");

        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Uncompress(ref dst, compressedBytes);
            Assert.False(err.Err(), $"Uncompress empty frame error: {err.Message}");
            Assert.Equal(0, dst.Length);
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证 Uncompress ref 重载对 1MB 随机数据解压后数据完整，覆盖大数据场景。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_LargeData_RoundTrip()
    {
        var random = new Random(456);
        var largeData = new byte[1024 * 1024];
        random.NextBytes(largeData);

        var (compressed, errComp) = ZstdCompressor.Compress(largeData);
        Assert.False(errComp.Err(), $"Compress error: {errComp.Message}");
        var compressedBytes = compressed.AsSpan().ToArray();
        compressed.Dispose();

        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Uncompress(ref dst, compressedBytes);
            Assert.False(err.Err(), $"Uncompress large data error: {err.Message}");
            Assert.Equal(largeData.Length, dst.Length);
            Assert.True(largeData.SequenceEqual(dst.AsSpan().ToArray()), "Large data roundtrip mismatch");
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：传入无法识别为 zstd 帧的垃圾数据，GetDecompressedSize 抛异常，
    ///       验证返回 Error code=3，message 含 "GetDecompressedSize fail"。
    ///       覆盖第一个 catch 分支。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_GarbageData_ReturnsCode3()
    {
        var garbage = new byte[] { 0x01, 0x02, 0x03, 0x04 }; // 不是合法 zstd 帧
        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Uncompress(ref dst, garbage);
            Assert.True(err.Err(), "Expected error for garbage input");
            Assert.Equal(3u, err.Code);
            Assert.Contains("GetDecompressedSize fail", err.Message);
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：传入头部合法但 body 已被篡改（启用 checksum）的 zstd 数据，
    ///       GetDecompressedSize 成功，TryUnwrap 因 checksum 校验失败而报错，
    ///       验证返回 Error code=4，message 含 "decompressor.TryUnwrap fail"。
    ///       覆盖第二个 catch 或 !success 分支。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_CorruptBody_ReturnsCode4()
    {
        // 用带 checksum 的压缩器生成数据，然后篡改 body 中间字节
        using var compressorWithChecksum = new ZstdSharp.Compressor();
        compressorWithChecksum.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        var src = System.Text.Encoding.UTF8.GetBytes("hello checksum for ref uncompress test");
        var bound = ZstdSharp.Compressor.GetCompressBound(src.Length);
        var dstBuf = new byte[bound];
        compressorWithChecksum.TryWrap(src, dstBuf, out int writtenBytes);
        var corrupt = dstBuf.Take(writtenBytes).ToArray();
        if (corrupt.Length > 10)
        {
            corrupt[corrupt.Length / 2] ^= 0xFF; // 翻转 body 中间字节，破坏 checksum
        }

        var dst = new RentedBuffer(0);
        try
        {
            var err = ZstdCompressor.Uncompress(ref dst, corrupt);
            Assert.True(err.Err(), "Expected error for corrupt zstd data");
            Assert.Equal(4u, err.Code);
            Assert.Contains("decompressor.TryUnwrap fail", err.Message);
        }
        finally
        {
            dst.Dispose();
        }
    }

    /// <summary>
    /// 意图：验证 Uncompress(ref RentedBuffer, ...) 在多线程并发下，
    ///       通过 [ThreadStatic] 独立使用解压器，不出现数据竞争或错误。
    /// </summary>
    [Fact]
    public void TestZstd_UncompressRef_Concurrency()
    {
        int threadCount = 8;
        int iterationsPerThread = 50;
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, threadCount, i =>
        {
            var random = new Random(i * 1013);
            for (int j = 0; j < iterationsPerThread; j++)
            {
                byte[] data = new byte[random.Next(100, 8192)];
                random.NextBytes(data);

                var (compressed, errComp) = ZstdCompressor.Compress(data);
                if (errComp.Err())
                {
                    errors.Add($"Thread {i} Iter {j} Compress Error: {errComp.Message}");
                    compressed.Dispose();
                    continue;
                }
                var compressedBytes = compressed.AsSpan().ToArray();
                compressed.Dispose();

                var dst = new RentedBuffer(0);
                try
                {
                    var err = ZstdCompressor.Uncompress(ref dst, compressedBytes);
                    if (err.Err())
                    {
                        errors.Add($"Thread {i} Iter {j} Uncompress Error: {err.Message}");
                    }
                    else if (!data.SequenceEqual(dst.AsSpan().ToArray()))
                    {
                        errors.Add($"Thread {i} Iter {j} Data Mismatch");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Thread {i} Iter {j} Exception: {ex}");
                }
                finally
                {
                    dst.Dispose();
                }
            }
        });

        Assert.Empty(errors);
    }

    private static void RunTestCase(TestCase tc)
    {
        _ = Array.Empty<byte>();
        byte[] originalData = Array.Empty<byte>();
        byte[] compressedData;
        if (!tc.IsCompressedInput)
        {
            originalData = tc.Input;
            // Test Compress
            var (compressed, err) = ZstdCompressor.Compress(tc.Input);

            if (tc.ExpectCompressError)
            {
                Assert.True(err.Err(), $"TestCase '{tc.Name}': Expected compression error but got none.");
                if (tc.ExpectedErrorCode != 0)
                {
                    Assert.Equal(tc.ExpectedErrorCode, err.Code);
                }

                if (!string.IsNullOrEmpty(tc.ExpectedErrorMsgContains))
                {
                    Assert.Contains(tc.ExpectedErrorMsgContains, err.Message);
                }

                compressed.Dispose();
                return; // Done
            }
            else
            {
                Assert.False(err.Err(), $"TestCase '{tc.Name}': Unexpected compression error: {err.Message}");
                // Zstd empty input produces a frame (9 bytes usually), so length > 0
                Assert.True(compressed.Length > 0 || (tc.Input.Length == 0 && compressed.Length >= 0));
                compressedData = compressed.AsSpan().ToArray();
                Assert.True(compressedData.Length > 0);
                compressed.Dispose();
            }
        }
        else
        {
            compressedData = tc.Input;
        }

        // Test Uncompress
        var (uncompressed, errUn) = ZstdCompressor.Uncompress(compressedData);

        if (tc.ExpectUncompressError)
        {
            Assert.True(errUn.Err(), $"TestCase '{tc.Name}': Expected uncompression error but got none.");
            if (tc.ExpectedErrorCode != 0)
            {
                Assert.Equal(tc.ExpectedErrorCode, errUn.Code);
            }

            if (!string.IsNullOrEmpty(tc.ExpectedErrorMsgContains))
            {
                Assert.Contains(tc.ExpectedErrorMsgContains, errUn.Message);
            }
        }
        else
        {
            Assert.False(errUn.Err(), $"TestCase '{tc.Name}': Unexpected uncompression error: {errUn.Message}");

            // If we started with uncompressed data, verify roundtrip equality
            if (!tc.IsCompressedInput)
            {
                var resultBytes = uncompressed.AsSpan().ToArray();
                Assert.Equal(originalData.Length, resultBytes.Length);
                Assert.True(originalData.SequenceEqual(resultBytes), $"TestCase '{tc.Name}': Data mismatch after roundtrip.");
            }
        }
        uncompressed.Dispose();
    }
}
