using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F29: a minimal deterministic DAG runner -- ordering, concurrency, and interrupted-resume
/// semantics only (typed inputs/outputs, resource claims, budgets, postconditions are out of scope
/// for this slice). Pure unit tests, no UIA/browser needed since dispatch is fully injected.</summary>
public sealed class WorkflowRunnerTests
{
    private static WorkflowNodeSpec Node(string id, string[]? prereqs = null)
        => new(id, "app", "noop", "{}", prereqs ?? []);

    private static bool Failed(object r) => r.GetType().GetProperty("error") != null;

    [Fact]
    public void Create_RejectsUnknownPrerequisite()
    {
        var runner = new WorkflowRunner();
        Assert.Throws<ArgumentException>(() => runner.Create("w1", [Node("a", ["missing"])]));
    }

    [Fact]
    public void Create_RejectsDuplicateNodeId()
    {
        var runner = new WorkflowRunner();
        Assert.Throws<ArgumentException>(() => runner.Create("w1", [Node("a"), Node("a")]));
    }

    [Fact]
    public void Create_RejectsDependencyCycle()
    {
        var runner = new WorkflowRunner();
        Assert.Throws<ArgumentException>(() => runner.Create("w1", [Node("a", ["b"]), Node("b", ["a"])]));
    }

    [Fact]
    public void Create_RejectsDuplicateWorkflowId()
    {
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("a")]);
        Assert.Throws<ArgumentException>(() => runner.Create("w1", [Node("b")]));
    }

    [Fact]
    public async Task RunAsync_TwoIndependentBranchesRunConcurrently_AndTheJoinWaitsForBoth()
    {
        // F29-T01: a node consuming two independent branches must not start until both are Verified.
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("left"), Node("right"), Node("join", ["left", "right"])]);

        var started = new ConcurrentBag_Fake();
        var gate = new TaskCompletionSource();
        var order = new List<string>();
        var lockObj = new object();

        async Task<object> Dispatch(WorkflowNodeSpec node, CancellationToken ct)
        {
            lock (lockObj) order.Add($"start:{node.Id}");
            if (node.Id is "left" or "right")
            {
                started.Mark(node.Id);
                // Both branches must be running concurrently (neither waits on the other) before
                // either is allowed to finish -- proves they were dispatched in the same wave.
                await started.WaitForBothAsync();
            }
            else
            {
                // The join must never be dispatched until left/right are both Verified already.
                Assert.Contains("done:left", order);
                Assert.Contains("done:right", order);
            }
            lock (lockObj) order.Add($"done:{node.Id}");
            return new { ok = true };
        }

        await runner.RunAsync("w1", Dispatch, Failed, CancellationToken.None);

        var run = runner.Find("w1")!;
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["left"].Status);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["right"].Status);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["join"].Status);
    }

    [Fact]
    public async Task RunAsync_FailedPrerequisite_NeverDispatchesTheDependent()
    {
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("a"), Node("b", ["a"])]);
        var dispatched = new List<string>();

        Task<object> Dispatch(WorkflowNodeSpec node, CancellationToken ct)
        {
            dispatched.Add(node.Id);
            return Task.FromResult<object>(node.Id == "a" ? new { error = "boom" } : new { ok = true });
        }

        await runner.RunAsync("w1", Dispatch, Failed, CancellationToken.None);

        Assert.DoesNotContain("b", dispatched);
        var run = runner.Find("w1")!;
        Assert.Equal(WorkflowNodeStatus.Failed, run.States["a"].Status);
        Assert.Equal(WorkflowNodeStatus.Pending, run.States["b"].Status);
    }

    [Fact]
    public async Task ResumeAfterInterruption_NeverSilentlyReExecutes_AndRequiresExplicitReconciliation()
    {
        // F29-T02: a node interrupted mid-dispatch (its outcome is genuinely unknown) must become
        // Uncertain on resume, never be silently re-run, and only leave Uncertain via an explicit
        // reconcile call.
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("a"), Node("b", ["a"])]);
        var dispatchCount = 0;
        using var cts = new CancellationTokenSource();

        Task<object> HangingDispatch(WorkflowNodeSpec node, CancellationToken ct)
        {
            dispatchCount++;
            return Task.Delay(Timeout.Infinite, ct).ContinueWith<object>(_ => new { ok = true }, ct);
        }

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("w1", HangingDispatch, Failed, cts.Token));

        var run = runner.Find("w1")!;
        Assert.Equal(WorkflowNodeStatus.Running, run.States["a"].Status);
        Assert.Equal(1, dispatchCount);

        var uncertain = runner.MarkInterruptedNodesUncertain("w1");
        Assert.Equal(["a"], uncertain);
        Assert.Equal(WorkflowNodeStatus.Uncertain, run.States["a"].Status);

        // Running again must not touch the Uncertain node (no automatic re-execution).
        await runner.RunAsync("w1", (n, t) => Task.FromResult<object>(new { ok = true }), Failed, CancellationToken.None);
        Assert.Equal(1, dispatchCount);
        Assert.Equal(WorkflowNodeStatus.Uncertain, run.States["a"].Status);
        Assert.Equal(WorkflowNodeStatus.Pending, run.States["b"].Status);

        runner.Reconcile("w1", "a", WorkflowNodeStatus.Verified);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["a"].Status);

        await runner.RunAsync("w1", (n, t) => Task.FromResult<object>(new { ok = true }), Failed, CancellationToken.None);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["b"].Status);
    }

    [Fact]
    public void Reconcile_RejectsReconcilingANodeThatIsNotUncertain()
    {
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("a")]);
        Assert.Throws<ArgumentException>(() => runner.Reconcile("w1", "a", WorkflowNodeStatus.Verified));
    }

    [Fact]
    public async Task PauseForTakeover_CancelsOnlyPendingNodes_AndCancelledNodesNeverDispatch()
    {
        // F54-R02/T02: a queued (Pending) action must be discarded, not merely skipped, by
        // takeover -- proven here by running the workflow again afterward and confirming the
        // dispatch delegate is never invoked for the cancelled node.
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("a"), Node("b", ["a"])]);
        await runner.RunAsync("w1", (n, t) => Task.FromResult<object>(new { ok = true }), Failed, CancellationToken.None);
        var run = runner.Find("w1")!;
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["a"].Status);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["b"].Status);

        runner.Create("w2", [Node("queued")]);
        var cancelled = runner.PauseForTakeover("w2");
        Assert.Equal(["queued"], cancelled);
        Assert.Equal(WorkflowNodeStatus.Cancelled, runner.Find("w2")!.States["queued"].Status);

        var dispatched = false;
        await runner.RunAsync("w2", (n, t) => { dispatched = true; return Task.FromResult<object>(new { ok = true }); }, Failed, CancellationToken.None);
        Assert.False(dispatched);
        Assert.Equal(WorkflowNodeStatus.Cancelled, runner.Find("w2")!.States["queued"].Status);
    }

    [Fact]
    public async Task PauseForTakeover_LeavesRunningAndTerminalNodesUntouched()
    {
        var runner = new WorkflowRunner();
        runner.Create("w1", [Node("done"), Node("running")]);
        await runner.RunAsync("w1", (n, t) => Task.FromResult<object>(new { ok = true }), Failed, CancellationToken.None);
        var run = runner.Find("w1")!;
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["done"].Status);
        run.States["running"].Status = WorkflowNodeStatus.Running; // simulate an in-flight dispatch

        var cancelled = runner.PauseForTakeover("w1");
        Assert.Empty(cancelled);
        Assert.Equal(WorkflowNodeStatus.Verified, run.States["done"].Status);
        Assert.Equal(WorkflowNodeStatus.Running, run.States["running"].Status);
    }

    [Fact]
    public void PauseForTakeover_Throws_WhenWorkflowNotFound()
    {
        var runner = new WorkflowRunner();
        Assert.Throws<ArgumentException>(() => runner.PauseForTakeover("missing"));
    }

    private sealed class ConcurrentBag_Fake
    {
        private readonly HashSet<string> _seen = [];
        private readonly TaskCompletionSource _both = new();
        private readonly object _lock = new();

        public void Mark(string id)
        {
            lock (_lock)
            {
                _seen.Add(id);
                if (_seen.Count >= 2)
                    _both.TrySetResult();
            }
        }

        public Task WaitForBothAsync() => _both.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
