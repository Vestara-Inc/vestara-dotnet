using Vestara.Diagnostics;
using Xunit;

namespace Vestara.Tests;

public class DuplicateCaptureTests
{
    [Fact]
    public void ExceptionData_IsNotMutated()
    {
        var tracker = new ExceptionTracker();
        var ex = new InvalidOperationException("test_error");

        var dataCountBefore = ex.Data.Count;
        tracker.ShouldCapture(ex, isFatal: false);

        Assert.Equal(dataCountBefore, ex.Data.Count);
        Assert.Empty(ex.Data);
    }

    [Fact]
    public void NonFatal_CanUpgradeTo_Fatal()
    {
        var tracker = new ExceptionTracker();
        var ex = new InvalidOperationException("handled_then_crashed");

        // 1. Captured as NonFatal
        Assert.True(tracker.ShouldCapture(ex, isFatal: false));

        // 2. Duplicate NonFatal is suppressed
        Assert.False(tracker.ShouldCapture(ex, isFatal: false));

        // 3. Escalating the same exception instance to Fatal is ALLOWED
        Assert.True(tracker.ShouldCapture(ex, isFatal: true));

        // 4. Duplicate Fatal is suppressed
        Assert.False(tracker.ShouldCapture(ex, isFatal: true));
    }

    [Fact]
    public void DuplicateFatal_SameInstance_IsSuppressed()
    {
        var tracker = new ExceptionTracker();
        var ex = new AccessViolationException("hard_crash");

        Assert.True(tracker.ShouldCapture(ex, isFatal: true));
        Assert.False(tracker.ShouldCapture(ex, isFatal: true));
    }

    [Fact]
    public void DistinctExceptionInstances_WithIdenticalMessages_AreBothCaptured()
    {
        var tracker = new ExceptionTracker();
        var ex1 = new TimeoutException("Database connection timeout");
        var ex2 = new TimeoutException("Database connection timeout");

        Assert.True(tracker.ShouldCapture(ex1, isFatal: false));
        Assert.True(tracker.ShouldCapture(ex2, isFatal: false)); // Different instance must not be suppressed
    }
}
