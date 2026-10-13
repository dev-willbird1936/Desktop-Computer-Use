using System.Diagnostics;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class ActionCoordinatorTests
{
    [Fact]
    public async Task DifferentBackgroundWindowsProgressWhileOneIsBusy()
    {
        string firstKey = Guid.NewGuid().ToString("N"), secondKey = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = ActionCoordinator.RunAsync(firstKey, false, async () => { entered.SetResult(); await release.Task; return 1; }, default);
        await entered.Task;
        try
        {
            Assert.Equal(2, await ActionCoordinator.RunAsync(secondKey, false, () => Task.FromResult(2), default).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.SetResult(); await first; }
    }

    [Fact]
    public async Task SameWindowWaitsUntilCurrentActionEnds()
    {
        string key = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = ActionCoordinator.RunAsync(key, false, async () => { entered.SetResult(); await release.Task; return 1; }, default);
        await entered.Task;
        var second = ActionCoordinator.RunAsync(key, false, () => Task.FromResult(2), default);
        try { await Task.Delay(50); Assert.False(second.IsCompleted); }
        finally { release.SetResult(); await first; }
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ForegroundInputSharesOneCursorAcrossWindows()
    {
        string key = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = ActionCoordinator.RunAsync(key, true, async () => { entered.SetResult(); await release.Task; return 1; }, default);
        await entered.Task;
        var second = ActionCoordinator.RunAsync(Guid.NewGuid().ToString("N"), true, () => Task.FromResult(2), default);
        try { await Task.Delay(50); Assert.False(second.IsCompleted); }
        finally { release.SetResult(); await first; }
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotKeepWindowReserved()
    {
        string key = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = ActionCoordinator.RunAsync(key, false, async () => { entered.SetResult(); await release.Task; return 1; }, default);
        await entered.Task;
        using var cancellation = new CancellationTokenSource(50);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ActionCoordinator.RunAsync(key, false, () => Task.FromResult(2), cancellation.Token));
        }
        finally { release.SetResult(); await first; }
        Assert.Equal(3, await ActionCoordinator.RunAsync(key, false, () => Task.FromResult(3), default));
    }

    [Fact]
    public async Task ExceptionReleasesAllLocks()
    {
        string key = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActionCoordinator.RunAsync<int>(key, true, () => throw new InvalidOperationException("failed"), default));
        Assert.Equal(2, await ActionCoordinator.RunAsync(key, true, () => Task.FromResult(2), default).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task NestedSameTargetSequenceDoesNotDeadlock()
    {
        string key = Guid.NewGuid().ToString("N");
        int result = await ActionCoordinator.RunAsync(key, true, () =>
            ActionCoordinator.RunAsync(key, true, () => Task.FromResult(42), default), default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task AbandonedControllerDoesNotLeaveOwnershipBehind()
    {
        string key = Guid.NewGuid().ToString("N");
        using var mutex = new Mutex(false, $"Local\\DCU.Action.{Process.GetCurrentProcess().SessionId}.{key}");
        var owner = new Thread(() => mutex.WaitOne());
        owner.Start();
        owner.Join();
        Assert.Equal(42, await ActionCoordinator.RunAsync(key, false, () => Task.FromResult(42), default).WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
