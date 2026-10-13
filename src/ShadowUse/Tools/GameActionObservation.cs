// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Tools;

/// <summary>One observation of an expected effect. Satisfied=null means the condition could not be evaluated on this frame.</summary>
public readonly record struct GameProgressReading(bool Captured, bool? Satisfied, string? UnmeasuredReason);

/// <summary>What a progress check measures. ActionSpecific is false when the measured region can change for reasons unrelated to the action.</summary>
public sealed record GameProgressCheck(string Basis, bool ActionSpecific);

/// <summary>Post-action observation timing for one call. Input is dispatched once; only observation waits and polls.</summary>
public readonly record struct ObservationWindow(int SettleMs, int PollMs, int TimeoutMs);

/// <summary>State of a camera-settlement wait after one frame comparison.</summary>
public readonly record struct CameraSettleReading(string State, long StillMs, bool Settled);

/// <summary>
/// Decides when the camera has stopped. Only confident stillness counts: a shift resets the interval, and so does anything
/// the motion estimator cannot classify (a turn, a cut, a new screen, an invalid comparison, or low confidence). A reset
/// never inherits earlier stability.
/// </summary>
public sealed class CameraSettleTracker(long stableMs)
{
    public const double MinStillConfidence = 0.5;
    public const string Baseline = "baseline", Still = "still", Moving = "moving", Unclassified = "unclassified";

    private long? stillSince;
    private long previousAtMs = -1;

    /// <summary>kind is the motion estimate between the previous and current frame, or null for the first frame (no comparison yet).</summary>
    public CameraSettleReading Observe(string? kind, double confidence, long nowMs)
    {
        long previous = previousAtMs;
        previousAtMs = nowMs;
        if (kind == null)
        {
            stillSince = null;
            return new(Baseline, 0, false);
        }
        if (kind == "shift")
        {
            stillSince = null;
            return new(Moving, 0, false);
        }
        if (kind != "none" || !double.IsFinite(confidence) || confidence < MinStillConfidence)
        {
            stillSince = null;
            return new(Unclassified, 0, false);
        }
        stillSince ??= previous >= 0 ? previous : nowMs;
        long stillMs = Math.Max(0, nowMs - stillSince.Value);
        return new(Still, stillMs, stillMs >= stableMs);
    }
}

/// <summary>
/// Pure decisions for game action observation. Settling and polling are fixed wall-clock values and never scale with
/// InputPace; input is dispatched once before observation starts and is never repeated while waiting.
/// </summary>
public static class GameActionObservation
{
    public const int SettleMs = 120;
    public const int PollMs = 100;
    public const int TimeoutMs = 1500;
    public const double FrameChangeThreshold = 0.015;
    public const double RegionChangeThreshold = 0.002;
    public const string Satisfied = "satisfied";
    public const string Timeout = "timeout";
    public const string Unmeasured = "unmeasured";
    public const string CaptureFailed = "capture_failed";

    public static GameProgressCheck? CheckFor(string? expectedEffect, bool hasProgressBox)
    {
        if (hasProgressBox) return new GameProgressCheck("progress_box", true);
        return expectedEffect switch
        {
            "frame_change" => new GameProgressCheck("expected_frame_change", false),
            "camera_motion" => new GameProgressCheck("expected_camera_motion", true),
            not null when expectedEffect.StartsWith("text:", StringComparison.Ordinal) => new GameProgressCheck("expected_text", true),
            _ => null,
        };
    }

    public const int MaxSettleMs = 10_000, MaxTimeoutMs = 60_000, MinPollMs = 50, MaxPollMs = 2_000;
    public static readonly ObservationWindow DefaultWindow = new(SettleMs, PollMs, TimeoutMs);

    /// <summary>Resolves per-call observation timing; a negative value keeps the default. Returns an error message when a value is out of range.</summary>
    public static string? TryResolveWindow(int settleMs, int pollMs, int timeoutMs, out ObservationWindow window)
    {
        window = DefaultWindow;
        if (settleMs > MaxSettleMs) return $"observe_settle_ms must be between 0 and {MaxSettleMs}.";
        if (pollMs >= 0 && (pollMs < MinPollMs || pollMs > MaxPollMs)) return $"observe_poll_ms must be between {MinPollMs} and {MaxPollMs}.";
        if (timeoutMs > MaxTimeoutMs) return $"observe_timeout_ms must be between 0 and {MaxTimeoutMs}.";
        window = new(settleMs >= 0 ? settleMs : SettleMs, pollMs >= 0 ? pollMs : PollMs, timeoutMs >= 0 ? timeoutMs : TimeoutMs);
        return null;
    }

    public static bool ShouldPoll(GameProgressReading reading, long elapsedMs) => ShouldPoll(reading, elapsedMs, DefaultWindow);

    public static bool ShouldPoll(GameProgressReading reading, long elapsedMs, ObservationWindow window)
        => reading.Satisfied != true && reading.UnmeasuredReason == null && elapsedMs + window.PollMs < window.TimeoutMs;

    /// <summary>What a satisfied check shows. None of these prove the avatar moved: the camera can turn without the avatar moving, and animated regions change on their own.</summary>
    public static string ProvesOf(string basis) => basis switch
    {
        "progress_box" => "region_pixels_changed",
        "expected_frame_change" => "some_pixels_changed",
        "expected_camera_motion" => "camera_translation",
        "expected_text" => "text_visible",
        _ => "unknown",
    };

    public static string StatusOf(GameProgressReading reading)
        => reading.Satisfied == true ? Satisfied
            : reading.UnmeasuredReason != null ? Unmeasured
            : !reading.Captured ? CaptureFailed
            : Timeout;

    public static string? UnmeasuredReasonOf(GameProgressReading reading, string status)
        => status == Unmeasured ? reading.UnmeasuredReason : status == CaptureFailed ? "capture_failed" : null;

    /// <summary>Maps an observation to a loop-guard input. Measured=false means the guard must not be updated.</summary>
    public static (bool Measured, bool Progressed, string Reason) LoopGuardInput(GameProgressCheck? check, string? status, string? unmeasuredReason)
    {
        if (check == null || status == null) return (false, false, "no_progress_check_requested");
        return status switch
        {
            Satisfied when !check.ActionSpecific => (false, false, "full_frame_change_not_action_specific"),
            Satisfied => (true, true, "progress_observed"),
            Timeout => (true, false, "no_progress_observed"),
            _ => (false, false, unmeasuredReason ?? "progress_not_measured"),
        };
    }

    public static object UnmeasuredLoopGuard(string reason) => new { status = Unmeasured, reason, should_stop = false };

    public static object MeasuredLoopGuard(string reason, int noProgressCount, bool shouldStop, string? diagnosis)
        => new { status = "measured", reason, no_progress_count = noProgressCount, should_stop = shouldStop, diagnosis };

    /// <summary>Jitter allowance in window-relative physical pixels, about 1% of the shorter window side and at least 12.</summary>
    public static double ClickTolerancePx(int windowWidth, int windowHeight)
        => Math.Max(12, Math.Min(windowWidth, windowHeight) * 0.01);
}
