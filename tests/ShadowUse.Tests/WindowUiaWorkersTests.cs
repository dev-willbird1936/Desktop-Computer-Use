using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class WindowUiaWorkersTests
{
    [Fact]
    public async Task WedgedProviderCannotBlockAnotherWindowsWorker()
    {
        using var fallback = new UiaThread();
        using var workers = new WindowUiaWorkers(fallback);
        var method = typeof(WindowUiaWorkers).GetMethod("GetOrCreate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (UiaThread)method.Invoke(workers, [(IntPtr)1, (uint)1])!;
        var second = (UiaThread)method.Invoke(workers, [(IntPtr)2, (uint)1])!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = first.InvokeAsync(() => { entered.Set(); release.Wait(); return 1; }, timeout: TimeSpan.FromMilliseconds(50));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => blocked);
            Assert.Equal(42, await second.InvokeAsync(() => 42).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Same(second, method.Invoke(workers, [(IntPtr)2, (uint)1]));
        }
        finally { release.Set(); }
    }
}
