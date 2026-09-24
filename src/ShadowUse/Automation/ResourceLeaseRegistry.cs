// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>F35-R01/R02: process-lifetime per-resource-key mutual exclusion for concurrent DAG
/// workers (WorkflowRunner). Acquiring a key that is already held waits until the holder disposes
/// its lease (real serialization -- F35-T01's "two workers requesting the same mutable target are
/// serialised"); different keys never block each other (independent contexts progress
/// concurrently). A lease held during a caller's own cancellation/exception is still released
/// because the caller wraps the guarded work in try/finally around DisposeAsync -- this registry
/// itself never leaks a permit either way, matching F35-T02 (an expired/cancelled worker cannot
/// keep an old lease that blocks everyone else forever).</summary>
public sealed class ResourceLeaseRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public async Task<IAsyncDisposable> AcquireAsync(string resourceKey, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(resourceKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>True if resourceKey is currently held by an outstanding, undisposed lease.
    /// Diagnostic only -- never used to decide whether to dispatch.</summary>
    public bool IsHeld(string resourceKey) => _gates.TryGetValue(resourceKey, out var gate) && gate.CurrentCount == 0;

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
