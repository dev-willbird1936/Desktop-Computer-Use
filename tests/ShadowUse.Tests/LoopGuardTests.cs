// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F28-R02 coverage: repeated no-progress action detection, independent of any adapter.</summary>
public sealed class LoopGuardTests
{
    [Fact]
    public void Progress_NeverFlagsShouldStop()
    {
        var guard = new LoopGuard(threshold: 3);
        for (var i = 0; i < 10; i++)
        {
            var verdict = guard.Record("scope", "click", progressed: true);
            Assert.Equal(0, verdict.NoProgressCount);
            Assert.False(verdict.ShouldStop);
            Assert.Null(verdict.Diagnosis);
        }
    }

    [Fact]
    public void RepeatedNoProgress_FlagsShouldStopAtThreshold()
    {
        var guard = new LoopGuard(threshold: 3);

        var first = guard.Record("scope", "click", progressed: false);
        Assert.Equal(1, first.NoProgressCount);
        Assert.False(first.ShouldStop);

        var second = guard.Record("scope", "click", progressed: false);
        Assert.Equal(2, second.NoProgressCount);
        Assert.False(second.ShouldStop);

        var third = guard.Record("scope", "click", progressed: false);
        Assert.Equal(3, third.NoProgressCount);
        Assert.True(third.ShouldStop);
        Assert.NotNull(third.Diagnosis);
    }

    [Fact]
    public void ProgressAfterNoProgress_ResetsTheCounter()
    {
        var guard = new LoopGuard(threshold: 3);
        guard.Record("scope", "click", progressed: false);
        guard.Record("scope", "click", progressed: false);

        var recovered = guard.Record("scope", "click", progressed: true);
        Assert.Equal(0, recovered.NoProgressCount);

        var afterRecovery = guard.Record("scope", "click", progressed: false);
        Assert.Equal(1, afterRecovery.NoProgressCount);
    }

    [Fact]
    public void DifferentScopes_AreTrackedIndependently()
    {
        var guard = new LoopGuard(threshold: 2);
        guard.Record("element-a", "click", progressed: false);
        var second = guard.Record("element-b", "click", progressed: false);

        Assert.Equal(1, second.NoProgressCount);
    }

    [Fact]
    public void DifferentActionFingerprint_WithinTheSameScope_ResetsTheCounter()
    {
        var guard = new LoopGuard(threshold: 3);
        guard.Record("scope", "click", progressed: false);
        guard.Record("scope", "click", progressed: false);

        // A different action (e.g. a different value being set) on the same target is not
        // the same repeated cycle -- it must not inherit the prior fingerprint's count.
        var differentAction = guard.Record("scope", "set_value:abc123", progressed: false);
        Assert.Equal(1, differentAction.NoProgressCount);
    }

    [Fact]
    public void Reset_ClearsTrackingForAScope()
    {
        var guard = new LoopGuard(threshold: 2);
        guard.Record("scope", "click", progressed: false);

        guard.Reset("scope");

        var afterReset = guard.Record("scope", "click", progressed: false);
        Assert.Equal(1, afterReset.NoProgressCount);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopGuard(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopGuard(-1));
    }
}
