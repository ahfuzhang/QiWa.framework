using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using QiWa.ConsoleLogger;
using Xunit;

namespace Tests.ConsoleLogger;

// Ensure all tests run sequentially to avoid conflicts with global Logger state and stdout capture
[CollectionDefinition("ConsoleLogger")]
public class ConsoleLoggerCollection : ICollectionFixture<ConsoleLoggerFixture>
{
}

public class ConsoleLoggerFixture : IDisposable
{
    public ConsoleLoggerFixture()
    {
        // Global initialization for all tests
        Logger.Init(LogLevel.Debug, 100, new Dictionary<string, string>
        {
            ["app"] = "test",
            ["env"] = "unit",
            ["version"] = "1.0"
        }, 1024 * 4);
    }

    public void Dispose()
    {
        try
        {
            ThreadLocalLogger.TestOutputCapture = null;
            Logger.Shutdown();
        }
        catch { }
    }
}

[Collection("ConsoleLogger")]
public abstract class TestBase
{
    private readonly ConcurrentQueue<string> _capturedOutput = new();

    protected TestBase()
    {
        // Reset captured output before each test
        _capturedOutput.Clear();

        // Hook into ThreadLocalLogger to capture output
        ThreadLocalLogger.TestOutputCapture = _capturedOutput.Enqueue;
    }

    protected string GetCapturedOutput()
    {
        // TestOutputCapture is invoked synchronously inside Flush(), so no wait is needed.
        var sb = new StringBuilder();
        while (_capturedOutput.TryDequeue(out var line))
        {
            sb.Append(line);
        }
        return sb.ToString();
    }

    protected List<string> GetCapturedLines()
    {
        // TestOutputCapture is invoked synchronously inside Flush(), so no wait is needed.
        var lines = new List<string>();
        while (_capturedOutput.TryDequeue(out var chunk))
        {
            // The chunk might contain multiple lines or partial lines
            // Simplification for now: assume each write contains full lines mostly
            // But better to normalize:
            var chunkLines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.AddRange(chunkLines);
        }
        return lines;
    }

    protected void ClearCapturedOutput()
    {
        _capturedOutput.Clear();
    }

    protected static void AssertAllLinesAreValidJson(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            try
            {
                using var _ = JsonDocument.Parse(trimmed);
            }
            catch (JsonException ex)
            {
                Assert.True(false, $"Log line is not valid JSON.\nLine: {trimmed}\nError: {ex.Message}");
            }
        }
    }
}
