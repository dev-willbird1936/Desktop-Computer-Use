using System.Diagnostics;

namespace ShadowUse.Automation;

/// <summary>Short-lived, cross-process action locks. No persistent agent ownership.</summary>
public static class ActionCoordinator
{
    private static readonly AsyncLocal<HashSet<string>?> Held = new();

    public static string WindowKey(AppTarget target)
        => $"window-{target.Pid}-{target.Hwnd.ToInt64():X}";

    public static async Task<T> RunAsync<T>(AppTarget target, bool realCursor, Func<Task<T>> action, CancellationToken ct)
        => await RunAsync(WindowKey(target), realCursor, action, ct).ConfigureAwait(false);

    public static async Task<T> RunAsync<T>(string windowKey, bool realCursor, Func<Task<T>> action, CancellationToken ct)
    {
        var previous = Held.Value;
        if (realCursor && previous?.Contains(windowKey) == true && !previous.Contains("cursor"))
            throw new InvalidOperationException("A background sequence cannot switch to foreground input midway; select foreground for the sequence.");
        var keys = realCursor ? new[] { "cursor", windowKey } : new[] { windowKey };
        var acquired = new List<Lease>();
        try
        {
            foreach (var key in keys)
                if (previous?.Contains(key) != true)
                    acquired.Add(await AcquireAsync(key, ct).ConfigureAwait(false));
            Held.Value = previous == null ? new(keys) : new(previous.Concat(keys));
            ct.ThrowIfCancellationRequested();
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Held.Value = previous;
            for (int i = acquired.Count - 1; i >= 0; i--) acquired[i].Dispose();
        }
    }

    private static Task<Lease> AcquireAsync(string key, CancellationToken ct)
    {
        var ready = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A named Mutex belongs to its acquiring OS thread. Keep that thread alive
        // until the async action finishes; never release it from a continuation thread.
        var thread = new Thread(() =>
        {
            Mutex? mutex = null;
            bool owned = false;
            Lease? lease = null;
            try
            {
                mutex = new Mutex(false, $"Local\\DCU.Action.{Process.GetCurrentProcess().SessionId}.{key}");
                try { owned = WaitHandle.WaitAny([ct.WaitHandle, mutex]) == 1; }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned || ct.IsCancellationRequested)
                {
                    ready.TrySetCanceled(ct);
                    return;
                }
                lease = new Lease(Thread.CurrentThread);
                ready.TrySetResult(lease);
                lease.Release.Wait();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally
            {
                if (owned) mutex!.ReleaseMutex();
                mutex?.Dispose();
            }
        }) { IsBackground = true, Name = "DCU.ActionLock" };
        thread.Start();
        return ready.Task;
    }

    private sealed class Lease(Thread owner) : IDisposable
    {
        public readonly ManualResetEventSlim Release = new();
        public void Dispose()
        {
            Release.Set();
            owner.Join();
            Release.Dispose();
        }
    }
}
