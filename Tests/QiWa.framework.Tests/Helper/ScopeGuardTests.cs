namespace Tests.Helper;

using QiWa.Helper;
using Xunit;

/// <summary>
/// Tests for ScopeGuard – verifies the Go-style defer behaviour.
///
/// All tests use the pattern requested by the user:
///   using var _ = new ScopeGuard(() => { ... });
///
/// Covered branches / scenarios:
///   1. Basic defer     – cleanup runs when the using-scope exits normally
///   2. Exception path  – cleanup still runs when an exception propagates
///   3. LIFO order      – multiple using-guards unwind in reverse (last-in, first-out)
///   4. Nested scopes   – inner guard disposes before outer guard
///   5. Double dispose  – Dispose() is idempotent; action is invoked exactly once
///   6. Default struct  – default(ScopeGuard) can be disposed without crashing
/// </summary>
public class ScopeGuardTests
{
    // ── 1. Basic defer ────────────────────────────────────────────────────────

    /// <summary>
    /// Prompt intent: 为 ScopeGuard 编写测试用例，用 using var _ = new ScopeGuard(){} 写法，
    /// 主要测试类似 golang 中 defer 的效果。
    /// </summary>
    [Fact]
    public void ScopeGuard_ActionIsCalledWhenScopeExits()
    {
        bool called = false;

        {
            using var _ = new ScopeGuard(() => called = true);
            Assert.False(called); // not yet called while inside scope
        }

        Assert.True(called); // called after scope exits
    }

    [Fact]
    public void ScopeGuard_ReceivesCorrectSideEffect_OnDispose()
    {
        var log = new List<string>();

        {
            using var _ = new ScopeGuard(() => log.Add("cleanup"));
            log.Add("work");
        }

        Assert.Equal(["work", "cleanup"], log);
    }

    // ── 2. Exception path ─────────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_ActionRunsEvenWhenExceptionThrown()
    {
        bool cleaned = false;
        Exception? caught = null;

        try
        {
            using var _ = new ScopeGuard(() => cleaned = true);
            throw new InvalidOperationException("simulated failure");
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.True(cleaned); // defer-like: runs despite exception
    }

    [Fact]
    public void ScopeGuard_PartialWorkIsCleanedUp_OnException()
    {
        var log = new List<string>();

        try
        {
            using var _ = new ScopeGuard(() => log.Add("cleanup"));
            log.Add("step1");
            throw new Exception("boom");
#pragma warning disable CS0162
            log.Add("step2"); // never reached
#pragma warning restore CS0162
        }
        catch { /* intentionally swallowed */ }

        Assert.Equal(["step1", "cleanup"], log);
        Assert.DoesNotContain("step2", log);
    }

    // ── 3. LIFO order ─────────────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_MultipleGuards_DisposeInReverseOrder()
    {
        // Sequential using declarations: the last one declared is disposed first,
        // exactly like Go's deferred calls.
        var order = new List<int>();

        {
            using var _1 = new ScopeGuard(() => order.Add(1)); // declared first
            using var _2 = new ScopeGuard(() => order.Add(2));
            using var _3 = new ScopeGuard(() => order.Add(3)); // declared last → disposed first
        }

        Assert.Equal([3, 2, 1], order); // LIFO
    }

    [Fact]
    public void ScopeGuard_ThreeResourcesAcquiredAndReleased_InReverseOrder()
    {
        var acquired = new List<string>();
        var released = new List<string>();

        // Acquire immediately; register cleanup as a deferred action.
        {
            acquired.Add("A");
            using var _a = new ScopeGuard(() => released.Add("A"));

            acquired.Add("B");
            using var _b = new ScopeGuard(() => released.Add("B"));

            acquired.Add("C");
            using var _c = new ScopeGuard(() => released.Add("C"));
            // All three are "held" inside this scope
        }

        Assert.Equal(["A", "B", "C"], acquired);   // acquire order
        Assert.Equal(["C", "B", "A"], released);   // release order (LIFO)
    }

    // ── 4. Nested scopes ─────────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_NestedScopes_InnerDisposesBeforeOuter()
    {
        var log = new List<string>();

        {
            using var outer = new ScopeGuard(() => log.Add("outer"));
            {
                using var inner = new ScopeGuard(() => log.Add("inner"));
                log.Add("body");
            } // inner disposes here
            log.Add("between");
        } // outer disposes here

        Assert.Equal(["body", "inner", "between", "outer"], log);
    }

    // ── 5. Double dispose ────────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_DisposeCalledTwice_ActionInvokedOnlyOnce()
    {
        int count = 0;
        var guard = new ScopeGuard(() => count++);

        guard.Dispose();
        guard.Dispose(); // second call must be a no-op

        Assert.Equal(1, count);
    }

    // ── 6. Default struct ────────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_DefaultConstructed_DisposeDoesNotThrow()
    {
        // default(ScopeGuard) has _onDispose == null; Dispose() should be safe
        var ex = Record.Exception(() =>
        {
            using var _ = new ScopeGuard(); // no action
        });
        Assert.Null(ex);
    }

    // ── 7. Real-world pattern ────────────────────────────────────────────────

    [Fact]
    public void ScopeGuard_SimulatesResourceLifetime()
    {
        // Simulate acquiring / releasing a resource in one place,
        // regardless of how the scope is exited.
        int refCount = 0;

        void Acquire() => refCount++;
        void Release() => refCount--;

        Acquire();
        {
            using var _ = new ScopeGuard(Release);
            Assert.Equal(1, refCount);
            // imagine complex work here...
        }

        Assert.Equal(0, refCount); // released on scope exit
    }
}
