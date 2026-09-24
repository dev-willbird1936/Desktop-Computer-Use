// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>F28-R02 coverage: browser_click/browser_fill loop_guard wiring against a fake engine, no live browser.</summary>
public sealed class BrowserLoopGuardTests
{
    private sealed class FakePage : IControlledPage
    {
        public string Url { get; set; } = "https://example.com/";
        public List<ObservedElement> Elements { get; } = [];

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            Url = url;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObservedElement>>(Elements);

        // A genuinely no-op click: the observation is identical before and after, which is
        // exactly the "no observable effect" case F28-R02 must detect.
        public Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var index = Elements.FindIndex(e => e.Selector == selector);
            if (index >= 0)
                Elements[index] = Elements[index] with { Text = value };
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(1);
        public Task<BrowserReadinessResult> CheckReadinessAsync(string selector, string frameId, BrowserActionKind operation, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(new BrowserReadinessResult(true, null));
        public Task PressAsync(string selector, string key, string frameId, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ScrollIntoViewAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> TextAsync(string selector, string frameId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string?> AttributeAsync(string selector, string name, string frameId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> EnabledAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> VisibleAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<string> DownloadAsync(string selector, string frameId, string downloadRoot, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<BrowserPointClickResult> PointClickAsync(string selector, string frameId, double? offsetX, double? offsetY, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<BrowserPointFillResult> PointFillAsync(string selector, string frameId, string value, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> UploadAsync(string selector, string frameId, string uploadRoot, string filePath, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> DialogAsync(string selector, string frameId, string dialogType, bool accept, string? promptText, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> WaitValueEqualsAsync(string selector, string frameId, string expectedValue, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> WaitTextEqualsAsync(string selector, string frameId, string expectedText, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> WaitElementDisappearsAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> WaitNavigationAsync(string beforeUrl, string expectedUrl, int timeoutMs, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<BrowserWebMcpPageSnapshot> DiscoverWebMcpAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<BrowserWebMcpInvokeResult> InvokeWebMcpAsync(BrowserWebMcpCandidate tool, System.Text.Json.Nodes.JsonObject arguments, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class FakeBrowser(FakePage page) : IControlledBrowser
    {
        public bool OwnsResources => true;
        public string ContextId { get; } = $"ctx-{Guid.NewGuid():N}";
        public IControlledPage Page { get; } = page;
        public string? HostHwnd => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEngine(FakePage page) : IBrowserEngine
    {
        public Task<IControlledBrowser> OpenAsync(BrowserSessionOptions options, CancellationToken cancellationToken)
            => Task.FromResult<IControlledBrowser>(new FakeBrowser(page));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(BrowserUseTools tools, string sessionId, string candidateId, FakePage page)> BuildFixtureAsync()
    {
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#save", "button", "Save", true, true, false, "Save"));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated }).ConfigureAwait(false);
        await session.GotoAsync("https://example.com/loop-guard").ConfigureAwait(false);
        var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.Off });
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());
        var observed = await session.Page.ObserveAsync().ConfigureAwait(false);
        var candidateId = observed.Candidates.Single().CandidateId;
        return (tools, session.Target.AdapterSessionId, candidateId, page);
    }

    private static System.Text.Json.Nodes.JsonObject? LoopGuardOf(object result) =>
        System.Text.Json.JsonSerializer.SerializeToNode(result)!.AsObject()["loop_guard"]?.AsObject();

    private static string NextCandidateId(object result) =>
        System.Text.Json.JsonSerializer.SerializeToNode(result)!.AsObject()["candidates"]!.AsArray().Single()!["id"]!.ToString();

    [Fact]
    public async Task RepeatedNoOpClicks_DiagnoseANoProgressCycleByTheThird()
    {
        var (tools, sessionId, candidateId, _) = await BuildFixtureAsync();

        // Each click's response carries a fresh candidate id for the still-present button
        // (candidate ids are minted per observation, not stable) -- chain through it exactly
        // as a real caller would, rather than reusing the id captured before any click.
        var r1 = await tools.Click(sessionId, candidateId);
        var r2 = await tools.Click(sessionId, NextCandidateId(r1));
        var r3 = await tools.Click(sessionId, NextCandidateId(r2));
        var first = LoopGuardOf(r1);
        var second = LoopGuardOf(r2);
        var third = LoopGuardOf(r3);

        Assert.True(first is null || first["should_stop"]!.GetValue<bool>() == false);
        Assert.True(second is null || second["should_stop"]!.GetValue<bool>() == false);
        Assert.NotNull(third);
        Assert.True(third!["should_stop"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(third["diagnosis"]?.ToString()));
    }

    [Fact]
    public async Task AFillThatActuallyChangesTheCandidateText_NeverFlagsShouldStop()
    {
        var (tools, sessionId, candidateId, _) = await BuildFixtureAsync();

        var currentCandidateId = candidateId;
        for (var i = 0; i < 5; i++)
        {
            // A different value each call is real progress every time -- the target's text
            // keeps changing, so this must never accumulate toward should_stop.
            var result = await tools.Fill(sessionId, currentCandidateId, value: $"value-{i}");
            var loopGuard = LoopGuardOf(result);
            Assert.True(loopGuard is null || loopGuard["should_stop"]!.GetValue<bool>() == false);
            currentCandidateId = NextCandidateId(result);
        }
    }

    [Fact]
    public async Task ADifferentCandidate_IsTrackedIndependently()
    {
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#a", "button", "A", true, true, false, "A"));
        page.Elements.Add(new ObservedElement("#b", "button", "B", true, true, false, "B"));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("https://example.com/loop-guard-scopes");
        var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.Off });
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());
        var observed = await session.Page.ObserveAsync();
        var candidateA = observed.Candidates.Single(c => c.Selector == "#a").CandidateId;
        var candidateB = observed.Candidates.Single(c => c.Selector == "#b").CandidateId;

        var afterFirstA = await tools.Click(session.Target.AdapterSessionId, candidateA);
        var refreshedCandidateA = System.Text.Json.JsonSerializer.SerializeToNode(afterFirstA)!.AsObject()["candidates"]!.AsArray()
            .Single(c => c!.AsObject()["name"]!.ToString() == "A")!["id"]!.ToString();
        await tools.Click(session.Target.AdapterSessionId, refreshedCandidateA);
        // Two no-op clicks on A must not push B toward should_stop on its first click.
        var bResult = await tools.Click(session.Target.AdapterSessionId, candidateB);
        var bLoopGuard = LoopGuardOf(bResult);

        Assert.True(bLoopGuard is null || bLoopGuard["should_stop"]!.GetValue<bool>() == false);
    }
}
