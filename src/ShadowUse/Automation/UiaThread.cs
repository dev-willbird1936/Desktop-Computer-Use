// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ShadowUse.Automation;

/// <summary>
/// Dedicated STA thread for all UI Automation COM calls.
/// COM objects created here live and die on this thread — no cross-apartment marshaling,
/// no deadlocks.
///
/// A COM call into an unresponsive target process can block the executing thread forever;
/// there is no safe way to abort a running STA COM call from another thread. When that
/// happens, a background watchdog abandons the wedged worker (marks its queue complete so
/// it exits on its own if the call ever returns, or leaks harmlessly as a background thread
/// for the rest of the process's life otherwise) and starts a fresh worker thread, carrying
/// over every not-yet-started queued item. Only the single call that was genuinely stuck
/// stays stuck — every other target and every subsequent call recovers automatically instead
/// of every tool call timing out until DCU is restarted.
/// </summary>
public sealed class UiaThread : IDisposable
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchdogPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WedgeGrace = TimeSpan.FromSeconds(2);

    private readonly object _swapLock = new();
    private readonly Thread _watchdog;
    private readonly ManualResetEventSlim _watchdogStop = new();
    private Worker _worker;
    private int _disposed;

    private abstract class WorkItem
    {
        public TimeSpan EffectiveTimeout;
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

    /// <summary>One backing (queue, thread) pair. A worker is abandoned, never reused, once its
    /// currently-executing item is judged wedged by the watchdog -- a fresh worker takes over.</summary>
    private sealed class Worker
    {
        public readonly BlockingCollection<WorkItem> Queue = new(128);
        public Thread? Thread;
        private readonly object _currentLock = new();
        private WorkItem? _currentItem;
        private long _currentItemStartedAtTicks;

        public void SetCurrent(WorkItem? item)
        {
            lock (_currentLock)
            {
                _currentItem = item;
                _currentItemStartedAtTicks = item == null ? 0 : Stopwatch.GetTimestamp();
            }
        }

        /// <summary>True once the currently-executing item has run longer than its OWN configured
        /// timeout plus a fixed grace period -- not a blanket constant -- so a caller that
        /// deliberately requested a longer timeout for a genuinely slow operation is not treated
        /// as wedged before that operation's own timeout would even have fired.</summary>
        public bool CurrentItemIsWedged(TimeSpan grace)
        {
            lock (_currentLock)
            {
                if (_currentItem == null) return false;
                var elapsed = Stopwatch.GetElapsedTime(_currentItemStartedAtTicks);
                return elapsed > _currentItem.EffectiveTimeout + grace;
            }
        }
    }

    public UiaThread()
    {
        _worker = StartWorker();
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "ShadowUse.UIA.Watchdog" };
        _watchdog.Start();
    }

    private static Worker StartWorker()
    {
        var worker = new Worker();
        var ready = new ManualResetEventSlim(false);
        var initError = (Exception?)null;
        var thread = new Thread(() =>
        {
            try
            {
                ready.Set();
                foreach (var item in worker.Queue.GetConsumingEnumerable())
                {
                    worker.SetCurrent(item);
                    item.Execute();
                    worker.SetCurrent(null);
                }
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
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (initError != null) throw initError;
        worker.Thread = thread;
        return worker;
    }

    /// <summary>Polls for a wedged worker and replaces it. Bounded, cheap polling (250ms) rather
    /// than a per-item timer: a dedicated watchdog thread is simpler to reason about than N
    /// concurrent per-item timers racing the same swap, and 250ms latency on top of an already
    /// multi-second timeout is immaterial.</summary>
    private void WatchdogLoop()
    {
        while (!_watchdogStop.Wait(WatchdogPollInterval))
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Worker current;
            lock (_swapLock) current = _worker;
            if (!current.CurrentItemIsWedged(WedgeGrace)) continue;
            lock (_swapLock)
            {
                if (!ReferenceEquals(_worker, current)) continue; // already swapped by a prior tick
                if (Volatile.Read(ref _disposed) != 0) return;
                ReplaceWedgedWorkerLocked(current);
            }
        }
    }

    /// <summary>Must be called under <see cref="_swapLock"/>. Starts a fresh worker, moves every
    /// not-yet-started item from the wedged worker's queue onto it, and marks the wedged queue
    /// complete so its thread exits on its own the moment its blocked COM call ever returns
    /// (instead of blocking forever afterward on an now-empty, no-longer-fed collection).</summary>
    private void ReplaceWedgedWorkerLocked(Worker wedged)
    {
        var replacement = StartWorker();
        try { wedged.Queue.CompleteAdding(); } catch (InvalidOperationException) { }
        while (wedged.Queue.TryTake(out var carried))
        {
            if (!replacement.Queue.TryAdd(carried))
                carried.Fail(new InvalidOperationException(
                    "UI Automation queue is full. Wait for pending operations or restart DCU."));
        }
        _worker = replacement;
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
        var item = new WorkItem<T> { Func = func, Tcs = tcs, Token = ct, EffectiveTimeout = effectiveTimeout };
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
            // Retried once: a watchdog swap can call CompleteAdding() on the worker read below
            // between that read and TryAdd -- an extremely narrow race, not a normal "queue full"
            // condition -- in which case TryAdd throws InvalidOperationException rather than
            // returning false. Re-reading _worker and retrying once targets whatever worker is
            // current after the swap instead of surfacing a spurious error to the caller.
            for (var attempt = 0; ; attempt++)
            {
                Worker worker;
                lock (_swapLock)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(UiaThread));
                    worker = _worker;
                }
                try
                {
                    if (!worker.Queue.TryAdd(item))
                        item.Fail(new InvalidOperationException(
                            "UI Automation queue is full. Wait for pending operations or restart DCU."));
                    break;
                }
                catch (InvalidOperationException) when (attempt == 0)
                {
                    // that worker's queue was completed by a concurrent watchdog swap; retry once
                }
            }
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
        _watchdogStop.Set();
        Worker current;
        lock (_swapLock) current = _worker;
        try { current.Queue.CompleteAdding(); } catch { }
        foreach (var item in current.Queue.GetConsumingEnumerable())
        {
            item.Cancel();
            item.DisposeRegistrations();
        }
        // Bounded: if the current worker's own thread is itself wedged on a blocked COM call,
        // Join will time out here rather than hang Dispose -- the thread is a background thread
        // and will not keep the process alive; any previously-abandoned wedged workers from
        // earlier watchdog swaps are not tracked or joined here for the same reason.
        current.Thread?.Join(2000);
        current.Queue.Dispose();
        _watchdogStop.Dispose();
    }
}
