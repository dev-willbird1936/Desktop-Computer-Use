// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Jev;
using ShadowUse.Vision;

namespace ShadowUse.Tools;

/// <summary>Maps calibrated game regions to Jev IDs and preserves their frame-bound coordinates.</summary>
public static class GameJevCandidateMapper
{
    public const string BloonsLayoutSource = "bloons_tdb2_calibrated_visual_layout";
    public const string UnspecifiedVisualSource = "unspecified_visual_source";
    public const string CallerVisualSource = "caller_multimodal_visual_parser";
    public const string McpClientVisualSource = "mcp_client_multimodal_visual_parser";
    public const string LocalFastVisualSource = "dcu_local_fast_heuristic_detector";
    public const string LocalYoloVisualSource = "dcu_local_yolo_ocr_edge_detector";
    public const string CachedStaticAnchorSource = "dcu_hash_verified_static_anchor_cache";

    public static IReadOnlyList<JevElement> Map(
        GameUiObservation observation,
        IReadOnlyDictionary<string, string>? visualLabels = null,
        string source = UnspecifiedVisualSource)
    {
        ArgumentNullException.ThrowIfNull(observation);
        visualLabels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return observation.Candidates.Select(candidate =>
        {
            bool hasDescription = visualLabels.TryGetValue(candidate.Key, out var visualDescription);
            string name = hasDescription ? $"{candidate.Label} — {visualDescription}" : candidate.Label;
            int screenX = observation.WindowRect.Left + (int)Math.Round(candidate.X / observation.ImageScale);
            int screenY = observation.WindowRect.Top + (int)Math.Round(candidate.Y / observation.ImageScale);
            int screenWidth = Math.Max(1, (int)Math.Round(candidate.Width / observation.ImageScale));
            int screenHeight = Math.Max(1, (int)Math.Round(candidate.Height / observation.ImageScale));
            var metadata = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["layout_id"] = observation.LayoutId,
                ["candidate_set_complete"] = observation.CandidateSetComplete,
                ["candidate_set_completeness_source"] = observation.CandidateSetCompletenessSource,
                ["processing_integrity"] = "candidate_schema_and_frame_geometry_validated",
                ["coverage_evidence"] = observation.CoverageEvidence,
                ["candidate_generator"] = observation.CandidateGenerator,
                ["coverage_status"] = source == BloonsLayoutSource
                    ? observation.CandidateSetComplete ? "validated_named_layout" : "incomplete_named_layout"
                    : source == UnspecifiedVisualSource
                        ? observation.CandidateSetComplete ? "source_unspecified_complete_unverified" : "source_unspecified_incomplete"
                    : source == McpClientVisualSource
                        ? observation.CandidateSetComplete ? "mcp_client_model_asserted_complete_unverified" : "mcp_client_model_reported_incomplete"
                        : source == CachedStaticAnchorSource
                            ? "cached_static_anchor_subset_incomplete"
                            : source is LocalFastVisualSource or LocalYoloVisualSource
                            ? "local_heuristic_incomplete"
                            : observation.CandidateSetComplete ? "caller_asserted_complete_unverified" : "incomplete_or_unknown",
                ["candidate_key"] = candidate.Key,
                ["coordinate_space"] = "screenshot_image_pixels",
                ["bbox"] = new { x = candidate.X, y = candidate.Y, width = candidate.Width, height = candidate.Height },
                ["center"] = new { x = candidate.CenterX, y = candidate.CenterY },
                ["screen_bbox"] = new { x = screenX, y = screenY, width = screenWidth, height = screenHeight },
                ["image_scale"] = observation.ImageScale,
                ["frame_dimensions"] = new { width = observation.ImageWidth, height = observation.ImageHeight },
                ["captured_frame_dimensions"] = new { width = observation.FrameWidth, height = observation.FrameHeight },
                ["frame_id"] = observation.ObservationId,
                ["captured_at_utc"] = observation.CapturedAtUtc,
                ["target_lifetime"] = observation.TargetLifetime,
                ["dpi_at_capture"] = observation.DpiAtCapture,
                ["anchor_region_hash_verified"] = observation.LayoutId == "cached-static-ui-anchors-v1",
                ["pixel_anchor_detected"] = candidate.PixelAnchorDetected,
                ["geometry_evidence"] = new { metric = candidate.EvidenceMetric, value = candidate.EvidenceFraction },
                ["actionability"] = "unverified_visual_candidate",
                ["game_enabled_state"] = "unknown",
                ["identity_status"] = hasDescription ? "unverified_visual_description" : candidate.IdentityStatus,
                ["identity_source"] = hasDescription ? "caller_visual_description" : candidate.IdentitySource ?? "none",
                ["recommendation_only"] = true,
            };
            return new JevElement(candidate.Id, candidate.Role, name, null, null,
                [JevRecommendationRequestBuilder.RecommendTargetOperation], metadata);
        }).ToArray();
    }
}
