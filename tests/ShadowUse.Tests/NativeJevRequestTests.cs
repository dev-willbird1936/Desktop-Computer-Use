// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Reflection;
using System.Text.Json;
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
        snapshot.Bounds = new NativeMethods.RECT { Left = 100, Top = 200, Right = 900, Bottom = 800 };
        snapshot.Elements.Add(new ElementInfo
        {
            Id = "e7",
            ControlType = "Edit",
            Name = "Password",
            Value = "hunter2",
            Enabled = true,
            ActionableNow = true,
            SupportedOperations = ["click", "set_value"],
            Actions = ["Invoke", "Value"],
            X = 10,
            Y = 20,
            Width = 30,
            Height = 40,
            ScreenX = 110,
            ScreenY = 220,
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
        var metadata = Assert.IsType<Dictionary<string, object?>>(element.Extra);
        Assert.Equal("uia", metadata["source"]);
        Assert.Equal("screen_pixels", metadata["coordinate_space"]);
        Assert.Equal("uia_current_bounding_rectangle", metadata["coordinate_provenance"]);
        Assert.Equal(snapshot.ObservationId, metadata["observation_id"]);
        var screenBox = JsonSerializer.SerializeToElement(metadata["bbox"]);
        Assert.Equal(110, screenBox.GetProperty("x").GetInt32());
        Assert.Equal(220, screenBox.GetProperty("y").GetInt32());
        Assert.Equal(30, screenBox.GetProperty("width").GetInt32());
        Assert.Equal(40, screenBox.GetProperty("height").GetInt32());
        var windowBox = JsonSerializer.SerializeToElement(metadata["window_bbox"]);
        Assert.Equal(10, windowBox.GetProperty("x").GetInt32());
        Assert.Equal(20, windowBox.GetProperty("y").GetInt32());
        Assert.Contains(request.KeyBindings!, binding => binding.Code == "PRESS_KEY_ENTER");
        Assert.Equal(snapshot.CreatedAt.AddSeconds(15), request.ExpiresAtUtc);
        Assert.True(request.CandidateSetComplete);
    }

    [Fact]
    public void GameModeRequiresVisualObservationEvenWhenBrowserChromeHasNamedUiAutomationControls()
    {
        var snapshot = NewSnapshot();
        snapshot.Elements.Add(new ElementInfo { Id = "e1", ControlType = "Button", Name = "Reload", Actions = ["Invoke"], ActionableNow = true, SupportedOperations = ["click"] });
        var method = typeof(ComputerUseTools).GetMethod("ShouldCaptureJevVisual", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.False((bool)method.Invoke(null, [snapshot, false])!);
        Assert.True((bool)method.Invoke(null, [snapshot, true])!);
    }

    [Fact]
    public void RequiredButUnavailableOcrMakesNativeObservationIncompleteWithReason()
    {
        var snapshot = NewSnapshot();
        snapshot.Elements.Add(new ElementInfo { Id = "e1", ControlType = "Button", Name = "Reload", Actions = ["Invoke"], ActionableNow = true, SupportedOperations = ["click"] });
        snapshot.VisualGroundingRequired = true;
        snapshot.VisualGroundingAttempted = true;
        snapshot.VisualGroundingUnsupportedReason = "PrintWindow capture failed";

        var request = Build(snapshot, "Play the game");

        Assert.False(request.CandidateSetComplete);
        Assert.Contains("PrintWindow capture failed", request.CandidateSetIssue);
    }

    [Fact]
    public void IncludesVisualOcrCandidatesAsUnverifiedClickTargetsAndObservedText()
    {
        var snapshot = NewSnapshot();
        snapshot.Visual = new ShadowUse.Vision.VisualObservation
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            WindowRectAtCapture = default,
            DpiAtCapture = 96,
            ParserId = "test-ocr",
            ParserVersion = "test",
            Candidates =
            [
                new ShadowUse.Vision.VisualCandidate
                {
                    Id = "v1", Text = "Start game", Confidence = 0.5,
                    ConfidenceSource = "uniqueness_heuristic", RegionHash = 123,
                },
            ],
        };

        var request = Build(snapshot, "Start the app");
        var candidate = Assert.Single(request.Elements);

        Assert.Equal("v1", candidate.Id);
        Assert.Equal("unverified OCR text", candidate.Role);
        Assert.Equal(["click_visual"], candidate.Operations);
        Assert.Contains("Start game", request.ObservedText);
        Assert.Equal("windows_ocr", Assert.IsType<Dictionary<string, object?>>(candidate.Extra)["source"]);
        Assert.True(request.CandidateSetComplete);
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
    public void DoesNotSilentlyTruncateCandidatesBeyondChoiceLimit()
    {
        var snapshot = NewSnapshot();
        for (var i = 0; i < 300; i++)
            snapshot.Elements.Add(new ElementInfo { Id = $"e{i}", ControlType = "Button", Name = $"Item {i}", Actions = ["Invoke"], ActionableNow = true, SupportedOperations = ["click"] });

        var request = Build(snapshot, "Pick one");

        Assert.Equal(300, request.Elements.Count);
        Assert.False(request.CandidateSetComplete);
        Assert.Contains("255-choice limit", request.CandidateSetIssue);
    }
}
