using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class UiaServiceSelectionTests
{
    [Fact]
    public void SelectApp_RejectsAmbiguousExactProcessNames()
    {
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)1, Title = "First" },
            new AppTarget { Pid = 200, ProcessName = "same", Hwnd = (IntPtr)2, Title = "Second" },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, "same"]));

        var ambiguity = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("ambiguous", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PID", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectApp_RejectsAmbiguousTitleSubstrings()
    {
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "alpha", Hwnd = (IntPtr)1, Title = "Report - January" },
            new AppTarget { Pid = 200, ProcessName = "beta", Hwnd = (IntPtr)2, Title = "Report - February" },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, "Report"]));

        var ambiguity = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("ambiguous", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PID 100", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PID 200", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
    }
}
