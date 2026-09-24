using System.Collections.Concurrent;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class TargetRegistryTests
{
    [Fact]
    public void TargetRegistry_SeparatesSessions()
    {
        var identity = Identity(101, 7, 0x1001);
        var first = new TargetRegistry("session-a");
        var second = new TargetRegistry("session-b");

        var firstTarget = first.Register(identity);
        var secondTarget = second.Register(identity);

        Assert.NotEqual(firstTarget.TargetId, secondTarget.TargetId);
        Assert.Equal("session-a", firstTarget.SessionId);
        Assert.Equal("session-b", secondTarget.SessionId);
        Assert.True(first.IsCurrent(firstTarget, identity));
        Assert.True(second.IsCurrent(secondTarget, identity));
        Assert.False(first.IsCurrent(secondTarget, identity));
        Assert.False(second.IsCurrent(firstTarget, identity));
    }

    [Fact]
    public void TargetRegistry_ChangedProcessBirthRetiresSameHwnd()
    {
        var registry = new TargetRegistry("session");
        var original = registry.Register(Identity(101, 7, 0x1001));
        var replacementIdentity = Identity(101, 8, 0x1001);

        var replacement = registry.Register(replacementIdentity);

        Assert.NotEqual(original.TargetId, replacement.TargetId);
        Assert.True(replacement.LifetimeEpoch > original.LifetimeEpoch);
        Assert.False(registry.IsCurrent(original, replacementIdentity));
        Assert.False(registry.TryGet(original.TargetId, out _));
        Assert.True(registry.IsCurrent(replacement, replacementIdentity));
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public void TargetRegistry_ExplicitInvalidationAllowsHwndReuseWithNewEpoch()
    {
        var registry = new TargetRegistry("session");
        var originalIdentity = Identity(101, 7, 0x1001);
        var original = registry.Register(originalIdentity);

        Assert.True(registry.Invalidate(original));
        Assert.False(registry.IsCurrent(original));
        Assert.False(registry.TryGet(original.TargetId, out _));
        Assert.Equal(0, registry.ActiveCount);

        var reused = registry.Register(Identity(202, 9, 0x1001));

        Assert.NotEqual(original.TargetId, reused.TargetId);
        Assert.True(reused.LifetimeEpoch > original.LifetimeEpoch);
        Assert.True(registry.IsCurrent(reused, reused.Identity));
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public void TargetRegistry_StaleIdCannotRetargetReplacement()
    {
        var registry = new TargetRegistry("session");
        var original = registry.Register(Identity(101, 7, 0x1001));
        Assert.True(registry.Invalidate(original));

        var replacement = registry.Register(Identity(202, 9, 0x1001));

        Assert.False(registry.TryGet(original.TargetId, out _));
        Assert.False(registry.IsCurrent(original, replacement.Identity));
        Assert.False(registry.Invalidate(original));
        Assert.False(registry.Invalidate(original.Identity));
        Assert.True(registry.IsCurrent(replacement));
    }

    [Fact]
    public void TargetRegistry_SiblingWindowsRemainIndependent()
    {
        var registry = new TargetRegistry("session");
        var first = registry.Register(Identity(101, 7, 0x1001));
        var sibling = registry.Register(Identity(101, 7, 0x1002));

        Assert.NotEqual(first.TargetId, sibling.TargetId);
        Assert.NotEqual(first.LifetimeEpoch, sibling.LifetimeEpoch);
        Assert.True(registry.Invalidate(first));
        Assert.False(registry.IsCurrent(first));
        Assert.True(registry.IsCurrent(sibling, sibling.Identity));
        Assert.True(registry.TryGet(sibling.TargetId, out var activeSibling));
        Assert.Equal(sibling, activeSibling);
        Assert.Equal(1, registry.ActiveCount);
    }

    [Fact]
    public void TargetRegistry_RejectsUnknownOrInvalidCreationIdentity()
    {
        var registry = new TargetRegistry("session");
        var invalidIdentities = new[]
        {
            new NativeTargetIdentity(0, 7, (IntPtr)0x1001),
            new NativeTargetIdentity(101, 0, (IntPtr)0x1001),
            new NativeTargetIdentity(101, -1, (IntPtr)0x1001),
            new NativeTargetIdentity(101, 7, IntPtr.Zero),
        };

        foreach (var identity in invalidIdentities)
        {
            Assert.False(registry.TryRegister(identity, out var rejected));
            Assert.False(rejected.IsValid);
        }

        Assert.Throws<ArgumentException>(
            () => registry.Register(new NativeTargetIdentity(101, 0, (IntPtr)0x1001)));
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public void TargetRegistry_RemovesRetiredStateAndBoundsActiveState()
    {
        var registry = new TargetRegistry("session", maxActiveTargets: 2);
        var first = registry.Register(Identity(101, 7, 0x1001));
        registry.Register(Identity(101, 7, 0x1002));

        Assert.False(registry.TryRegister(Identity(101, 7, 0x1003), out _));
        Assert.Equal(2, registry.ActiveCount);
        Assert.True(registry.Invalidate(first));
        Assert.Equal(1, registry.ActiveCount);

        Assert.True(registry.TryRegister(Identity(101, 7, 0x1003), out var admitted));
        Assert.True(registry.IsCurrent(admitted));
        Assert.Equal(2, registry.ActiveCount);
    }

    [Fact]
    public void TargetRegistry_ConcurrentRegistrationsRemainConsistent()
    {
        var registry = new TargetRegistry("session", maxActiveTargets: 64);
        var registered = new ConcurrentBag<NativeTargetReference>();

        Parallel.For(1, 33, hwnd =>
        {
            Assert.True(registry.TryRegister(Identity(101, 7, hwnd), out var target));
            registered.Add(target);
        });

        Assert.Equal(32, registered.Count);
        Assert.Equal(32, registry.ActiveCount);
        Assert.Equal(32, registered.Select(target => target.TargetId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(registered, target => Assert.True(registry.IsCurrent(target, target.Identity)));
    }

    private static NativeTargetIdentity Identity(int pid, long processCreationToken, long hwnd)
        => new(pid, processCreationToken, (IntPtr)hwnd);
}
