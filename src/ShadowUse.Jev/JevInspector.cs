// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Jev;

/// <summary>One recorded Jev decision, whether or not it was executed (F49).</summary>
public sealed record JevInspectorEntry(
    string Surface,
    string TargetId,
    string Goal,
    string Operation,
    string? TargetElementId,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities,
    bool? GoalReached,
    double? GoalReachedProbability,
    string DecisionSource,
    bool Executed,
    long LatencyMs,
    string Model,
    DateTimeOffset At,
    double? TargetConfidence = null,
    IReadOnlyDictionary<string, double>? OperationProbabilities = null,
    bool DisambiguatedByScore = false,
    double? TopChoiceMargin = null);

/// <summary>
/// Bounded, non-activating, read-only decision history (F49). Never influences a
/// decision or execution — jev_decide/jev_act keep working identically whether or
/// not anything ever calls the inspector tool.
/// </summary>
public sealed class JevInspector
{
    private const int MaxEntries = 50;
    private readonly ConcurrentQueue<JevInspectorEntry> _entries = new();

    public void Record(JevInspectorEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries)
            _entries.TryDequeue(out _);
    }

    public IReadOnlyList<JevInspectorEntry> Recent(int count) => [.. _entries.Reverse().Take(Math.Max(0, count))];
}
