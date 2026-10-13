// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>Pure decisions behind game action observation timing and the explicit loop_guard status.</summary>
public sealed class GameActionObservationTests
{
    [Fact]
    public void Settle_IsFixedWallClock_NotScaledByFastInputPace()
    {
        using (InputPace.Scope("fast", 0))
        {
            Assert.True(InputPace.Ms(120) < 60);
            Assert.Equal(120, GameActionObservation.SettleMs);
        }
    }

    [Fact]
    public void CheckFor_ProgressBoxWinsAndIsActionSpecific()
    {
        var check = GameActionObservation.CheckFor("frame_change", hasProgressBox: true);
        Assert.Equal("progress_box", check!.Basis);
        Assert.True(check.ActionSpecific);
    }

    [Theory]
    [InlineData("frame_change", "expected_frame_change", false)]
    [InlineData("camera_motion", "expected_camera_motion", true)]
    [InlineData("text:Quest", "expected_text", true)]
    public void CheckFor_ExpectedEffect_MarksOnlyFullFrameChangeAsNotActionSpecific(string effect, string basis, bool actionSpecific)
    {
        var check = GameActionObservation.CheckFor(effect, hasProgressBox: false);
        Assert.Equal(basis, check!.Basis);
        Assert.Equal(actionSpecific, check.ActionSpecific);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything_else")]
    public void CheckFor_NoRecognisedCondition_ReturnsNull(string? effect)
        => Assert.Null(GameActionObservation.CheckFor(effect, hasProgressBox: false));

    [Fact]
    public void ShouldPoll_OnlyWhileUnsatisfiedAndMeasurableWithinTimeout()
    {
        Assert.True(GameActionObservation.ShouldPoll(new(true, false, null), 0));
        Assert.True(GameActionObservation.ShouldPoll(new(false, null, null), 0));
        Assert.False(GameActionObservation.ShouldPoll(new(true, true, null), 0));
        Assert.False(GameActionObservation.ShouldPoll(new(true, null, "baseline_unavailable"), 0));
        Assert.True(GameActionObservation.ShouldPoll(new(true, false, null), GameActionObservation.TimeoutMs - GameActionObservation.PollMs - 1));
        Assert.False(GameActionObservation.ShouldPoll(new(true, false, null), GameActionObservation.TimeoutMs - GameActionObservation.PollMs));
    }

    [Fact]
    public void StatusOf_DistinguishesSatisfiedTimeoutUnmeasuredAndCaptureFailed()
    {
        Assert.Equal("satisfied", GameActionObservation.StatusOf(new(true, true, null)));
        Assert.Equal("timeout", GameActionObservation.StatusOf(new(true, false, null)));
        Assert.Equal("unmeasured", GameActionObservation.StatusOf(new(true, null, "ocr_unsupported")));
        Assert.Equal("capture_failed", GameActionObservation.StatusOf(new(false, null, null)));
    }

    [Fact]
    public void UnmeasuredReasonOf_ReportsReasonOnlyForUnmeasuredOrCaptureFailed()
    {
        Assert.Equal("ocr_unsupported", GameActionObservation.UnmeasuredReasonOf(new(true, null, "ocr_unsupported"), "unmeasured"));
        Assert.Equal("capture_failed", GameActionObservation.UnmeasuredReasonOf(new(false, null, null), "capture_failed"));
        Assert.Null(GameActionObservation.UnmeasuredReasonOf(new(true, true, null), "satisfied"));
        Assert.Null(GameActionObservation.UnmeasuredReasonOf(new(true, false, null), "timeout"));
    }

    [Fact]
    public void LoopGuardInput_FullFrameChangeIsNotActionSpecificProgress()
    {
        var frame = GameActionObservation.CheckFor("frame_change", false);
        var result = GameActionObservation.LoopGuardInput(frame, "satisfied", null);
        Assert.False(result.Measured);
        Assert.Equal("full_frame_change_not_action_specific", result.Reason);
    }

    [Fact]
    public void LoopGuardInput_MapsStatusesTruthfully()
    {
        var box = GameActionObservation.CheckFor(null, true);
        Assert.Equal((true, true, "progress_observed"), GameActionObservation.LoopGuardInput(box, "satisfied", null));
        Assert.Equal((true, false, "no_progress_observed"), GameActionObservation.LoopGuardInput(box, "timeout", null));
        Assert.Equal((false, false, "baseline_unavailable"), GameActionObservation.LoopGuardInput(box, "unmeasured", "baseline_unavailable"));
        Assert.Equal((false, false, "capture_failed"), GameActionObservation.LoopGuardInput(box, "capture_failed", "capture_failed"));
        Assert.Equal((false, false, "no_progress_check_requested"), GameActionObservation.LoopGuardInput(null, null, null));
    }

    [Fact]
    public void UnmeasuredLoopGuard_IsExplicitAndNeverStops()
    {
        var node = JsonSerializer.SerializeToElement(GameActionObservation.UnmeasuredLoopGuard("input_not_sent"));
        Assert.Equal("unmeasured", node.GetProperty("status").GetString());
        Assert.Equal("input_not_sent", node.GetProperty("reason").GetString());
        Assert.False(node.GetProperty("should_stop").GetBoolean());
    }

    [Fact]
    public void MeasuredLoopGuard_CarriesCountAndStopDecision()
    {
        var node = JsonSerializer.SerializeToElement(GameActionObservation.MeasuredLoopGuard("no_progress_observed", 3, true, "stuck"));
        Assert.Equal("measured", node.GetProperty("status").GetString());
        Assert.Equal(3, node.GetProperty("no_progress_count").GetInt32());
        Assert.True(node.GetProperty("should_stop").GetBoolean());
    }

    [Theory]
    [InlineData(800, 600, 12)]
    [InlineData(1920, 1080, 12)]
    [InlineData(3840, 2160, 21.6)]
    public void ClickTolerancePx_IsOnePercentOfShorterSideWithFloor(int width, int height, double expected)
        => Assert.Equal(expected, GameActionObservation.ClickTolerancePx(width, height), 3);

    [Fact]
    public void LoopGuard_SmallJitterKeepsRepeatedFailedClickCount_AcrossBucketBoundaries()
    {
        var guard = new LoopGuard(threshold: 3);
        double tol = GameActionObservation.ClickTolerancePx(1920, 1080);
        guard.Record("game|w", "click:left:1:0", false, [(99, 99)], tol);
        guard.Record("game|w", "click:left:1:0", false, [(101, 100)], tol);
        var third = guard.Record("game|w", "click:left:1:0", false, [(100, 102)], tol);
        Assert.Equal(3, third.NoProgressCount);
        Assert.True(third.ShouldStop);
    }

    [Fact]
    public void LoopGuard_DriftIsMeasuredFromFirstPosition_SoSlowWalkingDoesNotAccumulate()
    {
        var guard = new LoopGuard(threshold: 3);
        guard.Record("s", "click", false, [(0, 0)], 12);
        guard.Record("s", "click", false, [(10, 0)], 12);
        var far = guard.Record("s", "click", false, [(20, 0)], 12);
        Assert.Equal(1, far.NoProgressCount);
    }

    [Fact]
    public void LoopGuard_DifferentTargetOrPointCountRestartsCount()
    {
        var guard = new LoopGuard(threshold: 3);
        guard.Record("s", "click", false, [(0, 0)], 12);
        Assert.Equal(1, guard.Record("s", "click", false, [(500, 500)], 12).NoProgressCount);
        Assert.Equal(1, guard.Record("s", "drag", false, [(500, 500)], 12).NoProgressCount);
        Assert.Equal(1, guard.Record("s", "drag", false, [(500, 500), (600, 600)], 12).NoProgressCount);
    }

    [Fact]
    public void LoopGuard_ProgressResetsAndPointlessOverloadKeepsLegacyBehaviour()
    {
        var guard = new LoopGuard(threshold: 3);
        guard.Record("s", "click", false, [(0, 0)], 12);
        Assert.Equal(0, guard.Record("s", "click", true, [(0, 0)], 12).NoProgressCount);
        Assert.Equal(1, guard.Record("s", "click", false, [(0, 0)], 12).NoProgressCount);
        Assert.Equal(1, guard.Record("s", "click", false).NoProgressCount);
        Assert.Equal(2, guard.Record("s", "click", false).NoProgressCount);
    }

    // ---- camera settlement ----

    private static CameraSettleReading Feed(CameraSettleTracker tracker, long atMs, string? kind, double confidence = 1) => tracker.Observe(kind, confidence, atMs);

    [Theory]
    [InlineData("changed", 0.9)]
    [InlineData("unverified", 1.0)]
    [InlineData("bogus", 1.0)]
    [InlineData("none", 0.0)]
    [InlineData("none", double.NaN)]
    public void CameraSettle_UnclassifiedOrInvalidMotionNeverSettles(string kind, double confidence)
    {
        var tracker = new CameraSettleTracker(0);
        Feed(tracker, 0, null);
        for (long t = 100; t <= 5000; t += 100)
        {
            var reading = Feed(tracker, t, kind, confidence);
            Assert.False(reading.Settled);
            Assert.Equal(CameraSettleTracker.Unclassified, reading.State);
        }
    }

    [Fact]
    public void CameraSettle_FirstFrameIsOnlyABaseline()
    {
        var reading = Feed(new CameraSettleTracker(0), 0, null);
        Assert.False(reading.Settled);
        Assert.Equal(CameraSettleTracker.Baseline, reading.State);
    }

    [Fact]
    public void CameraSettle_ConfidentStillnessMustPersistForTheRequestedInterval()
    {
        var tracker = new CameraSettleTracker(500);
        Feed(tracker, 0, null);
        Assert.False(Feed(tracker, 250, "none").Settled);
        Assert.False(Feed(tracker, 400, "none").Settled);
        var settled = Feed(tracker, 500, "none");
        Assert.True(settled.Settled);
        Assert.Equal(500, settled.StillMs);
    }

    [Fact]
    public void CameraSettle_MotionResetsTheStableInterval()
    {
        var tracker = new CameraSettleTracker(500);
        Feed(tracker, 0, null);
        Feed(tracker, 300, "none");
        Assert.Equal(CameraSettleTracker.Moving, Feed(tracker, 400, "shift", 0.9).State);
        Assert.False(Feed(tracker, 800, "none").Settled);
        Assert.False(Feed(tracker, 899, "none").Settled);
        Assert.True(Feed(tracker, 900, "none").Settled);
    }

    [Fact]
    public void CameraSettle_SceneChangeCannotInheritEarlierStability()
    {
        var tracker = new CameraSettleTracker(500);
        Feed(tracker, 0, null);
        Assert.False(Feed(tracker, 400, "none").Settled);
        Assert.Equal(CameraSettleTracker.Unclassified, Feed(tracker, 500, "changed", 0.8).State);
        Assert.False(Feed(tracker, 600, "none").Settled);
        Assert.False(Feed(tracker, 900, "none").Settled);
        Assert.True(Feed(tracker, 1100, "none").Settled);
    }

    [Fact]
    public void CameraSettle_LowConfidenceStillnessDoesNotCount()
    {
        var tracker = new CameraSettleTracker(0);
        Feed(tracker, 0, null);
        Assert.False(Feed(tracker, 100, "none", CameraSettleTracker.MinStillConfidence - 0.01).Settled);
        Assert.True(Feed(tracker, 200, "none", CameraSettleTracker.MinStillConfidence).Settled);
    }

    // ---- configurable observation window ----

    [Fact]
    public void ResolveWindow_NegativeKeepsDefaultsAndValuesOverride()
    {
        Assert.Null(GameActionObservation.TryResolveWindow(-1, -1, -1, out var defaults));
        Assert.Equal(new ObservationWindow(120, 100, 1500), defaults);
        Assert.Null(GameActionObservation.TryResolveWindow(0, 250, 12_000, out var custom));
        Assert.Equal(new ObservationWindow(0, 250, 12_000), custom);
        Assert.Null(GameActionObservation.TryResolveWindow(-1, -1, 0, out var none));
        Assert.Equal(0, none.TimeoutMs);
    }

    [Theory]
    [InlineData(10_001, -1, -1, "observe_settle_ms")]
    [InlineData(-1, 49, -1, "observe_poll_ms")]
    [InlineData(-1, 2001, -1, "observe_poll_ms")]
    [InlineData(-1, -1, 60_001, "observe_timeout_ms")]
    public void ResolveWindow_RejectsOutOfRangeValuesByName(int settle, int poll, int timeout, string name)
        => Assert.Contains(name, GameActionObservation.TryResolveWindow(settle, poll, timeout, out _));

    [Fact]
    public void ShouldPoll_HonoursTheConfiguredWindow()
    {
        var window = new ObservationWindow(0, 250, 12_000);
        var waiting = new GameProgressReading(true, false, null);
        Assert.True(GameActionObservation.ShouldPoll(waiting, 11_000, window));
        Assert.False(GameActionObservation.ShouldPoll(waiting, 11_750, window));
        Assert.False(GameActionObservation.ShouldPoll(waiting, 0, new ObservationWindow(120, 100, 0)));
    }

    [Theory]
    [InlineData("expected_camera_motion", "camera_translation")]
    [InlineData("progress_box", "region_pixels_changed")]
    [InlineData("expected_frame_change", "some_pixels_changed")]
    [InlineData("expected_text", "text_visible")]
    public void ProvesOf_NamesWhatTheCheckShowsAndNeverAvatarMovement(string basis, string proves)
        => Assert.Equal(proves, GameActionObservation.ProvesOf(basis));

    // ---- compact sequence results ----

    private static JsonElement Step(object payload) => JsonSerializer.SerializeToElement(payload);

    private static Dictionary<string, object?> ActionPayload(string tool, string frameId, bool interrupted = false) => new()
    {
        ["ok"] = !interrupted, ["tool"] = tool, ["method"] = "sendinput_game_key", ["effect"] = "frame_changed", ["frame_change_pct"] = 12.5,
        ["motion"] = null, ["text"] = null, ["bars"] = null, ["progress"] = null, ["progress_change_pct"] = null, ["image_scale"] = 0.6667, ["pace"] = "fast",
        ["loop_guard"] = new { status = "unmeasured", reason = "no_progress_check_requested", should_stop = false },
        ["frame_id"] = frameId, ["frame_timestamp"] = "2026-10-03T10:00:00+00:00", ["frame_dimensions"] = new { width = 1920, height = 1080 },
        ["target_lifetime"] = "target:abc123:4:132900000000", ["target_lifetime_verified"] = true,
        ["source_frame_id"] = null, ["source_frame_age_ms"] = null, ["geometry_stale"] = false, ["camera_moved_since_source_frame"] = null,
        ["camera_motion_unclassified"] = null, ["interrupted"] = interrupted, ["interruption_reason"] = interrupted ? "foreground_lost" : null, ["elapsed_hold_ms"] = interrupted ? 180 : 0,
        ["attempt_count"] = 1, ["input_sent"] = true,
        ["input_ownership"] = new
        {
            state = "owner", reason = (string?)null, controller_pid = 4242, controller_process_creation_token = "132900000000", controller_id = "dcu-4242-aaaa", claimed_utc = "2026-10-03T09:59:00Z",
            this_controller_pid = 4242, window = new { root_hwnd = 983040, pid = 9001, process_creation_token = "132800000000", key = "983040:9001:132800000000" }, active_operations = 1,
            lease_path = "C:\\Users\\someone\\AppData\\Local\\shadow-use\\leases\\983040-9001.lease", error = (string?)null,
        },
        ["timing"] = new { queue_ms = 0, activation_ms = 0, capture_ms = 31, input_ms = 96, dispatch_ms = 97, retry_wait_ms = 0, ocr_ms = 0, observation_wait_ms = 150, observation_polls = 1, total_ms = 290 },
    };

    [Fact]
    public void CompactSequence_HoistsSharedMetadataAndKeepsPerStepFacts()
    {
        var steps = new[]
        {
            ("game_key", Step(ActionPayload("game_key", "f1"))),
            ("game_key", Step(ActionPayload("game_key", "f2", interrupted: true))),
            ("game_key", Step(ActionPayload("game_key", "f3"))),
        };
        var (shared, compacted) = GameSequenceResult.Compact(steps);

        Assert.Equal("target:abc123:4:132900000000", shared["target_lifetime"]!.GetValue<string>());
        Assert.NotNull(shared["input_ownership"]);
        Assert.NotNull(shared["frame_dimensions"]);
        Assert.False(shared.ContainsKey("interrupted"));
        foreach (var step in compacted)
        {
            Assert.False(step.ContainsKey("input_ownership"));
            Assert.False(step.ContainsKey("target_lifetime"));
            Assert.False(step.ContainsKey("tool"));
            Assert.False(step.ContainsKey("source_frame_id"));
            Assert.False(step["timing"]!.AsObject().ContainsKey("queue_ms"));
        }
        Assert.Equal("f2", compacted[1]["frame_id"]!.GetValue<string>());
        Assert.True(compacted[1]["interrupted"]!.GetValue<bool>());
        Assert.Equal("foreground_lost", compacted[1]["interruption_reason"]!.GetValue<string>());
        Assert.Equal(180, compacted[1]["elapsed_hold_ms"]!.GetValue<int>());
        Assert.Equal(97, compacted[1]["timing"]!["dispatch_ms"]!.GetValue<int>());
    }

    [Fact]
    public void CompactSequence_CutsSerializedSizeByAtLeastSixtyPercent()
    {
        var payloads = Enumerable.Range(1, 6).Select(i => ("game_key", Step(ActionPayload("game_key", $"f{i}")))).ToArray();
        var (shared, compacted) = GameSequenceResult.Compact(payloads);
        int before = payloads.Sum(p => p.Item2.GetRawText().Length);
        int after = shared.ToJsonString().Length + compacted.Sum(c => c.ToJsonString().Length);
        Assert.True(after <= before * 0.4, $"compact {after} chars vs full {before} chars");
    }

    [Fact]
    public void CompactSequence_DoesNotHoistWhenStepsDisagree()
    {
        var a = ActionPayload("game_key", "f1");
        var b = ActionPayload("game_key", "f2");
        b["pace"] = "human";
        var (shared, compacted) = GameSequenceResult.Compact([("game_key", Step(a)), ("game_key", Step(b))]);
        Assert.False(shared.ContainsKey("pace"));
        Assert.Equal("fast", compacted[0]["pace"]!.GetValue<string>());
        Assert.Equal("human", compacted[1]["pace"]!.GetValue<string>());
    }
}
