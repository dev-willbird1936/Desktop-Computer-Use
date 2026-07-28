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
}
