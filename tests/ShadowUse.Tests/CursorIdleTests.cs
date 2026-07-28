using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class CursorIdleTests
{
    [Fact]
    public void CursorIdleState_HidesAtOneMinuteOfInactivity()
    {
        var stateType = typeof(UiaService).Assembly.GetType("ShadowUse.Overlay.CursorIdleState");
        Assert.NotNull(stateType);
        var state = Activator.CreateInstance(stateType, TimeSpan.FromMinutes(1));
        Assert.NotNull(state);
        var record = stateType.GetMethod("RecordActivity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var shouldHide = stateType.GetMethod("ShouldHide", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(record);
        Assert.NotNull(shouldHide);
        var start = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        record.Invoke(state, [start]);

        Assert.False(Assert.IsType<bool>(shouldHide.Invoke(state, [start.AddMilliseconds(59_999)])));
        Assert.True(Assert.IsType<bool>(shouldHide.Invoke(state, [start.AddMinutes(1)])));
    }

    [Fact]
    public void CursorIdleState_NewActivityRestartsDeadline()
    {
        var stateType = typeof(UiaService).Assembly.GetType("ShadowUse.Overlay.CursorIdleState");
        Assert.NotNull(stateType);
        var state = Activator.CreateInstance(stateType, TimeSpan.FromMinutes(1));
        Assert.NotNull(state);
        var record = stateType.GetMethod("RecordActivity", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var shouldHide = stateType.GetMethod("ShouldHide", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(record);
        Assert.NotNull(shouldHide);
        var start = new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

        record.Invoke(state, [start]);
        record.Invoke(state, [start.AddSeconds(50)]);

        Assert.False(Assert.IsType<bool>(shouldHide.Invoke(state, [start.AddSeconds(70)])));
        Assert.True(Assert.IsType<bool>(shouldHide.Invoke(state, [start.AddSeconds(110)])));
    }
}
