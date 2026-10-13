// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using ShadowUse.Native;

namespace ShadowUse.Vision;

public sealed record ScreenDiscoveryFrame(
    Bitmap Bitmap,
    byte[] Jpeg,
    string ObservationId,
    DateTimeOffset CapturedAtUtc,
    string TargetLifetime,
    int FrameWidth,
    int FrameHeight,
    int ImageWidth,
    int ImageHeight,
    double ImageScale,
    uint DpiAtCapture,
    NativeMethods.RECT WindowRect);

public interface IScreenDiscoveryBackend
{
    Task<GameUiObservation> DiscoverAsync(ScreenDiscoveryFrame frame, string method,
        bool includeLocalYolo, string? detectorError, CancellationToken ct);
}

/// <summary>Owns screenshot candidate generation and binds every proposal to the exact input frame.</summary>
public sealed class ScreenDiscoveryService(IScreenDiscoveryBackend backend)
{
    public const int MaxCandidates = LocalGameUiCandidateFusion.MaxYoloCandidates + LocalGameUiCandidateFusion.MaxLocalCandidates;

    public async Task<GameUiObservation> DiscoverAsync(ScreenDiscoveryFrame frame, string method,
        bool includeLocalYolo = false, string? detectorError = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Bitmap is null || frame.Jpeg is not { Length: > 0 } || string.IsNullOrWhiteSpace(frame.ObservationId)
            || string.IsNullOrWhiteSpace(frame.TargetLifetime) || frame.FrameWidth < 1 || frame.FrameHeight < 1
            || frame.ImageWidth < 1 || frame.ImageHeight < 1 || !double.IsFinite(frame.ImageScale) || frame.ImageScale <= 0
            || frame.Bitmap.Width != frame.FrameWidth || frame.Bitmap.Height != frame.FrameHeight)
            throw new ArgumentException("The screenshot frame is incomplete or internally inconsistent.", nameof(frame));

        var observation = await backend.DiscoverAsync(frame, method, includeLocalYolo, detectorError, ct).ConfigureAwait(false);
        if (observation.ObservationId != frame.ObservationId || observation.TargetLifetime != frame.TargetLifetime
            || observation.FrameWidth != frame.FrameWidth || observation.FrameHeight != frame.FrameHeight
            || observation.ImageWidth != frame.ImageWidth || observation.ImageHeight != frame.ImageHeight
            || observation.ImageScale != frame.ImageScale || observation.CapturedAtUtc != frame.CapturedAtUtc)
            throw new InvalidDataException("The discovery backend returned candidates for a different screenshot frame.");
        if (observation.Candidates.Count > MaxCandidates)
            throw new InvalidDataException($"The discovery backend exceeded the {MaxCandidates}-candidate cap.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in observation.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id) || !ids.Add(candidate.Id)
                || string.IsNullOrWhiteSpace(candidate.Key) || !keys.Add(candidate.Key)
                || candidate.X < 0 || candidate.Y < 0 || candidate.Width < 1 || candidate.Height < 1
                || (long)candidate.X + candidate.Width > frame.ImageWidth
                || (long)candidate.Y + candidate.Height > frame.ImageHeight)
                throw new InvalidDataException("The discovery backend returned a duplicate or out-of-frame candidate.");
        }

        string limitation = "Owned local discovery produces proposals, not an exhaustive inventory; identity, interactivity, enabled state, safe click point, and recall are unverified.";
        return observation with
        {
            CandidateSetComplete = false,
            CandidateSetIssue = Join(observation.CandidateSetIssue, limitation),
            CandidateSetCompletenessSource = "owned_local_discovery_incomplete",
            CoverageEvidence = Join(observation.CoverageEvidence, limitation),
        };
    }

    private static string Join(string? existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing.Contains(addition, StringComparison.Ordinal) ? existing : existing + " " + addition;
}

/// <summary>General, local-only backend. It uses the configured detector and generic OCR/edge proposals, never a named-game layout.</summary>
public sealed class LocalScreenDiscoveryBackend(ILocalYoloCandidateDetector? yoloDetector = null) : IScreenDiscoveryBackend
{
    public async Task<GameUiObservation> DiscoverAsync(ScreenDiscoveryFrame frame, string method,
        bool includeLocalYolo, string? detectorError, CancellationToken ct)
    {
        var local = await FastUiCandidateDetector.DetectAsync(frame.Bitmap, method, frame.ObservationId, ct).ConfigureAwait(false);
        if (!includeLocalYolo)
        {
            int Start(int value, int extent) => Math.Clamp((int)Math.Floor(value * frame.ImageScale), 0, Math.Max(0, extent - 1));
            int End(int value, int extent) => Math.Clamp((int)Math.Ceiling(value * frame.ImageScale), 1, extent);
            var candidates = local.Candidates.Select(candidate =>
            {
                int left = Start(candidate.X, frame.ImageWidth), top = Start(candidate.Y, frame.ImageHeight);
                int right = Math.Clamp(Math.Max(left + 1, End(candidate.X + candidate.Width, frame.ImageWidth)), left + 1, frame.ImageWidth);
                int bottom = Math.Clamp(Math.Max(top + 1, End(candidate.Y + candidate.Height, frame.ImageHeight)), top + 1, frame.ImageHeight);
                int width = right - left, height = bottom - top;
                return new GameUiCandidate($"{frame.ObservationId}:local:{candidate.Key}", candidate.Key,
                    candidate.Role, candidate.Label, left, top, width, height, left + width / 2, top + height / 2,
                    false, candidate.ProposalScore, candidate.Method, "unverified_visual_candidate",
                    "unverified_visual_description", IdentitySource: candidate.Method);
            }).ToArray();
            string issue = local.CandidateSetIssue ?? "Local OCR/edge proposals are heuristic and incomplete.";
            return new GameUiObservation(frame.ObservationId, frame.CapturedAtUtc, frame.TargetLifetime,
                frame.FrameWidth, frame.FrameHeight, frame.ImageWidth, frame.ImageHeight, frame.ImageScale,
                frame.DpiAtCapture, frame.WindowRect, "fast-local-visual-candidate-set-v1",
                $"Visible UI-like regions proposed by local {local.Method} screenshot heuristics", false,
                issue, local.ObservedText, candidates, [], "owned_local_heuristic_proposals_incomplete",
                CandidateGenerator: $"ShadowUse.FastUiCandidateDetector:{local.Method}");
        }

        if (yoloDetector is null) throw new InvalidOperationException("The configured local YOLO detector service is unavailable.");
        var yolo = await yoloDetector.DetectAsync(frame.Jpeg, frame.ObservationId,
            frame.ImageWidth, frame.ImageHeight, ct).ConfigureAwait(false);
        return LocalGameUiCandidateFusion.CreateObservation(frame.ObservationId, frame.CapturedAtUtc,
            frame.TargetLifetime, frame.FrameWidth, frame.FrameHeight, frame.ImageWidth, frame.ImageHeight,
            frame.ImageScale, frame.DpiAtCapture, frame.WindowRect, yolo, local, detectorError);
    }
}
