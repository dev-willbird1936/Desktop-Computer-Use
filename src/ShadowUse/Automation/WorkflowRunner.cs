// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

public enum WorkflowNodeStatus { Pending, Running, Verified, Failed, Uncertain, Cancelled }

public sealed record WorkflowNodeSpec(string Id, string App, string Tool, string ArgsJson, IReadOnlyList<string> Prerequisites);

public sealed class WorkflowNodeState
{
    public WorkflowNodeStatus Status { get; set; } = WorkflowNodeStatus.Pending;
    public object? Result { get; set; }
    public string? Error { get; set; }
}

public sealed class WorkflowRun
{
    public required string WorkflowId { get; init; }
    public required IReadOnlyDictionary<string, WorkflowNodeSpec> Nodes { get; init; }
    public Dictionary<string, WorkflowNodeState> States { get; } = new();
}

/// <summary>F29-R01/R02: a minimal deterministic DAG runner -- nodes dispatch to already-existing
/// tools (never invents new automation), independent nodes whose prerequisites are all Verified
/// run concurrently (F29-T01), and a node never runs until every one of its prerequisites is
/// Verified. Node states persist for the process lifetime, matching the same scoping already used
/// by ActionResolutionCache/ExperienceStore. Resuming never silently re-executes a node an
/// interrupted prior run left Running -- it becomes Uncertain and needs an explicit reconciliation
/// call before the workflow can make further progress (F29-T02). Typed inputs/outputs, resource
/// claims, budgets, and postconditions from the full spec requirement are intentionally deferred;
/// this slice is ordering, concurrency, and interrupted-resume semantics only.</summary>
public sealed class WorkflowRunner
{
    private readonly ConcurrentDictionary<string, WorkflowRun> _runs = new();

    public WorkflowRun Create(string workflowId, IReadOnlyList<WorkflowNodeSpec> nodes)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new ArgumentException("workflow_id is required.");
        if (nodes.Count == 0)
            throw new ArgumentException("A workflow needs at least one node.");
        var byId = new Dictionary<string, WorkflowNodeSpec>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                throw new ArgumentException("Every node needs a non-empty id.");
            if (!byId.TryAdd(node.Id, node))
                throw new ArgumentException($"Duplicate node id '{node.Id}'.");
        }
        foreach (var node in nodes)
            foreach (var prereq in node.Prerequisites)
                if (!byId.ContainsKey(prereq))
                    throw new ArgumentException($"Node '{node.Id}' depends on unknown node '{prereq}'.");
        DetectCycle(byId);

        var run = new WorkflowRun { WorkflowId = workflowId, Nodes = byId };
        foreach (var id in byId.Keys)
            run.States[id] = new WorkflowNodeState();
        if (!_runs.TryAdd(workflowId, run))
            throw new ArgumentException($"Workflow '{workflowId}' already exists.");
        return run;
    }

    private static void DetectCycle(Dictionary<string, WorkflowNodeSpec> nodes)
    {
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id))
                throw new ArgumentException($"Workflow has a dependency cycle involving '{id}'.");
            foreach (var prereq in nodes[id].Prerequisites)
                Visit(prereq);
            visiting.Remove(id);
            visited.Add(id);
        }
        foreach (var id in nodes.Keys)
            Visit(id);
    }

    public WorkflowRun? Find(string workflowId) => _runs.GetValueOrDefault(workflowId);

    /// <summary>Runs every currently-ready wave to completion. A node is ready when its own status
    /// is Pending and every prerequisite is Verified. Independent ready nodes in the same wave run
    /// concurrently; a node with an unverified/failed/uncertain prerequisite is never dispatched.
    /// Stops when a wave is empty (finished, or blocked on a failure/uncertain node).</summary>
    public async Task RunAsync(
        string workflowId,
        Func<WorkflowNodeSpec, CancellationToken, Task<object>> dispatch,
        Func<object, bool> hasFailed,
        CancellationToken ct)
    {
        var run = _runs.GetValueOrDefault(workflowId) ?? throw new ArgumentException($"Workflow '{workflowId}' was not found.");
        while (true)
        {
            var ready = run.Nodes.Values
                .Where(n => run.States[n.Id].Status == WorkflowNodeStatus.Pending
                    && n.Prerequisites.All(p => run.States[p].Status == WorkflowNodeStatus.Verified))
                .ToArray();
            if (ready.Length == 0)
                return;
            foreach (var node in ready)
                run.States[node.Id].Status = WorkflowNodeStatus.Running;
            var tasks = ready.Select(node => RunOneAsync(run, node, dispatch, hasFailed, ct)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task RunOneAsync(
        WorkflowRun run,
        WorkflowNodeSpec node,
        Func<WorkflowNodeSpec, CancellationToken, Task<object>> dispatch,
        Func<object, bool> hasFailed,
        CancellationToken ct)
    {
        var state = run.States[node.Id];
        // A cancellation mid-dispatch leaves Status at Running on purpose: we genuinely do not know
        // whether the underlying action landed, so it is neither Verified nor Failed until a caller
        // explicitly resumes (MarkInterruptedNodesUncertain) and reconciles it (F29-T02).
        var result = await dispatch(node, ct).ConfigureAwait(false);
        state.Result = result;
        state.Status = hasFailed(result) ? WorkflowNodeStatus.Failed : WorkflowNodeStatus.Verified;
        if (state.Status == WorkflowNodeStatus.Failed)
            state.Error = "Node dispatch returned an error result.";
    }

    /// <summary>F29-T02: flips every node an interrupted prior run left Running to Uncertain and
    /// returns their ids -- called before resuming, never automatically. A restart that finds no
    /// Running nodes is a no-op (nothing to reconcile).</summary>
    public IReadOnlyList<string> MarkInterruptedNodesUncertain(string workflowId)
    {
        var run = _runs.GetValueOrDefault(workflowId) ?? throw new ArgumentException($"Workflow '{workflowId}' was not found.");
        var uncertain = new List<string>();
        foreach (var (id, state) in run.States)
        {
            if (state.Status == WorkflowNodeStatus.Running)
            {
                state.Status = WorkflowNodeStatus.Uncertain;
                uncertain.Add(id);
            }
        }
        return uncertain;
    }

    /// <summary>Explicit reconciliation of an Uncertain node -- the only way an Uncertain node ever
    /// leaves that state. verified/failed unblock or block dependents normally; pending re-queues
    /// the node itself for a plain retry on the next RunAsync.</summary>
    public void Reconcile(string workflowId, string nodeId, WorkflowNodeStatus resolution)
    {
        if (resolution is not (WorkflowNodeStatus.Verified or WorkflowNodeStatus.Failed or WorkflowNodeStatus.Pending))
            throw new ArgumentException("resolution must be verified, failed, or pending (retry).");
        var run = _runs.GetValueOrDefault(workflowId) ?? throw new ArgumentException($"Workflow '{workflowId}' was not found.");
        var state = run.States.GetValueOrDefault(nodeId) ?? throw new ArgumentException($"Node '{nodeId}' was not found.");
        if (state.Status != WorkflowNodeStatus.Uncertain)
            throw new ArgumentException($"Node '{nodeId}' is not Uncertain (currently {state.Status}); nothing to reconcile.");
        state.Status = resolution;
    }

    /// <summary>F54-R02/T02: cancels every currently Pending node -- a queued action that has not
    /// yet dispatched is discarded structurally, not merely skipped: RunAsync's own ready-node
    /// query only ever selects Status==Pending, so a Cancelled node can never be dispatched by a
    /// later RunAsync call. A node already Running when takeover starts is left alone -- it cannot
    /// be un-dispatched, a genuinely different case from a node that never started (covered
    /// separately by MarkInterruptedNodesUncertain). Terminal nodes are untouched.</summary>
    public IReadOnlyList<string> PauseForTakeover(string workflowId)
    {
        var run = _runs.GetValueOrDefault(workflowId) ?? throw new ArgumentException($"Workflow '{workflowId}' was not found.");
        var cancelled = new List<string>();
        foreach (var (id, state) in run.States)
        {
            if (state.Status == WorkflowNodeStatus.Pending)
            {
                state.Status = WorkflowNodeStatus.Cancelled;
                cancelled.Add(id);
            }
        }
        return cancelled;
    }
}
