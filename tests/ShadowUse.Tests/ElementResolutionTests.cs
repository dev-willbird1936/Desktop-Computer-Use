using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class ElementResolutionTests
{
    [Fact]
    public void ResolveRequiredElement_RejectsMissingSnapshot()
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "ResolveRequiredElement",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var error = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [null, "e1"]));

        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void SelectElement_UsesUniqueAutomationIdAndControlTypeAfterRuntimeIdChanges()
    {
        var target = Element([1], automationId: "save", name: "Save");
        var candidates = new[]
        {
            Element([2], automationId: "save", name: "Save now"),
            Element([3], automationId: "other", name: "Save"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Found, selection.Status);
        Assert.Equal(0, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_RejectsDuplicateAutomationIdWithoutNameFallback()
    {
        var target = Element([1], automationId: "delete", name: "Delete");
        var candidates = new[]
        {
            Element([2], automationId: "delete", name: "Delete", controlType: "Button"),
            Element([3], automationId: "delete", name: "Other", controlType: "Button"),
            Element([4], automationId: "other", name: "Delete", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Ambiguous, selection.Status);
        Assert.Equal(-1, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_RejectsDuplicateNameAndControlType()
    {
        var target = Element([1], name: "Delete", controlType: "Button");
        var candidates = new[]
        {
            Element([2], name: "Delete", controlType: "Button"),
            Element([3], name: "Delete", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Ambiguous, selection.Status);
    }

    [Fact]
    public void SelectElement_RejectsIncompatibleControlType()
    {
        var target = Element([1], automationId: "delete", name: "Delete", controlType: "Button");
        var candidates = new[]
        {
            Element([2], automationId: "delete", name: "Delete", controlType: "Text"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.NotFound, selection.Status);
    }

    [Fact]
    public void SelectElement_PrefersExactRuntimeIdOverFallbackCandidates()
    {
        var target = Element([42], automationId: "delete", name: "Delete", controlType: "Button");
        var candidates = new[]
        {
            Element([42], automationId: "other", name: "Other", controlType: "Button"),
            Element([43], automationId: "delete", name: "Delete", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Found, selection.Status);
        Assert.Equal(0, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_DoesNotRepairChangedRuntimeIdTypeByName()
    {
        var target = Element([42], automationId: "delete", name: "Delete", controlType: "Button");
        var candidates = new[]
        {
            Element([42], automationId: "delete", name: "Delete", controlType: "Text"),
            Element([43], automationId: "other", name: "Delete", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.NotFound, selection.Status);
    }

    [Fact]
    public void SelectElement_ExactRuntimeIdDoesNotNeedFallbackMetadataFromOtherCandidates()
    {
        var target = Element([42], automationId: "save", name: "Save", controlType: "Button");
        var candidates = new[]
        {
            Element([42], controlType: "Button"),
            Element([43]),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Found, selection.Status);
        Assert.Equal(0, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_UsesNameOnlyWhenAutomationIdEvidenceIsAbsent()
    {
        var target = Element([1], automationId: "missing", name: "Save", controlType: "Button");
        var candidates = new[]
        {
            Element([2], automationId: "other", name: "Save", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates);

        Assert.Equal(ElementResolutionStatus.Found, selection.Status);
        Assert.Equal(0, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_ReportsIncompleteTraversalBeforeChoosingAUniqueMatch()
    {
        var target = Element([1], automationId: "save", name: "Save", controlType: "Button");
        var candidates = new[]
        {
            Element([2], automationId: "save", name: "Save", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates, traversalComplete: false);

        Assert.Equal(ElementResolutionStatus.Incomplete, selection.Status);
        Assert.Equal(-1, selection.CandidateIndex);
    }

    [Fact]
    public void SelectElement_ReportsIncompleteTraversalBeforeChoosingAnExactRuntimeId()
    {
        var target = Element([42], automationId: "save", name: "Save", controlType: "Button");
        var candidates = new[]
        {
            Element([42], automationId: "save", name: "Save", controlType: "Button"),
        };

        var selection = UiaService.SelectElement(target, candidates, traversalComplete: false);

        Assert.Equal(ElementResolutionStatus.Incomplete, selection.Status);
        Assert.Equal(-1, selection.CandidateIndex);
    }

    [Fact]
    public async Task ResolveElementWithStatusAsync_MapsReadOnlyTimeoutToIncomplete()
    {
        using var thread = new UiaThread();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = thread.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
            return 1;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            var service = new UiaService(thread);
            var result = await service.ResolveElementWithStatusAsync(
                IntPtr.Zero,
                Element([1], name: "Save"));

            Assert.Equal(ElementResolutionStatus.Incomplete, result.Status);
            Assert.Null(result.Element);
        }
        finally
        {
            release.Set();
            await blocker;
        }
    }

    [Fact]
    public async Task ResolveElementWithStatusAsync_PreservesCallerCancellation()
    {
        using var thread = new UiaThread();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = thread.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait();
            return 1;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        using var cancellation = new CancellationTokenSource();

        try
        {
            var service = new UiaService(thread);
            var resolution = service.ResolveElementWithStatusAsync(
                IntPtr.Zero,
                Element([1], name: "Save"),
                cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution);
        }
        finally
        {
            release.Set();
            await blocker;
        }
    }

    [Theory]
    [InlineData(ElementResolutionStatus.NotFound, "no longer exists")]
    [InlineData(ElementResolutionStatus.Ambiguous, "ambiguous")]
    [InlineData(ElementResolutionStatus.Incomplete, "incomplete")]
    public void CallerMapsResolutionFailuresToExistingErrorMessageShape(
        ElementResolutionStatus status,
        string expectedText)
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "ElementResolutionMessage",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var message = Assert.IsType<string>(method.Invoke(null, ["e7", status]));

        Assert.Contains("e7", message);
        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
    }

    private static ElementInfo Element(
        int[] runtimeId,
        string automationId = "",
        string name = "",
        string controlType = "Button")
        => new()
        {
            Id = "fixture",
            RuntimeId = runtimeId,
            AutomationId = automationId,
            Name = name,
            ControlType = controlType,
        };
}
