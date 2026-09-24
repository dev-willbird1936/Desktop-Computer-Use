// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>Unit coverage of ComputerUseTools' private DeltaOf/ChangedProperties (F07), invoked via reflection like the project's other private-static tests.</summary>
public sealed class NativeSnapshotDeltaTests
{
    private static object DeltaOf(Snapshot? before, Snapshot after)
    {
        var method = typeof(ComputerUseTools).GetMethod("DeltaOf", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, [before, after])!;
    }

    private static string[] ChangedProperties(ElementInfo before, ElementInfo after)
    {
        var method = typeof(ComputerUseTools).GetMethod("ChangedProperties", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string[])method.Invoke(null, [before, after])!;
    }

    private static Snapshot NewSnapshot() => new() { Revision = 1, App = "app", Pid = Environment.ProcessId, Hwnd = (IntPtr)1, Title = "t", Bounds = default };

    private static object[] Get(object result, string property)
        => (object[])result.GetType().GetProperty(property)!.GetValue(result)!;

    [Fact]
    public void NoPriorSnapshot_ReturnsAnExplicitNote()
    {
        var after = NewSnapshot();
        after.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [1] });

        var result = DeltaOf(null, after);

        Assert.Equal("no prior snapshot", result.GetType().GetProperty("note")!.GetValue(result));
    }

    [Fact]
    public void UnchangedSnapshot_ReportsNothingDespiteFreshDisplayIds()
    {
        var before = NewSnapshot();
        before.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [42], Name = "Save", Value = "" });
        var after = NewSnapshot();
        after.Elements.Add(new ElementInfo { Id = "e99", RuntimeId = [42], Name = "Save", Value = "" }); // different display id, same native identity

        var result = DeltaOf(before, after);

        Assert.Empty(Get(result, "added"));
        Assert.Empty(Get(result, "removed"));
        Assert.Empty(Get(result, "changed"));
    }

    [Fact]
    public void ValueOnlyChange_IsReportedWithValuePropertyName()
    {
        var before = NewSnapshot();
        before.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [7], Name = "Query", Value = "" });
        var after = NewSnapshot();
        after.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [7], Name = "Query", Value = "fixture-query" });

        var result = DeltaOf(before, after);
        var changed = Get(result, "changed");

        var entry = Assert.Single(changed);
        var changedProperties = (string[])entry.GetType().GetProperty("changed_properties")!.GetValue(entry)!;
        Assert.Contains("value", changedProperties);
    }

    [Fact]
    public void NewElement_IsReportedAsAdded()
    {
        var before = NewSnapshot();
        var after = NewSnapshot();
        after.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [1], Name = "New button" });

        var result = DeltaOf(before, after);

        var added = Get(result, "added");
        Assert.Single(added);
        Assert.Empty(Get(result, "removed"));
    }

    [Fact]
    public void DisappearedElement_IsReportedAsRemoved()
    {
        var before = NewSnapshot();
        before.Elements.Add(new ElementInfo { Id = "e1", RuntimeId = [1], Name = "Vanishing" });
        var after = NewSnapshot();

        var result = DeltaOf(before, after);

        var removed = Get(result, "removed");
        Assert.Single(removed);
        Assert.Empty(Get(result, "added"));
    }

    [Fact]
    public void PositionOnlyChange_IsIgnoredAsCosmetic()
    {
        var before = new ElementInfo { Id = "e1", RuntimeId = [1], Name = "Save", Value = "", X = 10, Y = 10, ScreenX = 10, ScreenY = 10 };
        var after = new ElementInfo { Id = "e1", RuntimeId = [1], Name = "Save", Value = "", X = 400, Y = 400, ScreenX = 400, ScreenY = 400 };

        Assert.Empty(ChangedProperties(before, after));
    }

    [Fact]
    public void EnabledChange_IsReported()
    {
        var before = new ElementInfo { Id = "e1", RuntimeId = [1], Enabled = false };
        var after = new ElementInfo { Id = "e1", RuntimeId = [1], Enabled = true };

        Assert.Contains("enabled", ChangedProperties(before, after));
    }
}
