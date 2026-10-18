// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json.Serialization;

namespace ShadowUse.Jev.Flow;

/// <summary>A rectangle in normalized (0-1) window-image space. Survives resolution changes, unlike pixels.</summary>
public readonly record struct NormBox(double Left, double Top, double Right, double Bottom)
{
    [JsonIgnore] public double CenterX => (Left + Right) / 2;
    [JsonIgnore] public double CenterY => (Top + Bottom) / 2;
    [JsonIgnore] public double Area => Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
    [JsonIgnore] public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) && double.IsFinite(Bottom)
        && Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 && Left < Right && Top < Bottom;

    public static NormBox FromJev(JevBoxDecision box) => new(box.Left, box.Top, box.Right, box.Bottom);
}

public static class FlowActionKind
{
    public const string Click = "click";
    public const string KeyTap = "key_tap";
}

/// <summary>One input the flow may send from a known screen. Persisted in the <see cref="JevActionBook"/>.</summary>
public sealed class FlowAction
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = FlowActionKind.Click;
    public string Description { get; set; } = "";
    public string? TargetDescription { get; set; }
    public string? Key { get; set; }

    /// <summary>Learned location (clicks only). Null until Jev Box or a local detector localized it.</summary>
    public NormBox? Box { get; set; }
    public double BoxConfidence { get; set; }

    /// <summary>"caller" (reviewed seed), "discovered" (local detector) or "learned" (Jev Box).</summary>
    public string Source { get; set; } = "caller";

    public int Uses { get; set; }
    /// <summary>Consecutive click dispatches that did not change the confirmed screen.</summary>
    public int NoEffectStreak { get; set; }
    /// <summary>Destination screen id → times observed after this action.</summary>
    public Dictionary<string, int> Transitions { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset LastUsedUtc { get; set; }

    [JsonIgnore]
    public string? ExpectedNextScreen => Transitions.Count == 0 ? null : Transitions.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;

    /// <summary>True when the flow can send this action without any visual localization.</summary>
    [JsonIgnore]
    public bool ExecutableWithoutVision => Kind == FlowActionKind.KeyTap || Box is not null;
}

public sealed class FlowScreen
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public List<FlowAction> Actions { get; set; } = [];

    /// <summary>Coarse luminance fingerprints (a few samples) used for free local screen verification.</summary>
    public List<byte[]> Fingerprints { get; set; } = [];
    public int Visits { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}

/// <summary>A captured window frame. Capturing is local and cheap; only sending <see cref="Jpeg"/> to Jev is expensive.</summary>
public sealed record FlowFrame(string FrameId, byte[] Jpeg, int Width, int Height, byte[]? Fingerprint, DateTimeOffset CapturedAtUtc);

public sealed record FlowDispatchResult(bool InputSent, string? Error = null, bool DoNotRetry = false);

public sealed record FlowScreenSummary(string Id, string Description);

public sealed record FlowBox(NormBox Box, double VisibleProbability, double Confidence);

public sealed record FlowDecisionRequest(
    string Goal,
    string FrameId,
    byte[]? ImageJpeg,
    string? PredictedScreenId,
    string? PreviousScreenId,
    string? LastActionId,
    IReadOnlyList<string> RecentActions,
    IReadOnlyList<FlowScreenSummary> KnownScreens,
    bool RecognizeScreen,
    IReadOnlyList<FlowAction> Menu,
    IReadOnlyList<FlowAction> NeedBox,
    bool AskGoalReached,
    bool AllowNeedVision,
    string? SessionContext);

public sealed record FlowDecision(
    string Choice,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities,
    string? RecognizedScreenId,
    double? ScreenConfidence,
    double? GoalReachedProbability,
    IReadOnlyDictionary<string, FlowBox> Boxes,
    long LatencyMs,
    bool ImageSent,
    string Model = "");

public static class FlowControl
{
    public const string NeedVision = "NEED_VISION";
    public const string Wait = "WAIT";
    public const string Stop = "STOP";
    public const string Done = "DONE";
    public const string AskUser = "ASK_USER";
    public const string UnknownScreen = "unknown_screen";

    public static bool IsControl(string choice) => choice is NeedVision or Wait or Stop or Done or AskUser;
}

public interface IJevFlowDecider
{
    Task<FlowDecision> DecideAsync(FlowDecisionRequest request, CancellationToken ct);
}

public interface IJevFlowEnvironment
{
    /// <summary>Capture the target window. Must not send anything over the network.</summary>
    Task<FlowFrame> CaptureAsync(CancellationToken ct);
    Task<FlowDispatchResult> ClickAsync(FlowFrame frame, NormBox target, CancellationToken ct);
    Task<FlowDispatchResult> TapKeyAsync(FlowFrame frame, string key, CancellationToken ct);
}

/// <summary>Optional local proposal source (edge/OCR/YOLO detector) used to map screens Jev has never seen.</summary>
public interface IJevFlowDiscoverer
{
    Task<IReadOnlyList<FlowAction>> DiscoverAsync(FlowFrame frame, CancellationToken ct);
}
