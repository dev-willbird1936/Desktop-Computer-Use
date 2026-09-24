// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>Unit coverage of ComputerUseTools.BuildNativeJevRequest's pure data mapping, invoked via reflection like the project's other private-static tests.</summary>
public sealed class NativeJevRequestTests
{
    private static JevDecideRequest Build(Snapshot snapshot, string goal, string[]? recentActions = null)
    {
        var method = typeof(ComputerUseTools).GetMethod("BuildNativeJevRequest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (JevDecideRequest)method.Invoke(null, [snapshot, goal, recentActions])!;
    }

    private static Snapshot NewSnapshot(string title = "Login") => new()
    {
        Revision = 1,
        App = "test-app",
        Pid = Environment.ProcessId,
        Hwnd = (IntPtr)1,
        Title = title,
        Bounds = default,
    };

    [Fact]
    public void FiltersToActionableElementsWithOperations()
    {
        var snapshot = NewSnapshot();
        snapshot.Elements.Add(new ElementInfo { Id = "e1", ControlType = "Button", Name = "Sign in", ActionableNow = true, SupportedOperations = ["click"] });
        snapshot.Elements.Add(new ElementInfo { Id = "e2", ControlType = "Button", Name = "Disabled", ActionableNow = false, SupportedOperations = ["click"] });
        snapshot.Elements.Add(new ElementInfo { Id = "e3", ControlType = "Text", Name = "Label", ActionableNow = true, SupportedOperations = [] });

        var request = Build(snapshot, "Sign in");

        var element = Assert.Single(request.Elements);
        Assert.Equal("e1", element.Id);
    }

    [Fact]
    public void MapsFieldsAndContext()
    {
        var snapshot = NewSnapshot(title: "Account settings");
        snapshot.Elements.Add(new ElementInfo
        {
            Id = "e7",
            ControlType = "Edit",
            Name = "Password",
            Value = "hunter2",
            Enabled = true,
            ActionableNow = true,
            SupportedOperations = ["click", "set_value"],
        });

        var request = Build(snapshot, "Sign in", ["typed email"]);

        Assert.Equal("Sign in", request.Goal);
        Assert.Equal(snapshot.ObservationId, request.ObservationId);
        Assert.Equal("Account settings", request.Context);
        Assert.Equal(["typed email"], request.RecentActions);
        var element = Assert.Single(request.Elements);
        Assert.Equal("e7", element.Id);
        Assert.Equal("Edit", element.Role);
        Assert.Equal("Password", element.Name);
        Assert.Equal("hunter2", element.CurrentValue);
        Assert.True(element.Enabled);
        Assert.Equal(["click", "set_value"], element.Operations);
    }

    [Fact]
    public void NullEnabled_DefaultsToTrueRatherThanExcludingTheElement()
    {
        var snapshot = NewSnapshot();
        snapshot.Elements.Add(new ElementInfo { Id = "e1", ControlType = "Button", Name = "Ok", Enabled = null, ActionableNow = true, SupportedOperations = ["click"] });

        var request = Build(snapshot, "Confirm");

        Assert.True(Assert.Single(request.Elements).Enabled);
    }

    [Fact]
    public void CapsAtTheChoice255OptionLimit()
    {
        var snapshot = NewSnapshot();
        for (var i = 0; i < 300; i++)
            snapshot.Elements.Add(new ElementInfo { Id = $"e{i}", ControlType = "Button", Name = $"Item {i}", ActionableNow = true, SupportedOperations = ["click"] });

        var request = Build(snapshot, "Pick one");

        Assert.Equal(255, request.Elements.Count);
    }
}
