using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class TextTargetIsolationTests
{
    [Fact]
    public void CanUseFocusedElement_RejectsElementFromAnotherProcess()
    {
        var method = typeof(BackgroundInput).GetMethod(
            "CanUseFocusedElement",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var accepted = Assert.IsType<bool>(method.Invoke(null, [100, 200]));

        Assert.False(accepted);
    }

    [Fact]
    public void CanUseFocusedElement_AllowsElementFromTargetProcess()
    {
        var method = typeof(BackgroundInput).GetMethod(
            "CanUseFocusedElement",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var accepted = Assert.IsType<bool>(method.Invoke(null, [100, 100]));

        Assert.True(accepted);
    }
}
