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
        public string? LastBody;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => throw new InvalidOperationException("Respond not configured.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            LastBody = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
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
        public BrowserReadinessResult Readiness { get; set; } = new(true, null);
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
            => Task.FromResult(Readiness);
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
            "operation": { "choice": "click", "confidence": 0.9, "probabilities": { "click": 0.95, "WAIT": 0.03, "DONE": 0.01, "BLOCKED": 0.01, "REOBSERVE": 0, "ASK_USER": 0, "STOP": 0 } },
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
        using var decideRequest = JsonDocument.Parse(handler.LastBody!);
        Assert.True(decideRequest.RootElement.GetProperty("questions").TryGetProperty("goal_reached", out _));
    }

    [Fact]
    public async Task JevAct_ExecutesTheChosenClick()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev", json.GetProperty("decision_source").GetString());
        Assert.Equal("click", json.GetProperty("operation").GetString());
        Assert.NotNull(page.LastClickedSelector);
        using var actRequest = JsonDocument.Parse(handler.LastBody!);
        Assert.False(actRequest.RootElement.GetProperty("questions").TryGetProperty("goal_reached", out _));

        // F51-R01: the decision phase (the Jev HTTP round trip) must be distinguishable from the
        // dispatch+settle phase (the actual click), not just a single lump total.
        var timing = json.GetProperty("result").GetProperty("timing");
        Assert.Equal(json.GetProperty("latency_ms").GetInt64(), timing.GetProperty("decision_ms").GetInt64());
        Assert.True(timing.GetProperty("dispatch_ms").GetInt64() >= 0);
        Assert.True(timing.GetProperty("settle_ms").GetInt64() >= 0);
        Assert.True(timing.GetProperty("total_ms").GetInt64() >= 0);
    }

    [Fact]
    public async Task JevAct_LowTargetConfidenceEscalatesWithoutDispatch()
    {
        var (manager, page, handler, jev) = BuildFixture();
        handler.Respond = _ => Json(LoginPageBody.Replace("\"confidence\": 0.97", "\"confidence\": 0.4", StringComparison.Ordinal));
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account"));

        Assert.Equal("jev_low_confidence_escalation", result.GetProperty("decision_source").GetString());
        Assert.Equal(0.9, result.GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(0.4, result.GetProperty("target_confidence").GetDouble(), 3);
        Assert.Equal(0.4, result.GetProperty("execution_confidence").GetDouble(), 3);
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_DisabledTargetReportsDispatchFailedWithoutClaimingExecution()
    {
        var (manager, page, _, jev) = BuildFixture();
        page.Readiness = new BrowserReadinessResult(false, BrowserReadinessFailure.Disabled);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var inspector = new JevInspector();
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore(), inspector);

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in"));
        var entry = Assert.Single(Entries(tools.JevInspect()));

        Assert.Equal("jev_dispatch_failed", result.GetProperty("decision_source").GetString());
        Assert.Contains("disabled", result.GetProperty("result").GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(result.GetProperty("result").GetProperty("action_call_returned").GetBoolean());
        Assert.False(entry.Executed);
        Assert.Equal("jev_dispatch_failed", entry.DecisionSource);
        Assert.Null(page.LastClickedSelector);
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
    public async Task JevAct_CandidateRoleFilterBypassesCachedActionRecipe()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var first = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in", task_intent: "signin_button"));
        Assert.Equal("jev", first.GetProperty("decision_source").GetString());
        Assert.Equal(1, handler.CallCount);

        var filtered = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in", task_intent: "signin_button", candidate_roles: ["button"]));
        Assert.Equal("jev", filtered.GetProperty("decision_source").GetString());
        Assert.Equal(2, handler.CallCount);
        Assert.Equal("button", filtered.GetProperty("observation").GetProperty("candidate_role_filter")[0].GetString());
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
        var (manager, page, handler, jev) = BuildFixture();
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
        using var request = JsonDocument.Parse(handler.LastBody!);
        Assert.Contains("tool execution attempted=True; effect not verified", request.RootElement.GetProperty("state").GetProperty("recent_actions")[0].GetString());
    }

    [Fact]
    public async Task BrowserJevRequestIncludesAccessibleCellNameAndOccupiedStateText()
    {
        var (manager, page, handler, jev) = BuildFixture();
        page.Elements.Clear();
        page.Elements.Add(new ObservedElement("#cell-x", "button", "Row 1, column 1", false, true, false, "X") { PointerReady = false });
        page.Elements.Add(new ObservedElement("#cell-empty", "button", "Row 1, column 2", true, true, false, ""));
        page.Elements.Add(new ObservedElement("#cell-empty-2", "button", "Row 1, column 3", true, true, false, ""));
        page.Elements.Add(new ObservedElement("#home", "a", "Home", true, true, false, "Home"));
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,game");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        await tools.JevDecide(session.Target.AdapterSessionId, "Play Tic-Tac-Toe as X", candidate_roles: ["button"]);
        using var request = JsonDocument.Parse(handler.LastBody!);
        var state = request.RootElement.GetProperty("state");

        var observedText = state.GetProperty("observed_text").GetString();
        Assert.Contains("Row 1, column 1; value: X [disabled; not actionable]", observedText);
        Assert.DoesNotContain("a: Home", observedText);
        Assert.Equal("button", state.GetProperty("candidate_role_filter")[0].GetString());
        Assert.Equal(2, state.GetProperty("candidates").GetArrayLength());
        Assert.Equal("Row 1, column 2", state.GetProperty("candidates")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task JevAct_OffMode_ReturnsFallbackErrorAndExecutesNothing()
    {
        var (manager, page, handler, jev) = BuildFixture(JevMode.Off);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var cachedCandidate = session.Page.LastObservation!.Candidates.Single(candidate => candidate.Selector == "#signin");
        tools.RememberActionResolution(session.Target.AdapterSessionId, cachedCandidate.CandidateId, "disabled_recipe");
        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account", task_intent: "disabled_recipe");
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
        var cachedCandidate = session.Page.LastObservation!.Candidates.Single(candidate => candidate.Selector == "#signin");
        tools.RememberActionResolution(session.Target.AdapterSessionId, cachedCandidate.CandidateId, "assist_recipe");

        var result = await tools.JevAct(session.Target.AdapterSessionId, "Sign in to the account", task_intent: "assist_recipe");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.Equal("jev_assist_recommendation", json.GetProperty("decision_source").GetString());
        Assert.Equal("click", json.GetProperty("operation").GetString());
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_AskUserChoiceIsNonExecuting()
    {
        const string askUserBody = """
            { "answers": { "operation": { "choice": "ASK_USER", "confidence": 0.95, "probabilities": { "click": 0, "WAIT": 0, "REOBSERVE": 0, "ASK_USER": 0.95, "STOP": 0, "DONE": 0.03, "BLOCKED": 0.02 } } } }
            """;
        var (manager, page, handler, jev) = BuildFixture();
        handler.Respond = _ => Json(askUserBody);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in"));

        Assert.Equal("ASK_USER", result.GetProperty("operation").GetString());
        Assert.Null(page.LastClickedSelector);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task JevAct_LowConfidence_EscalatesWithoutExecuting()
    {
        const string lowConfidenceBody = """
            {
              "answers": {
                "operation": { "choice": "click", "confidence": 0.4, "probabilities": { "click": 0.4, "WAIT": 0.3, "DONE": 0.2, "BLOCKED": 0.1, "REOBSERVE": 0, "ASK_USER": 0, "STOP": 0 } },
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
                "operation": { "choice": "fill", "confidence": 0.92, "probabilities": { "fill": 0.92, "click": 0.05, "WAIT": 0.01, "DONE": 0.01, "BLOCKED": 0.01, "REOBSERVE": 0, "ASK_USER": 0, "STOP": 0 } },
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
                "operation": { "choice": "fill", "confidence": 0.92, "probabilities": { "fill": 0.92, "click": 0.05, "WAIT": 0.01, "DONE": 0.01, "BLOCKED": 0.01, "REOBSERVE": 0, "ASK_USER": 0, "STOP": 0 } },
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

    [Fact]
    public async Task BrowserJev_UsesVisibleGameButtonsEvenWhenHiddenControlsPrecedeThem()
    {
        var (manager, page, handler, jev) = BuildFixture();
        page.Elements.Clear();
        for (var i = 0; i < 60; i++)
            page.Elements.Add(new ObservedElement($"#hidden-{i}", "a", $"Hidden {i}", true, false, false, null));
        for (var i = 1; i <= 9; i++)
            page.Elements.Add(new ObservedElement($"#cell-{i}", "button", $"Cell {i}, empty", true, true, false, null));
        page.Elements.Add(new ObservedElement("#covered", "button", "Covered by overlay", true, true, false, null) { PointerReady = false });

        var operationProbabilities = new Dictionary<string, double>
        {
            ["click"] = 0.9, ["WAIT"] = 0.02, ["REOBSERVE"] = 0.02, ["ASK_USER"] = 0.02,
            ["STOP"] = 0.01, ["DONE"] = 0.02, ["BLOCKED"] = 0.01,
        };
        var targetProbabilities = Enumerable.Range(1, 9).ToDictionary(i => i.ToString(), i => i == 5 ? 0.9 : 0.1 / 8);
        handler.Respond = _ => Json(JsonSerializer.Serialize(new
        {
            answers = new
            {
                operation = new { choice = "click", confidence = 0.9, probabilities = operationProbabilities },
                click_target = new { choice = "5", confidence = 0.9, probabilities = targetProbabilities },
            },
        }));

        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,game");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Play the strongest Tic-Tac-Toe move as X"));
        using var request = JsonDocument.Parse(handler.LastBody!);

        Assert.Equal("jev", result.GetProperty("decision_source").GetString());
        Assert.Equal("#cell-5", page.LastClickedSelector);
        Assert.Equal("Cell 5, empty", result.GetProperty("selected_candidate").GetProperty("name").GetString());
        Assert.Equal(9, request.RootElement.GetProperty("state").GetProperty("candidate_count").GetInt32());
        Assert.True(request.RootElement.GetProperty("state").GetProperty("candidate_set_complete").GetBoolean());
    }

    [Fact]
    public async Task BrowserObservation_FiltersHiddenElementsAndUsesAnOverflowSentinel()
    {
        var (manager, page, _, _) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        page.Elements.Clear();
        for (var i = 0; i < 60; i++)
            page.Elements.Add(new ObservedElement($"#hidden-{i}", "a", $"Hidden {i}", true, false, false, null));
        for (var i = 0; i < 9; i++)
            page.Elements.Add(new ObservedElement($"#visible-{i}", "button", $"Visible {i}", true, true, false, null));

        var observation = await session.Page.ObserveAsync();
        Assert.True(observation.Complete);
        Assert.Equal(9, observation.Candidates.Count);

        page.Elements.Clear();
        for (var i = 0; i < 128; i++)
            page.Elements.Add(new ObservedElement($"#at-limit-{i}", "button", $"Button {i}", true, true, false, null));
        observation = await session.Page.ObserveAsync();
        Assert.True(observation.Complete);
        Assert.Equal(128, observation.Candidates.Count);

        page.Elements.Add(new ObservedElement("#overflow", "button", "Overflow", true, true, false, null));
        observation = await session.Page.ObserveAsync();
        Assert.False(observation.Complete);
        Assert.Equal("candidate_limit", observation.Truncation);
        Assert.Equal(128, observation.Candidates.Count);
    }

    [Fact]
    public async Task BrowserJev_AllowsCompleteCandidatesWhenAncillaryTextIsTruncated()
    {
        var (manager, page, handler, jev) = BuildFixture();
        page.Elements[0] = new ObservedElement("#signin", "button", new string('S', 9_000), true, true, false, null);
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Select the visible sign-in button"));
        using var request = JsonDocument.Parse(handler.LastBody!);

        Assert.Equal("jev", result.GetProperty("decision_source").GetString());
        Assert.True(request.RootElement.GetProperty("state").GetProperty("candidate_set_complete").GetBoolean());
        Assert.True(request.RootElement.GetProperty("state").GetProperty("observed_text_truncated").GetBoolean());
        Assert.Equal("#signin", page.LastClickedSelector);
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
    public async Task JevAct_RejectsASecondObservationEvenWhenTheDocumentEpochIsUnchanged()
    {
        var (manager, page, handler, jev) = BuildFixture();
        await using var scope = manager;
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("data:text/html,login");
        var tools = new BrowserUseTools(manager, jev, new WorkflowValueStore());
        handler.Respond = _ =>
        {
            session.Page.ObserveAsync(CancellationToken.None).GetAwaiter().GetResult();
            return Json(LoginPageBody);
        };

        var result = JsonSerializer.SerializeToElement(await tools.JevAct(session.Target.AdapterSessionId, "Sign in"));

        Assert.Contains("observation changed", result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(page.LastClickedSelector);
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
