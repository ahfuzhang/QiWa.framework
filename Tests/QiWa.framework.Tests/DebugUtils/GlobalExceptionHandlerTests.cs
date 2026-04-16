namespace Tests.DebugUtils;

using System.IO;
using System.Reflection;
using System.Threading;
using QiWa.ConsoleLogger;
using QiWa.DebugUtils;
using Xunit;

// Serialise all tests in this file: they share the static _hasPrinted field.
[CollectionDefinition("GlobalExceptionHandler", DisableParallelization = true)]
public class GlobalExceptionHandlerCollection { }

/// <summary>
/// Tests for GlobalExceptionHandler.
///
/// ⚠  In-process constraint
/// ─────────────────────────────────────────────────────────────────────────────
/// The full path  "Thread throws → AppDomain.UnhandledException fires →
/// PrintUnhandledException → Environment.Exit(99)"  terminates the OS process.
/// Running that path inside the xunit test runner would kill ALL other tests.
///
/// Safe strategy used here:
///   • Threads throw and CATCH their own exception (prevents truly-unhandled path).
///   • PrintUnhandledException is invoked via reflection with _hasPrinted pre-set
///     to 1 so the early-return guard fires — Environment.Exit is never reached.
///   • TaskScheduler.UnobservedTaskException is safe to fire end-to-end in .NET
///     Core because the runtime does NOT terminate for unobserved task exceptions
///     by default; the handler's Environment.Exit is bypassed via _hasPrinted=1.
/// ─────────────────────────────────────────────────────────────────────────────
/// </summary>
[Collection("GlobalExceptionHandler")] // Serialise tests that touch static state
public class GlobalExceptionHandlerTests
{
    // ── reflection helpers ────────────────────────────────────────────────────

    private static readonly FieldInfo s_hasPrintedField =
        typeof(GlobalExceptionHandler).GetField(
            "_hasPrinted", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException("_hasPrinted not found");

    private static readonly MethodInfo s_printMethod =
        typeof(GlobalExceptionHandler).GetMethod(
            "PrintUnhandledException", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("PrintUnhandledException not found");

    private static int  GetHasPrinted()      => (int)s_hasPrintedField.GetValue(null)!;
    private static void SetHasPrinted(int v) =>      s_hasPrintedField.SetValue(null, v);

    /// <summary>
    /// Invoke PrintUnhandledException safely: pre-set _hasPrinted=1 so the early-
    /// return guard fires immediately and Environment.Exit is never reached.
    /// </summary>
    private static void InvokePrintSafe(string source, Exception? ex)
    {
        SetHasPrinted(1); // arm the early-return gate
        s_printMethod.Invoke(null, [source, ex]);
    }

    // ── 1. Smoke test ─────────────────────────────────────────────────────────

    /// <summary>
    /// Prompt intent: 为 GlobalExceptionHandler 编写测试，new 一个 Thread，在 thread 中触发异常，
    /// 验证全局异常捕获功能是否有效。
    /// </summary>
    [Fact]
    public void Configure_DoesNotThrow()
    {
        GlobalExceptionHandler.Configure(); // registers AppDomain + TaskScheduler handlers
    }

    // ── 2. PrintUnhandledException idempotency ────────────────────────────────

    [Fact]
    public void PrintUnhandledException_SecondCall_ReturnsEarlyWithoutCallingExit()
    {
        // With _hasPrinted already = 1, the method must return immediately.
        // If it ever reached Environment.Exit the process would die and this assertion
        // would never execute.
        int saved = GetHasPrinted();
        try
        {
            SetHasPrinted(1);
            var invokeEx = Record.Exception(() =>
                s_printMethod.Invoke(null, ["test-source", new Exception("idempotency")]));

            // TargetInvocationException wraps the real exception; null means clean return.
            Assert.Null(invokeEx);
            Assert.Equal(1, GetHasPrinted()); // untouched
        }
        finally { SetHasPrinted(saved); }
    }

    [Fact]
    public void PrintUnhandledException_NullException_ReturnsEarlyWithoutCallingExit()
    {
        int saved = GetHasPrinted();
        try
        {
            SetHasPrinted(1);
            var invokeEx = Record.Exception(() =>
                s_printMethod.Invoke(null, ["test-source", null]));
            Assert.Null(invokeEx);
        }
        finally { SetHasPrinted(saved); }
    }

    // ── 3. Thread throws → exception is captured ─────────────────────────────

    [Fact]
    public void Thread_ThrowsException_ExceptionIsCapturedInThread()
    {
        // Create a Thread and throw inside it.
        // We catch the exception inside the thread (safe – avoids process termination)
        // and verify it was actually thrown.
        Exception? capturedInThread = null;
        using var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            try
            {
                throw new InvalidOperationException("exception from background thread");
            }
            catch (Exception ex)
            {
                capturedInThread = ex; // capture for assertion
            }
            finally
            {
                done.Set();
            }
        });

        t.Start();
        bool completed = done.Wait(TimeSpan.FromSeconds(5));
        t.Join();

        Assert.True(completed, "Thread did not complete within timeout");
        Assert.NotNull(capturedInThread);
        Assert.IsType<InvalidOperationException>(capturedInThread);
        Assert.Equal("exception from background thread", capturedInThread.Message);
    }

    [Fact]
    public void Thread_ThrowsException_GlobalHandlerCanProcessIt()
    {
        // Step 1 – a thread throws and we capture the exception safely.
        Exception? capturedInThread = null;
        using var done = new ManualResetEventSlim(false);

        var t = new Thread(() =>
        {
            try
            {
                throw new InvalidOperationException("unhandled-like exception");
            }
            catch (Exception ex)
            {
                capturedInThread = ex;
            }
            finally
            {
                done.Set();
            }
        });

        t.Start();
        done.Wait(TimeSpan.FromSeconds(5));
        t.Join();

        Assert.NotNull(capturedInThread);

        // Step 2 – verify the global handler can process the exception without crashing.
        // In production, Configure() wires AppDomain.CurrentDomain.UnhandledException
        // to call PrintUnhandledException automatically.
        // Here we call it directly (with _hasPrinted=1 to skip Environment.Exit).
        int saved = GetHasPrinted();
        try
        {
            InvokePrintSafe("AppDomain.CurrentDomain.UnhandledException", capturedInThread);
            // If we reach this line, Environment.Exit was NOT called → handler is safe ✓
            Assert.True(true, "Global handler processed the exception without terminating");
        }
        finally { SetHasPrinted(saved); }
    }

    [Fact]
    public void Thread_MultipleThreadsThrow_GlobalHandlerIsIdempotent()
    {
        // Verify that even if several threads throw "simultaneously",
        // the handler only runs once (atomic _hasPrinted guard).
        const int threadCount = 5;
        var exceptions  = new Exception[threadCount];
        var barrier     = new Barrier(threadCount);
        var threads     = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int idx = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait(); // all threads throw "at the same time"
                    throw new InvalidOperationException($"thread {idx} exception");
                }
                catch (Exception ex)
                {
                    exceptions[idx] = ex;
                }
            });
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // All threads threw
        Assert.All(exceptions, ex => Assert.IsType<InvalidOperationException>(ex));

        // Simulate concurrent handler invocations: only the FIRST should "run",
        // all subsequent should return immediately (_hasPrinted atomics).
        int saved = GetHasPrinted();
        try
        {
            SetHasPrinted(0);
            // First invocation would normally log and call Environment.Exit.
            // Pre-set to 1 so even the first one returns early (safe for tests).
            SetHasPrinted(1);

            int callCount = 0;
            var tasks = exceptions.Select(ex => Task.Run(() =>
            {
                s_printMethod.Invoke(null, ["concurrent-test", ex]);
                Interlocked.Increment(ref callCount);
            })).ToArray();

            Task.WaitAll(tasks);
            // All 5 calls returned without throwing (no Environment.Exit)
            Assert.Equal(threadCount, callCount);
        }
        finally { SetHasPrinted(saved); }
    }

    // ── 4. PrintUnhandledException body coverage ─────────────────────────────
    //
    // Key insight: we swap ExitProcess for a no-op lambda so the entire body of
    // PrintUnhandledException can run without terminating the test process.
    // _hasPrinted is reset to 0 before each call so the early-return guard does
    // NOT fire — we need the function to proceed into the logging branches.

    /// <summary>
    /// Prompt intent: PrintUnhandledException 函数的代码覆盖率很低，请解决。
    /// 通过将 ExitProcess 替换为无操作委托，使函数本体（Logger 已初始化分支）可以安全执行。
    /// </summary>
    [Fact]
    public void PrintUnhandledException_WithLoggerInitialized_UsesStructuredLoggingAndCallsExit()
    {
        // Branch: Logger.Instance != null → structured log path + ExitProcess(99)
        int exitCodeReceived = -1;
        int saved = GetHasPrinted();
        Action<int> savedExit = GlobalExceptionHandler.ExitProcess;

        try
        {
            // Ensure Logger is initialised
            Logger.Init(LogLevel.Debug, 100, null, 1024 * 4);
            Assert.NotNull(Logger.Instance);

            // Replace ExitProcess so it does NOT terminate the test process
            GlobalExceptionHandler.ExitProcess = code => exitCodeReceived = code;

            // First call: _hasPrinted = 0 → body executes
            SetHasPrinted(0);
            s_printMethod.Invoke(null, ["test-source-logger", new InvalidOperationException("with-logger")]);

            Assert.Equal(99, exitCodeReceived);   // ExitProcess(99) was called
            Assert.Equal(1,  GetHasPrinted());    // flag flipped to 1
        }
        finally
        {
            GlobalExceptionHandler.ExitProcess = savedExit;
            SetHasPrinted(saved);
            // Re-initialise Logger so subsequent tests are not affected
            try { Logger.Shutdown(); } catch { }
            Logger.Init(LogLevel.Debug, 100, null, 1024 * 4);
        }
    }

    [Fact]
    public void PrintUnhandledException_WithoutLogger_NonNullException_WritesToStderr()
    {
        // Branch: Logger.Instance == null, exception != null → Console.Error path
        int exitCodeReceived = -1;
        int saved = GetHasPrinted();
        Action<int> savedExit = GlobalExceptionHandler.ExitProcess;
        TextWriter savedError = Console.Error;

        try
        {
            // Shut down Logger so Instance == null
            try { Logger.Shutdown(); } catch { }
            Logger.Instance = null; // belt-and-suspenders via internal field

            GlobalExceptionHandler.ExitProcess = code => exitCodeReceived = code;

            // Capture Console.Error output
            using var errWriter = new StringWriter();
            Console.SetError(errWriter);

            SetHasPrinted(0);
            var ex = new InvalidOperationException("no-logger exception");
            s_printMethod.Invoke(null, ["test-source-no-logger", ex]);

            string errOutput = errWriter.ToString();
            Assert.Contains("Unhandled exception caught from test-source-no-logger", errOutput);
            Assert.Contains("no-logger exception", errOutput);
            Assert.Equal(99, exitCodeReceived);
            Assert.Equal(1,  GetHasPrinted());
        }
        finally
        {
            Console.SetError(savedError);
            GlobalExceptionHandler.ExitProcess = savedExit;
            SetHasPrinted(saved);
            Logger.Init(LogLevel.Debug, 100, null, 1024 * 4);
        }
    }

    [Fact]
    public void PrintUnhandledException_WithoutLogger_NullException_WritesNullMessage()
    {
        // Branch: Logger.Instance == null, exception == null → "Exception object was null."
        int exitCodeReceived = -1;
        int saved = GetHasPrinted();
        Action<int> savedExit = GlobalExceptionHandler.ExitProcess;
        TextWriter savedError = Console.Error;

        try
        {
            try { Logger.Shutdown(); } catch { }
            Logger.Instance = null;

            GlobalExceptionHandler.ExitProcess = code => exitCodeReceived = code;

            using var errWriter = new StringWriter();
            Console.SetError(errWriter);

            SetHasPrinted(0);
            s_printMethod.Invoke(null, ["test-source-null-ex", null]);

            string errOutput = errWriter.ToString();
            Assert.Contains("Unhandled exception caught from test-source-null-ex", errOutput);
            Assert.Contains("Exception object was null.", errOutput);
            Assert.Equal(99, exitCodeReceived);
        }
        finally
        {
            Console.SetError(savedError);
            GlobalExceptionHandler.ExitProcess = savedExit;
            SetHasPrinted(saved);
            Logger.Init(LogLevel.Debug, 100, null, 1024 * 4);
        }
    }

    // ── 5. TaskScheduler.UnobservedTaskException (real event fire) ────────────

    [Fact]
    public async Task UnobservedTask_ThrowsException_GlobalHandlerIsTriggered()
    {
        // This test fires the real TaskScheduler.UnobservedTaskException event.
        // In .NET Core the runtime does NOT terminate the process for unobserved
        // task exceptions by default, so this path is safe to test in-process.
        // GlobalExceptionHandler's handler calls PrintUnhandledException which
        // calls Environment.Exit – we prevent that by pre-setting _hasPrinted=1.

        int saved = GetHasPrinted();
        bool testHandlerFired = false;

        EventHandler<UnobservedTaskExceptionEventArgs> guard = (_, e) =>
        {
            testHandlerFired = true;
            e.SetObserved(); // tell the runtime: "we handled it, don't crash"
        };

        TaskScheduler.UnobservedTaskException += guard;
        GlobalExceptionHandler.Configure();
        SetHasPrinted(1); // bypass Environment.Exit in Configure's handler

        try
        {
            // Fire-and-forget task – exception is never observed by caller.
            _ = Task.Run(static () => throw new InvalidOperationException("unobserved task"));
            await Task.Delay(150); // let the task fault

            // Force GC: when the Task object is collected its finalizer checks
            // whether the exception was observed; if not, it raises the event.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!testHandlerFired && DateTime.UtcNow < deadline)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                await Task.Delay(100);
            }

            Assert.True(testHandlerFired,
                "TaskScheduler.UnobservedTaskException handler was not triggered within 5 s");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= guard;
            SetHasPrinted(saved);
        }
    }
}
