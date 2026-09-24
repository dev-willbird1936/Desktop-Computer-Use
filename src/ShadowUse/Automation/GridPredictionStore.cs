// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadowUse.Automation;

/// <summary>F57-R01: rule-based heading-to-schema classifier. Deterministic, no model call --
/// a heading/sample pair either matches a recognizable pattern or falls back to "text".</summary>
public static class GridSchemaPreview
{
    private static readonly Regex NumberPattern = new(@"^-?\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex DatePattern = new(@"^\d{4}-\d{2}-\d{2}$|^\d{1,2}/\d{1,2}/\d{2,4}$", RegexOptions.Compiled);
    private static readonly string[] BooleanValues = ["true", "false", "yes", "no", "y", "n"];

    public static string InferType(string heading, string? sample)
    {
        var lower = heading.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(sample))
        {
            var trimmed = sample.Trim();
            if (DatePattern.IsMatch(trimmed)) return "date";
            if (EmailPattern.IsMatch(trimmed)) return "email";
            if (NumberPattern.IsMatch(trimmed)) return "number";
            if (BooleanValues.Contains(trimmed.ToLowerInvariant())) return "boolean";
        }
        if (lower.Contains("date") || lower.Contains("due") || lower.Contains("created") || lower.Contains("updated")) return "date";
        if (lower.Contains("email")) return "email";
        if (lower.Contains("amount") || lower.Contains("price") || lower.Contains("cost") || lower.Contains("total") || lower.Contains("qty") || lower.Contains("quantity") || lower.Contains("count")) return "number";
        if (lower.StartsWith("is_") || lower.StartsWith("has_") || lower.Contains("active") || lower.Contains("enabled")) return "boolean";
        return "text";
    }
}

public sealed record GridCellKey(string ContextKey, string RowId, string Column);
public sealed record GridCellState(string? Value, int EditVersion);
public sealed record GridPredictionKey(string ContentHash, string RubricKey, string ModelId);
public sealed record CachedGridPrediction(string Value, DateTimeOffset CachedAt);

/// <summary>F57-R01/R02: a pure store backing an optional predictive-grid experience.
/// DCU never generates cell values itself (same no-live-model-key constraint as F24/F27/F46/F47/F55)
/// -- the caller supplies a predicted value from its own configured generator, keyed by
/// (content_hash, rubric, model) so identical row inputs never trigger a redundant request
/// (F57-T02 "cache hits do not trigger unnecessary API requests"). Every cell carries an
/// edit_version bumped on each manual edit; applying a prediction captured against a stale
/// version is refused rather than silently overwriting a newer user edit (F57-T01). Applying
/// a prediction records the pre-run value under its batch id so Undo can restore the exact
/// prior workbook state for that local operation (F57-T02); Keep simply discards that undo
/// record once the caller accepts the result.</summary>
public sealed class GridPredictionStore
{
    private readonly ConcurrentDictionary<GridCellKey, GridCellState> _cells = new();
    private readonly ConcurrentDictionary<GridPredictionKey, CachedGridPrediction> _predictionCache = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<GridCellKey, GridCellState>> _undoSnapshots = new();

    public static string HashContent(string input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];

    public GridCellState GetCell(GridCellKey key) => _cells.TryGetValue(key, out var state) ? state : new GridCellState(null, 0);

    /// <summary>Records a direct user edit, bumping edit_version so any in-flight prediction
    /// captured against the prior version is naturally rejected as stale on arrival.</summary>
    public int RecordEdit(GridCellKey key, string? value) =>
        _cells.AddOrUpdate(key, _ => new GridCellState(value, 1), (_, prev) => new GridCellState(value, prev.EditVersion + 1)).EditVersion;

    public bool TryGetCachedPrediction(GridPredictionKey key, out CachedGridPrediction cached) => _predictionCache.TryGetValue(key, out cached!);

    public void RememberPrediction(GridPredictionKey key, string value) => _predictionCache[key] = new CachedGridPrediction(value, DateTimeOffset.UtcNow);

    /// <summary>Applies a predicted value if, and only if, the cell's edit_version still matches
    /// the version captured when inference started. Returns false (rejected as stale) otherwise.</summary>
    public bool TryApplyPrediction(GridCellKey key, int expectedEditVersion, string predictedValue, string batchId)
    {
        var current = GetCell(key);
        if (current.EditVersion != expectedEditVersion)
            return false;

        var snapshot = _undoSnapshots.GetOrAdd(batchId, _ => new ConcurrentDictionary<GridCellKey, GridCellState>());
        snapshot.TryAdd(key, current); // first-seen value for this batch is the true pre-run state

        _cells[key] = current with { Value = predictedValue, EditVersion = current.EditVersion + 1 };
        return true;
    }

    /// <summary>Restores every cell touched by batchId to its exact pre-run state (value and
    /// edit_version), then discards the snapshot so it cannot be replayed.</summary>
    public bool TryUndo(string batchId, out IReadOnlyDictionary<GridCellKey, GridCellState> restored)
    {
        if (!_undoSnapshots.TryRemove(batchId, out var snapshot))
        {
            restored = new Dictionary<GridCellKey, GridCellState>();
            return false;
        }
        foreach (var (key, state) in snapshot)
            _cells[key] = state;
        restored = snapshot;
        return true;
    }

    /// <summary>Accepts the batch's results permanently by discarding its undo snapshot.</summary>
    public bool Keep(string batchId) => _undoSnapshots.TryRemove(batchId, out _);
}
