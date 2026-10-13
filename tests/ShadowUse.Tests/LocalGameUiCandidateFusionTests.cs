using System.Text.Json;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class LocalGameUiCandidateFusionTests
{
    [Fact]
    public void FusesFrameBoundDetectionsWithIncompleteProvenanceAndHardLimit()
    {
        var detections = Enumerable.Range(0, 230).Select(index => new LocalYoloUiProposal(
            $"interactive-region-{index + 1:D3}", "interactive_region", "possible interactive region; identity unknown",
            index % 20 * 40, index / 20 * 30, 20, 20, 0.99 - index * 0.001)).ToList();
        detections.Insert(1, detections[0] with { Key = "overlap-duplicate", Score = 0.5 });
        var yolo = new LocalYoloDetectionResult(1000, 600, detections, detections.Count, false, 240);
        var edgeCandidates = Enumerable.Range(0, 30).Select(index => new FastUiCandidate(
            $"edge-{index + 1:D3}", "possible_visual_region", "outlined control-like region",
            120 + index * 30, 90, 24, 18, "fast_edge_component", 0.7 - index * 0.01)).ToArray();
        var localCandidates = edgeCandidates.Append(new FastUiCandidate(
            "ocr-001", "text_region", "Ready!", 4, 4, 10, 6, "windows_media_ocr", 0.65)).ToArray();
        var local = new FastUiDetectionResult("combined", false, "OCR and edge proposals are incomplete.",
            ["Ready", "Cancel"], localCandidates, 45);
        var rect = new NativeMethods.RECT { Left = 50, Top = 100, Right = 1250, Bottom = 820 };

        var observation = LocalGameUiCandidateFusion.CreateObservation(
            "frame-fused", DateTimeOffset.UtcNow, "target-1", 1200, 720, 1000, 600,
            1000 / 1200.0, 96, rect, yolo, local);

        Assert.False(observation.CandidateSetComplete);
        Assert.Equal(LocalGameUiCandidateFusion.LayoutId, observation.LayoutId);
        Assert.Equal(LocalGameUiCandidateFusion.CandidateSetCompletenessSource, observation.CandidateSetCompletenessSource);
        Assert.Equal(187, observation.Candidates.Count);
        Assert.Equal(["Ready", "Cancel"], observation.ObservedText);
        Assert.Contains("160 model", observation.CandidateSetIssue);
        Assert.Contains("27 OCR/edge", observation.CandidateSetIssue);
        Assert.All(observation.Candidates, candidate => Assert.False(candidate.PixelAnchorDetected));

        var firstYolo = observation.Candidates[0];
        Assert.Equal("yolo:interactive-region-001", firstYolo.Key);
        Assert.Equal(0, firstYolo.X);
        Assert.Equal("unverified_visual_candidate", firstYolo.Actionability);
        Assert.Equal("omniparser_icon_detect_v3_yolov9e+windows_media_ocr", firstYolo.IdentitySource);
        Assert.Contains("overlapping unverified OCR text: Ready!", firstYolo.Label);
        Assert.Equal("not_computed", firstYolo.EvidenceMetric);

        var firstLocal = observation.Candidates[160];
        Assert.Equal("local:edge-001", firstLocal.Key);
        Assert.Equal(100, firstLocal.X);
        Assert.Equal(75, firstLocal.Y);
        Assert.Equal(20, firstLocal.Width);
        Assert.Equal(15, firstLocal.Height);

        var mapped = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.LocalYoloVisualSource);
        Assert.Equal(187, mapped.Count);
        var metadata = Assert.IsType<Dictionary<string, object?>>(mapped[0].Extra);
        Assert.Equal(GameJevCandidateMapper.LocalYoloVisualSource, metadata["source"]);
        Assert.Equal("local_heuristic_incomplete", metadata["coverage_status"]);
        using var screenBox = JsonDocument.Parse(JsonSerializer.Serialize(metadata["screen_bbox"]));
        Assert.Equal(50, screenBox.RootElement.GetProperty("x").GetInt32());
        Assert.Equal(100, screenBox.RootElement.GetProperty("y").GetInt32());
    }

    [Fact]
    public void AttachesAdjacentSameRowOcrAsAnUnverifiedClue()
    {
        var yolo = new LocalYoloDetectionResult(1280, 400,
        [
            new("toggle", "interactive_region", "possible interactive region; semantic identity unknown", 600, 100, 50, 44, 0.9),
            new("wide-panel", "interactive_region", "possible interactive region; semantic identity unknown", 600, 100, 300, 44, 0.8),
            new("left-item", "interactive_region", "possible interactive region; semantic identity unknown", 10, 100, 50, 44, 0.7),
            new("far-right", "interactive_region", "possible interactive region; semantic identity unknown", 900, 100, 50, 44, 0.6),
        ], 4, false, 10);
        var local = new FastUiDetectionResult("combined", false, "OCR remains unverified.", ["Automatic updates"],
        [new("ocr-auto-updates", "text_region", "Automatic updates", 100, 105, 160, 20, "windows_media_ocr", 0.9)], 20);
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 400 };

        var observation = LocalGameUiCandidateFusion.CreateObservation(
            "frame-row-text", DateTimeOffset.UtcNow, "target-1", 1280, 400, 1280, 400,
            1, 96, rect, yolo, local);

        var control = Assert.Single(observation.Candidates, candidate => candidate.Key == "yolo:toggle");
        Assert.Contains("nearby unverified OCR text: Automatic updates", control.Label);
        Assert.Equal("omniparser_icon_detect_v3_yolov9e+windows_media_ocr", control.IdentitySource);
        Assert.Equal("unverified_visual_candidate", control.Actionability);
        var broadRegion = Assert.Single(observation.Candidates, candidate => candidate.Key == "yolo:wide-panel");
        Assert.DoesNotContain("Automatic updates", broadRegion.Label);
        var unrelated = Assert.Single(observation.Candidates, candidate => candidate.Key == "yolo:left-item");
        Assert.DoesNotContain("Automatic updates", unrelated.Label);
        var fartherControl = Assert.Single(observation.Candidates, candidate => candidate.Key == "yolo:far-right");
        Assert.DoesNotContain("Automatic updates", fartherControl.Label);
    }

    [Fact]
    public void PrioritizesNamedLayoutAnchorsAndSuppressesOverlappingYoloBoxes()
    {
        var yolo = new LocalYoloDetectionResult(100, 100,
        [
            new("template-duplicate", "interactive_region", "generic region", 10, 10, 20, 20, 0.99),
            new("independent", "interactive_region", "generic region", 70, 70, 10, 10, 0.8),
        ], 2, false, 25);
        var calibrated = Enumerable.Range(0, 23).Select(index => new FastUiCandidate(
            $"bloons-anchor-{index + 1:D2}", "button", $"calibrated control {index + 1}",
            index == 0 ? 10 : 1 + index % 6 * 10,
            index == 0 ? 10 : 1 + index / 6 * 10,
            index == 0 ? 20 : 4,
            index == 0 ? 20 : 4,
            "bloons_calibrated_pixel_anchor", 0.5)).ToArray();
        var edges = Enumerable.Range(0, 30).Select(index => new FastUiCandidate(
            $"edge-{index + 1:D3}", "possible_visual_region", "edge region",
            index % 10 * 9, index / 10 * 10, 5, 5, "fast_edge_component", 0.99));
        var local = new FastUiDetectionResult("combined+calibrated-bloons-layout", false,
            "The named layout has an unsupported aspect ratio; its anchors remain partial evidence.",
            [], calibrated.Concat(edges).ToArray(), 60);
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 };

        var observation = LocalGameUiCandidateFusion.CreateObservation(
            "frame-template", DateTimeOffset.UtcNow, "target-1", 100, 100, 100, 100,
            1, 96, rect, yolo, local);

        Assert.False(observation.CandidateSetComplete);
        Assert.Equal(28, observation.Candidates.Count);
        Assert.DoesNotContain(observation.Candidates, candidate => candidate.Key == "yolo:template-duplicate");
        Assert.Contains(observation.Candidates, candidate => candidate.Key == "yolo:independent");
        Assert.Equal(23, observation.Candidates.Count(candidate => candidate.Key.StartsWith("local:bloons-anchor-", StringComparison.Ordinal)));
        Assert.Contains("named calibrated layout", observation.CandidateSetIssue);
        Assert.Contains("coverage remains unverified", observation.CandidateSetIssue);
    }

    [Fact]
    public void RejectsYoloCoordinatesFromDifferentFrameDimensions()
    {
        var yolo = new LocalYoloDetectionResult(999, 600, [], 0, false, 10);
        var local = new FastUiDetectionResult("combined", false, "incomplete", [], [], 10);
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1200, Bottom = 720 };

        Assert.Throws<InvalidDataException>(() => LocalGameUiCandidateFusion.CreateObservation(
            "frame-fused", DateTimeOffset.UtcNow, "target-1", 1200, 720, 1000, 600,
            1000 / 1200.0, 96, rect, yolo, local));
    }
}
