// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Native;

namespace ShadowUse.Vision;

/// <summary>One OCR-labelled text region from a visual-grounding capture. Coordinates are
/// window-relative (same frame of reference as ElementInfo.X/Y), captured from an unscaled
/// 1:1 window bitmap so no scale-factor conversion is needed. Never a verified control:
/// callers must treat every candidate as "unverified" until the caller re-observes.</summary>
public sealed class VisualCandidate
{
    public required string Id;              // "v1", "v2", ... — distinct per observation, never reused across a new capture
    public required string Text;
    public int X, Y, Width, Height;         // window-relative frame
    public int ScreenX, ScreenY;            // absolute frame origin (top-left)
    public double? Confidence;              // heuristic only — OcrWord exposes no model probability
    public string? ConfidenceSource;        // e.g. "uniqueness_heuristic"; null when Confidence is null
    public ulong RegionHash;                // dHash of the captured bbox pixels, for staleness re-checks at click time
}

/// <summary>One visual-grounding capture and its OCR results, bound to the exact window
/// geometry/DPI it was taken against. A click against any candidate must re-validate this
/// binding before dispatching — see VisualGroundingService.TryRevalidateAsync.</summary>
public sealed class VisualObservation
{
    public required DateTimeOffset CapturedAtUtc;
    public required NativeMethods.RECT WindowRectAtCapture;
    public required uint DpiAtCapture;
    public required string ParserId;        // "windows.media.ocr"
    public required string ParserVersion;   // OS build + recognizer language tag
    public required IReadOnlyList<VisualCandidate> Candidates;
    public bool UsesGameCapture;              // capture path that also works for foreground GPU-rendered windows
}

/// <summary>Result of attempting a visual-grounding capture: either a successful observation,
/// or an explicit unsupported reason (missing OCR language, capture failure) — never a
/// guessed/partial result standing in for a real one.</summary>
public sealed class VisualGroundingOutcome
{
    public VisualObservation? Observation;
    public string? UnsupportedReason;

    public static VisualGroundingOutcome Success(VisualObservation observation) => new() { Observation = observation };
    public static VisualGroundingOutcome Unsupported(string reason) => new() { UnsupportedReason = reason };
}
