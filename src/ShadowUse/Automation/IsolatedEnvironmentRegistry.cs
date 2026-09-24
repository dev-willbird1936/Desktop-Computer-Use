// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum IsolatedEnvironmentState { Created, Attached, Active, Destroyed }

/// <summary>F36-R01/R02: no sandbox/VM infrastructure (Windows Sandbox, Hyper-V, Docker Desktop)
/// is available in this environment -- confirmed and documented, not assumed (same standing
/// investigation as prior sessions). DCU cannot itself create or run an isolated OS-level
/// environment here. What IS genuinely DCU's own job, per this project's established pattern
/// (F24/F57/F58 all scaffold deterministic state around an externally-supplied resource rather
/// than performing the infrastructure-requiring step themselves): the create/attach/reset/destroy
/// lifecycle bookkeeping and lease-expiry enforcement around an environment an external provider
/// already created. F36-T01's actual host-isolation claim (an isolated fixture using interactive
/// input without touching the host) is NOT demonstrated by this class -- it requires a real
/// sandbox to run inside, which does not exist here, and is documented as the honest remaining
/// gap rather than silently assumed proven by the lifecycle bookkeeping below.</summary>
public sealed class IsolatedEnvironment
{
    public required string EnvironmentId { get; init; }
    public required string ProviderKind { get; init; }
    public IsolatedEnvironmentState State { get; set; } = IsolatedEnvironmentState.Created;
    public string? ExternalHandle { get; set; }
    public string? LeaseHolderActorId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public IReadOnlyList<string> RetainedArtifactIds { get; set; } = [];
}

public sealed class IsolatedEnvironmentRegistry
{
    private readonly ConcurrentDictionary<string, IsolatedEnvironment> _environments = new();

    /// <summary>F36-R01: register a new isolated-environment record. provider_kind is a
    /// caller-asserted label (e.g. "external_vm", "external_sandbox") -- DCU never launches,
    /// validates, or connects to anything; this is pure bookkeeping from the start.</summary>
    public IsolatedEnvironment Create(string environmentId, string providerKind)
    {
        if (string.IsNullOrWhiteSpace(environmentId))
            throw new ArgumentException("environment_id is required.");
        if (string.IsNullOrWhiteSpace(providerKind))
            throw new ArgumentException("provider_kind is required.");
        if (!_environments.TryAdd(environmentId, new IsolatedEnvironment { EnvironmentId = environmentId, ProviderKind = providerKind }))
            throw new ArgumentException($"Isolated environment '{environmentId}' already exists.");
        return _environments[environmentId];
    }

    public IsolatedEnvironment Find(string environmentId) =>
        _environments.GetValueOrDefault(environmentId) ?? throw new ArgumentException($"Isolated environment '{environmentId}' was not found.");

    /// <summary>Records that this environment is now attached to an externally-supplied
    /// connection descriptor. The handle is opaque to DCU -- never dialed, never validated.</summary>
    public IsolatedEnvironment Attach(string environmentId, string externalHandle)
    {
        var env = Find(environmentId);
        if (env.State == IsolatedEnvironmentState.Destroyed)
            throw new ArgumentException($"Isolated environment '{environmentId}' was already destroyed.");
        if (string.IsNullOrWhiteSpace(externalHandle))
            throw new ArgumentException("external_handle is required.");
        env.ExternalHandle = externalHandle;
        env.State = IsolatedEnvironmentState.Attached;
        return env;
    }

    /// <summary>F36-T02: acquiring a lease is exclusive and time-bounded -- a different actor is
    /// refused while a valid lease is held, mirroring ReviewRoomStore's (F60) lease shape.</summary>
    public (bool Acquired, string? Reason) AcquireLease(string environmentId, string actorId, TimeSpan duration)
    {
        var env = Find(environmentId);
        if (env.State == IsolatedEnvironmentState.Destroyed)
            return (false, "environment_destroyed");
        var now = DateTimeOffset.UtcNow;
        if (env.LeaseHolderActorId is { } holder && holder != actorId && env.LeaseExpiresAt is { } expiry && expiry > now)
            return (false, "lease_held_by_another_actor");
        env.LeaseHolderActorId = actorId;
        env.LeaseExpiresAt = now + duration;
        env.State = IsolatedEnvironmentState.Active;
        return (true, null);
    }

    /// <summary>F36-T02: an expired sandbox lease blocks further commands -- the check is a pure
    /// timestamp comparison, never inferred from any other state.</summary>
    public bool IsLeaseValid(string environmentId, string actorId)
    {
        var env = Find(environmentId);
        return env.LeaseHolderActorId == actorId && env.LeaseExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow;
    }

    /// <summary>Reset clears session/lease state but keeps the environment record -- bookkeeping
    /// only, never a real reset of anything external.</summary>
    public IsolatedEnvironment Reset(string environmentId)
    {
        var env = Find(environmentId);
        if (env.State == IsolatedEnvironmentState.Destroyed)
            throw new ArgumentException($"Isolated environment '{environmentId}' was already destroyed.");
        env.LeaseHolderActorId = null;
        env.LeaseExpiresAt = null;
        env.State = IsolatedEnvironmentState.Attached;
        return env;
    }

    /// <summary>F36-T02: teardown records exactly the caller-declared retained artifact ids and
    /// touches nothing else -- this store has no code path that deletes any file or resource, so
    /// "does not delete unrelated resources" holds structurally, not just by convention.</summary>
    public IsolatedEnvironment Destroy(string environmentId, IReadOnlyList<string> retainedArtifactIds)
    {
        var env = Find(environmentId);
        env.State = IsolatedEnvironmentState.Destroyed;
        env.RetainedArtifactIds = retainedArtifactIds;
        env.LeaseHolderActorId = null;
        env.LeaseExpiresAt = null;
        return env;
    }
}
