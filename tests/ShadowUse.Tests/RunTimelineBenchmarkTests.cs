using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F53: pure unit coverage of RunTimelineBenchmark.Summarize -- no live tools, no file I/O.</summary>
public sealed class RunTimelineBenchmarkTests
{
    private static RunTimelineEvent Ev(string surface, string action, bool ok, long? total, long? decision = null, long? dispatch = null, long? settle = null)
        => new(surface, "target", action, "e1", ok, null, total, DateTimeOffset.UtcNow, decision, dispatch, settle);

    [Fact]
    public void Summarize_GroupsBySurfaceActionAndJevDriven()
    {
        var events = new[]
        {
            Ev("native", "click", true, 100, dispatch: 40, settle: 55),
            Ev("native", "click", true, 120, dispatch: 45, settle: 60),
            Ev("native", "click", true, 300, decision: 200, dispatch: 40, settle: 55),
        };

        var groups = RunTimelineBenchmark.Summarize(events);

        Assert.Equal(2, groups.Count);
        var plain = Assert.Single(groups, g => !g.JevDriven);
        Assert.Equal(2, plain.Count);
        Assert.Equal(110, plain.AvgTotalMs);
        var jev = Assert.Single(groups, g => g.JevDriven);
        Assert.Equal(1, jev.Count);
        Assert.Equal(200, jev.AvgDecisionMs);
    }

    [Fact]
    public void Summarize_IdentifiesTheDominantPhase()
    {
        // Settle clearly dominates dispatch across the group.
        var events = new[]
        {
            Ev("native", "click", true, 200, dispatch: 10, settle: 180),
            Ev("native", "click", true, 220, dispatch: 15, settle: 190),
        };

        var group = Assert.Single(RunTimelineBenchmark.Summarize(events));

        Assert.Equal("settle", group.DominantPhase);
    }

    [Fact]
    public void Summarize_NoPhaseBreakdown_ReportsUnattributed()
    {
        var events = new[] { Ev("native", "press_key", true, 50) };
        // Older-style entry with no DecisionMs/DispatchMs/SettleMs at all.
        var withoutBreakdown = new[] { events[0] with { DispatchMs = null, SettleMs = null } };

        var group = Assert.Single(RunTimelineBenchmark.Summarize(withoutBreakdown));

        Assert.Equal("unattributed", group.DominantPhase);
        Assert.Null(group.AvgDispatchMs);
        Assert.Null(group.AvgSettleMs);
    }

    [Fact]
    public void Summarize_CountsFailuresSeparatelyFromOkCount()
    {
        var events = new[]
        {
            Ev("browser", "browser_click", true, 100, dispatch: 60, settle: 30),
            Ev("browser", "browser_click", false, 50, dispatch: 50),
        };

        var group = Assert.Single(RunTimelineBenchmark.Summarize(events));

        Assert.Equal(2, group.Count);
        Assert.Equal(1, group.OkCount);
    }

    [Fact]
    public void Summarize_EmptyInput_ReturnsNoGroups()
    {
        Assert.Empty(RunTimelineBenchmark.Summarize([]));
    }
}
