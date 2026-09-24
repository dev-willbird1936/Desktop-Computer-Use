using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class UiaThreadCancellationTests
{
    [Fact]
    public async Task InvokeAsync_TimesOutRunningWork()
    {
        using var thread = new UiaThread();
        using var release = new ManualResetEventSlim();
        var work = thread.InvokeAsync(
            () =>
            {
                release.Wait();
                return 1;
            },
            default,
            TimeSpan.FromMilliseconds(50));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => work);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task InvokeAsync_CancelsQueuedWorkPromptly()
    {
        using var thread = new UiaThread();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocking = thread.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
            return 1;
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();
        var queued = thread.InvokeAsync(() => 2, cancellation.Token);

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => queued.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            release.Set();
            await blocking;
        }
    }

    /// <summary>Regression coverage for a real bug found during a bug-hunt pass: the caller-side
    /// timeout on a UIA InvokeAsync call only marks that ONE call's own Task as failed -- it does
    /// not, and safely cannot, abort the underlying STA COM call still running on the single
    /// dedicated thread. Before the watchdog/worker-replacement fix, a single genuinely wedged
    /// target (an unresponsive process) would silently wedge the thread forever: every OTHER
    /// queued or future call, against any other target, would also time out, with no recovery
    /// short of restarting DCU. This proves a SECOND, unrelated, fast call still completes
    /// (rather than itself timing out) once the watchdog has had time to detect and replace the
    /// wedged worker -- the actual server-wide-wedge scenario, not just the already-covered
    /// single-call timeout.</summary>
    [Fact]
    public async Task InvokeAsync_RecoversAfterAWedgedCall_SoASubsequentUnrelatedCallStillCompletes()
    {
        using var thread = new UiaThread();
        using var neverRelease = new ManualResetEventSlim();
        try
        {
            // A short per-call timeout so this item's own Task fails fast (proving the existing
            // per-call timeout behavior is unchanged), while the underlying Func keeps blocking
            // the thread regardless -- exactly the "timed out but still running" scenario that
            // makes the thread wedged from every other caller's perspective.
            var wedged = thread.InvokeAsync(
                () => { neverRelease.Wait(); return 1; },
                default,
                TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAsync<TimeoutException>(() => wedged);

            // The watchdog's wedge bound is this item's own 200ms timeout plus a fixed 2s grace;
            // poll past that instead of a single fixed sleep, so the test is not flaky under
            // slow CI scheduling.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var recovered = await thread.InvokeAsync(() => 42, default, TimeSpan.FromSeconds(2));
                    Assert.Equal(42, recovered);
                    return; // recovered -- the bug is fixed
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                    await Task.Delay(100);
                }
            }
            Assert.Fail($"a fast, unrelated call never completed after the wedge -- the thread " +
                $"stayed wedged instead of the watchdog replacing it. Last error: {lastError}");
        }
        finally
        {
            neverRelease.Set();
        }
    }
}
