namespace Tests.Syscall;

using System.Runtime.InteropServices;
using System.Text;
using QiWa.Syscall;
using Xunit;

/// <summary>
/// Unit tests for NativeWrite.WriteStdout.
///
/// Branch coverage:
///   UNIX   build (macOS / Linux): WriteStdout → UnixWrite → write(1, …)
///   WINDOWS build               : WriteStdout → WindowsWrite → WriteFile(GetStdHandle(-11), …)
///   #else  build (no platform)  : WriteStdout → throw PlatformNotSupportedException
///
/// The active branch is selected at compile time in QiWa.framework.csproj via
/// MSBuild platform conditions (UNIX / WINDOWS).  On this machine only the
/// UNIX branch is compiled, so only that branch is exercised here.
///
/// Note: WriteStdout calls write(2=fd, …) directly – it bypasses Console.SetOut().
/// Tests therefore only assert that no exception is thrown; output verification
/// would require dup2 / pipe manipulation at the OS fd level.
/// </summary>
public class NativeWriteTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when neither UNIX nor WINDOWS was compiled in (the #else branch).
    /// Detected at runtime via PlatformNotSupportedException on a probe call.
    /// </summary>
    private static readonly bool s_isElseBranch = DetectElseBranch();

    private static bool DetectElseBranch()
    {
        try
        {
            NativeWrite.WriteStdout(ReadOnlySpan<byte>.Empty);
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return true;
        }
    }

    // ── table-driven: inputs that must not throw ──────────────────────────────

    /// <summary>
    /// Prompt intent: 为 NativeWrite.WriteStdout 补充测试，覆盖 UNIX/Windows/#else 分支。
    /// 先修复 csproj 缺少平台 DefineConstants 的问题（UNIX/WINDOWS），再编写不同输入的测试用例。
    /// </summary>
    public struct WriteStdoutTestCase
    {
        public string Name;
        public byte[] Data;
    }

    [Fact]
    public void WriteStdout_OnSupportedPlatform_DoesNotThrow()
    {
        if (s_isElseBranch)
        {
            // Neither UNIX nor WINDOWS compiled in → skip this test
            return;
        }

        var testCases = new WriteStdoutTestCase[]
        {
            new() { Name = "ASCII text",    Data = Encoding.UTF8.GetBytes("NativeWriteTest\n")   },
            new() { Name = "multi-line",    Data = Encoding.UTF8.GetBytes("line1\nline2\nline3\n") },
            new() { Name = "binary data",   Data = [0x00, 0x01, 0x7F, 0xFF]                        },
            new() { Name = "single byte",   Data = [0x0A]                                          },
            new() { Name = "64 KB payload", Data = new byte[64 * 1024]                            },
        };

        foreach (var tc in testCases)
        {
            var ex = Record.Exception(() => NativeWrite.WriteStdout(tc.Data.AsSpan()));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void WriteStdout_EmptySpan_DoesNotThrow()
    {
        if (s_isElseBranch)
        {
            return;
        }

        // write(1, ptr, 0) is a valid no-op on UNIX; WriteFile with 0 bytes is also fine.
        var ex = Record.Exception(() => NativeWrite.WriteStdout(ReadOnlySpan<byte>.Empty));
        Assert.Null(ex);
    }

    // ── #else branch ──────────────────────────────────────────────────────────

    [Fact]
    public void WriteStdout_ElseBranch_ThrowsPlatformNotSupportedException()
    {
        if (!s_isElseBranch)
        {
            // UNIX or WINDOWS is compiled in → this branch is not active
            return;
        }

        Assert.Throws<PlatformNotSupportedException>(
            () => NativeWrite.WriteStdout("test"u8));
    }

    // ── runtime platform smoke-test ───────────────────────────────────────────

    [Fact]
    public void WriteStdout_RuntimePlatformMatchesCompiledBranch()
    {
        bool isUnixOrWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (isUnixOrWindows)
        {
            // A supported platform should have the UNIX or WINDOWS symbol compiled in.
            Assert.False(s_isElseBranch,
                "UNIX/WINDOWS define is missing from QiWa.framework.csproj for this OS.");
        }
        else
        {
            // Unknown platform → #else branch is expected
            Assert.True(s_isElseBranch);
        }
    }
}
