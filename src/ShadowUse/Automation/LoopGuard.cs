// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>Verdict of one LoopGuard.Record call. Purely diagnostic: DCU never refuses
/// to execute an action because of this; a caller (human, script, or Jev's fast loop)
/// decides whether to reobserve, change route, replan, or stop (F28-R02).</summary>
public readonly record struct LoopGuardVerdict(int NoProgressCount, bool ShouldStop, string? Diagnosis);

/// <summary>
/// Detects repeated no-progress action fingerprints per (app, target) scope (F28-R02).
/// Tracks only a rolling last-fingerprint/count pair per scope — bounded memory, no
/// history retained. Any observed progress (a genuinely different outcome, or a
/// different action) resets the counter for that scope immediately.
/// </summary>
public sealed class LoopGuard
{
    public const int DefaultNoProgressThreshold = 3;

    private readonly ConcurrentDictionary<string, (string Fingerprint, int Count)> _state = new(StringComparer.Ordinal);
    private readonly int _threshold;

    public LoopGuard(int threshold = DefaultNoProgressThreshold)
    {
        if (threshold < 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Threshold must be at least 1.");
        _threshold = threshold;
    }

    /// <param name="scope">Identifies the target being repeatedly acted on, e.g. "windowId|elementId".</param>
    /// <param name="actionFingerprint">Identifies the specific action attempted, e.g. "click" or a hash of the value being set.</param>
    /// <param name="progressed">True if this attempt had an observably different effect than doing nothing.</param>
    public LoopGuardVerdict Record(string scope, string actionFingerprint, bool progressed)
    {
        if (progressed)
        {
            _state.TryRemove(scope, out _);
            return new LoopGuardVerdict(0, false, null);
        }

        var updated = _state.AddOrUpdate(
            scope,
            _ => (actionFingerprint, 1),
            (_, existing) => existing.Fingerprint == actionFingerprint
                ? (existing.Fingerprint, existing.Count + 1)
                : (actionFingerprint, 1));

        bool shouldStop = updated.Count >= _threshold;
        string? diagnosis = shouldStop
            ? $"No observable effect from the same action {updated.Count} times in a row. This is a no-progress cycle -- reobserve, choose a different target, or stop; retrying the identical action again is unlikely to help."
            : null;
        return new LoopGuardVerdict(updated.Count, shouldStop, diagnosis);
    }

    /// <summary>Clears tracking for a scope, e.g. when the caller explicitly re-observes.</summary>
    public void Reset(string scope) => _state.TryRemove(scope, out _);
}
