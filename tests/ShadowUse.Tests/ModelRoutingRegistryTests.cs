// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F27-R01/R02 coverage: pure selection/failover/compatibility logic, no network calls
/// anywhere (this environment has no live model API key). F27-T01/T02 map directly to
/// RecordFailure-driven failover and adapter-gated capability substitution below.</summary>
public sealed class ModelRoutingRegistryTests
{
    private static ProviderCapabilities Caps(bool structured = true, string[]? modalities = null, string destination = "local")
        => new(structured, modalities ?? ["text"], destination);

    [Fact]
    public void Configure_Rejects_EmptyProviderId()
    {
        var registry = new ModelRoutingRegistry();
        Assert.Throws<ArgumentException>(() => registry.Configure(ModelRole.Planner, "", 0, Caps(), "model-v1", null));
    }

    [Fact]
    public void Configure_Rejects_MissingPinnedModelId()
    {
        // F27-R02: a floating alias alone is not acceptable.
        var registry = new ModelRoutingRegistry();
        Assert.Throws<ArgumentException>(() => registry.Configure(ModelRole.Planner, "p1", 0, Caps(), "", null));
    }

    [Fact]
    public void Configure_Rejects_InvalidDataDestination()
    {
        var registry = new ModelRoutingRegistry();
        Assert.Throws<ArgumentException>(() => registry.Configure(ModelRole.Planner, "p1", 0, Caps(destination: "somewhere"), "model-v1", null));
    }

    [Fact]
    public void Select_ReturnsNotFound_WhenNothingConfiguredForThatRole()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Planner, "p1", 0, Caps(), "model-v1", null);

        var selection = registry.Select(ModelRole.Judge, new RequiredCapabilities(false, [], false));

        Assert.False(selection.Found);
        Assert.Equal("no_provider_configured", selection.Reason);
    }

    [Fact]
    public void Select_PicksLowestPriority_WhenBothSatisfyRequirements()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Planner, "secondary", 5, Caps(), "model-v1", null);
        registry.Configure(ModelRole.Planner, "primary", 0, Caps(), "model-v1", null);

        var selection = registry.Select(ModelRole.Planner, new RequiredCapabilities(false, [], false));

        Assert.True(selection.Found);
        Assert.Equal("primary", selection.ProviderId);
    }

    [Fact]
    public void Select_Refuses_MissingRequiredCapability_WithoutAnAdapter()
    {
        // F27-T02: a provider missing a required typed primitive is never substituted silently.
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Judge, "p1", 0, Caps(structured: false), "model-v1", null);

        var selection = registry.Select(ModelRole.Judge, new RequiredCapabilities(true, [], false));

        Assert.False(selection.Found);
        Assert.Equal("no_compatible_provider", selection.Reason);
    }

    [Fact]
    public void Select_AcceptsMissingCapability_OnlyThroughAnExplicitlyAttachedAdapter()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Judge, "p1", 0, Caps(structured: false), "model-v1", null);
        registry.AttachCompatibilityAdapter(ModelRole.Judge, "p1", new CompatibilityAdapter("structured_output", "wraps free text in a deterministic parser"));

        var selection = registry.Select(ModelRole.Judge, new RequiredCapabilities(true, [], false));

        Assert.True(selection.Found);
        Assert.True(selection.AdapterUsed);
    }

    [Fact]
    public void Select_AdapterForOneGap_DoesNotBridgeADifferentGap()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Judge, "p1", 0, Caps(structured: false, modalities: ["text"]), "model-v1", null);
        registry.AttachCompatibilityAdapter(ModelRole.Judge, "p1", new CompatibilityAdapter("modality:image", "unrelated bridge"));

        var selection = registry.Select(ModelRole.Judge, new RequiredCapabilities(true, [], false));

        Assert.False(selection.Found);
    }

    [Fact]
    public void Select_LocalOnly_NeverPicksACloudProvider_EvenAsLastResort()
    {
        // F27-R02: local-only runs must refuse a cloud fallback outright.
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.TextGenerator, "cloud-only", 0, Caps(destination: "cloud"), "model-v1", null);

        var selection = registry.Select(ModelRole.TextGenerator, new RequiredCapabilities(false, [], LocalOnly: true));

        Assert.False(selection.Found);
        Assert.Equal("no_compatible_provider", selection.Reason);
    }

    [Fact]
    public void RecordFailure_CausesFailoverToTheNextConfiguredCandidate()
    {
        // F27-T01: simulated timeout/rate-limit -> the configured fallback is used instead.
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Planner, "primary", 0, Caps(), "model-v1", null);
        registry.Configure(ModelRole.Planner, "fallback", 1, Caps(), "model-v2", null);

        registry.RecordFailure(ModelRole.Planner, "primary", "timeout");
        var selection = registry.Select(ModelRole.Planner, new RequiredCapabilities(false, [], false));

        Assert.True(selection.Found);
        Assert.Equal("fallback", selection.ProviderId);
    }

    [Fact]
    public void RecordFailure_Rejects_EmptyReason()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Planner, "p1", 0, Caps(), "model-v1", null);
        Assert.Throws<ArgumentException>(() => registry.RecordFailure(ModelRole.Planner, "p1", ""));
    }

    [Fact]
    public void RecordFailure_OnUnconfiguredProvider_Throws()
    {
        var registry = new ModelRoutingRegistry();
        Assert.Throws<ArgumentException>(() => registry.RecordFailure(ModelRole.Planner, "missing", "timeout"));
    }

    [Fact]
    public void AttachCompatibilityAdapter_OnUnconfiguredProvider_Throws()
    {
        var registry = new ModelRoutingRegistry();
        Assert.Throws<ArgumentException>(() => registry.AttachCompatibilityAdapter(ModelRole.Planner, "missing", new CompatibilityAdapter("structured_output", "x")));
    }

    [Fact]
    public void Select_RequiredModality_MustBeSatisfiedOrAdapted()
    {
        var registry = new ModelRoutingRegistry();
        registry.Configure(ModelRole.Perception, "text-only", 0, Caps(modalities: ["text"]), "model-v1", null);

        var selection = registry.Select(ModelRole.Perception, new RequiredCapabilities(false, ["image"], false));

        Assert.False(selection.Found);
    }
}
