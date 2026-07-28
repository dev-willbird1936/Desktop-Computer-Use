// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>
/// Dedicated STA thread for all UI Automation COM calls.
/// COM objects created here live and die on this thread — no cross-apartment marshaling,
/// no deadlocks.
/// </summary>
public sealed class UiaThread : IDisposable
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);
    private readonly BlockingCollection<WorkItem> _queue = new(128);
    private readonly Thread _thread;
    private int _disposed;

    private abstract class WorkItem
    {
        public abstract void Execute();
        public abstract void Fail(Exception ex);
        public abstract void Cancel();
        public abstract void Timeout(TimeSpan timeout);
        public abstract void DisposeRegistrations();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        public required Func<T> Func;
        public required TaskCompletionSource<T> Tcs;
        public CancellationToken Token;
        public CancellationTokenRegistration CancellationRegistration;
        public CancellationTokenSource? TimeoutSource;
        public CancellationTokenRegistration TimeoutRegistration;
        public override void Execute()
        {
            try
            {
                if (Tcs.Task.IsCompleted) return;
                if (Token.IsCancellationRequested) { Tcs.TrySetCanceled(Token); return; }
                Tcs.TrySetResult(Func());
            }
            catch (Exception ex) { Tcs.TrySetException(ex); }
            finally { DisposeRegistrations(); }
        }
        public override void Fail(Exception ex)
        {
            Tcs.TrySetException(ex);
            DisposeRegistrations();
        }
        public override void Cancel() => Tcs.TrySetCanceled(Token);
        public override void Timeout(TimeSpan timeout)
            => Tcs.TrySetException(new TimeoutException(
                $"UI Automation operation timed out after {timeout.TotalSeconds:0.###} seconds."));
        public override void DisposeRegistrations()
        {
            CancellationRegistration.Dispose();
            TimeoutRegistration.Dispose();
            TimeoutSource?.Dispose();
        }
    }

    public UiaThread()
    {
        var ready = new ManualResetEventSlim(false);
        var initError = (Exception?)null;
        _thread = new Thread(() =>
        {
            try
            {
                // Apartment state is already set to STA below (before Start()); setting it
                // again here from within the thread itself is redundant.
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

    public Task<T> InvokeAsync<T>(
        Func<T> func,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UiaThread));
        var effectiveTimeout = timeout ?? DefaultOperationTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem<T> { Func = func, Tcs = tcs, Token = ct };
        if (ct.CanBeCanceled)
            item.CancellationRegistration = ct.Register(
                static state => ((WorkItem<T>)state!).Cancel(),
                item);
        item.TimeoutSource = new CancellationTokenSource(effectiveTimeout);
        item.TimeoutRegistration = item.TimeoutSource.Token.Register(
            static state =>
            {
                var (workItem, operationTimeout) = ((WorkItem<T>, TimeSpan))state!;
                workItem.Timeout(operationTimeout);
            },
            (item, effectiveTimeout));
        try
        {
            if (!_queue.TryAdd(item))
                item.Fail(new InvalidOperationException(
                    "UI Automation queue is full. Wait for pending operations or restart DCU."));
        }
        catch
        {
            item.DisposeRegistrations();
            throw;
        }
        return tcs.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _queue.CompleteAdding(); } catch { }
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Cancel();
            item.DisposeRegistrations();
        }
        _thread.Join(2000);
        _queue.Dispose();
    }
}
