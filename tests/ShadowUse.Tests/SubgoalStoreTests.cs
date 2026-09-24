// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F23-R01/R02 coverage: the subgoal state machine itself, independent of any live UIA
/// postcondition check (that half is exercised live in native_evolution_regression.py via the
/// existing wait_for reuse).</summary>
public sealed class SubgoalStoreTests
{
    [Fact]
    public void Define_ThenFind_RoundTrips()
    {
        var store = new SubgoalStore();
        var record = store.Define("g1", "Sign in as the fixture user", ["captured_username"], null, null, null);

        Assert.Equal(SubgoalStatus.Active, record.Status);
        Assert.Equal("Sign in as the fixture user", record.Objective);
        Assert.Same(record, store.Find("g1"));
    }

    [Fact]
    public void Define_Rejects_DuplicateId()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        Assert.Throws<ArgumentException>(() => store.Define("g1", "obj2", [], null, null, null));
    }

    [Fact]
    public void Define_Rejects_PartialPostcondition()
    {
        var store = new SubgoalStore();
        Assert.Throws<ArgumentException>(() => store.Define("g1", "obj", [], "app", null, null));
        Assert.Throws<ArgumentException>(() => store.Define("g1", "obj", [], null, "text_exists", null));
    }

    [Fact]
    public void CheckReadiness_ReportsMissingRequiredData()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", ["a", "b"], null, null, null);
        var values = new WorkflowValueStore();
        values.Set("a", "x");

        var (ready, missing) = store.CheckReadiness("g1", values);

        Assert.False(ready);
        Assert.Equal(["b"], missing);
    }

    [Fact]
    public void CheckReadiness_ReadyWhenAllRequiredDataSet()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", ["a"], null, null, null);
        var values = new WorkflowValueStore();
        values.Set("a", "x");

        var (ready, missing) = store.CheckReadiness("g1", values);

        Assert.True(ready);
        Assert.Empty(missing);
    }

    [Fact]
    public void Escalate_MarksOriginalEscalated_AndCreatesARevisionWithTheSameObjective()
    {
        // F23-T02: escalation never changes the original user objective.
        var store = new SubgoalStore();
        store.Define("g1", "Sign in as the fixture user", ["a"], "App", "text_exists", "Ready");

        var revised = store.Escalate("g1", "node_failed:submit", null, null, null, null);

        var original = store.Find("g1")!;
        Assert.Equal(SubgoalStatus.Escalated, original.Status);
        Assert.Equal("node_failed:submit", original.EscalationReason);
        Assert.Equal("g1#r1", revised.SubgoalId);
        Assert.Equal(SubgoalStatus.Active, revised.Status);
        Assert.Equal("Sign in as the fixture user", revised.Objective); // unchanged
        Assert.Equal("g1", revised.RevisedFrom);
        Assert.Equal(["a"], revised.RequiredData); // inherited, not overridden
    }

    [Fact]
    public void Escalate_CanReviseRequiredDataAndPostcondition_WithoutTouchingObjective()
    {
        var store = new SubgoalStore();
        store.Define("g1", "Sign in as the fixture user", ["a"], "App", "text_exists", "Ready");

        var revised = store.Escalate("g1", "postcondition_never_met", ["a", "b"], "App2", "window_active", null);

        Assert.Equal("Sign in as the fixture user", revised.Objective);
        Assert.Equal(["a", "b"], revised.RequiredData);
        Assert.Equal("App2", revised.PostconditionApp);
        Assert.Equal("window_active", revised.PostconditionCondition);
    }

    [Fact]
    public void Escalate_Twice_ProducesIndependentlyNumberedRevisions()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        var r1 = store.Escalate("g1", "first blocker", null, null, null, null);
        var r2 = store.Escalate(r1.SubgoalId, "second blocker", null, null, null, null);

        Assert.Equal("g1#r1", r1.SubgoalId);
        Assert.Equal("g1#r1#r1", r2.SubgoalId);
        Assert.Equal("obj", r2.Objective);
    }

    [Fact]
    public void Escalate_Rejects_AlreadyEscalatedSubgoal()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        store.Escalate("g1", "blocker", null, null, null, null);
        Assert.Throws<ArgumentException>(() => store.Escalate("g1", "again", null, null, null, null));
    }

    [Fact]
    public void Escalate_Rejects_EmptyReason()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        Assert.Throws<ArgumentException>(() => store.Escalate("g1", "", null, null, null, null));
    }

    [Fact]
    public void Complete_MarksActiveSubgoalCompleted()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        store.Complete("g1");
        Assert.Equal(SubgoalStatus.Completed, store.Find("g1")!.Status);
    }

    [Fact]
    public void Complete_Rejects_NonActiveSubgoal()
    {
        var store = new SubgoalStore();
        store.Define("g1", "obj", [], null, null, null);
        store.Complete("g1");
        Assert.Throws<ArgumentException>(() => store.Complete("g1"));
    }

    [Fact]
    public void UnknownSubgoalId_Throws_ForEveryOperation()
    {
        var store = new SubgoalStore();
        Assert.Null(store.Find("missing"));
        Assert.Throws<ArgumentException>(() => store.Escalate("missing", "r", null, null, null, null));
        Assert.Throws<ArgumentException>(() => store.Complete("missing"));
        Assert.Throws<ArgumentException>(() => store.CheckReadiness("missing", new WorkflowValueStore()));
    }
}
