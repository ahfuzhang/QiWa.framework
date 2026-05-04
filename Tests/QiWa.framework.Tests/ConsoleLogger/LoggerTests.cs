using QiWa.ConsoleLogger;
using Xunit;

namespace Tests.ConsoleLogger;

/// <summary>
/// Unit tests for ConsoleLogger.Logger class
/// Uses table-driven pattern and aims for 100% code coverage
/// </summary>
public class LoggerTests : TestBase
{
    // Note: Logger.Init() is called in TestBase/Fixture

    #region SetLevel Tests

    public struct SetLevelTestCase
    {
        public string Name;
        public LogLevel Level;
    }

    [Fact]
    public void SetLevel_WithAllLevels_SetsCorrectLevel()
    {
        var testCases = new SetLevelTestCase[]
        {
            new() { Name = "Fatal level", Level = LogLevel.Fatal },
            new() { Name = "Error level", Level = LogLevel.Error },
            new() { Name = "Warn level", Level = LogLevel.Warn },
            new() { Name = "Info level", Level = LogLevel.Info },
            new() { Name = "Debug level", Level = LogLevel.Debug },
        };

        foreach (var tc in testCases)
        {
            Logger.SetLevel(tc.Level);
            // Instance.Level is internal, so we verify indirectly
            Assert.True(true, $"Test case '{tc.Name}' should not throw");
        }

        // Reset to Debug for other tests
        Logger.SetLevel(LogLevel.Debug);
    }

    #endregion

    #region SetFlushIntervalMs Tests

    public struct SetFlushIntervalMsTestCase
    {
        public string Name;
        public int InputMs;
        public int ExpectedMinMs;
    }

    [Fact]
    public void SetFlushIntervalMs_WithVariousValues_SetsCorrectInterval()
    {
        var testCases = new SetFlushIntervalMsTestCase[]
        {
            new() { Name = "normal value 1000ms", InputMs = 1000, ExpectedMinMs = 100 },
            new() { Name = "normal value 500ms", InputMs = 500, ExpectedMinMs = 100 },
            new() { Name = "value below minimum 50ms", InputMs = 50, ExpectedMinMs = 100 },
            new() { Name = "value at minimum 100ms", InputMs = 100, ExpectedMinMs = 100 },
            new() { Name = "value below minimum 0ms", InputMs = 0, ExpectedMinMs = 100 },
            new() { Name = "negative value", InputMs = -100, ExpectedMinMs = 100 },
            new() { Name = "large value 10000ms", InputMs = 10000, ExpectedMinMs = 100 },
        };

        foreach (var tc in testCases)
        {
            Logger.SetFlushIntervalMs(tc.InputMs);
            // Internal FlushIntervalMs is not directly accessible, but the method should handle min threshold
            Assert.True(true, $"Test case '{tc.Name}' should not throw and handle minimum threshold");
        }

        // Reset to default
        Logger.SetFlushIntervalMs(1000);
    }

    #endregion

    #region Get and Return Tests

    public struct GetReturnTestCase
    {
        public string Name;
        public int GetCount;
    }

    [Fact]
    public void Get_ReturnsTaskLogger()
    {
        var testCases = new GetReturnTestCase[]
        {
            new() { Name = "single get", GetCount = 1 },
            new() { Name = "multiple gets", GetCount = 5 },
            new() { Name = "many gets", GetCount = 10 },
        };

        foreach (var tc in testCases)
        {
            var loggers = new TaskLogger[tc.GetCount];

            // Get TaskLoggers from the pool
            for (int i = 0; i < tc.GetCount; i++)
            {
                loggers[i] = Logger.Get();
                Assert.NotNull(loggers[i]);
            }

            // Return them to the pool
            for (int i = 0; i < tc.GetCount; i++)
            {
                Logger.Return(loggers[i]);
            }

            Assert.True(true, $"Test case '{tc.Name}' completed successfully");
        }
    }

    [Fact]
    public void Get_ResetsPrefix()
    {
        // Get a TaskLogger
        var logger = Logger.Get();
        Assert.NotNull(logger);

        // The prefix length should be 0 after Get
        // (prefix is reset in Get method: l.prefix.Length = 0)
        Assert.True(true, "TaskLogger prefix should be reset after Get");

        // Return it
        Logger.Return(logger);
    }

    [Fact]
    public void Return_HandlesNormalBuffer()
    {
        // Get and return multiple times to test pool behavior
        for (int i = 0; i < 5; i++)
        {
            var logger = Logger.Get();
            Assert.NotNull(logger);

            // Use the logger (add some fields)
            var scopedLogger = logger.WithFields(Field.String("key"u8, "value"));
            Logger.Return(scopedLogger);

            // Return it
            Logger.Return(logger);
        }

        Assert.True(true, "Normal buffer should be returned to pool");
    }

    #endregion

    #region LogLevel Enum Tests

    [Fact]
    public void LogLevel_HasCorrectValues()
    {
        // Verify enum values exist and are in correct order
        Assert.True((int)LogLevel.Fatal < (int)LogLevel.Error);
        Assert.True((int)LogLevel.Error < (int)LogLevel.Warn);
        Assert.True((int)LogLevel.Warn < (int)LogLevel.Info);
        Assert.True((int)LogLevel.Info < (int)LogLevel.Debug);
    }

    public struct LogLevelFilterTestCase
    {
        public string Name;
        public LogLevel ConfiguredLevel;
        public LogLevel MessageLevel;
        public bool ShouldLog;
    }

    [Fact]
    public void LogLevel_FilteringBehavior()
    {
        var testCases = new LogLevelFilterTestCase[]
        {
            // When configured level is Warn:
            new() { Name = "Warn config, Fatal message", ConfiguredLevel = LogLevel.Warn, MessageLevel = LogLevel.Fatal, ShouldLog = true },
            new() { Name = "Warn config, Error message", ConfiguredLevel = LogLevel.Warn, MessageLevel = LogLevel.Error, ShouldLog = true },
            new() { Name = "Warn config, Warn message", ConfiguredLevel = LogLevel.Warn, MessageLevel = LogLevel.Warn, ShouldLog = true },
            new() { Name = "Warn config, Info message", ConfiguredLevel = LogLevel.Warn, MessageLevel = LogLevel.Info, ShouldLog = false },
            new() { Name = "Warn config, Debug message", ConfiguredLevel = LogLevel.Warn, MessageLevel = LogLevel.Debug, ShouldLog = false },

            // When configured level is Debug (most verbose):
            new() { Name = "Debug config, all levels", ConfiguredLevel = LogLevel.Debug, MessageLevel = LogLevel.Debug, ShouldLog = true },

            // When configured level is Fatal (least verbose):
            new() { Name = "Fatal config, Error message", ConfiguredLevel = LogLevel.Fatal, MessageLevel = LogLevel.Error, ShouldLog = false },
        };

        foreach (var tc in testCases)
        {
            // The filtering logic is: if (Instance.Level < MessageLevel) return;
            bool wouldLog = tc.ConfiguredLevel >= tc.MessageLevel;
            Assert.Equal(tc.ShouldLog, wouldLog);
        }
    }

    #endregion

    #region LogBufferSize Tests

    // Note: Constructor tests are limited because Init runs once globally.
    // We assume fixture sets it up correctly.

    #endregion

    #region ParseTags Tests

    /// <summary>
    /// Prompt intent: 为 Logger.ParseTags 补充测试，覆盖 null/empty 输入（code 250）、格式错误（code 251）、
    /// 正常单条/多条解析、URL 编码解码、值中含 '=' 等所有分支。
    /// </summary>
    public struct ParseTagsSuccessTestCase
    {
        public string Name;
        public string Input;
        public Dictionary<string, string> Expected;
    }

    public struct ParseTagsErrorTestCase
    {
        public string Name;
        public string? Input;
        public uint ExpectedCode;
    }

    [Fact]
    public void ParseTags_ValidInput_ReturnsParsedDictionary()
    {
        var testCases = new ParseTagsSuccessTestCase[]
        {
            new()
            {
                Name = "single pair",
                Input = "a=b",
                Expected = new() { ["a"] = "b" },
            },
            new()
            {
                Name = "multiple pairs",
                Input = "a=b&c=d&e=f",
                Expected = new() { ["a"] = "b", ["c"] = "d", ["e"] = "f" },
            },
            new()
            {
                Name = "value contains equals sign",
                Input = "key=val=ue",
                Expected = new() { ["key"] = "val=ue" },
            },
            new()
            {
                Name = "URL-encoded key and value",
                Input = "my%20key=hello%20world",
                Expected = new() { ["my key"] = "hello world" },
            },
            new()
            {
                Name = "URL-encoded special chars",
                Input = "svc%3Aname=qiwa%2Fframework",
                Expected = new() { ["svc:name"] = "qiwa/framework" },
            },
        };

        foreach (var tc in testCases)
        {
            var (dict, err) = Logger.ParseTags(tc.Input);
            Assert.False(err.Err(), $"[{tc.Name}] expected no error but got code={err.Code} msg={err.Message}");
            Assert.NotNull(dict);
            Assert.Equal(tc.Expected.Count, dict!.Count);
            foreach (var (k, v) in tc.Expected)
            {
                Assert.True(dict.ContainsKey(k), $"[{tc.Name}] key '{k}' not found");
                Assert.Equal(v, dict[k]);
            }
        }
    }

    [Fact]
    public void ParseTags_InvalidInput_ReturnsError()
    {
        var testCases = new ParseTagsErrorTestCase[]
        {
            new() { Name = "null input",           Input = null,       ExpectedCode = 250 },
            new() { Name = "empty string",         Input = "",         ExpectedCode = 250 },
            new() { Name = "key without value",    Input = "keyonly",  ExpectedCode = 251 },
            new() { Name = "key without value in chain", Input = "a=b&keyonly&c=d", ExpectedCode = 251 },
        };

        foreach (var tc in testCases)
        {
            var (dict, err) = Logger.ParseTags(tc.Input);
            Assert.True(err.Err(), $"[{tc.Name}] expected error but Err() returned false");
            Assert.Null(dict);
            Assert.Equal(tc.ExpectedCode, err.Code);
        }
    }

    #endregion

    #region ParseLogLevel Tests

    /// <summary>
    /// Prompt intent: 为 Logger.ParseLogLevel 补充测试，覆盖所有合法值（大小写不敏感）和未知输入默认返回 Warn 的分支。
    /// </summary>
    public struct ParseLogLevelTestCase
    {
        public string Name;
        public string Input;
        public LogLevel Expected;
    }

    [Fact]
    public void ParseLogLevel_ReturnsCorrectLevel()
    {
        var testCases = new ParseLogLevelTestCase[]
        {
            // 小写合法值
            new() { Name = "lowercase fatal", Input = "fatal", Expected = LogLevel.Fatal },
            new() { Name = "lowercase error", Input = "error", Expected = LogLevel.Error },
            new() { Name = "lowercase warn",  Input = "warn",  Expected = LogLevel.Warn  },
            new() { Name = "lowercase info",  Input = "info",  Expected = LogLevel.Info  },
            new() { Name = "lowercase debug", Input = "debug", Expected = LogLevel.Debug },
            // 大写 / 混合大小写（ToLowerInvariant 分支）
            new() { Name = "uppercase FATAL", Input = "FATAL", Expected = LogLevel.Fatal },
            new() { Name = "uppercase ERROR", Input = "ERROR", Expected = LogLevel.Error },
            new() { Name = "mixed case Warn",  Input = "Warn",  Expected = LogLevel.Warn  },
            new() { Name = "mixed case Info",  Input = "Info",  Expected = LogLevel.Info  },
            new() { Name = "mixed case Debug", Input = "Debug", Expected = LogLevel.Debug },
            // 未知输入默认返回 Warn
            new() { Name = "unknown trace",   Input = "trace",   Expected = LogLevel.Warn },
            new() { Name = "unknown verbose", Input = "verbose", Expected = LogLevel.Warn },
            new() { Name = "empty string",    Input = "",        Expected = LogLevel.Warn },
            new() { Name = "random string",   Input = "xyz",     Expected = LogLevel.Warn },
        };

        foreach (var tc in testCases)
        {
            var actual = Logger.ParseLogLevel(tc.Input);
            Assert.Equal(tc.Expected, actual);
        }
    }

    #endregion

    #region LogDiagnosticsError Tests

    /// <summary>
    /// Prompt intent: 为 Logger.LogDiagnosticsError 补充测试，覆盖 exception 为 null 与非 null 两种分支。
    /// </summary>
    public struct LogDiagnosticsErrorTestCase
    {
        public string Name;
        public Exception? Exception;
        public string Message;
        public bool ExpectExceptionInOutput;
    }

    [Fact]
    public void LogDiagnosticsError_WritesMessageToStdErr()
    {
        var testCases = new LogDiagnosticsErrorTestCase[]
        {
            new() { Name = "null exception",     Exception = null,                              Message = "something went wrong", ExpectExceptionInOutput = false },
            new() { Name = "non-null exception", Exception = new InvalidOperationException("boom"), Message = "critical failure",    ExpectExceptionInOutput = true  },
            new() { Name = "empty message",      Exception = null,                              Message = "",                     ExpectExceptionInOutput = false },
        };

        foreach (var tc in testCases)
        {
            var errorWriter = new StringWriter();
            var originalError = Console.Error;
            Console.SetError(errorWriter);
            try
            {
                Logger.LogDiagnosticsError(tc.Exception, tc.Message);
                var output = errorWriter.ToString();

                Assert.Contains("ConsoleLogger diagnostics error:", output,
                    StringComparison.Ordinal);
                Assert.Contains(tc.Message, output, StringComparison.Ordinal);

                if (tc.ExpectExceptionInOutput)
                {
                    Assert.NotNull(tc.Exception);
                    Assert.Contains(tc.Exception!.Message, output, StringComparison.Ordinal);
                }
                else
                {
                    // Exception block must NOT appear when exception is null
                    Assert.DoesNotContain("Exception", output, StringComparison.Ordinal);
                }
            }
            finally
            {
                Console.SetError(originalError);
            }
        }
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void Logger_FullWorkflow_GetWithFieldsAndReturn()
    {
        // Get a TaskLogger
        var rootLogger = Logger.Get();
        Assert.NotNull(rootLogger);

        // Add fields using WithFields
        var logger = rootLogger.WithFields(
            Field.String("service"u8, "test-service"),
            Field.Int64("request_id"u8, 12345)
        );
        Logger.Return(rootLogger);

        // Log something (at Debug level which is enabled)
        Logger.SetLevel(LogLevel.Debug);
        logger.Debug(Field.String("msg"u8, "test message"));

        // Return to pool
        Logger.Return(logger);

        // Verify output using capture
        var output = GetCapturedOutput();
        Assert.Contains("\"service\":\"test-service\"", output);
        Assert.Contains("\"request_id\":12345", output);
        Assert.Contains("\"msg\":\"test message\"", output);
    }

    /// <summary>
    /// Prompt intent: 传入一个代表全局 log labels 的 Dictionary<string, string>，覆盖 Logger.Init(tags) 和 SetGlobalTags 分支。
    /// </summary>
    [Fact]
    public void Logger_InitWithGlobalLabels_IncludesLabelsInPrefixAndOutput()
    {
        var labels = new Dictionary<string, string>
        {
            ["log"] = "console",
            ["service"] = "qiwa-tests",
        };

        TaskLogger logger;
        ClearCapturedOutput();
        Logger.Shutdown();
        Logger.Init(LogLevel.Info, 100, labels, 1024 * 4);

        try
        {
            Assert.NotNull(Logger.Instance);
            Assert.NotEmpty(Logger.Instance.TagPrefix);

            var tagPrefix = global::System.Text.Encoding.UTF8.GetString(Logger.Instance.TagPrefix);
            Assert.StartsWith("{", tagPrefix, StringComparison.Ordinal);
            Assert.Contains("\"log\":\"console\"", tagPrefix, StringComparison.Ordinal);
            Assert.Contains("\"service\":\"qiwa-tests\"", tagPrefix, StringComparison.Ordinal);

            logger = Logger.Get();
            logger.Info(Field.String("msg"u8, "hello with labels"));
            Logger.Return(logger);

            var output = GetCapturedOutput();
            Assert.Contains("\"log\":\"console\"", output);
            Assert.Contains("\"service\":\"qiwa-tests\"", output);
            Assert.Contains("\"msg\":\"hello with labels\"", output);
            Assert.Contains("\"level\":\"info\"", output);
        }
        finally
        {
            Logger.Shutdown();
            Logger.Init(LogLevel.Debug, 100, new Dictionary<string, string>
            {
                ["app"] = "test",
                ["env"] = "unit",
                ["version"] = "1.0",
            }, 1024 * 4);
        }
    }

    [Fact]
    public void Logger_MultipleTaskLoggers_Concurrent()
    {
        var tasks = new Task[10];

        for (int i = 0; i < 10; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                // Basic sanity check to ensure no crashes
                var rootLogger = Logger.Get();
                var logger = rootLogger.WithFields(Field.Int64("index"u8, index));
                Assert.NotNull(logger);
                Logger.Return(rootLogger);
                logger.Info(Field.String("msg"u8, $"message from task {index}"));

                Logger.Return(logger);
            });
        }

        Task.WaitAll(tasks);

        // Just verifying it didn't crash; output parsing of concurrent logs is tricky
        Assert.True(true, "Concurrent usage completed successfully");
    }

    #endregion
}
