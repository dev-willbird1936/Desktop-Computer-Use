// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Runtime.InteropServices;
using ShadowUse.Native;

namespace ShadowUse.Automation;

/// <summary>Lifecycle of the dedicated native WinEvent monitor.</summary>
public enum NativeTargetMonitorState
{
    NotStarted,
    Starting,
    Active,
    Stopping,
    Stopped,
    Failed,
    Disposed,
}

/// <summary>
/// Watches native window create/destroy WinEvents on a dedicated message-pumped thread.
/// Only explicitly tracked target references can be invalidated. The monitor is lazy: the
/// WinEvent hook is installed when the first reference is tracked. WinEvent delivery is
/// asynchronous, so this bridge does not provide an atomic operating-system lifetime lock.
/// A destroy callback must match the tracked owner thread and observe the HWND as gone before
/// exact references are invalidated. A live/reused HWND or ambiguous event quarantines the
/// monitor without mutating registry references. The check and invalidation are still subject
/// to the tiny OS-level TOCTOU window inherent in asynchronous WinEvent delivery.
/// </summary>
public sealed class NativeTargetMonitor : IDisposable
{
    private const uint CheckPendingDestroyMessage = 0x8000 + 0x5A;
    private const int MaxTrackedReferencesPerHwnd = 4;
    private static readonly TimeSpan DestroyCheckGrace = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly TargetRegistry _registry;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _startupCompleted = new(false);
    private readonly NativeMethods.WinEventProc _winEventProc;
    private readonly Dictionary<IntPtr, List<TrackedTarget>> _trackedByHwnd = [];
    private readonly Dictionary<IntPtr, PendingDestroy> _pendingDestroys = [];
    private Thread? _thread;
    private IntPtr _hook;
    private uint _threadId;
    private Exception? _failure;
    private int _state = (int)NativeTargetMonitorState.NotStarted;
    private int _disposeRequested;

    private sealed record TrackedTarget(NativeTargetReference Reference, uint OwnerThreadId);
    private sealed record PendingDestroy(TrackedTarget[] Targets);

    public NativeTargetMonitor(
        TargetRegistry registry,
        TimeSpan? startupTimeout = null,
        TimeSpan? shutdownTimeout = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _startupTimeout = ValidateTimeout(startupTimeout ?? DefaultStartupTimeout, nameof(startupTimeout));
        _shutdownTimeout = ValidateTimeout(shutdownTimeout ?? DefaultShutdownTimeout, nameof(shutdownTimeout));
        _winEventProc = HandleWinEvent;
    }

    /// <summary>Current monitor state. Failed and stopped states are never reported as active.</summary>
    public NativeTargetMonitorState State
        => (NativeTargetMonitorState)Volatile.Read(ref _state);

    /// <summary>True only after SetWinEventHook succeeded and before shutdown starts.</summary>
    public bool IsActive => State == NativeTargetMonitorState.Active;

    /// <summary>
    /// The startup, message-loop, callback, unhook, or bounded-shutdown failure, if any.
    /// A non-null value is observable even when the monitor has stopped.
    /// </summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
                return _failure;
        }
    }

    /// <summary>
    /// Tracks one active registry reference. The out error describes why registration was
    /// refused; a false result never means that native lifetime tracking is active.
    /// </summary>
    public bool TryTrack(NativeTargetReference target, out string? error)
    {
        error = null;

        // ObserveTarget can retire a previously tracked reference after this method refuses
        // a replacement. Sweep every HWND bucket before any early return so stale lifetimes
        // from distinct HWNDs cannot accumulate. This is managed registry state only; the
        // native callback path remains unchanged.
        PruneStaleTracked();

        if (!target.IsValid)
        {
            error = "The native target reference is invalid.";
            return false;
        }

        if (!_registry.IsCurrent(target))
        {
            error = "The native target reference is not active in this registry.";
            return false;
        }

        if (!EnsureStarted(out error))
            return false;

        uint ownerThreadId = NativeMethods.GetWindowThreadProcessId(target.Hwnd, out uint ownerPid);
        if (ownerThreadId == 0
            || ownerPid != (uint)target.Pid
            || !NativeMethods.IsWindow(target.Hwnd))
        {
            // A registry replacement can retire another tracked reference while the native
            // availability check is in flight. Sweep again on this failed-registration path.
            PruneStaleTracked();
            error = "The native target window is unavailable or belongs to another process.";
            return false;
        }

        lock (_gate)
        {
            // Recheck under the monitor gate so a registry replacement that races the
            // native availability check is removed before any registration refusal.
            PruneStaleTrackedNoLock();

            if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active)
            {
                error = StateDescriptionNoLock();
                return false;
            }

            if (!_registry.IsCurrent(target))
            {
                error = "The native target reference stopped being current during registration.";
                return false;
            }

            List<TrackedTarget> tracked;
            bool newBucket;
            if (_trackedByHwnd.TryGetValue(target.Hwnd, out var existingTracked))
            {
                tracked = existingTracked;
                newBucket = false;
            }
            else
            {
                tracked = [];
                newBucket = true;
            }

            if (!tracked.Any(item => item.Reference == target))
            {
                if (tracked.Count >= MaxTrackedReferencesPerHwnd)
                {
                    error = $"Native target monitoring refused the HWND because its tracked lifetime list reached the limit ({MaxTrackedReferencesPerHwnd}).";
                    return false;
                }

                // TargetRegistry allows at most MaxActiveTargets current references. The
                // global sweep above removes retired references before this check, so this
                // monitor-wide bound also protects against cross-HWND accumulation.
                if (CountTrackedReferencesNoLock() >= _registry.MaxActiveTargets)
                {
                    error = $"Native target monitoring refused the reference because its global tracked lifetime list reached the limit ({_registry.MaxActiveTargets}).";
                    return false;
                }

                if (newBucket)
                    _trackedByHwnd.Add(target.Hwnd, tracked);
                tracked.Add(new TrackedTarget(target, ownerThreadId));
            }
        }

        // Close the small race between the availability check and adding the reference. This
        // check is outside the callback and therefore cannot make the WinEvent path blocking.
        if (!NativeMethods.IsWindow(target.Hwnd))
        {
            RemoveFailedTracking(target);
            error = "The native target window was destroyed during registration.";
            return false;
        }

        // Quarantine can race the post-registration liveness check. Confirm the monitor and
        // exact reference while holding the same gate used by the callback before succeeding.
        if (!TryConfirmTracking(target, out error))
        {
            RemoveFailedTracking(target);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Confirms that the monitor is still healthy and is tracking the exact active reference.
    /// This is intentionally separate from <see cref="IsActive"/> so callers can make the
    /// final health check under the monitor gate immediately before dispatch.
    /// </summary>
    public bool TryConfirmTracking(NativeTargetReference target, out string? error)
    {
        error = null;
        if (!target.IsValid)
        {
            error = "The native target reference is invalid.";
            return false;
        }

        lock (_gate)
        {
            if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active
                || _failure != null)
            {
                error = StateDescriptionNoLock();
                return false;
            }

            PruneTrackedNoLock(target.Hwnd);
            if (!_registry.IsCurrent(target))
            {
                error = "The native target reference is no longer current in this registry.";
                return false;
            }

            if (!_trackedByHwnd.TryGetValue(target.Hwnd, out var tracked)
                || !tracked.Any(item => item.Reference == target))
            {
                error = "The native target reference is no longer tracked.";
                return false;
            }

            return true;
        }
    }

    /// <summary>Stops watching one exact reference without affecting an HWND replacement.</summary>
    public bool Untrack(NativeTargetReference target)
    {
        if (!target.IsValid)
            return false;

        lock (_gate)
        {
            if (!_trackedByHwnd.TryGetValue(target.Hwnd, out var tracked))
                return false;

            int removed = tracked.RemoveAll(item => item.Reference == target);
            if (tracked.Count == 0)
                _trackedByHwnd.Remove(target.Hwnd);
            return removed != 0;
        }
    }

    /// <summary>
    /// Removes only the failed reference from monitor state and registry state. The registry
    /// compares the exact opaque reference, so a replacement on the same HWND is preserved.
    /// </summary>
    private void RemoveFailedTracking(NativeTargetReference target)
    {
        Untrack(target);
        _registry.Invalidate(target);
    }

    private void PruneStaleTracked()
    {
        lock (_gate)
            PruneStaleTrackedNoLock();
    }

    private void PruneStaleTrackedNoLock()
    {
        foreach (var (hwnd, tracked) in _trackedByHwnd.ToArray())
        {
            tracked.RemoveAll(item => !_registry.IsCurrent(item.Reference));
            if (tracked.Count == 0)
                _trackedByHwnd.Remove(hwnd);
        }
    }

    private int CountTrackedReferencesNoLock()
        => _trackedByHwnd.Values.Sum(tracked => tracked.Count);

    private void PruneTrackedNoLock(IntPtr hwnd)
    {
        if (!_trackedByHwnd.TryGetValue(hwnd, out var tracked))
            return;

        tracked.RemoveAll(item => !_registry.IsCurrent(item.Reference));
        if (tracked.Count == 0)
            _trackedByHwnd.Remove(hwnd);
    }

    private bool EnsureStarted(out string? error)
    {
        error = null;
        Thread? threadToStart = null;
        lock (_gate)
        {
            switch ((NativeTargetMonitorState)_state)
            {
                case NativeTargetMonitorState.Active:
                    return true;
                case NativeTargetMonitorState.Starting:
                    break;
                case NativeTargetMonitorState.NotStarted:
                    _state = (int)NativeTargetMonitorState.Starting;
                    threadToStart = new Thread(Run)
                    {
                        IsBackground = true,
                        Name = "ShadowUse.NativeWinEvent",
                    };
                    _thread = threadToStart;
                    break;
                default:
                    error = StateDescriptionNoLock();
                    return false;
            }
        }

        if (threadToStart != null)
        {
            try
            {
                threadToStart.Start();
            }
            catch (Exception ex)
            {
                MarkFailure(ex);
                error = $"Native target monitoring could not start: {ex.Message}";
                return false;
            }
        }

        if (!_startupCompleted.Wait(_startupTimeout))
        {
            var timeout = new TimeoutException(
                $"Native target monitoring did not start within {_startupTimeout.TotalSeconds:0.###} seconds.");
            MarkFailure(timeout);
            RequestStop();
            JoinThread();
            error = timeout.Message;
            return false;
        }

        lock (_gate)
        {
            if ((NativeTargetMonitorState)_state == NativeTargetMonitorState.Active)
                return true;

            error = StateDescriptionNoLock();
            return false;
        }
    }

    private void Run()
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();

            // A thread message queue is created by PeekMessage. SetWinEventHook then binds
            // delivery to this thread, which must continue pumping until shutdown.
            NativeMethods.PeekMessageW(
                out _,
                IntPtr.Zero,
                0,
                0,
                NativeMethods.PM_NOREMOVE);

            lock (_gate)
            {
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    _startupCompleted.Set();
                    return;
                }
            }

            hook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_CREATE,
                NativeMethods.EVENT_OBJECT_DESTROY,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (hook == IntPtr.Zero)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error, "SetWinEventHook failed.");
            }

            lock (_gate)
            {
                _hook = hook;
                _state = Volatile.Read(ref _disposeRequested) == 0
                    ? (int)NativeTargetMonitorState.Active
                    : (int)NativeTargetMonitorState.Stopping;
                _startupCompleted.Set();
            }

            if (Volatile.Read(ref _disposeRequested) != 0)
                return;

            while (true)
            {
                int result = NativeMethods.GetMessageW(
                    out var message,
                    IntPtr.Zero,
                    0,
                    0);
                if (result == -1)
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw new Win32Exception(error, "The native target monitor message loop failed.");
                }

                if (result == 0)
                    break;

                if (message.Message == CheckPendingDestroyMessage)
                {
                    ProcessPendingDestroy(message.WParam);
                    continue;
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }
        }
        catch (Exception ex)
        {
            MarkFailure(ex);
            _startupCompleted.Set();
        }
        finally
        {
            if (hook != IntPtr.Zero)
            {
                bool unhooked = false;
                try
                {
                    // This is intentionally executed on the registering thread. Calling
                    // UnhookWinEvent from Dispose's caller would violate the Win32 contract.
                    unhooked = NativeMethods.UnhookWinEvent(hook);
                }
                catch (Exception ex)
                {
                    MarkFailure(ex);
                }

                if (!unhooked)
                {
                    int error = Marshal.GetLastPInvokeError();
                    MarkFailure(new Win32Exception(error, "UnhookWinEvent failed."));
                }
            }

            lock (_gate)
            {
                _hook = IntPtr.Zero;
                if (Volatile.Read(ref _disposeRequested) != 0)
                {
                    _state = _failure == null
                        ? (int)NativeTargetMonitorState.Disposed
                        : (int)NativeTargetMonitorState.Failed;
                }
                else
                {
                    _state = _failure == null
                        ? (int)NativeTargetMonitorState.Stopped
                        : (int)NativeTargetMonitorState.Failed;
                }
                _trackedByHwnd.Clear();
                _pendingDestroys.Clear();
                _startupCompleted.Set();
            }
        }
    }

    private void HandleWinEvent(
        IntPtr hWinEventHook,
        uint @event,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint idEventThread,
        uint msEventTime)
    {
        try
        {
            // CREATE is intentionally observed by the hook but needs no registry mutation.
            // DESTROY is the only event that can retire a tracked native lifetime.
            if (@event != NativeMethods.EVENT_OBJECT_DESTROY
                || hwnd == IntPtr.Zero
                || idObject != NativeMethods.OBJID_WINDOW
                || idChild != NativeMethods.CHILDID_SELF)
                return;

            TrackedTarget[] matching;
            bool authenticationAmbiguous;
            bool trackingOverflow;
            lock (_gate)
            {
                if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active)
                    return;

                // Registry replacement can retire a reference without a corresponding
                // callback. Prune those entries before matching so this list stays bounded
                // and stale lifetimes cannot make an event ambiguous forever.
                PruneTrackedNoLock(hwnd);
                if (!_trackedByHwnd.TryGetValue(hwnd, out var tracked))
                    return;

                trackingOverflow = tracked.Count > MaxTrackedReferencesPerHwnd;
                matching = trackingOverflow
                    ? []
                    : tracked
                        .Where(item => item.OwnerThreadId == idEventThread)
                        .ToArray();
                authenticationAmbiguous = !trackingOverflow && matching.Length != 1;
            }

            if (trackingOverflow)
            {
                // Do not perform unbounded callback matching if an unexpected state has
                // already exceeded the per-HWND bound. Quarantine instead of guessing.
                Quarantine($"The native target HWND exceeded the tracked lifetime limit ({MaxTrackedReferencesPerHwnd}).");
                return;
            }

            if (authenticationAmbiguous)
            {
                // A tracked HWND received an event that does not identify exactly one
                // lifetime. Do not guess which lifetime the asynchronous event describes.
                Quarantine("The native target destroy event did not identify exactly one tracked lifetime.");
                return;
            }

            // EVENT_OBJECT_DESTROY can be delivered while the owner thread is still inside
            // DestroyWindow. Defer the bounded native-liveness check until this callback has
            // returned, so a clean event is not mistaken for a live HWND merely because the
            // asynchronous callback won the teardown race.
            lock (_gate)
            {
                if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active)
                    return;

                _pendingDestroys[hwnd] = new PendingDestroy(matching);
            }

            if (!NativeMethods.PostThreadMessageW(
                    _threadId,
                    CheckPendingDestroyMessage,
                    hwnd,
                    IntPtr.Zero))
            {
                int error = Marshal.GetLastPInvokeError();
                Quarantine($"The native target destroy event could not be deferred for authentication (Win32 error {error}).");
            }
        }
        catch (Exception ex)
        {
            // Exceptions must not cross the unmanaged callback boundary. Stop advertising
            // active tracking if an unexpected callback failure occurs.
            MarkFailure(ex);
            RequestStop();
        }
    }

    private void ProcessPendingDestroy(IntPtr hwnd)
    {
        PendingDestroy? pending;
        lock (_gate)
        {
            if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active
                || !_pendingDestroys.Remove(hwnd, out pending))
                return;
        }

        // A queued destroy event can arrive after the HWND has been reused. A live HWND
        // therefore makes the event ambiguous; retain every registry reference and stop
        // accepting new dispatch targets rather than invalidating a replacement.
        if (NativeMethods.IsWindow(hwnd))
        {
            // WinEvent delivery can precede the owner thread's return from DestroyWindow.
            // Keep the grace bounded and outside the unmanaged callback, then authenticate
            // the HWND again before deciding that it is a live/reused target.
            Thread.Sleep(DestroyCheckGrace);
            if (NativeMethods.IsWindow(hwnd))
            {
                Quarantine("The native target destroy event was ambiguous because its HWND is still live.");
                return;
            }
        }

        lock (_gate)
        {
            if ((NativeTargetMonitorState)_state != NativeTargetMonitorState.Active)
                return;

            PruneTrackedNoLock(hwnd);
            if (!_trackedByHwnd.TryGetValue(hwnd, out var tracked))
                return;

            foreach (var item in pending.Targets)
                tracked.Remove(item);
            if (tracked.Count == 0)
                _trackedByHwnd.Remove(hwnd);
        }

        // Remove tracking before invalidating so a reentrant callback cannot process the
        // same reference twice. TargetRegistry performs the exact-reference check. The
        // IsWindow check above is deliberately fail-closed but cannot close the tiny OS
        // TOCTOU window between checking and invalidating.
        foreach (var item in pending.Targets)
            _registry.Invalidate(item.Reference);
    }

    private void MarkFailure(Exception error)
    {
        lock (_gate)
        {
            _failure ??= error;
            if ((NativeTargetMonitorState)_state is NativeTargetMonitorState.NotStarted
                or NativeTargetMonitorState.Starting
                or NativeTargetMonitorState.Active
                or NativeTargetMonitorState.Stopping)
                _state = (int)NativeTargetMonitorState.Failed;
        }
    }

    private void Quarantine(string reason)
    {
        // Keep tracked registry references untouched. Once failed, EnsureStarted/TryTrack
        // reject future dispatch rather than trusting a monitor that saw an ambiguous event.
        MarkFailure(new InvalidOperationException(reason));
        RequestStop();
    }

    private void RequestStop()
    {
        uint threadId;
        lock (_gate)
        {
            Volatile.Write(ref _disposeRequested, 1);
            if ((NativeTargetMonitorState)_state is NativeTargetMonitorState.Active
                or NativeTargetMonitorState.Starting)
                _state = (int)NativeTargetMonitorState.Stopping;
            threadId = _threadId;
        }

        if (threadId != 0)
        {
            try
            {
                NativeMethods.PostThreadMessageW(
                    threadId,
                    NativeMethods.WM_QUIT,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            catch
            {
                // The bounded Join below reports a stuck cleanup; the caller thread never
                // invokes UnhookWinEvent itself.
            }
        }
    }

    private void JoinThread()
    {
        Thread? thread;
        lock (_gate)
            thread = _thread;

        if (thread == null || Thread.CurrentThread.ManagedThreadId == thread.ManagedThreadId)
            return;

        try
        {
            if (!thread.Join(_shutdownTimeout))
            {
                MarkFailure(new TimeoutException(
                    $"Native target monitor shutdown did not complete within {_shutdownTimeout.TotalSeconds:0.###} seconds."));
            }
        }
        catch (ThreadStateException ex)
        {
            MarkFailure(ex);
        }
    }

    private string StateDescriptionNoLock()
    {
        return (NativeTargetMonitorState)_state switch
        {
            NativeTargetMonitorState.Failed when _failure != null
                => $"Native target monitoring failed: {_failure.Message}",
            NativeTargetMonitorState.Disposed
                => "Native target monitoring has been disposed.",
            NativeTargetMonitorState.Stopping
                => "Native target monitoring is stopping.",
            NativeTargetMonitorState.Stopped
                => "Native target monitoring has stopped.",
            _ => $"Native target monitoring is not active (state: {(NativeTargetMonitorState)_state}).",
        };
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, "The timeout must be positive and fit a thread wait.");
        return timeout;
    }

    public void Dispose()
    {
        bool alreadyDisposed;
        lock (_gate)
        {
            alreadyDisposed = (NativeTargetMonitorState)_state == NativeTargetMonitorState.Disposed
                && (_thread is null || !_thread.IsAlive);
            if (alreadyDisposed)
                return;

            if (_thread == null)
            {
                Volatile.Write(ref _disposeRequested, 1);
                _state = (int)NativeTargetMonitorState.Disposed;
                _trackedByHwnd.Clear();
                _pendingDestroys.Clear();
                _startupCompleted.Set();
                return;
            }
        }

        RequestStop();
        JoinThread();

        lock (_gate)
        {
            if (!_thread.IsAlive && _failure == null)
                _state = (int)NativeTargetMonitorState.Disposed;
        }

        GC.SuppressFinalize(this);
    }
}
