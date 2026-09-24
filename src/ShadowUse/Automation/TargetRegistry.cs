// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Generic;

namespace ShadowUse.Automation;

/// <summary>
/// Native identity observed for one top-level target. A positive process-creation token
/// is required so a PID is never treated as a lifetime identity on its own.
/// </summary>
public readonly record struct NativeTargetIdentity(
    int Pid,
    long ProcessCreationToken,
    IntPtr Hwnd)
{
    public bool IsValid
        => Pid > 0
        && ProcessCreationToken > 0
        && Hwnd != IntPtr.Zero;
}

/// <summary>
/// Opaque, session-scoped reference to a native target lifetime.
/// </summary>
public readonly record struct NativeTargetReference(
    string TargetId,
    string SessionId,
    int Pid,
    long ProcessCreationToken,
    IntPtr Hwnd,
    long LifetimeEpoch)
{
    public NativeTargetIdentity Identity
        => new(Pid, ProcessCreationToken, Hwnd);

    public bool IsValid
        => !string.IsNullOrWhiteSpace(TargetId)
        && !string.IsNullOrWhiteSpace(SessionId)
        && LifetimeEpoch > 0
        && Identity.IsValid;
}

/// <summary>
/// Session-scoped registry of currently live native target lifetimes.
/// Retired references are removed rather than retained as a history cache.
/// </summary>
public sealed class TargetRegistry
{
    public const int DefaultMaxActiveTargets = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, NativeTargetReference> _activeById = new(StringComparer.Ordinal);
    private readonly Dictionary<IntPtr, string> _activeIdByHwnd = [];
    private readonly int _maxActiveTargets;
    private long _nextLifetimeEpoch;

    public TargetRegistry(
        string? sessionId = null,
        int maxActiveTargets = DefaultMaxActiveTargets)
    {
        if (maxActiveTargets <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActiveTargets), "The active target limit must be positive.");

        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")
            : sessionId;
        _maxActiveTargets = maxActiveTargets;
    }

    public string SessionId { get; }

    public int MaxActiveTargets => _maxActiveTargets;

    public int ActiveCount
    {
        get
        {
            lock (_gate)
                return _activeById.Count;
        }
    }

    /// <summary>
    /// Registers an identity or returns its existing active reference. A new native
    /// lifetime on the same HWND retires the previous reference before admitting the new one.
    /// </summary>
    public NativeTargetReference Register(NativeTargetIdentity identity)
    {
        ValidateIdentity(identity);

        lock (_gate)
        {
            if (TryFindSameHwndNoLock(identity, out var prior))
            {
                if (prior.Identity == identity)
                    return prior;

                // Replacing one HWND does not increase active state, so a full registry
                // still admits this replacement while preserving the limit.
                RetireNoLock(prior);
            }
            else if (_activeById.Count >= _maxActiveTargets)
            {
                throw new InvalidOperationException(
                    $"The target registry has reached its active limit ({_maxActiveTargets}).");
            }

            var target = CreateReferenceNoLock(identity);
            _activeById.Add(target.TargetId, target);
            _activeIdByHwnd.Add(identity.Hwnd, target.TargetId);
            return target;
        }
    }

    /// <summary>
    /// Attempts registration without throwing for an unknown/invalid identity or a full registry.
    /// </summary>
    public bool TryRegister(
        NativeTargetIdentity identity,
        out NativeTargetReference target)
    {
        target = default;
        if (!identity.IsValid)
            return false;

        lock (_gate)
        {
            if (TryFindSameHwndNoLock(identity, out var prior))
            {
                if (prior.Identity == identity)
                {
                    target = prior;
                    return true;
                }

                RetireNoLock(prior);
            }
            else if (_activeById.Count >= _maxActiveTargets)
            {
                return false;
            }

            target = CreateReferenceNoLock(identity);
            _activeById.Add(target.TargetId, target);
            _activeIdByHwnd.Add(identity.Hwnd, target.TargetId);
            return true;
        }
    }

    /// <summary>
    /// Returns the active reference for an opaque target ID. Retired IDs are not retained.
    /// </summary>
    public bool TryGet(string? targetId, out NativeTargetReference target)
    {
        target = default;
        if (string.IsNullOrWhiteSpace(targetId))
            return false;

        lock (_gate)
            return _activeById.TryGetValue(targetId, out target);
    }

    /// <summary>
    /// Compares both the opaque reference and the currently observed native identity.
    /// Unknown creation identity fails closed, even when PID and HWND match.
    /// </summary>
    public bool IsCurrent(
        NativeTargetReference target,
        NativeTargetIdentity currentIdentity)
    {
        if (!target.IsValid || !currentIdentity.IsValid)
            return false;

        lock (_gate)
        {
            return string.Equals(target.SessionId, SessionId, StringComparison.Ordinal)
                && _activeById.TryGetValue(target.TargetId, out var active)
                && active == target
                && active.Identity == currentIdentity;
        }
    }

    /// <summary>Checks whether the exact reference remains active.</summary>
    public bool IsCurrent(NativeTargetReference target)
    {
        if (!target.IsValid)
            return false;

        lock (_gate)
        {
            return string.Equals(target.SessionId, SessionId, StringComparison.Ordinal)
                && _activeById.TryGetValue(target.TargetId, out var active)
                && active == target;
        }
    }

    /// <summary>
    /// Explicitly retires the exact reference. A stale reference cannot invalidate a replacement.
    /// </summary>
    public bool Invalidate(NativeTargetReference target)
    {
        if (!target.IsValid)
            return false;

        lock (_gate)
        {
            if (!string.Equals(target.SessionId, SessionId, StringComparison.Ordinal)
                || !_activeById.TryGetValue(target.TargetId, out var active)
                || active != target)
                return false;

            RetireNoLock(active);
            return true;
        }
    }

    /// <summary>
    /// Explicitly retires the current target for an exact native identity. An event carrying
    /// an old process-creation token cannot invalidate a replacement that reuses the HWND.
    /// </summary>
    public bool Invalidate(NativeTargetIdentity identity)
    {
        if (!identity.IsValid)
            return false;

        lock (_gate)
        {
            if (!TryFindSameHwndNoLock(identity, out var active)
                || active.Identity != identity)
                return false;

            RetireNoLock(active);
            return true;
        }
    }

    private static void ValidateIdentity(NativeTargetIdentity identity)
    {
        if (!identity.IsValid)
            throw new ArgumentException(
                "A positive PID, known positive process-creation token, and non-zero HWND are required; PID alone is not a lifetime identity.",
                nameof(identity));
    }

    private bool TryFindSameHwndNoLock(
        NativeTargetIdentity identity,
        out NativeTargetReference target)
    {
        if (_activeIdByHwnd.TryGetValue(identity.Hwnd, out var targetId)
            && _activeById.TryGetValue(targetId, out target))
            return true;

        target = default;
        return false;
    }

    private NativeTargetReference CreateReferenceNoLock(NativeTargetIdentity identity)
    {
        string targetId;
        do
        {
            targetId = Guid.NewGuid().ToString("N");
        }
        while (_activeById.ContainsKey(targetId));

        if (_nextLifetimeEpoch == long.MaxValue)
            throw new InvalidOperationException("The target lifetime epoch is exhausted.");

        return new NativeTargetReference(
            targetId,
            SessionId,
            identity.Pid,
            identity.ProcessCreationToken,
            identity.Hwnd,
            ++_nextLifetimeEpoch);
    }

    private void RetireNoLock(NativeTargetReference target)
    {
        _activeById.Remove(target.TargetId);
        if (_activeIdByHwnd.TryGetValue(target.Hwnd, out var currentId)
            && string.Equals(currentId, target.TargetId, StringComparison.Ordinal))
            _activeIdByHwnd.Remove(target.Hwnd);
    }
}
