namespace Tests.StringUtils;

using QiWa.StringUtils;
using Xunit;

/// <summary>
/// Unit tests for StringUtils.ParseBufferSize.
/// Covers all reachable branches:
///   – All 10 unit-suffix branches: pb, p, tb, t, gb, g, mb, m, kb, k
///   – No-suffix (plain integer)
///   – long.TryParse failure  → Error code 1
///   – result &gt; maxSize (1 GB) → clamped to 1 GB
///   – result &lt;= maxSize      → returned as-is
///   – Trim() and ToLowerInvariant() paths
/// </summary>
public class StringUtilsTests
{
    private const long KB = 1024L;
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * 1024 * 1024;
    private const long MaxSize = GB; // function's internal cap

    // ─────────────────────────────────────────────────────────────────────────
    // Table-driven: valid inputs → expected byte count
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prompt intent: 为 StringUtils.ParseBufferSize 补充测试，覆盖所有后缀分支和边界条件，尽量达到 100% 代码覆盖率。
    /// </summary>
    public struct SuccessTestCase
    {
        public string Name;
        public string Input;
        public long Expected;
    }

    [Fact]
    public void ParseBufferSize_ValidInputs_ReturnsExpectedBytes()
    {
        var testCases = new SuccessTestCase[]
        {
            // ── each suffix branch (coverage of 10 if/else-if arms) ──────────
            new() { Name = "1pb  (PB 2-char suffix)",  Input = "1pb",  Expected = MaxSize },  // clamped
            new() { Name = "1p   (PB 1-char suffix)",  Input = "1p",   Expected = MaxSize },  // clamped
            new() { Name = "1tb  (TB 2-char suffix)",  Input = "1tb",  Expected = MaxSize },  // clamped
            new() { Name = "1t   (TB 1-char suffix)",  Input = "1t",   Expected = MaxSize },  // clamped
            new() { Name = "1gb  (GB 2-char suffix)",  Input = "1gb",  Expected = MaxSize },  // == maxSize
            new() { Name = "1g   (GB 1-char suffix)",  Input = "1g",   Expected = MaxSize },  // == maxSize
            new() { Name = "512mb (MB 2-char suffix)", Input = "512mb", Expected = 512 * MB }, // < maxSize
            new() { Name = "512m  (MB 1-char suffix)", Input = "512m",  Expected = 512 * MB }, // < maxSize
            new() { Name = "64kb (KB 2-char suffix)",  Input = "64kb",  Expected = 64 * KB  },
            new() { Name = "64k  (KB 1-char suffix)",  Input = "64k",   Expected = 64 * KB  },
            // ── no suffix (plain integer) ────────────────────────────────────
            new() { Name = "1024 (no suffix)",         Input = "1024",  Expected = 1024 },
            new() { Name = "0    (zero)",               Input = "0",     Expected = 0    },
            new() { Name = "1    (one byte)",           Input = "1",     Expected = 1    },
            // ── clamping: result > maxSize → MaxSize ─────────────────────────
            new() { Name = "2gb  → clamped to 1GB",   Input = "2gb",   Expected = MaxSize },
            new() { Name = "1024mb == 1GB → capped",  Input = "1024mb", Expected = MaxSize },
            new() { Name = "2g   → clamped to 1GB",   Input = "2g",    Expected = MaxSize },
            // ── Trim() path ──────────────────────────────────────────────────
            new() { Name = "leading/trailing spaces",  Input = "  64k  ", Expected = 64 * KB },
            // ── ToLowerInvariant() path ──────────────────────────────────────
            new() { Name = "uppercase KB suffix",      Input = "64KB",  Expected = 64 * KB  },
            new() { Name = "uppercase MB suffix",      Input = "128MB", Expected = 128 * MB },
            new() { Name = "mixed case Gb suffix",     Input = "1Gb",   Expected = MaxSize  },
            new() { Name = "mixed case Mb suffix",     Input = "256Mb", Expected = 256 * MB },
        };

        foreach (var tc in testCases)
        {
            var (result, err) = StringUtils.ParseBufferSize(tc.Input);
            Assert.False(err.Err(), $"[{tc.Name}] unexpected error: code={err.Code} msg={err.Message}");
            Assert.Equal(tc.Expected, result);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table-driven: invalid inputs → Error code 1
    // ─────────────────────────────────────────────────────────────────────────

    public struct ErrorTestCase
    {
        public string Name;
        public string Input;
    }

    [Fact]
    public void ParseBufferSize_InvalidInputs_ReturnsError1()
    {
        var testCases = new ErrorTestCase[]
        {
            new() { Name = "non-numeric string",   Input = "abc"    },
            new() { Name = "float with kb suffix", Input = "1.5kb"  },  // TryParse("1.5") fails
            new() { Name = "empty string",         Input = ""       },  // TryParse("") fails
            new() { Name = "whitespace only",      Input = "   "    },  // after Trim → ""
            new() { Name = "suffix only no number",Input = "kb"     },  // numPart = "" → TryParse fails
            new() { Name = "suffix only mb",       Input = "mb"     },
            new() { Name = "suffix only gb",       Input = "gb"     },
            new() { Name = "letters inside number",Input = "1k2b"   },  // ends with 'b'? no → numPart="1k2b", TryParse fails
        };

        foreach (var tc in testCases)
        {
            var (result, err) = StringUtils.ParseBufferSize(tc.Input);
            Assert.True(err.Err(),  $"[{tc.Name}] expected Err()=true but got false");
            Assert.Equal(1u, err.Code);
            Assert.Equal(0L, result);
        }
    }
}
