// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

/// <summary>End-to-end coverage of the browser_jev_decide / browser_jev_act MCP tools against a fake engine, no live network.</summary>
public sealed class BrowserJevToolTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int CallCount;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => throw new InvalidOperationException("Respond not configured.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class FakePage : IControlledPage
    {
        public string Url { get; private set; } = "about:blank";
        public List<ObservedElement> Elements { get; } = [];
        public string? LastClickedSelector { get; private set; }
        public string? LastFilledSelector { get; private set; }
        public string? LastFilledValue { get; private set; }

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            Url = url;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObservedElement>>(Elements);

        public Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            LastClickedSelector = selector;
            return Task.CompletedTask;
        }

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
        public List<BrowserDiagnosticEntry> Diagnostics { get; } = [];
        public Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<BrowserDiagnosticEntry>>(Diagnostics);
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

    private const string LoginPageBody = """
        {
          "answers": {
            "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.95, "WAIT": 0.03, "DONE": 0.01, "BLOCKED": 0.01 } },
            "click_target": { "choice": "1", "confidence": 0.97, "probabilities": { "1": 0.97, "2": 0.03 } },
            "goal_reached": { "noul": 0.2 }
          }
        }
        """;

    private static (BrowserSessionManager manager, FakePage page, RecordingHandler handler, JevController jev) BuildFixture(JevMode mode = JevMode.FastLoop)
    {
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#signin", "button", "Sign in", true, true, false, null));
        page.Elements.Add(new ObservedElement("#cancel", "button", "Cancel", true, true, false, null));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var handler = new RecordingHandler { Respond = _ => Json(LoginPageBody) };
        var envVar = "JEV_TEST_TOOL_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var jev = new JevController(new HttpClient(handler), new JevOptions { Mode = mode, ApiKeyEnvironmentVariable = envVar });
        return (manager, page, handler, jev);
    }

    [Fact]
    public async Task JevDecide_ReturnsChoiceWithoutExecuting()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevDecide(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("click", json.GetProperty("operation").GetString());
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_ExecutesTheChosenClick()
    {
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev", json.GetProperty("decision_source").GetString());
        Assert.Equal("click", json.GetProperty("operation").GetString());
        Assert.NotNull(page.LastClickedSelector);

        // F51-R01: the decision phase (the Jev HTTP round trip) must be distinguishable from the
        // dispatch+settle phase (the actual click), not just a single lump total.
        var timing = json.GetProperty("result").GetProperty("timing");
        Assert.Equal(json.GetProperty("latency_ms").GetInt64(), timing.GetProperty("decision_ms").GetInt64());
        Assert.True(timing.GetProperty("dispatch_ms").GetInt64() >= 0);
        Assert.True(timing.GetProperty("settle_ms").GetInt64() >= 0);
        Assert.True(timing.GetProperty("total_ms").GetInt64() >= 0);
    }

    [Fact]
    public async Task JevAct_WithTaskIntent_SecondCallTakesCacheHitFastPathWithoutCallingJevAgain()
    {
        // F28/F31 convergence: the first call with a task_intent decides via real Jev (one HTTP
        // call) and auto-remembers the chosen candidate; a second call with the SAME task_intent
        // must skip Jev entirely -- handler.CallCount must NOT increase.
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var first = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account", task_intent: "signin_button");
        var firstJson = JsonSerializer.SerializeToElement(first);
        Assert.Equal("jev", firstJson.GetProperty("decision_source").GetString());
        Assert.Equal(1, handler.CallCount);

        var second = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account", task_intent: "signin_button");
        var secondJson = JsonSerializer.SerializeToElement(second);
        Assert.Equal("cache_hit_fast_path", secondJson.GetProperty("decision_source").GetString());
        Assert.Equal(1, handler.CallCount); // no second Jev HTTP call
        Assert.True(secondJson.GetProperty("decision_ms").GetInt64() >= 0);
        Assert.NotNull(page.LastClickedSelector);
    }

    [Fact]
    public async Task JevAct_UnknownTaskIntent_FallsThroughToNormalJevDecision()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account", task_intent: "never_remembered");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev", json.GetProperty("decision_source").GetString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_RepeatedNoProgressClick_SurfacesLoopGuardDiagnosis()
    {
        // F28-R02 parity: jev_act must diagnose a no-progress cycle exactly like browser_click
        // does, since it now executes through the same Act helper instead of a bypassing path.
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        JsonElement? third = null;
        for (var i = 0; i < 3; i++)
        {
            var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
            var json = JsonSerializer.SerializeToElement(result);
            var resultProperty = json.GetProperty("result");
            if (i < 2)
            {
                if (resultProperty.TryGetProperty("loop_guard", out var early) && early.ValueKind != JsonValueKind.Null)
                    Assert.False(early.GetProperty("should_stop").GetBoolean());
            }
            else
            {
                third = resultProperty;
            }
        }

        var loopGuard = third!.Value.GetProperty("loop_guard");
        Assert.True(loopGuard.GetProperty("should_stop").GetBoolean());
        Assert.False(string.IsNullOrEmpty(loopGuard.GetProperty("diagnosis").GetString()));
        Assert.NotNull(page.LastClickedSelector);
    }

    [Fact]
    public async Task JevAct_OffMode_ReturnsFallbackErrorAndExecutesNothing()
    {
        var (manager, page, handler, jev) = BuildFixture(JevMode.Off);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("error", out _));
        Assert.True(json.TryGetProperty("fallback", out _));
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_AssistMode_ReturnsRecommendationWithoutExecuting()
    {
        var (manager, page, handler, jev) = BuildFixture(JevMode.Assist);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev_assist_recommendation", json.GetProperty("decision_source").GetString());
        Assert.Equal("click", json.GetProperty("operation").GetString());
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_LowConfidence_EscalatesWithoutExecuting()
    {
        const string lowConfidenceBody = """
            {
              "answers": {
                "operation": { "choice": "click", "confidence": 0.4, "probabilities": { "click": 0.4, "WAIT": 0.3, "DONE": 0.2, "BLOCKED": 0.1 } },
                "click_target": { "choice": "1", "confidence": 0.5, "probabilities": { "1": 0.55, "2": 0.45 } }
              }
            }
            """;
        var (manager, page, handler, jev) = BuildFixture();
        handler.Respond = _ => Json(lowConfidenceBody);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev_low_confidence_escalation", json.GetProperty("decision_source").GetString());
        Assert.Null(page.LastClickedSelector);
    }

    [Fact]
    public async Task JevAct_FillOperation_ResolvesValueRefInsteadOfALiteral()
    {
        const string fillPasswordBody = """
            {
              "answers": {
                "operation": { "choice": "fill", "confidence": 0.92, "probabilities": { "fill": 0.92, "click": 0.05, "WAIT": 0.01, "DONE": 0.01, "BLOCKED": 0.01 } },
                "fill_target": { "choice": "1", "confidence": 0.99, "probabilities": { "1": 1.0 } },
                "goal_reached": { "noul": 0.1 }
              }
            }
            """;
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#pw", "textbox", "Password", true, true, true, null));
        page.Elements.Add(new ObservedElement("#signin", "button", "Sign in", true, true, false, null));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var handler = new RecordingHandler { Respond = _ => Json(fillPasswordBody) };
        var envVar = "JEV_TEST_TOOL_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var jev = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar });
        var values = new WorkflowValueStore();
        values.Set("saved_pw", "hunter2");

        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, values);

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Enter the saved password", value_ref: "saved_pw");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("fill", json.GetProperty("operation").GetString());
        Assert.Equal("#pw", page.LastFilledSelector);
        Assert.Equal("hunter2", page.LastFilledValue);
    }

    [Fact]
    public async Task JevAct_FillOperation_NoValueSourceSupplied_ReturnsErrorWithoutFilling()
    {
        const string fillPasswordBody = """
            {
              "answers": {
                "operation": { "choice": "fill", "confidence": 0.92, "probabilities": { "fill": 0.92, "click": 0.05, "WAIT": 0.01, "DONE": 0.01, "BLOCKED": 0.01 } },
                "fill_target": { "choice": "1", "confidence": 0.99, "probabilities": { "1": 1.0 } },
                "goal_reached": { "noul": 0.1 }
              }
            }
            """;
        var page = new FakePage();
        page.Elements.Add(new ObservedElement("#pw", "textbox", "Password", true, true, true, null));
        page.Elements.Add(new ObservedElement("#signin", "button", "Sign in", true, true, false, null));
        var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var handler = new RecordingHandler { Respond = _ => Json(fillPasswordBody) };
        var envVar = "JEV_TEST_TOOL_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "test-key");
        var jev = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.FastLoop, ApiKeyEnvironmentVariable = envVar });

        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Enter the saved password");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.TryGetProperty("error", out _));
        Assert.Null(page.LastFilledSelector);
    }

    private static IReadOnlyList<JevInspectorEntry> Entries(object inspectResult)
        => (IReadOnlyList<JevInspectorEntry>)inspectResult.GetType().GetProperty("entries")!.GetValue(inspectResult)!;

    [Fact]
    public async Task JevInspect_RecordsAnExecutedFastLoopDecision_MatchingTheExecutedCandidate()
    {
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var inspector = new JevInspector();
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore(), inspector);

        await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var entries = Entries(tools.JevInspect());

        var entry = Assert.Single(entries);
        Assert.Equal("browser", entry.Surface);
        Assert.Equal("jev", entry.DecisionSource);
        Assert.True(entry.Executed);
        Assert.Equal("click", entry.Operation);
        Assert.Equal("#signin", page.LastClickedSelector);
    }

    [Fact]
    public async Task JevInspect_RecordsADecideOnlyCallAsUnexecuted()
    {
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var inspector = new JevInspector();
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore(), inspector);

        await tools.JevDecide(session.Target.AdapterSessionId, "Sign in to the account");
        var entries = Entries(tools.JevInspect());

        var entry = Assert.Single(entries);
        Assert.Equal("jev_decide", entry.DecisionSource);
        Assert.False(entry.Executed);
        Assert.Null(page.LastClickedSelector);
    }

    [Fact]
    public async Task JevInspect_IsReadOnly_CallingItTwiceChangesNothing()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var inspector = new JevInspector();
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore(), inspector);
        await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var callsAfterAct = handler.CallCount;

        var first = Entries(tools.JevInspect());
        var second = Entries(tools.JevInspect());

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first[0], second[0]);
        Assert.Equal(callsAfterAct, handler.CallCount);
        Assert.Equal("#signin", page.LastClickedSelector);
    }

    [Fact]
    public async Task Diagnostics_ReturnsThePagesRecentConsoleErrorsAndFailedRequests()
    {
        var (manager, page, _, jev) = BuildFixture();
        page.Diagnostics.Add(new BrowserDiagnosticEntry("console_error", "TypeError: x is undefined", "data:text/html,login", null, DateTimeOffset.UtcNow));
        page.Diagnostics.Add(new BrowserDiagnosticEntry("request_failed", "HTTP 500", "https://example.test/api", 500, DateTimeOffset.UtcNow));
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.Diagnostics(session.Target.AdapterSessionId);
        var entries = (IReadOnlyList<BrowserDiagnosticEntry>)result.GetType().GetProperty("entries")!.GetValue(result)!;

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Kind == "console_error");
        Assert.Contains(entries, e => e.Kind == "request_failed" && e.Status == 500);
    }

    [Fact]
    public async Task Click_RecordsToTheRunTimelineWhenEnabled()
    {
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var observation = await session.GotoAsync("data:text/html,login");
        var signinId = observation.Candidates.Single(item => item.Binding.Selector == "#signin").CandidateId;
        var path = Path.Combine(Path.GetTempPath(), $"dcu-run-timeline-browser-test-{Guid.NewGuid():N}.jsonl");
        var timeline = new RunTimeline(true, path);
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore(), inspector: null, timeline: timeline);
        try
        {
            var result = await tools.Click(session.Target.AdapterSessionId, signinId);
            var entries = timeline.ReadRecent(10);

            var entry = Assert.Single(entries);
            Assert.Equal("browser", entry.Surface);
            Assert.Equal("click", entry.Action);
            Assert.True(entry.Ok);
            Assert.Equal("#signin", page.LastClickedSelector);

            // F51-R01: a plain (non-Jev) click has no decision phase, but still reports
            // dispatch/settle/total -- the breakdown is not Jev-exclusive.
            Assert.Null(entry.DecisionMs);
            Assert.NotNull(entry.DispatchMs);
            Assert.NotNull(entry.SettleMs);
            var timing = JsonSerializer.SerializeToElement(result).GetProperty("timing");
            Assert.Equal(JsonValueKind.Null, timing.GetProperty("decision_ms").ValueKind);
            Assert.True(timing.GetProperty("dispatch_ms").GetInt64() >= 0);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task JevAct_RejectsDecisionMadeAgainstAStaleObservation()
    {
        var (manager, page, _, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var staleObservation = session.Page.LastObservation!;
        var decision = new JevDecision("click", "does-not-matter", 0.9, new Dictionary<string, double>(), false, 0.5, "a-superseded-observation-id", "jev-test", 1, "{}", "{}");

        Assert.True(decision.IsStaleAgainst($"{staleObservation.Target.PageId}:{staleObservation.Target.DocumentEpoch + 1}"));
    }
}
