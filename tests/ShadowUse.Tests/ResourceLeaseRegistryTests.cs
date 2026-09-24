using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F35-R01/T01/T02: pure unit coverage of the per-resource-key mutual exclusion used to
/// serialize conflicting DAG-node targets while leaving independent targets fully concurrent.</summary>
public sealed class ResourceLeaseRegistryTests
{
    [Fact]
    public async Task AcquireAsync_SameKey_SerializesRealConcurrentHolders()
    {
        // Two tasks race for the same key; neither may observe the other "inside" its held window.
        // A shared mutable counter that must never exceed 1 while held proves genuine mutual
        // exclusion under real Task-pool concurrency, not just sequential test-code ordering.
        var registry = new ResourceLeaseRegistry();
        var concurrentHolders = 0;
        var maxObserved = 0;
        var lockObj = new object();

        async Task HoldAsync()
        {
            await using var lease = await registry.AcquireAsync("same-key");
            lock (lockObj)
            {
                concurrentHolders++;
                maxObserved = Math.Max(maxObserved, concurrentHolders);
            }
            await Task.Delay(30);
            lock (lockObj) { concurrentHolders--; }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => HoldAsync()));

        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public async Task AcquireAsync_DifferentKeys_NeverBlockEachOther()
    {
        // Two different-key holders must both be inside their held window SIMULTANEOUSLY at some
        // point -- proven with the same shared-gate technique WorkflowRunnerTests uses for its own
        // concurrency proof (would deadlock/time out under any accidental cross-key serialization).
        var registry = new ResourceLeaseRegistry();
        var bothInside = new TaskCompletionSource();
        var seen = new HashSet<string>();
        var lockObj = new object();

        async Task HoldAsync(string key)
        {
            await using var lease = await registry.AcquireAsync(key);
            lock (lockObj)
            {
                seen.Add(key);
                if (seen.Count == 2)
                    bothInside.TrySetResult();
            }
            await bothInside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await Task.WhenAll(HoldAsync("key-a"), HoldAsync("key-b"));

        Assert.True(bothInside.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AcquireAsync_ReleasedLease_UnblocksTheNextWaiterOnTheSameKey()
    {
        var registry = new ResourceLeaseRegistry();
        var lease1 = await registry.AcquireAsync("key");
        Assert.True(registry.IsHeld("key"));

        var acquireTask = registry.AcquireAsync("key");
        await Task.Delay(20);
        Assert.False(acquireTask.IsCompleted);

        await lease1.DisposeAsync();
        var lease2 = await acquireTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(registry.IsHeld("key"));
        await lease2.DisposeAsync();
        Assert.False(registry.IsHeld("key"));
    }

    [Fact]
    public async Task AcquireAsync_CancelledWhileWaiting_NeverLeaksThePermit()
    {
        var registry = new ResourceLeaseRegistry();
        var lease1 = await registry.AcquireAsync("key");

        using var cts = new CancellationTokenSource();
        var waiting = registry.AcquireAsync("key", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);

        await lease1.DisposeAsync();
        // A fresh, uncancelled acquire on the same key must succeed immediately -- the cancelled
        // waiter never actually held (and therefore never needed to release) the permit.
        var lease2 = await registry.AcquireAsync("key").WaitAsync(TimeSpan.FromSeconds(2));
        await lease2.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ReleasesOnlyOnce()
    {
        var registry = new ResourceLeaseRegistry();
        var lease = await registry.AcquireAsync("key");
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        // A double-release would over-release the semaphore, letting two concurrent acquires both
        // succeed at once -- assert only one of two racing acquirers observes the "inside" window
        // at a time, immediately after the double-dispose.
        var concurrentHolders = 0;
        var maxObserved = 0;
        var lockObj = new object();
        async Task HoldAsync()
        {
            await using var l = await registry.AcquireAsync("key");
            lock (lockObj) { concurrentHolders++; maxObserved = Math.Max(maxObserved, concurrentHolders); }
            await Task.Delay(20);
            lock (lockObj) { concurrentHolders--; }
        }
        await Task.WhenAll(HoldAsync(), HoldAsync());
        Assert.Equal(1, maxObserved);
    }
}
