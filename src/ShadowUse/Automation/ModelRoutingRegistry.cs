// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum ModelRole { Planner, TextGenerator, Judge, Perception }

public sealed record ProviderCapabilities(bool SupportsStructuredOutput, IReadOnlyList<string> Modalities, string DataDestination);

/// <summary>F27-T02: a compatibility adapter must be attached explicitly, per provider, and
/// declares exactly ONE capability gap it bridges ("structured_output" or "modality:&lt;name&gt;").
/// It is never auto-applied and never bridges a gap it did not declare.</summary>
public sealed record CompatibilityAdapter(string BridgesCapability, string Description);

public sealed class ProviderRegistration
{
    public required string ProviderId { get; init; }
    public required ModelRole Role { get; init; }
    public required int Priority { get; init; }
    public required ProviderCapabilities Capabilities { get; init; }
    public required string PinnedModelId { get; init; }
    public string? ResolvedAlias { get; set; }
    public CompatibilityAdapter? Adapter { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public string? FailureReason { get; set; }
}

public sealed record RequiredCapabilities(bool RequireStructuredOutput, IReadOnlyList<string> RequiredModalities, bool LocalOnly);

public sealed record ProviderSelection(bool Found, string? ProviderId, string? PinnedModelId, bool AdapterUsed, string? Reason);

/// <summary>F27-R01/R02: a capability-aware routing boundary across independently configured
/// planner/text-generator/judge/perception roles. Deliberately makes no network/provider calls of
/// its own -- this environment has no live model API key (same documented constraint as F46/F47/
/// F55), so the genuinely new, testable content is selection/failover/compatibility logic, not an
/// actual provider integration. A caller (or a future live adapter) is responsible for the real
/// request; this registry only ever answers "which configured provider, if any, may be used".</summary>
public sealed class ModelRoutingRegistry
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<(ModelRole Role, string ProviderId), ProviderRegistration> _providers = new();

    public ProviderRegistration Configure(ModelRole role, string providerId, int priority, ProviderCapabilities capabilities, string pinnedModelId, string? resolvedAlias)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("provider_id is required.");
        if (string.IsNullOrWhiteSpace(pinnedModelId))
            throw new ArgumentException("pinned_model_id is required -- F27-R02 requires a pinned identifier, not a floating alias.");
        if (capabilities.DataDestination is not ("local" or "cloud"))
            throw new ArgumentException("data_destination must be 'local' or 'cloud'.");
        var registration = new ProviderRegistration
        {
            ProviderId = providerId, Role = role, Priority = priority,
            Capabilities = capabilities, PinnedModelId = pinnedModelId, ResolvedAlias = resolvedAlias,
        };
        _providers[(role, providerId)] = registration;
        return registration;
    }

    public ProviderRegistration Find(ModelRole role, string providerId) =>
        _providers.GetValueOrDefault((role, providerId)) ?? throw new ArgumentException($"Provider '{providerId}' for role {role} was not configured.");

    public void AttachCompatibilityAdapter(ModelRole role, string providerId, CompatibilityAdapter adapter) =>
        Find(role, providerId).Adapter = adapter;

    public void RecordFailure(ModelRole role, string providerId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("record_provider_failure requires a non-empty reason.");
        var registration = Find(role, providerId);
        registration.FailedAt = DateTimeOffset.UtcNow;
        registration.FailureReason = reason;
    }

    /// <summary>F27-R01/T02: returns the first-priority provider that genuinely satisfies every
    /// required capability -- an explicitly attached CompatibilityAdapter may bridge exactly the
    /// one gap it declares, never a silent substitution for anything else. F27-R02/T01: local_only
    /// refuses any cloud-destination candidate outright, never as a fallback of last resort; a
    /// provider still inside its failure cooldown is skipped so failover genuinely reaches the
    /// next configured candidate.</summary>
    public ProviderSelection Select(ModelRole role, RequiredCapabilities required)
    {
        var candidates = _providers.Values.Where(p => p.Role == role).OrderBy(p => p.Priority).ToArray();
        if (candidates.Length == 0)
            return new ProviderSelection(false, null, null, false, "no_provider_configured");

        foreach (var candidate in candidates)
        {
            if (candidate.FailedAt is { } failedAt && DateTimeOffset.UtcNow - failedAt < FailureCooldown)
                continue;
            if (required.LocalOnly && candidate.Capabilities.DataDestination != "local")
                continue;
            var (satisfied, adapterUsed) = Satisfies(candidate, required);
            if (satisfied)
                return new ProviderSelection(true, candidate.ProviderId, candidate.PinnedModelId, adapterUsed, null);
        }
        return new ProviderSelection(false, null, null, false, "no_compatible_provider");
    }

    private static (bool Satisfied, bool AdapterUsed) Satisfies(ProviderRegistration candidate, RequiredCapabilities required)
    {
        var adapterUsed = false;
        if (required.RequireStructuredOutput && !candidate.Capabilities.SupportsStructuredOutput)
        {
            if (candidate.Adapter?.BridgesCapability != "structured_output")
                return (false, false);
            adapterUsed = true;
        }
        foreach (var modality in required.RequiredModalities)
        {
            if (candidate.Capabilities.Modalities.Contains(modality, StringComparer.OrdinalIgnoreCase))
                continue;
            if (candidate.Adapter?.BridgesCapability != $"modality:{modality}")
                return (false, false);
            adapterUsed = true;
        }
        return (true, adapterUsed);
    }
}
