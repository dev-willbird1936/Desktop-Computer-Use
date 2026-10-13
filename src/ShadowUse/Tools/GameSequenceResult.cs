// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShadowUse.Tools;

/// <summary>
/// Shrinks game execute_sequence results without losing information. Null fields and zero timing entries are dropped, the
/// duplicated step name is removed, and window-level metadata that is identical on every step that reports it is reported once
/// under <c>shared</c>. Interruptions, partial-input details, frame ids and per-step image counts always stay on their step.
/// </summary>
public static class GameSequenceResult
{
    /// <summary>Metadata that describes the target window rather than one action. Everything else stays on its step.</summary>
    private static readonly string[] SharedKeys =
    [
        "target_lifetime", "target_lifetime_verified", "input_ownership", "image_scale", "pace", "frame_dimensions", "geometry_stale", "interrupted",
    ];

    public static (JsonObject Shared, JsonObject[] Steps) Compact(IReadOnlyList<(string Tool, JsonElement Payload)> steps)
    {
        var compacted = steps.Select(s => Strip(JsonNode.Parse(s.Payload.GetRawText()) as JsonObject ?? [], s.Tool)).ToArray();
        var shared = new JsonObject();
        foreach (var key in SharedKeys)
        {
            var carriers = compacted.Where(step => step.ContainsKey(key)).ToArray();
            if (carriers.Length < 2) continue;
            var first = carriers[0][key];
            if (!carriers.All(step => JsonNode.DeepEquals(step[key], first))) continue;
            shared[key] = first?.DeepClone();
            foreach (var step in carriers) step.Remove(key);
        }
        return (shared, compacted);
    }

    private static JsonObject Strip(JsonObject payload, string tool)
    {
        StripNulls(payload);
        if (payload["tool"]?.GetValue<string>() == tool) payload.Remove("tool");
        if (payload["timing"] is JsonObject timing)
            foreach (var zero in timing.Where(p => p.Value is JsonValue v && v.TryGetValue<long>(out var n) && n == 0).Select(p => p.Key).ToArray())
                timing.Remove(zero);
        return payload;
    }

    private static void StripNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToArray()) obj.Remove(key);
                foreach (var value in obj.Select(p => p.Value)) StripNulls(value);
                break;
            case JsonArray array:
                foreach (var item in array) StripNulls(item);
                break;
        }
    }
}
