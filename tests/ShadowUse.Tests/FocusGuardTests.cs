using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class FocusGuardTests
{
    [Fact]
    public void ShouldRestoreFocus_DoesNotOverrideUserSwitchToAnotherProcess()
    {
        var focusGuardType = typeof(UiaService).Assembly.GetType("ShadowUse.Safety.FocusGuard");
        Assert.NotNull(focusGuardType);
        var method = focusGuardType.GetMethod(
            "ShouldRestoreFocus",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var shouldRestore = Assert.IsType<bool>(
            method.Invoke(null, [(IntPtr)10, (IntPtr)20, (uint)200, (uint)300]));

        Assert.False(shouldRestore);
    }

    [Fact]
    public void ShouldRestoreFocus_RestoresWhenTargetProcessTookForeground()
    {
        var focusGuardType = typeof(UiaService).Assembly.GetType("ShadowUse.Safety.FocusGuard");
        Assert.NotNull(focusGuardType);
        var method = focusGuardType.GetMethod(
            "ShouldRestoreFocus",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var shouldRestore = Assert.IsType<bool>(
            method.Invoke(null, [(IntPtr)10, (IntPtr)20, (uint)200, (uint)200]));

        Assert.True(shouldRestore);
    }
}
