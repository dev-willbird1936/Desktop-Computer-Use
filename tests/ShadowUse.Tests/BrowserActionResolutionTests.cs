// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>F31 coverage: browser_remember_action_resolution/browser_resolve_cached_action against a fake engine, no live browser.</summary>
public sealed class BrowserActionResolutionTests
{
    private sealed class FakePage : IControlledPage
    {
        public string Url { get; set; } = "about:blank";
        public List<ObservedElement> Elements { get; } = [];

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            Url = url;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObservedElement>>(Elements);

        public Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;
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

    private static async Task<(BrowserUseTools tools, string sessionId, FakePage page)> BuildFixtureAsync()
    {
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#submit", "button", "Sign in", true, true, false, "Sign in"));
        page.Elements.Add(new ObservedElement("#account", "textbox", "Account", true, true, false, "acct-42"));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated }).ConfigureAwait(false);
        await session.GotoAsync("https://example.com/login").ConfigureAwait(false);
        var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.Off });
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());
        return (tools, session.Target.AdapterSessionId, page);
    }

    [Fact]
    public async Task ResolveCachedAction_Misses_WhenNothingWasRemembered()
    {
        var (tools, sessionId, _) = await BuildFixtureAsync();

        var result = await tools.ResolveCachedAction(sessionId, "submit_button");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("\"hit\":false", json);
        Assert.Contains("no_cached_recipe", json);
    }

    [Fact]
    public async Task RememberThenResolve_HitsAndReturnsTheCurrentCandidateId()
    {
        var (tools, sessionId, page) = await BuildFixtureAsync();

        var candidates = await ObserveCandidatesAsync(tools, sessionId);
        var submit = candidates.Single(c => c["role"]?.ToString() == "button");

        var remember = tools.RememberActionResolution(sessionId, submit["id"]!.ToString(), "submit_button");
        Assert.Contains("\"ok\":true", System.Text.Json.JsonSerializer.Serialize(remember));

        var resolved = await tools.ResolveCachedAction(sessionId, "submit_button");
        var node = System.Text.Json.JsonSerializer.SerializeToNode(resolved)!.AsObject();

        // Browser candidate ids are minted fresh per observation (unlike native's stable
        // RuntimeId-keyed ids), so a hit's candidate_id necessarily differs from the one
        // captured at remember-time; what must hold is that it is a live, non-empty id
        // resolved against the current page and it identifies the same real button.
        Assert.True(node["hit"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(node["candidate_id"]?.ToString()));
        Assert.Equal("button", node["role"]?.ToString());
        Assert.Equal("Sign in", node["name"]?.ToString());
    }

    [Fact]
    public async Task ResolveCachedAction_Misses_AfterStructuralSchemaChange()
    {
        var (tools, sessionId, page) = await BuildFixtureAsync();
        var candidates = await ObserveCandidatesAsync(tools, sessionId);
        var submit = candidates.Single(c => c["role"]?.ToString() == "button");
        tools.RememberActionResolution(sessionId, submit["id"]!.ToString(), "submit_button");

        // F31-R02: the page's real structure changes (a field is added), so the schema
        // fingerprint changes and the prior recipe must not be reused.
        page.Elements.Add(new ObservedElement("#extra", "checkbox", "Remember me", true, true, false, "false"));

        var resolved = await tools.ResolveCachedAction(sessionId, "submit_button");
        Assert.Contains("\"hit\":false", System.Text.Json.JsonSerializer.Serialize(resolved));
    }

    [Fact]
    public async Task ResolveCachedAction_Misses_AfterOriginChanges_EvenWithAnIdenticalButton()
    {
        var (tools, sessionId, page) = await BuildFixtureAsync();
        var candidates = await ObserveCandidatesAsync(tools, sessionId);
        var submit = candidates.Single(c => c["role"]?.ToString() == "button");
        tools.RememberActionResolution(sessionId, submit["id"]!.ToString(), "submit_button");

        // F31-T02: a superficially identical button on a different origin must not reuse
        // a recipe cached against the original page's identity.
        await page.GotoAsync("https://another-site.example/login", 0, default);

        var resolved = await tools.ResolveCachedAction(sessionId, "submit_button");
        Assert.Contains("\"hit\":false", System.Text.Json.JsonSerializer.Serialize(resolved));
    }

    [Fact]
    public async Task RememberActionResolution_ReturnsError_ForUnknownCandidateId()
    {
        var (tools, sessionId, _) = await BuildFixtureAsync();
        await ObserveCandidatesAsync(tools, sessionId);

        var result = tools.RememberActionResolution(sessionId, "not-a-real-candidate-id", "submit_button");

        Assert.Contains("error", System.Text.Json.JsonSerializer.Serialize(result));
    }

    private static async Task<List<System.Text.Json.Nodes.JsonObject>> ObserveCandidatesAsync(BrowserUseTools tools, string sessionId)
    {
        var observed = await tools.Observe(sessionId).ConfigureAwait(false);
        var node = System.Text.Json.JsonSerializer.SerializeToNode(observed)!.AsObject();
        return node["candidates"]!.AsArray().Select(c => c!.AsObject()).ToList();
    }
}
