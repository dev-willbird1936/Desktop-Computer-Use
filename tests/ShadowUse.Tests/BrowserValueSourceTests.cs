// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>F39 coverage: browser_fill/browser_capture_value value_ref and value_env sources against a fake engine, no live browser.</summary>
public sealed class BrowserValueSourceTests
{
    private sealed class FakePage : IControlledPage
    {
        public string Url { get; private set; } = "about:blank";
        public List<ObservedElement> Elements { get; } = [];
        public string? LastFilledSelector { get; private set; }
        public string? LastFilledValue { get; private set; }

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            Url = url;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObservedElement>>(Elements);

        public Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            LastFilledSelector = selector;
            LastFilledValue = value;
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

    private static async Task<(BrowserUseTools tools, string sessionId, string sourceCandidateId, string destCandidateId, WorkflowValueStore store)> BuildFixtureAsync()
    {
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#account", "textbox", "Account", true, true, false, "acct-42"));
        page.Elements.Add(new ObservedElement("#password", "textbox", "Password", true, true, true, null));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated }).ConfigureAwait(false);
        await session.GotoAsync("data:text/html,account").ConfigureAwait(false);
        var store = new WorkflowValueStore();
        var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.Off });
        var tools = new BrowserUseTools(manager, jev, store);
        var observed = await session.Page.ObserveAsync().ConfigureAwait(false);
        var sourceId = observed.Candidates.Single(c => c.Selector == "#account").CandidateId;
        var destId = observed.Candidates.Single(c => c.Selector == "#password").CandidateId;
        return (tools, session.Target.AdapterSessionId, sourceId, destId, store);
    }

    [Fact]
    public async Task Fill_UsesLiteralValue_WhenOnlyValueSupplied()
    {
        var (tools, sessionId, _, destId, _) = await BuildFixtureAsync();
        var page = new FakePage();

        var result = await tools.Fill(sessionId, destId, value: "direct-value");

        Assert.Contains("\"ok\":true", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Fill_UsesValueRef_WhenStoredUnderName()
    {
        var (tools, sessionId, sourceId, destId, store) = await BuildFixtureAsync();
        store.Set("captured", "from-store");

        var result = await tools.Fill(sessionId, destId, value_ref: "captured");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("\"ok\":true", json);
    }

    [Fact]
    public async Task Fill_ReturnsError_WhenNoSourceSupplied()
    {
        var (tools, sessionId, _, destId, _) = await BuildFixtureAsync();

        var result = await tools.Fill(sessionId, destId);

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("error", json);
    }

    [Fact]
    public async Task Fill_ReturnsError_WhenBothValueAndValueRefSupplied()
    {
        var (tools, sessionId, _, destId, store) = await BuildFixtureAsync();
        store.Set("captured", "from-store");

        var result = await tools.Fill(sessionId, destId, value: "literal", value_ref: "captured");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("error", json);
    }

    [Fact]
    public async Task CaptureValue_StoresCandidateText_ForLaterFill()
    {
        var (tools, sessionId, sourceId, destId, store) = await BuildFixtureAsync();

        var capture = await tools.CaptureValue(sessionId, sourceId, "account-name");
        var captureJson = System.Text.Json.JsonSerializer.Serialize(capture);
        Assert.Contains("\"ok\":true", captureJson);
        Assert.True(store.TryGet("account-name", out var stored));
        Assert.Equal("acct-42", stored);

        var fill = await tools.Fill(sessionId, destId, value_ref: "account-name");
        Assert.Contains("\"ok\":true", System.Text.Json.JsonSerializer.Serialize(fill));
    }

    [Fact]
    public async Task CaptureValue_ReturnsError_ForPasswordCandidateWithMaskedText()
    {
        var (tools, sessionId, _, destId, _) = await BuildFixtureAsync();

        var capture = await tools.CaptureValue(sessionId, destId, "should-fail");

        var json = System.Text.Json.JsonSerializer.Serialize(capture);
        Assert.Contains("error", json);
    }
}
