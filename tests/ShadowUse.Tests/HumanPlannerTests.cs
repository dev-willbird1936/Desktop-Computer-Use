using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class HumanPlannerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PlanPath_StartsAtSourceEndsOnTargetWithMonotonicTime(int seed)
    {
        var plan = HumanPlanner.PlanPath((100, 100), (900, 500), 40, 1, new Random(seed));

        Assert.Equal(new PathPoint(100, 100, 0), plan.Points[0]);
        Assert.Equal((900, 500), (plan.Points[^1].X, plan.Points[^1].Y));
        for (int i = 1; i < plan.Points.Count; i++)
            Assert.True(plan.Points[i].TMs >= plan.Points[i - 1].TMs);
        Assert.True(plan.Points.Count > 10);
        Assert.InRange(plan.DurationMs, 200, 3_000);
    }

    [Fact]
    public void PlanPath_ZeroDistanceIsInstant()
    {
        var plan = HumanPlanner.PlanPath((50, 50), (50, 50), 20, 1, new Random(1));
        Assert.Single(plan.Points);
        Assert.Equal(0, plan.DurationMs);
    }

    [Fact]
    public void PlanPath_HigherSpeedIsFaster()
    {
        var slow = HumanPlanner.PlanPath((0, 0), (1000, 0), 30, 0.5, new Random(7));
        var fast = HumanPlanner.PlanPath((0, 0), (1000, 0), 30, 3, new Random(7));
        Assert.True(fast.DurationMs < slow.DurationMs);
    }

    [Fact]
    public void PickPoint_StaysInsideBoxAndAvoidsEdges()
    {
        var rng = new Random(4);
        for (int i = 0; i < 200; i++)
        {
            var (x, y) = HumanPlanner.PickPoint(200, 300, 100, 40, (0, 0), rng);
            Assert.InRange(x, 200 + 3, 300 - 3);
            Assert.InRange(y, 300 + 3, 340 - 3);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void PlanTyping_ReplayReproducesText(double typoRate)
    {
        const string text = "Hello, World! The quick brown fox: 42 jumps over (the) lazy dog.\tTabbed\nNew line é 😀";
        for (int seed = 0; seed < 25; seed++)
            Assert.Equal(text, HumanPlanner.Replay(HumanPlanner.PlanTyping(text, 1, typoRate, new Random(seed))));
    }

    [Fact]
    public void PlanTyping_NoTyposWhenRateIsZero()
    {
        var steps = HumanPlanner.PlanTyping("some ordinary sentence with letters", 1, 0, new Random(9));
        Assert.DoesNotContain(steps, s => s.Glyph == "\b");
    }

    [Fact]
    public void PlanTyping_TyposEmitBackspaceAndNeverStartAWord()
    {
        var steps = HumanPlanner.PlanTyping("abcdefgh ijklmnop qrstuvwx", 1, 1, new Random(3));
        Assert.Contains(steps, s => s.Glyph == "\b");
        Assert.NotEqual("\b", steps[1].Glyph);
        Assert.Equal("a", steps[0].Glyph);
    }

    [Fact]
    public void PlanTyping_TimingsAreHumanScale()
    {
        var steps = HumanPlanner.PlanTyping(new string('a', 200), 1, 0, new Random(5));
        double meanMs = steps.Average(s => s.HoldMs + s.DelayAfterMs);
        Assert.InRange(meanMs, 90, 220);
        Assert.All(steps, s => Assert.True(s.HoldMs >= 12 && s.DelayAfterMs >= 12));
    }

    [Fact]
    public void PlanPath_SamplesLikeAHighRateMouse()
    {
        var plan = HumanPlanner.PlanPath((0, 0), (1200, 300), 40, 1, new Random(11));
        var gaps = plan.Points.Zip(plan.Points.Skip(1), (a, b) => b.TMs - a.TMs).Where(g => g > 0).OrderBy(g => g).ToList();
        Assert.InRange(gaps[gaps.Count / 2], 1, 6);
        Assert.True(plan.Points.Count > 100);
    }

    [Fact]
    public void PlanTyping_KeyHoldsSpanAtLeastAFrame()
    {
        var steps = HumanPlanner.PlanTyping("hold times matter in games", 3, 0.2, new Random(6));
        Assert.All(steps, s => Assert.True(s.HoldMs >= 45));
    }

    [Fact]
    public void KeyHoldMs_StaysInHumanRange()
    {
        var rng = new Random(2);
        for (int i = 0; i < 500; i++) Assert.InRange(HumanPlanner.KeyHoldMs(rng), 45, 220);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PlanTremor_StaysWithinTwoPixelsAndReturnsToStart(int seed)
    {
        var nudges = HumanPlanner.PlanTremor(2_000, new Random(seed));
        int ox = 0, oy = 0, last = 0;
        foreach (var (t, dx, dy) in nudges)
        {
            Assert.True(t >= last && t <= 2_000);
            last = t;
            Assert.True(Math.Abs(dx) <= 2 && Math.Abs(dy) <= 2);
            ox += dx; oy += dy;
            Assert.InRange(ox, -2, 2);
            Assert.InRange(oy, -2, 2);
        }
        Assert.Equal((0, 0), (ox, oy));
    }

    [Fact]
    public void PlanWheelDelays_OnePerNotch()
    {
        Assert.Equal(5, HumanPlanner.PlanWheelDelays(5, new Random(1)).Count);
    }

    [Fact]
    public void Settings_HumanizedNormalizesOutOfRangeValues()
    {
        var settings = new ShadowSettings { HumanizedMouseSpeed = 99, HumanizedTypingSpeed = double.NaN, HumanizedTypoRate = -1 };
        typeof(ShadowSettings).GetMethod("Normalize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);

        Assert.Equal(4, settings.HumanizedMouseSpeed);
        Assert.Equal(1, settings.HumanizedTypingSpeed);
        Assert.Equal(0, settings.HumanizedTypoRate);
        Assert.False(new ShadowSettings().HumanizedInput);
    }

    [Fact]
    public void CheckBackgroundMode_AcceptsHumanizedOnlyWhereInteractiveSupported()
    {
        var method = typeof(ComputerUseTools).GetMethod("CheckBackgroundMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Null(method.Invoke(null, ["humanized", true]));
        Assert.NotNull(method.Invoke(null, ["humanized", false]));
    }
}
