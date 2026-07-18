using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>
/// Dedicated STA thread for all UI Automation COM calls.
/// COM objects created here live and die on this thread — no cross-apartment marshaling,
/// no deadlocks.
/// </summary>
public sealed class UiaThread : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    private abstract class WorkItem
    {
        public abstract void Execute();
        public abstract void Fail(Exception ex);
        public abstract void Cancel();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        public required Func<T> Func;
        public required TaskCompletionSource<T> Tcs;
        public CancellationToken Token;
        public override void Execute()
        {
            if (Token.IsCancellationRequested) { Tcs.TrySetCanceled(Token); return; }
            try { Tcs.TrySetResult(Func()); }
            catch (Exception ex) { Tcs.TrySetException(ex); }
        }
        public override void Fail(Exception ex) => Tcs.TrySetException(ex);
        public override void Cancel() => Tcs.TrySetCanceled();
    }

    public UiaThread()
    {
        var ready = new ManualResetEventSlim(false);
        var initError = (Exception?)null;
        _thread = new Thread(() =>
        {
            try
            {
                // CoInitialize as STA
                Thread.CurrentThread.SetApartmentState(ApartmentState.STA);
                ready.Set();
                foreach (var item in _queue.GetConsumingEnumerable())
                    item.Execute();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ThreadInterruptedException)
            {
                // queue completed / shutdown
            }
            catch (Exception ex)
            {
                initError = ex;
                ready.Set();
            }
        })
        { IsBackground = true, Name = "ShadowUse.UIA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (initError != null) throw initError;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UiaThread));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem<T> { Func = func, Tcs = tcs, Token = ct });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _queue.CompleteAdding(); } catch { }
        foreach (var item in _queue.GetConsumingEnumerable()) item.Cancel();
        _thread.Join(2000);
        _queue.Dispose();
    }
}
