// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;

namespace ShadowUse.Vision;

public sealed record ExternalGameVisualCandidateSet(
    string Scope,
    bool CandidateSetComplete,
    string? CandidateSetIssue,
    string? CoverageEvidence,
    IReadOnlyList<string> ObservedText,
    IReadOnlyList<GameUiCandidate> Candidates,
    IReadOnlyList<GameUiContextRegion> ContextRegions);

/// <summary>Validates a frame-bound candidate inventory from any upstream screenshot parser.</summary>
public static class ExternalGameVisualCandidateSetParser
{
    public const int MaxJsonLength = 64 * 1024;
    public const int MaxCandidates = 255;
    public const int MaxContextRegions = 32;

    public static bool TryParse(
        string json,
        string observationId,
        int expectedImageWidth,
        int expectedImageHeight,
        out ExternalGameVisualCandidateSet? candidateSet,
        out string? error)
    {
        candidateSet = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
        {
            error = $"candidate_set_json must be non-empty and at most {MaxJsonLength} characters.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryString(root, "frame_id", out var frameId)
                || frameId != observationId
                || !TryInt(root, "image_width", out var imageWidth)
                || imageWidth != expectedImageWidth
                || !TryInt(root, "image_height", out var imageHeight)
                || imageHeight != expectedImageHeight
                || !TryString(root, "scope", out var scope)
                || scope.Length > 500
                || !root.TryGetProperty("candidate_set_complete", out var completeElement)
                || completeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("candidates", out var candidatesElement)
                || candidatesElement.ValueKind != JsonValueKind.Array
                || candidatesElement.GetArrayLength() > MaxCandidates)
            {
                error = "candidate_set_json needs the matching frame_id, exact image_width/image_height, a scope, candidate_set_complete, and 0-255 candidates.";
                return false;
            }

            var observedText = ReadStringArray(root, "observed_text", maxItems: 40, maxLength: 250, out error);
            if (observedText is null) return false;
            var candidates = new List<GameUiCandidate>(candidatesElement.GetArrayLength());
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in candidatesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryString(item, "key", out var key)
                    || key.Length > 80
                    || !keys.Add(key)
                    || !TryString(item, "role", out var role)
                    || role.Length > 80
                    || !TryString(item, "label", out var label)
                    || label.Length > 240
                    || !item.TryGetProperty("bbox", out var bbox)
                    || bbox.ValueKind != JsonValueKind.Object
                    || !TryInt(bbox, "x", out var x)
                    || !TryInt(bbox, "y", out var y)
                    || !TryInt(bbox, "width", out var width)
                    || !TryInt(bbox, "height", out var height)
                    || width <= 0 || height <= 0 || x < 0 || y < 0
                    || (long)x + width > expectedImageWidth
                    || (long)y + height > expectedImageHeight)
                {
                    error = "Each candidate needs a unique key, role, label, and positive image-space bbox fully inside the screenshot.";
                    return false;
                }

                string identitySource = TryString(item, "identity_source", out var source)
                    ? source[..Math.Min(source.Length, 100)]
                    : "upstream_visual_parser_unverified";
                candidates.Add(new GameUiCandidate(
                    $"{observationId}:visual:{key}", key, role, label,
                    x, y, width, height,
                    x + width / 2, y + height / 2,
                    PixelAnchorDetected: false,
                    EvidenceFraction: 0,
                    EvidenceMetric: "upstream_bbox_not_locally_verified",
                    Actionability: "unverified",
                    IdentityStatus: "caller_supplied_unverified",
                    IdentitySource: identitySource));
            }

            var contexts = new List<GameUiContextRegion>();
            if (root.TryGetProperty("context_regions", out var contextElement))
            {
                if (contextElement.ValueKind != JsonValueKind.Array || contextElement.GetArrayLength() > MaxContextRegions)
                {
                    error = $"context_regions must be an array of at most {MaxContextRegions} items.";
                    return false;
                }
                foreach (var item in contextElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !TryString(item, "key", out var key)
                        || key.Length > 80
                        || !TryString(item, "label", out var label)
                        || label.Length > 200
                        || !item.TryGetProperty("bbox", out var bbox)
                        || bbox.ValueKind != JsonValueKind.Object
                        || !TryInt(bbox, "x", out var x)
                        || !TryInt(bbox, "y", out var y)
                        || !TryInt(bbox, "width", out var width)
                        || !TryInt(bbox, "height", out var height)
                        || width <= 0 || height <= 0 || x < 0 || y < 0
                        || (long)x + width > expectedImageWidth
                        || (long)y + height > expectedImageHeight)
                    {
                        error = "Each context region needs a key, label, and positive image-space bbox fully inside the screenshot.";
                        return false;
                    }
                    string note = TryString(item, "note", out var itemNote) ? itemNote[..Math.Min(itemNote.Length, 240)] : "Upstream-reported context region; actionability is unknown.";
                    contexts.Add(new GameUiContextRegion(key, label, x, y, width, height, note));
                }
            }

            string? issue = TryString(root, "candidate_set_issue", out var candidateIssue)
                ? candidateIssue[..Math.Min(candidateIssue.Length, 500)]
                : null;
            string? coverageEvidence = TryString(root, "coverage_evidence", out var evidence)
                ? evidence[..Math.Min(evidence.Length, 500)]
                : null;
            if (!completeElement.GetBoolean() && issue is null)
            {
                error = "Incomplete candidate sets must include a non-empty candidate_set_issue.";
                return false;
            }
            if (completeElement.GetBoolean() && coverageEvidence is null)
            {
                error = "A complete-coverage claim must include a non-empty coverage_evidence note; DCU will preserve it as unverified caller evidence.";
                return false;
            }
            candidateSet = new ExternalGameVisualCandidateSet(scope, completeElement.GetBoolean(), issue,
                coverageEvidence, observedText, candidates, contexts);
            return true;
        }
        catch (JsonException ex)
        {
            error = "candidate_set_json is invalid JSON: " + ex.Message;
            return false;
        }
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement obj, string name, int maxItems, int maxLength, out string? error)
    {
        error = null;
        if (!obj.TryGetProperty(name, out var property)) return [];
        if (property.ValueKind != JsonValueKind.Array || property.GetArrayLength() > maxItems)
        {
            error = $"{name} must be an array of at most {maxItems} strings.";
            return null;
        }
        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"Each {name} item must be a string of at most {maxLength} characters.";
                return null;
            }
            string value = item.GetString() ?? "";
            if (value.Length > maxLength)
            {
                error = $"Each {name} item must be a string of at most {maxLength} characters.";
                return null;
            }
            result.Add(value);
        }
        return result;
    }
}
