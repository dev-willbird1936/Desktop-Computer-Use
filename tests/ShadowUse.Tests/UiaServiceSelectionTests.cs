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

    [Fact]
    public void SelectApp_RejectsPidThatOwnsMultipleWindows()
    {
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)1, Title = "First" },
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)2, Title = "Second" },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, "100"]));

        var ambiguity = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("window", ambiguity.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectApp_AcceptsPidAndWindowHandleSelector()
    {
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)1, Title = "First" },
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)2, Title = "Second" },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var selected = Assert.IsType<AppTarget>(
            selectApp.Invoke(null, [candidates, "100:0x2"]));

        Assert.Equal((IntPtr)2, selected.Hwnd);
    }

    [Fact]
    public void SelectApp_ExplicitWindowDoesNotFallThroughToFuzzyMatching()
    {
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "same", Hwnd = (IntPtr)1, Title = "100:0x99" },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, "100:0x99"]));

        Assert.Contains("not found", Assert.IsType<InvalidOperationException>(error.InnerException).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectApp_UsesPrefixedTargetIdWithoutTreatingBareHexTitleAsTargetId()
    {
        const string bareHexTitle = "0123456789abcdef0123456789abcdef";
        var registry = new TargetRegistry("selection-target-id-test");
        var reference = registry.Register(new NativeTargetIdentity(100, 1234, (IntPtr)1));
        var target = new AppTarget
        {
            Pid = 100,
            ProcessName = "fixture",
            Hwnd = (IntPtr)1,
            Title = "Window",
            TargetReference = reference,
        };
        var hexTitle = new AppTarget
        {
            Pid = 200,
            ProcessName = "hex-title",
            Hwnd = (IntPtr)2,
            Title = bareHexTitle,
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        Assert.StartsWith(UiaService.TargetIdPrefix, target.TargetId, StringComparison.Ordinal);
        var selectedByTargetId = Assert.IsType<AppTarget>(
            selectApp.Invoke(null, [new[] { target }, target.TargetId]));
        var selectedByHexTitle = Assert.IsType<AppTarget>(
            selectApp.Invoke(null, [new[] { hexTitle }, bareHexTitle]));

        Assert.Same(target, selectedByTargetId);
        Assert.Same(hexTitle, selectedByHexTitle);
    }

    [Fact]
    public void SelectApp_StalePrefixedTargetDoesNotFallThroughToFuzzyMatching()
    {
        const string targetId = "dcu-target-0123456789abcdef0123456789abcdef";
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "fixture", Hwnd = (IntPtr)1, Title = targetId },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, targetId]));

        Assert.Contains("stale", Assert.IsType<InvalidOperationException>(error.InnerException).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectApp_MalformedPrefixedTargetFailsClosed()
    {
        const string malformedTargetId = "dcu-target-not-a-target-id";
        var candidates = new[]
        {
            new AppTarget { Pid = 100, ProcessName = "fixture", Hwnd = (IntPtr)1, Title = malformedTargetId },
        };
        var selectApp = typeof(UiaService).GetMethod(
            "SelectApp",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(selectApp);
        var error = Assert.Throws<TargetInvocationException>(
            () => selectApp.Invoke(null, [candidates, malformedTargetId]));

        var failure = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("malformed", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
