// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum DemonstrationStatus { Draft, Admitted }

/// <summary>One step of a demonstration. TaskIntent names an ALREADY-remembered recipe (via
/// remember_action_resolution/browser_remember_action_resolution, F31) -- a demonstration compiles
/// a chosen ordered sequence of known recipes into a reusable workflow, it never captures a new
/// recipe itself. ArgsJson carries everything a dispatch needs EXCEPT the resolved element/
/// candidate id, which is injected fresh at replay time -- a demonstration never stores an id or
/// a recorded absolute screen coordinate (F30-T01).</summary>
public sealed record DemonstrationStepSpec(string App, string Tool, string TaskIntent, string ArgsJson);

public sealed class Demonstration
{
    public required string Id { get; init; }
    public int Version { get; init; } = 1;
    public DemonstrationStatus Status { get; set; } = DemonstrationStatus.Draft;
    public required IReadOnlyList<DemonstrationStepSpec> Steps { get; init; }
    public string? CompletionApp { get; init; }
    public string? CompletionCondition { get; init; }
    public string? CompletionText { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? AdmittedAt { get; set; }
}

/// <summary>F30-R01/R02: process-lifetime store of demonstrations compiled from already-remembered
/// F31 recipes. A demonstration is Draft until explicitly admitted (never auto-admitted) and, once
/// Admitted, is immutable -- there is no update operation, only Create for a new id/version, so a
/// changed step sequence or schema can never silently inherit a prior admission (F30-T02). Replay
/// itself lives in ComputerUseTools (it needs live dispatch/resolution), this store only tracks
/// definition and lifecycle state.</summary>
public sealed class DemonstrationStore
{
    private static readonly HashSet<string> AllowedTools = ["click", "set_value", "browser_click", "browser_fill"];
    private readonly ConcurrentDictionary<string, Demonstration> _demos = new();

    public Demonstration Create(string id, IReadOnlyList<DemonstrationStepSpec> steps, string? completionApp, string? completionCondition, string? completionText)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("demonstration_id is required.");
        if (steps.Count == 0)
            throw new ArgumentException("A demonstration needs at least one step.");
        foreach (var step in steps)
        {
            if (!AllowedTools.Contains(step.Tool))
                throw new ArgumentException($"Unsupported demonstration step tool '{step.Tool}'. Only click, set_value, browser_click, browser_fill are supported -- F30 targets element/candidate-resolved steps, not passthrough tools like wait_for.");
            if (string.IsNullOrWhiteSpace(step.TaskIntent))
                throw new ArgumentException("Every step needs a task_intent naming an already-remembered recipe.");
        }
        if ((completionCondition is null) != (completionApp is null))
            throw new ArgumentException("completion_app and completion_condition must be supplied together, or not at all.");

        var demo = new Demonstration
        {
            Id = id,
            Steps = steps,
            CompletionApp = completionApp,
            CompletionCondition = completionCondition,
            CompletionText = completionText,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (!_demos.TryAdd(id, demo))
            throw new ArgumentException($"Demonstration '{id}' already exists.");
        return demo;
    }

    public Demonstration? Find(string id) => _demos.GetValueOrDefault(id);

    /// <summary>The only transition out of Draft. Never re-enterable: an already-Admitted
    /// demonstration throws rather than silently re-stamping AdmittedAt.</summary>
    public void Admit(string id)
    {
        var demo = _demos.GetValueOrDefault(id) ?? throw new ArgumentException($"Demonstration '{id}' was not found.");
        if (demo.Status == DemonstrationStatus.Admitted)
            throw new ArgumentException($"Demonstration '{id}' is already admitted. Create a new demonstration to change it.");
        demo.Status = DemonstrationStatus.Admitted;
        demo.AdmittedAt = DateTimeOffset.UtcNow;
    }
}
