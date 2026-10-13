using ShadowUse.Native;

namespace ShadowUse.Automation;

/// <summary>Isolate each window's accessibility provider from unrelated targets.</summary>
public sealed class WindowUiaWorkers(UiaThread fallback) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<(IntPtr Hwnd, uint Pid), UiaThread> workers = new();
    private bool disposed;

    public UiaThread ForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return fallback;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) throw new InvalidOperationException("Target window no longer exists; reobserve the app.");
        return GetOrCreate(hwnd, pid);
    }

    private UiaThread GetOrCreate(IntPtr hwnd, uint pid)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = (hwnd, pid);
            if (!workers.TryGetValue(key, out var thread)) workers[key] = thread = new UiaThread();
            return thread;
        }
    }

    public void Prune()
    {
        List<UiaThread> retired = [];
        lock (gate)
        {
            foreach (var key in workers.Keys.ToArray())
            {
                NativeMethods.GetWindowThreadProcessId(key.Hwnd, out var pid);
                if (pid == key.Pid) continue;
                retired.Add(workers[key]);
                workers.Remove(key);
            }
        }
        // Never make discovery wait for a retired provider's bounded shutdown.
        foreach (var thread in retired) _ = Task.Run(thread.Dispose);
    }

    public void Dispose()
    {
        UiaThread[] all;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            all = workers.Values.ToArray();
            workers.Clear();
        }
        foreach (var thread in all) thread.Dispose();
    }
}
