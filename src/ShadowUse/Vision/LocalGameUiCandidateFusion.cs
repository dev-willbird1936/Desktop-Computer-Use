// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using ShadowUse.Native;

namespace ShadowUse.Vision;

/// <summary>Bounded, frame-preserving fusion for local detector and OCR/edge proposals.</summary>
public static class LocalGameUiCandidateFusion
{
    public const string LayoutId = "local-yolov9-ocr-edge-candidate-set-v1";
    public const string CandidateSetCompletenessSource = "local_yolo_ocr_edge_heuristics_incomplete";
    public const int MaxYoloCandidates = 160;
    public const int MaxLocalCandidates = 27;
    public const double YoloNmsThreshold = 0.20;

    public static GameUiObservation CreateObservation(
        string frameId,
        DateTimeOffset capturedAt,
        string targetLifetime,
        int frameWidth,
        int frameHeight,
        int imageWidth,
        int imageHeight,
        double imageScale,
        uint dpiAtCapture,
        NativeMethods.RECT windowRect,
        LocalYoloDetectionResult yolo,
        FastUiDetectionResult local,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(yolo);
        ArgumentNullException.ThrowIfNull(local);
        if (string.IsNullOrWhiteSpace(frameId) || string.IsNullOrWhiteSpace(targetLifetime))
            throw new ArgumentException("The frame and target lifetime must be non-empty.");
        if (frameWidth < 1 || frameHeight < 1 || imageWidth < 1 || imageHeight < 1
            || !double.IsFinite(imageScale) || imageScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Frame geometry and scale must be positive.");
        if (yolo.ImageWidth != imageWidth || yolo.ImageHeight != imageHeight)
            throw new InvalidDataException("YOLO dimensions do not match the screenshot frame.");
        if (local.Candidates.Any(candidate => candidate.X < 0 || candidate.Y < 0 || candidate.Width < 1 || candidate.Height < 1
            || (long)candidate.X + candidate.Width > frameWidth || (long)candidate.Y + candidate.Height > frameHeight))
            throw new InvalidDataException("A local OCR/edge proposal falls outside the captured frame.");

        Rectangle ToImageBox(FastUiCandidate candidate)
        {
            int left = Math.Clamp((int)Math.Floor(candidate.X * imageScale), 0, imageWidth - 1);
            int top = Math.Clamp((int)Math.Floor(candidate.Y * imageScale), 0, imageHeight - 1);
            int right = Math.Clamp((int)Math.Ceiling((candidate.X + candidate.Width) * imageScale), left + 1, imageWidth);
            int bottom = Math.Clamp((int)Math.Ceiling((candidate.Y + candidate.Height) * imageScale), top + 1, imageHeight);
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        var ocrText = local.Candidates
            .Where(candidate => candidate.Method == "windows_media_ocr" && !string.IsNullOrWhiteSpace(candidate.Label))
            .Select(candidate => (Text: candidate.Label.Trim(), Box: ToImageBox(candidate)))
            .ToArray();
        var calibratedBoxes = local.Candidates
            .Where(candidate => candidate.Method == "bloons_calibrated_pixel_anchor")
            .Select(ToImageBox)
            .ToArray();
        var proposals = new List<GameUiCandidate>(MaxYoloCandidates + MaxLocalCandidates);
        var retainedYolo = new List<(LocalYoloUiProposal Candidate, Rectangle Box)>(MaxYoloCandidates);
        foreach (var candidate in yolo.Detections.OrderByDescending(candidate => candidate.Score))
        {
            var box = new Rectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height);
            if (retainedYolo.Any(existing => IntersectionOverUnion(box, existing.Box) > YoloNmsThreshold)
                || calibratedBoxes.Any(existing => IntersectionOverUnion(box, existing) > YoloNmsThreshold)) continue;
            retainedYolo.Add((candidate, box));
            if (retainedYolo.Count == MaxYoloCandidates) break;
        }

        var overlappingTextByKey = retainedYolo.ToDictionary(
            item => item.Candidate.Key,
            item => ocrText
                .Select(text =>
                {
                    var relation = RelateOcr(item.Box, text.Box, imageWidth);
                    double textArea = (double)text.Box.Width * text.Box.Height;
                    return (text.Text, text.Box, textArea, relation.OverlapFraction);
                })
                .Where(text => text.OverlapFraction >= 0.6)
                .OrderByDescending(text => text.OverlapFraction)
                .ThenBy(text => text.textArea)
                .ThenBy(text => text.Box.Y).ThenBy(text => text.Box.X)
                .Select(text => text.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray(),
            StringComparer.Ordinal);
        var nearbyTextByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var assignedNearbyText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in ocrText
            .OrderBy(text => text.Box.Width * (double)text.Box.Height)
            .ThenBy(text => text.Box.Y).ThenBy(text => text.Box.X))
        {
            if (assignedNearbyText.Contains(text.Text)) continue;
            var nearest = retainedYolo
                .Where(item => overlappingTextByKey[item.Candidate.Key].Length == 0
                    && !nearbyTextByKey.ContainsKey(item.Candidate.Key))
                .Select(item =>
                {
                    var relation = RelateOcr(item.Box, text.Box, imageWidth);
                    double centerDistance = Math.Abs(item.Box.Left + item.Box.Width / 2d - text.Box.Left - text.Box.Width / 2d);
                    return (item.Candidate, relation, centerDistance);
                })
                .Where(item => item.relation.SameRow)
                .OrderBy(item => item.relation.HorizontalGap)
                .ThenBy(item => item.relation.VerticalDistance)
                .ThenBy(item => item.centerDistance)
                .ThenByDescending(item => item.Candidate.Score)
                .FirstOrDefault();
            if (nearest.Candidate is null) continue;
            nearbyTextByKey.Add(nearest.Candidate.Key, text.Text);
            assignedNearbyText.Add(text.Text);
        }

        foreach (var (candidate, box) in retainedYolo)
        {
            var overlappingText = overlappingTextByKey[candidate.Key];
            nearbyTextByKey.TryGetValue(candidate.Key, out string? nearbyText);
            string label = candidate.Label;
            string identitySource = "omniparser_icon_detect_v3_yolov9e";
            if (overlappingText.Length > 0)
                label += "; overlapping unverified OCR text: " + string.Join(" | ", overlappingText);
            if (nearbyText is not null)
                label += "; nearby unverified OCR text: " + nearbyText;
            if (overlappingText.Length > 0 || nearbyText is not null)
                identitySource += "+windows_media_ocr";
            string key = "yolo:" + candidate.Key;
            proposals.Add(ToObservationCandidate(frameId, key, candidate.Role, label, box, identitySource));
        }

        foreach (var candidate in local.Candidates
            .OrderBy(candidate => candidate.Method == "bloons_calibrated_pixel_anchor" ? 0 : 1)
            .ThenByDescending(candidate => candidate.ProposalScore)
            .ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.X)
            .Take(MaxLocalCandidates))
        {
            string key = "local:" + candidate.Key;
            proposals.Add(ToObservationCandidate(frameId, key, candidate.Role, candidate.Label,
                ToImageBox(candidate), candidate.Method));
        }

        int calibratedCount = local.Candidates.Count(candidate => candidate.Method == "bloons_calibrated_pixel_anchor");
        string issue = $"Local YOLOv9-E plus {local.Method} produced {retainedYolo.Count} model and {proposals.Count - retainedYolo.Count} OCR/edge/template proposals. "
            + "This bounded proposal set may omit visible targets and can include non-interactive regions; semantic identity, clickability, enabled state, and exhaustive coverage are unverified.";
        if (calibratedCount > 0)
            issue += $" It includes {calibratedCount} anchors from a named calibrated layout; layout coverage remains unverified for this frame.";
        if (!string.IsNullOrWhiteSpace(local.CandidateSetIssue))
            issue += " " + local.CandidateSetIssue[..Math.Min(local.CandidateSetIssue.Length, 300)];
        if (yolo.DetectionsTruncated)
            issue += $" The worker returned only its top {yolo.Detections.Count} of {yolo.DetectedCount} model detections.";
        if (!string.IsNullOrWhiteSpace(error)) issue += " " + error[..Math.Min(error.Length, 300)];
        return new GameUiObservation(
            frameId, capturedAt, targetLifetime, frameWidth, frameHeight, imageWidth, imageHeight,
            imageScale, dpiAtCapture, windowRect, LayoutId,
            "Visible UI-like regions proposed by a local YOLO detector and local OCR/edge heuristics, with optional named-layout anchors; descriptions are unverified.",
            false, issue, local.ObservedText, proposals, [], CandidateSetCompletenessSource,
            CandidateGenerator: $"local_persistent_omniparser_icon_detect_v3_yolov9e+ShadowUse.FastUiCandidateDetector:{local.Method}");
    }

    private static (double OverlapFraction, int HorizontalGap, double VerticalDistance, bool SameRow) RelateOcr(
        Rectangle controlBox, Rectangle textBox, int imageWidth)
    {
        var overlap = Rectangle.Intersect(controlBox, textBox);
        double textArea = (double)textBox.Width * textBox.Height;
        double overlapFraction = textArea > 0 ? overlap.Width * (double)overlap.Height / textArea : 0;
        int horizontalGap = textBox.Right < controlBox.Left ? controlBox.Left - textBox.Right
            : controlBox.Right < textBox.Left ? textBox.Left - controlBox.Right : 0;
        double verticalDistance = Math.Abs(textBox.Top + textBox.Height / 2d - controlBox.Top - controlBox.Height / 2d);
        double rowTolerance = Math.Min(48, Math.Max(24, Math.Max(controlBox.Height, textBox.Height) * 0.5));
        bool textAtOrLeftOfControl = textBox.Left + textBox.Width / 2d <= controlBox.Left + controlBox.Width / 2d
            || horizontalGap <= 32;
        bool similarSizedControl = controlBox.Width * (double)controlBox.Height <= 4 * textArea;
        bool sameRow = controlBox.Height <= 160 && textBox.Height <= 80 && similarSizedControl && textAtOrLeftOfControl
            && verticalDistance <= rowTolerance && horizontalGap <= Math.Max(80, imageWidth * 0.5);
        return (overlapFraction, horizontalGap, verticalDistance, sameRow);
    }

    private static GameUiCandidate ToObservationCandidate(
        string frameId, string key, string role, string label, Rectangle box, string identitySource)
    {
        string safeKey = key[..Math.Min(key.Length, 80)];
        return new GameUiCandidate(
            $"{frameId}:local:{safeKey}", safeKey, role[..Math.Min(role.Length, 80)], label[..Math.Min(label.Length, 240)],
            box.X, box.Y, box.Width, box.Height,
            box.X + box.Width / 2, box.Y + box.Height / 2,
            PixelAnchorDetected: false, EvidenceFraction: 0, EvidenceMetric: "not_computed",
            Actionability: "unverified_visual_candidate", IdentityStatus: "unverified_visual_description",
            IdentitySource: identitySource[..Math.Min(identitySource.Length, 100)]);
    }

    private static double IntersectionOverUnion(Rectangle first, Rectangle second)
    {
        var intersection = Rectangle.Intersect(first, second);
        if (intersection.Width <= 0 || intersection.Height <= 0) return 0;
        double intersectionArea = (double)intersection.Width * intersection.Height;
        double unionArea = (double)first.Width * first.Height + (double)second.Width * second.Height - intersectionArea;
        return unionArea <= 0 ? 0 : intersectionArea / unionArea;
    }
}
