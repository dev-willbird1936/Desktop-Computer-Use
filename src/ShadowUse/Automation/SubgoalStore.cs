// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum SubgoalStatus { Active, Escalated, Completed }

/// <summary>F23-R01/R02: a subgoal is an explicit, typed unit of planner intent -- required_data
/// (WorkflowValueStore names that must be set first) and an optional postcondition (same shape as
/// wait_for) -- kept separate from low-level execution (nodes/clicks dispatch through the ordinary
/// tool surface, entirely unaware of any subgoal). Objective is set once at Define and never has a
/// code path to change it afterward, on either the original record or any revision (F23-T02).</summary>
public sealed class SubgoalRecord
{
    public required string SubgoalId { get; init; }
    public required string Objective { get; init; }
    public required IReadOnlyList<string> RequiredData { get; init; }
    public string? PostconditionApp { get; init; }
    public string? PostconditionCondition { get; init; }
    public string? PostconditionText { get; init; }
    public SubgoalStatus Status { get; set; } = SubgoalStatus.Active;
    public string? EscalationReason { get; set; }
    public string? RevisedFrom { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>F23-T02: escalating an Active subgoal never mutates it or its Objective -- it marks
/// the current record terminal (Escalated, permanently) and creates a brand new record carrying
/// the identical Objective forward. required_data/postcondition MAY be revised on the new record
/// (that is the point of replanning); Objective structurally cannot be, since no method on this
/// store accepts an Objective override anywhere except the original Define call.</summary>
public sealed class SubgoalStore
{
    private readonly ConcurrentDictionary<string, SubgoalRecord> _subgoals = new();
    private readonly ConcurrentDictionary<string, int> _revisionCounters = new();

    public SubgoalRecord Define(string subgoalId, string objective, IReadOnlyList<string> requiredData,
        string? postconditionApp, string? postconditionCondition, string? postconditionText)
    {
        if (string.IsNullOrWhiteSpace(subgoalId))
            throw new ArgumentException("subgoal_id is required.");
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("objective is required.");
        if (_subgoals.ContainsKey(subgoalId))
            throw new ArgumentException($"Subgoal '{subgoalId}' already exists.");
        if ((postconditionApp is null) != (postconditionCondition is null))
            throw new ArgumentException("postcondition_app and postcondition_condition must be supplied together, or not at all.");
        var record = new SubgoalRecord
        {
            SubgoalId = subgoalId, Objective = objective, RequiredData = requiredData,
            PostconditionApp = postconditionApp, PostconditionCondition = postconditionCondition, PostconditionText = postconditionText,
            RevisedFrom = null, CreatedAt = DateTimeOffset.UtcNow,
        };
        _subgoals[subgoalId] = record;
        return record;
    }

    public SubgoalRecord? Find(string subgoalId) => _subgoals.GetValueOrDefault(subgoalId);

    /// <summary>F23-R02: replan on an explicit blocker, repeated no-progress state, or a failed
    /// completion check -- the caller asserts which (reason), driven by signals it already
    /// observes (a Failed WorkflowRunner node, a should_stop LoopGuard verdict, or a false
    /// check_subgoal_postcondition result); this store does not re-derive those signals itself.</summary>
    public SubgoalRecord Escalate(string subgoalId, string reason,
        IReadOnlyList<string>? newRequiredData, string? newPostconditionApp, string? newPostconditionCondition, string? newPostconditionText)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("escalate_subgoal requires a non-empty reason.");
        var current = _subgoals.GetValueOrDefault(subgoalId) ?? throw new ArgumentException($"Subgoal '{subgoalId}' was not found.");
        if (current.Status != SubgoalStatus.Active)
            throw new ArgumentException($"Subgoal '{subgoalId}' is not Active (currently {current.Status}); only an active subgoal can be escalated.");
        current.Status = SubgoalStatus.Escalated;
        current.EscalationReason = reason;

        var revision = _revisionCounters.AddOrUpdate(subgoalId, 1, (_, v) => v + 1);
        var revisedId = $"{subgoalId}#r{revision}";
        var revised = new SubgoalRecord
        {
            SubgoalId = revisedId,
            Objective = current.Objective,
            RequiredData = newRequiredData ?? current.RequiredData,
            PostconditionApp = newPostconditionApp ?? current.PostconditionApp,
            PostconditionCondition = newPostconditionCondition ?? current.PostconditionCondition,
            PostconditionText = newPostconditionText ?? current.PostconditionText,
            RevisedFrom = subgoalId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _subgoals[revisedId] = revised;
        return revised;
    }

    public void Complete(string subgoalId)
    {
        var record = _subgoals.GetValueOrDefault(subgoalId) ?? throw new ArgumentException($"Subgoal '{subgoalId}' was not found.");
        if (record.Status != SubgoalStatus.Active)
            throw new ArgumentException($"Subgoal '{subgoalId}' is not Active (currently {record.Status}).");
        record.Status = SubgoalStatus.Completed;
    }

    public (bool Ready, IReadOnlyList<string> Missing) CheckReadiness(string subgoalId, WorkflowValueStore values)
    {
        var record = _subgoals.GetValueOrDefault(subgoalId) ?? throw new ArgumentException($"Subgoal '{subgoalId}' was not found.");
        var missing = record.RequiredData.Where(name => !values.TryGet(name, out _)).ToArray();
        return (missing.Length == 0, missing);
    }
}
