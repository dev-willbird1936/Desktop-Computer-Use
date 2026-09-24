// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>F36-R01/T02 pure lifecycle/lease coverage. No sandbox/VM infrastructure exists in
/// this environment -- this only proves the create/attach/reset/destroy bookkeeping and
/// lease-expiry enforcement around an externally-supplied environment handle. F36-T01's actual
/// host-isolation claim is NOT covered here or anywhere in this codebase; it requires a real
/// sandbox to run inside, which does not exist, and remains the honest documented gap.</summary>
public sealed class IsolatedEnvironmentRegistryTests
{
    [Fact]
    public void CreateThenAttachTransitionsState()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        var env = registry.Attach("env1", "vm-connection-descriptor");
        Assert.Equal(IsolatedEnvironmentState.Attached, env.State);
        Assert.Equal("vm-connection-descriptor", env.ExternalHandle);
    }

    [Fact]
    public void CreatingTheSameEnvironmentIdTwiceThrows()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        Assert.Throws<ArgumentException>(() => registry.Create("env1", "external_vm"));
    }

    [Fact]
    public void LeaseIsAcquiredWhenUnheld()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        var (acquired, reason) = registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        Assert.True(acquired);
        Assert.Null(reason);
        Assert.True(registry.IsLeaseValid("env1", "alice"));
    }

    [Fact]
    public void LeaseRefusesADifferentActorWhileStillValid()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        var (acquired, reason) = registry.AcquireLease("env1", "bob", TimeSpan.FromSeconds(30));
        Assert.False(acquired);
        Assert.Equal("lease_held_by_another_actor", reason);
    }

    [Fact]
    public void LeaseIsReentrantForTheSameActor()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        var (acquired, _) = registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        Assert.True(acquired);
    }

    [Fact]
    public void ExpiredLeaseIsInvalidAndBlocksFurtherCommands()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromMilliseconds(-1)); // already expired
        Assert.False(registry.IsLeaseValid("env1", "alice"));
    }

    [Fact]
    public void ExpiredLeaseCanBeAcquiredByAnotherActor()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromMilliseconds(-1)); // already expired
        var (acquired, reason) = registry.AcquireLease("env1", "bob", TimeSpan.FromSeconds(30));
        Assert.True(acquired);
        Assert.Null(reason);
    }

    [Fact]
    public void ResetClearsLeaseButKeepsTheEnvironmentRecord()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        var env = registry.Reset("env1");
        Assert.Equal(IsolatedEnvironmentState.Attached, env.State);
        Assert.False(registry.IsLeaseValid("env1", "alice"));
        Assert.NotNull(registry.Find("env1"));
    }

    [Fact]
    public void DestroyRecordsExactlyTheDeclaredRetainedArtifacts()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        var env = registry.Destroy("env1", ["screenshot-1", "log-1"]);
        Assert.Equal(IsolatedEnvironmentState.Destroyed, env.State);
        Assert.Equal(["screenshot-1", "log-1"], env.RetainedArtifactIds);
    }

    [Fact]
    public void DestroyedEnvironmentRefusesFurtherAttach()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.Destroy("env1", []);
        Assert.Throws<ArgumentException>(() => registry.Attach("env1", "handle"));
    }

    [Fact]
    public void DestroyedEnvironmentLeaseIsAlwaysInvalid()
    {
        var registry = new IsolatedEnvironmentRegistry();
        registry.Create("env1", "external_vm");
        registry.AcquireLease("env1", "alice", TimeSpan.FromSeconds(30));
        registry.Destroy("env1", []);
        Assert.False(registry.IsLeaseValid("env1", "alice"));
    }

    [Fact]
    public void FindOnAnUncreatedEnvironmentThrows()
    {
        var registry = new IsolatedEnvironmentRegistry();
        Assert.Throws<ArgumentException>(() => registry.Find("never-created"));
    }
}
