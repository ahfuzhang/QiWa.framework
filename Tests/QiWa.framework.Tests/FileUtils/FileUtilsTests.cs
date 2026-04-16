namespace Tests.FileUtils;

using System.IO;
using QiWa.Common;
using QiWa.FileUtils;
using Xunit;

/// <summary>
/// Unit tests for FileUtils.ReadAllAndRentAync.
/// Covers all reachable branches:
///   Branch 1 – outer catch  (file open fails)      → code 4
///   Branch 2 – file too large (&gt;100 MB)          → code 1
///   Branch 3 – empty file                           → code 2
///   Branch 4 – normal small read                    → success
///   Branch 5 – read loop runs multiple iterations   → success
///
/// Notes on unreachable-in-practice branches:
///   "ReadAsync throws" (inner catch → code 3) requires a mid-read I/O error
///   on a FileStream, which cannot be triggered deterministically with real files.
///   "read == 0 before EOF" (→ code 3 "not read all") requires the file to be
///   truncated by another process DURING the async read – inherently racy.
///   Both are purely defensive paths that protect against OS-level failures.
/// </summary>
public class FileUtilsTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Create a temp file with the given content and register it for cleanup.</summary>
    private string CreateTempFile(byte[] content)
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>Create a sparse temp file of the requested size (no actual disk usage).</summary>
    private string CreateSparseTempFile(long sizeBytes)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        // Seeking past the end and writing one byte sets the file size without
        // filling all the intermediate space, giving us a sparse file.
        fs.Seek(sizeBytes - 1, SeekOrigin.Begin);
        fs.WriteByte(0);
        return path;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    // ── error-case table ──────────────────────────────────────────────────────

    /// <summary>
    /// Prompt intent: 为 FileUtils.ReadAllAndRentAync 补充测试，覆盖所有可达分支，尽量达到 100% 代码覆盖率。
    /// Table-driven tests for all error-returning branches.
    /// </summary>
    public struct ErrorTestCase
    {
        public string Name;
        public Func<string> GetPath;   // deferred so sparse file is created per-test
        public uint ExpectedCode;
    }

    [Fact]
    public async Task ReadAllAndRentAync_ErrorCases_ReturnExpectedCodes()
    {
        // Branch 3: empty file – code 2
        var emptyPath    = CreateTempFile([]);
        // Branch 2: file > 100 MB – code 1  (sparse: metadata-only, no real disk write)
        var largePath    = CreateSparseTempFile(1024L * 1024 * 100 + 1);  // 100 MB + 1 byte

        var testCases = new ErrorTestCase[]
        {
            new()
            {
                Name         = "non-existent file → code 4",
                GetPath      = () => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "missing.bin"),
                ExpectedCode = 4,
            },
            new()
            {
                Name         = "file too large (>100 MB) → code 1",
                GetPath      = () => largePath,
                ExpectedCode = 1,
            },
            new()
            {
                Name         = "empty file → code 2",
                GetPath      = () => emptyPath,
                ExpectedCode = 2,
            },
        };

        foreach (var tc in testCases)
        {
            var (buf, err) = await FileUtils.ReadAllAndRentAync(tc.GetPath());
            Assert.True(err.Err(),  $"[{tc.Name}] expected Err()=true but got false");
            Assert.Null(buf.Data);
            Assert.Equal(tc.ExpectedCode, err.Code);
        }
    }

    // ── success cases ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAllAndRentAync_SmallFile_ReturnsCorrectContent()
    {
        // Branch 4: normal read path – file fits in a single ReadAsync call
        var content = "Hello, QiWa Framework!"u8.ToArray();
        var path    = CreateTempFile(content);

        var (buf, err) = await FileUtils.ReadAllAndRentAync(path);
        try
        {
            Assert.False(err.Err(), $"Expected no error, got code={err.Code}: {err.Message}");
            Assert.NotNull(buf.Data);
            Assert.Equal(content.Length, buf.Length);
            Assert.Equal(content, buf.Data![..buf.Length]);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public async Task ReadAllAndRentAync_BinaryContent_PreservesBytes()
    {
        // Verify that arbitrary binary data (including 0x00) is read back intact.
        var content = new byte[256];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)i;
        var path = CreateTempFile(content);

        var (buf, err) = await FileUtils.ReadAllAndRentAync(path);
        try
        {
            Assert.False(err.Err(), $"Expected no error, got code={err.Code}: {err.Message}");
            Assert.Equal(content, buf.Data![..buf.Length]);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public async Task ReadAllAndRentAync_FileLargerThanInternalBuffer_ReturnsFullContent()
    {
        // Branch 5: forces the while-loop to run multiple iterations.
        // The internal FileStream buffer is 64 KB; use 256 KB to ensure
        // at least one loop repetition is needed.
        const int size = 256 * 1024;
        var content = new byte[size];
        new Random(42).NextBytes(content);
        var path = CreateTempFile(content);

        var (buf, err) = await FileUtils.ReadAllAndRentAync(path);
        try
        {
            Assert.False(err.Err(), $"Expected no error, got code={err.Code}: {err.Message}");
            Assert.NotNull(buf.Data);
            Assert.Equal(size, buf.Length);
            Assert.Equal(content, buf.Data![..buf.Length]);
        }
        finally
        {
            buf.Dispose();
        }
    }
}
