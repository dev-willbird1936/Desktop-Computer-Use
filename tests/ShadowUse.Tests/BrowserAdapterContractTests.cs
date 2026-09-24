using System.Runtime.InteropServices;
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class BrowserAdapterContractTests
{
    [Fact]
    public void Manager_Constructor_DoesNotOpenBrowser()
    {
        var calls = 0;
        var manager = new BrowserSessionManager(() =>
        {
            calls++;
            return new FakeEngine();
        });

        Assert.Equal(0, calls);
        Assert.False(manager.HasOpened);
    }

    [Theory]
    [InlineData("http://example.com:9222")]
    [InlineData("http://192.168.1.20:9222")]
    [InlineData("file:///C:/tmp/browser")]
    [InlineData("http://user:secret@127.0.0.1:9222")]
    [InlineData("http://127.0.0.1:9222/json/version")]
    [InlineData("http://127.0.0.1:9222/?token=secret")]
    [InlineData("http://127.0.0.1:9222/#fragment")]
    [InlineData("ftp://127.0.0.1:9222")]
    public void Attach_RejectsNonLoopbackEndpoint(string endpoint)
    {
        var options = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = endpoint,
        };

        Assert.Throws<BrowserValidationException>(options.Validate);
    }

    [Theory]
    [InlineData("http://127.0.0.1:9222", true)]
    [InlineData("https://localhost:9222/", true)]
    [InlineData("ws://[::1]:9222/devtools/browser/abc", true)]
    [InlineData("wss://localhost:9222/devtools/browser/abc", true)]
    [InlineData("http://127.0.0.1:9222/custom", false)]
    [InlineData("http://localhost.evil.test:9222", false)]
    public void Attach_LoopbackEndpointRequiresApprovedRootOrWebSocketPath(string endpoint, bool valid)
    {
        Assert.Equal(valid, BrowserSessionOptions.IsLoopback(endpoint));
    }

    [Fact]
    public void PersistentSession_RequiresAbsoluteProfile_AndFreshSession_RejectsMissingExecutable()
    {
        var persistent = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.DedicatedPersistent,
            Channel = "msedge",
            ProfilePath = "relative-profile",
        };
        var fresh = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.FreshIsolated,
            Channel = null,
            ExecutablePath = null,
        };

        Assert.Throws<BrowserValidationException>(persistent.Validate);
        Assert.Throws<BrowserValidationException>(fresh.Validate);
    }

    [Fact]
    public async Task StaleBinding_AndDuplicateSelector_FailClosed()
    {
        await using var manager = new BrowserSessionManager(() => new FakeEngine());
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var observation = await session.Page.ObserveAsync();
        var binding = observation.Candidates.Single(candidate => candidate.Selector == "#save").Binding;

        await session.GotoAsync("https://example.test/changed");

        await Assert.ThrowsAsync<BrowserStaleException>(() => session.Page.ClickAsync(binding));
        await Assert.ThrowsAsync<BrowserAmbiguousException>(() => session.Page.ClickAsync(binding with
        {
            DocumentEpoch = session.Page.Target.DocumentEpoch,
            Selector = "button",
        }));
    }

    [Fact]
    public async Task PasswordObservation_IsMasked_WhileFillKeepsExactValue()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var observation = await session.Page.ObserveAsync();
        var password = observation.Candidates.Single(candidate => candidate.Password);

        Assert.Null(password.Text);
        await session.Page.FillAsync(password.Binding, "synthetic-password-42!");

        Assert.Equal("synthetic-password-42!", page.LastFilledValue);
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.ReadTextAsync(password.Binding));
    }

    [Fact]
    public async Task Readiness_PreventsBlockedClickButAllowsSemanticValueAction()
    {
        var page = new FakePage { PointerClickReady = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var save = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var clickError = await Assert.ThrowsAsync<BrowserAmbiguousException>(() => session.Page.ClickAsync(save.Binding));
        Assert.Contains("no click was dispatched", clickError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, page.ClickCalls);

        page.ForcedReadiness = null;
        await session.Page.FillAsync(save.Binding, "synthetic-value");
        Assert.Equal("synthetic-value", page.LastFilledValue);
        Assert.Equal(0, page.ClickCalls);
    }

    [Fact]
    public async Task CheckClickReadinessAsync_ExposesTheSameFailureAsClickAsyncWithoutDispatching()
    {
        // F42-R02: route_click's pre-check must decide the route the same way ClickAsync itself
        // would fail, WITHOUT ever attempting the dispatch -- a structural check, not exception
        // parsing.
        var page = new FakePage { PointerClickReady = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var save = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");

        var readiness = await session.Page.CheckClickReadinessAsync(save.Binding);

        Assert.False(readiness.Ready);
        Assert.Equal(BrowserReadinessFailure.Obscured, readiness.Failure);
        Assert.Equal(0, page.ClickCalls);
        Assert.Equal(0, page.PointClickCalls);
    }

    [Fact]
    public async Task BrowserUseTools_CheckClickReady_ReportsObscuredWithoutDispatch()
    {
        var page = new FakePage { PointerClickReady = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var save = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#save");

        var (ready, found, failure) = await tools.CheckClickReady(session.Target.AdapterSessionId, save.CandidateId, CancellationToken.None);

        Assert.True(found);
        Assert.False(ready);
        Assert.Equal(nameof(BrowserReadinessFailure.Obscured), failure);
        Assert.Equal(0, page.ClickCalls);
    }

    [Fact]
    public async Task BrowserUseTools_PointClick_SurfacesAbortWithZeroExecutedEffect()
    {
        // F42-R02 safety contract: an abort from the engine (probe mismatch, no bounding box,
        // etc.) must surface as ok:false with the real abort reason, never a silent success.
        var page = new FakePage();
        page.PointClickHandler = (_, _) => new BrowserPointClickResult(false, false, false, "element-verified", 12, 34, "elementFromPoint at the target coordinates did not resolve to this element; no click dispatched.");
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var save = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#save");

        var result = await tools.PointClick(session.Target.AdapterSessionId, save.CandidateId);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("executed").GetBoolean());
        Assert.False(json.GetProperty("move_verified").GetBoolean());
        Assert.Contains("elementFromPoint", json.GetProperty("abort_reason").GetString());
        Assert.Equal(1, page.PointClickCalls);
    }

    [Fact]
    public async Task BrowserUseTools_PointClick_ReportsPressVerifiedFalseWhenPressLandedOnTheWrongTarget()
    {
        // A press CAN still be dispatched (Executed:true) if the move probe passed but the press
        // probe later mismatched (layout shift) -- must be reported honestly, not hidden as ok.
        var page = new FakePage();
        page.PointClickHandler = (_, _) => new BrowserPointClickResult(true, true, false, "element-verified", 12, 34, "A press was dispatched, but the recorded pointerdown target did not match the probed element.");
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var save = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#save");

        var result = await tools.PointClick(session.Target.AdapterSessionId, save.CandidateId);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.False(json.GetProperty("press_verified").GetBoolean());
    }

    [Fact]
    public async Task BrowserUseTools_PointClick_ReportsOkTrueWhenBothProbesVerify()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var save = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#save");

        var result = await tools.PointClick(session.Target.AdapterSessionId, save.CandidateId);
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("element-verified", json.GetProperty("verification_kind").GetString());
        Assert.Equal(1, page.PointClickCalls);
    }

    [Fact]
    public async Task CheckFillReadinessAsync_ExposesTheSameFailureAsFillAsyncWithoutDispatching()
    {
        // F42-R02: route_set_text's pre-check must decide the route the same way FillAsync
        // itself would fail, WITHOUT ever attempting the dispatch.
        var page = new FakePage { PointerFillReady = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var field = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#field");

        var readiness = await session.Page.CheckFillReadinessAsync(field.Binding);

        Assert.False(readiness.Ready);
        Assert.Equal(BrowserReadinessFailure.Obscured, readiness.Failure);
        Assert.Null(page.LastFilledValue);
        Assert.Equal(0, page.PointFillCalls);
    }

    [Fact]
    public async Task BrowserUseTools_CheckFillReady_ReportsObscuredWithoutDispatch()
    {
        var page = new FakePage { PointerFillReady = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var field = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#field");

        var (ready, found, failure) = await tools.CheckFillReady(session.Target.AdapterSessionId, field.CandidateId, CancellationToken.None);

        Assert.True(found);
        Assert.False(ready);
        Assert.Equal(nameof(BrowserReadinessFailure.Obscured), failure);
        Assert.Null(page.LastFilledValue);
    }

    [Fact]
    public async Task BrowserUseTools_PointFill_SurfacesAbortWithZeroExecutedEffect()
    {
        // F42-R02 safety contract: an abort from the engine must surface as ok:false with the
        // real abort reason, never a silent success -- and never a key event sent.
        var page = new FakePage();
        page.PointFillHandler = (_, _, _) => new BrowserPointFillResult(false, false, false, 12, 34, null, "elementFromPoint at the target coordinates did not resolve to this element; no fill dispatched.");
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var field = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#field");

        var result = await tools.PointFill(session.Target.AdapterSessionId, field.CandidateId, "synthetic-value");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("executed").GetBoolean());
        Assert.False(json.GetProperty("focus_verified").GetBoolean());
        Assert.Contains("elementFromPoint", json.GetProperty("abort_reason").GetString());
        Assert.Equal(1, page.PointFillCalls);
    }

    [Fact]
    public async Task BrowserUseTools_PointFill_ReportsFocusVerifiedFalseWhenClickLandedOnTheWrongTarget()
    {
        // A click CAN still be dispatched (Executed:true) to place focus, but if
        // document.activeElement did not become the target element, zero key events must have
        // been sent -- reported honestly, not hidden as ok.
        var page = new FakePage();
        page.PointFillHandler = (_, _, _) => new BrowserPointFillResult(true, false, false, 12, 34, null, "A click was dispatched to place focus, but document.activeElement did not become this element; no key events sent.");
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var field = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#field");

        var result = await tools.PointFill(session.Target.AdapterSessionId, field.CandidateId, "synthetic-value");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("executed").GetBoolean());
        Assert.False(json.GetProperty("focus_verified").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("observed_value").ValueKind);
    }

    [Fact]
    public async Task BrowserUseTools_PointFill_ReportsOkTrueWhenBothProbesVerify()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.Page.ObserveAsync();
        var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
        var field = session.Page.LastObservation!.Candidates.Single(item => item.Selector == "#field");

        var result = await tools.PointFill(session.Target.AdapterSessionId, field.CandidateId, "synthetic-value");
        var json = JsonSerializer.SerializeToElement(result);

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("synthetic-value", json.GetProperty("observed_value").GetString());
        Assert.Equal("synthetic-value", page.LastFilledValue);
        Assert.Equal(1, page.PointFillCalls);
    }

    [Fact]
    public async Task Readiness_RejectsDisabledAndReadOnlyWithoutDispatch()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var button = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        page.ForcedReadiness = new BrowserReadinessResult(false, BrowserReadinessFailure.Disabled);
        var disabled = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.ClickAsync(button.Binding));
        Assert.Contains("disabled", disabled.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, page.ClickCalls);

        page.ForcedReadiness = new BrowserReadinessResult(false, BrowserReadinessFailure.ReadOnly);
        var readOnly = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.FillAsync(button.Binding, "no-change"));
        Assert.Contains("read-only", readOnly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(page.LastFilledValue);
    }

    [Fact]
    public async Task LiveInstalledEdge_ReadinessRefusesOverlayClickButAllowsSemanticFill()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 2_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <input id="field" aria-label="Field" oninput="document.getElementById('marker').textContent=this.value">
                <button id="target" onclick="document.getElementById('marker').textContent='clicked'">Target</button>
                <button id="disabled" disabled onclick="document.getElementById('marker').textContent='disabled-clicked'">Disabled</button>
                <input id="readonly" aria-label="Readonly" readonly value="fixed">
                <button id="marker">untouched</button>
                <div id="overlay"></div>
                <style>
                #target { position:absolute; left:0; top:0; width:180px; height:60px; }
                #overlay { position:absolute; z-index:10; left:0; top:0; width:180px; height:60px; background:rgba(0,0,0,.3); }
                </style>
                """;
            var opened = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var target = opened.Candidates.Single(item => item.Selector == "#target");
            var field = opened.Candidates.Single(item => item.Selector == "#field");
            var disabled = opened.Candidates.Single(item => item.Selector == "#disabled");
            var readOnly = opened.Candidates.Single(item => item.Selector == "#readonly");

            var blocked = await Assert.ThrowsAsync<BrowserAmbiguousException>(() => session.Page.ClickAsync(target.Binding));
            Assert.Contains("obscured", blocked.Message, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.ClickAsync(disabled.Binding));
            await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.FillAsync(readOnly.Binding, "rejected"));
            await session.Page.FillAsync(field.Binding, "synthetic-value");
            var after = await session.Page.ObserveAsync();
            Assert.Equal("synthetic-value", after.Candidates.Single(item => item.Selector == "#marker").Text);
            Assert.Equal("fixed", await session.Page.ReadAttributeAsync(after.Candidates.Single(item => item.Selector == "#readonly").Binding, "value"));
        }
    }

    [Fact]
    public async Task BrowserWait_ReturnsUnknownOnTimeoutAndAcceptsTypedPredicate()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var candidate = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var value = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.ValueEquals, candidate.Binding, "done", null, 250));
        Assert.True(value.Satisfied);
        Assert.False(value.TimedOut);

        page.WaitTextResult = false;
        var missing = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.TextEquals, candidate.Binding, "never", null, 50));
        Assert.False(missing.Satisfied);
        Assert.True(missing.TimedOut);
        Assert.Contains("unknown", missing.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserWait_ValidatesConditionArgumentsAndStaleBindings()
    {
        await using var manager = new BrowserSessionManager(() => new FakeEngine());
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.ValueEquals, null, "x", null, 100)));
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.NavigationCommitted, null, null, null, 100)));
        var candidate = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        await session.GotoAsync("about:blank#new-document");
        await Assert.ThrowsAsync<BrowserStaleException>(() => session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.TextEquals, candidate.Binding, "done", null, 100)));
    }

    [Fact]
    public async Task BrowserWait_DownloadPredicateRequiresAnOwnedSavedDownload()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.DownloadCompleted, null, null, null, 100)));
    }

    [Fact]
    public async Task BrowserWait_DownloadCompletedRechecksExactSavedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-wait-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var page = new FakePage();
            await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
            var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, AllowedDownloadRoot = root });
            var candidate = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
            await session.Page.DownloadAsync(candidate.Binding);
            var completed = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.DownloadCompleted, null, null, null, 500));
            Assert.True(completed.Satisfied);
            Assert.False(completed.TimedOut);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_Extract_ReturnsTypedFieldsWithSourceAndDistinctMissingAmbiguousStates()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <div id="name">Fixture Widget</div>
                <div id="price">$1,234.56</div>
                <div id="qty">out of stock</div>
                <div id="ordered">2026-03-14</div>
                <div class="dup">first</div>
                <div class="dup">second</div>
                """;
            await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));

            var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
            var result = await tools.Extract(session.Target.AdapterSessionId,
            [
                new BrowserExtractField("name", "#name", "text", Required: true),
                new BrowserExtractField("price", "#price", "currency", Required: true),
                new BrowserExtractField("qty", "#qty", "number"),
                new BrowserExtractField("ordered", "#ordered", "date"),
                new BrowserExtractField("missing", "#does-not-exist", "text", Required: true),
                new BrowserExtractField("ambiguous", ".dup", "text"),
            ]);

            var fields = (IReadOnlyList<BrowserExtractResult>)result.GetType().GetProperty("fields")!.GetValue(result)!;
            var byName = fields.ToDictionary(f => f.Name);

            Assert.Equal("ok", byName["name"].Status);
            Assert.Equal("Fixture Widget", byName["name"].Value);

            Assert.Equal("ok", byName["price"].Status);
            Assert.Equal(1234.56, byName["price"].Value);
            Assert.Equal("$", byName["price"].Unit);
            Assert.Equal("#price", byName["price"].Source.Selector);

            Assert.Equal("type_mismatch", byName["qty"].Status);
            Assert.Equal("out of stock", byName["qty"].RawText);

            Assert.Equal("ok", byName["ordered"].Status);
            Assert.StartsWith("2026-03-14", (string)byName["ordered"].Value!);

            Assert.Equal("missing", byName["missing"].Status);
            Assert.Equal("ambiguous", byName["ambiguous"].Status);

            var ok = (bool)result.GetType().GetProperty("ok")!.GetValue(result)!;
            Assert.False(ok); // "missing" is required and did not resolve
            var missingRequired = (string[])result.GetType().GetProperty("missing_required")!.GetValue(result)!;
            Assert.Contains("missing", missingRequired);
            Assert.DoesNotContain("name", missingRequired);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_Extract_QuoteMatchesSourceByteForByteAndRejectsAnAlteredQuote()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            const string terms = "Refunds are available within 30 days of purchase, no exceptions.";
            await session.GotoAsync("data:text/html," + Uri.EscapeDataString($"<div id=\"terms\">{terms}</div>"));

            var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
            var exact = await tools.Extract(session.Target.AdapterSessionId,
                [new BrowserExtractField("terms", "#terms", "text", Quote: "within 30 days of purchase")]);
            var exactField = ((IReadOnlyList<BrowserExtractResult>)exact.GetType().GetProperty("fields")!.GetValue(exact)!).Single();

            Assert.NotNull(exactField.QuoteOffset);
            Assert.Equal("within 30 days of purchase", exactField.QuoteText);
            Assert.Equal(terms.Substring(exactField.QuoteOffset!.Value, exactField.QuoteText!.Length), exactField.QuoteText);
            Assert.NotNull(exactField.CapturedAt);

            var altered = await tools.Extract(session.Target.AdapterSessionId,
                [new BrowserExtractField("terms", "#terms", "text", Quote: "within 30 days, no exceptions")]); // not a verbatim substring
            var alteredField = ((IReadOnlyList<BrowserExtractResult>)altered.GetType().GetProperty("fields")!.GetValue(altered)!).Single();

            Assert.Null(alteredField.QuoteOffset);
            Assert.Null(alteredField.QuoteText);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_Diagnostics_CapturesConsoleErrorAndFailedRequest()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <script>
                console.error('fixture console error');
                fetch('https://dcu-fixture-does-not-resolve.invalid/x').catch(() => {});
                </script>
                """;
            await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));

            IReadOnlyList<BrowserDiagnosticEntry> entries = [];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                entries = await session.Page.DiagnosticsAsync();
                if (entries.Any(e => e.Kind == "console_error") && entries.Any(e => e.Kind == "request_failed"))
                    break;
                await Task.Delay(100);
            }

            Assert.Contains(entries, e => e.Kind == "console_error" && e.Text.Contains("fixture console error", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.Kind == "request_failed");
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_ConditionWaits_HandleDebouncePollingNavigationAndTimeout()
    {
        using var navigationPage = LoopbackPage.Start("<a id=\"next\" href=\"?next\">Next</a><script>setTimeout(()=>location.assign('?next'),300)</script>");
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <input id="query" aria-label="Query">
                <button id="result">idle</button>
                <button id="vanish">temporary</button>
                <script>
                const input = document.getElementById('query');
                input.addEventListener('input', () => setTimeout(() => {
                  if (input.value === 'fixture-query') document.getElementById('result').textContent = 'validated';
                }, 260));
                setInterval(() => { document.body.dataset.polls = String(Number(document.body.dataset.polls || 0) + 1); }, 30);
                document.getElementById('result').addEventListener('click', () => setTimeout(() => document.getElementById('vanish').remove(), 180));
                </script>
                """;
            var opened = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var query = opened.Candidates.Single(item => item.Selector == "#query");
            var result = opened.Candidates.Single(item => item.Selector == "#result");
            var vanish = opened.Candidates.Single(item => item.Selector == "#vanish");
            await session.Page.FillAsync(query.Binding, "fixture-query");
            var validated = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.TextEquals, result.Binding, "validated", null, 3_000));
            Assert.True(validated.Satisfied);
            Assert.Equal("validated", validated.Observation.Candidates.Single(item => item.Selector == "#result").Text);
            var exactValue = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.ValueEquals, query.Binding, "fixture-query", null, 1_000));
            Assert.True(exactValue.Satisfied);
            await session.Page.ClickAsync(validated.Observation.Candidates.Single(item => item.Selector == "#result").Binding);
            var disappeared = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.ElementDisappears, vanish.Binding, null, null, 3_000));
            Assert.True(disappeared.Satisfied);
            Assert.DoesNotContain(disappeared.Observation.Candidates, item => item.Selector == "#vanish");

            var missing = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.TextEquals, result.Binding, "never", null, 120));
            Assert.False(missing.Satisfied);
            Assert.True(missing.TimedOut);
            Assert.Contains("unknown", missing.Reason!, StringComparison.OrdinalIgnoreCase);

            var beforeNav = await session.GotoAsync(navigationPage.Url);
            var expectedUrl = new Uri(navigationPage.Url).GetLeftPart(UriPartial.Path) + "?next";
            var alreadyAtUrl = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.NavigationCommitted, null, null, new Uri(navigationPage.Url).GetLeftPart(UriPartial.Path), 100));
            Assert.False(alreadyAtUrl.Satisfied);
            Assert.True(alreadyAtUrl.TimedOut);
            var nav = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.NavigationCommitted, null, null, expectedUrl, 3_000));
            Assert.True(nav.Satisfied, $"{nav.Reason}; expected={expectedUrl}; actual={nav.Url}");
            Assert.Equal(expectedUrl, nav.Url);
        }
    }

    [Fact]
    public void WebMcpSchema_ValidatesObjectArgumentsAndRejectsUnsupportedSchema()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{"query":{"type":"string","minLength":1},"limit":{"type":"integer","minimum":1,"maximum":5}},"required":["query"],"additionalProperties":false}
            """)!;
        Assert.Null(BrowserWebMcp.ValidateSchema(schema));
        Assert.Null(BrowserWebMcp.ValidateArguments(schema, System.Text.Json.Nodes.JsonNode.Parse("{\"query\":\"desk\",\"limit\":3}")!.AsObject()));
        Assert.Contains("Required", BrowserWebMcp.ValidateArguments(schema, new System.Text.Json.Nodes.JsonObject())!, StringComparison.Ordinal);
        Assert.Contains("not allowed", BrowserWebMcp.ValidateArguments(schema, System.Text.Json.Nodes.JsonNode.Parse("{\"query\":\"desk\",\"extra\":true}")!.AsObject())!, StringComparison.Ordinal);
        var unsupported = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"patternProperties\":{}}")!;
        Assert.NotNull(BrowserWebMcp.ValidateSchema(unsupported));
        var malformed = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":7}")!;
        Assert.NotNull(BrowserWebMcp.ValidateSchema(malformed));
        var invalidBound = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"string\",\"minLength\":-1}")!;
        Assert.NotNull(BrowserWebMcp.ValidateSchema(invalidBound));
        var undeclaredRequired = System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"required\":[\"missing\"]}")!;
        Assert.NotNull(BrowserWebMcp.ValidateSchema(undeclaredRequired));
    }

    [Fact]
    public async Task WebMcpAndBrowserFallback_ReachTheSameOutcomeAndRecordRoute()
    {
        static async Task<(string Route, string Outcome)> RunAsync(bool supported)
        {
            var page = new FakePage
            {
                WebMcpSnapshot = supported
                    ? new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
                        "finish-task", "Complete the task", "http://127.0.0.1:8080",
                        System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"))])
                    : new BrowserWebMcpPageSnapshot(false, "http://127.0.0.1:8080", []),
            };
            page.GotoAsync("http://127.0.0.1:8080/task", 1000, CancellationToken.None).GetAwaiter().GetResult();
            await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
            var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
            await session.GotoAsync("http://127.0.0.1:8080/task");
            var observation = await session.Page.ObserveAsync();
            var save = observation.Candidates.Single(candidate => candidate.Selector == "#save");
            var catalog = await session.Page.DiscoverWebMcpAsync();
            var tool = catalog.Tools.FirstOrDefault(candidate => candidate.Name == "finish-task");
            var route = await session.Page.RouteWebMcpOrClickAsync(tool, new System.Text.Json.Nodes.JsonObject(), save.Binding);
            return (route.Route, route.Observation.Candidates.Single(candidate => candidate.Selector == "#save").Text!);
        }

        var withWebMcp = await RunAsync(true);
        var withoutWebMcp = await RunAsync(false);
        Assert.Equal(("webmcp", "done"), withWebMcp);
        Assert.Equal(("browser", "done"), withoutWebMcp);
    }

    [Fact]
    public async Task LiveInstalledEdge_WebMcpEnabledAndAbsent_UseExplicitRoutesWithSameOutcome()
    {
        const string pageContent = """
            <button id="outcome">idle</button>
            <button id="finish">Finish</button>
            <script>
            document.getElementById('finish').addEventListener('click', () => {
              document.getElementById('outcome').textContent = 'done';
            });
            __WEBMCP_FIXTURE__
            </script>
            """;
        const string webMcpFixture = """
            document.modelContext = {
              async getTools() {
                return [{
                  name: 'finish-task',
                  description: 'Finish the local task',
                  inputSchema: { type: 'object', properties: {}, additionalProperties: false },
                  origin: location.origin,
                  window
                }];
              },
              async executeTool(tool, args) {
                document.getElementById('outcome').textContent = 'done';
                return { content: [{ type: 'text', text: 'done' }] };
              }
            };
            """;
        using var enabledPage = LoopbackPage.Start(pageContent.Replace("__WEBMCP_FIXTURE__", webMcpFixture, StringComparison.Ordinal));
        using var absentPage = LoopbackPage.Start(pageContent.Replace("__WEBMCP_FIXTURE__", "", StringComparison.Ordinal));
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var enabled = await session.GotoAsync(enabledPage.Url);
            var enabledCatalog = await session.Page.DiscoverWebMcpAsync();
            Assert.True(enabledCatalog.Supported);
            var tool = Assert.Single(enabledCatalog.Tools);
            Assert.True(tool.Valid, tool.ValidationError);
            var finish = enabled.Candidates.Single(candidate => candidate.Selector == "#finish");
            var webRoute = await session.Page.RouteWebMcpOrClickAsync(tool, new System.Text.Json.Nodes.JsonObject(), finish.Binding);
            Assert.Equal("webmcp", webRoute.Route);
            Assert.Equal("done", webRoute.Observation.Candidates.Single(candidate => candidate.Selector == "#outcome").Text);

            var absent = await session.GotoAsync(absentPage.Url);
            var absentCatalog = await session.Page.DiscoverWebMcpAsync();
            Assert.False(absentCatalog.Supported);
            Assert.Empty(absentCatalog.Tools);
            var fallback = absent.Candidates.Single(candidate => candidate.Selector == "#finish");
            var browserRoute = await session.Page.RouteWebMcpOrClickAsync(null, null, fallback.Binding);
            Assert.Equal("browser", browserRoute.Route);
            Assert.Equal("done", browserRoute.Observation.Candidates.Single(candidate => candidate.Selector == "#outcome").Text);
        }
    }

    [Fact]
    public async Task WebMcpInvalidRegistration_FallsBackWithoutCallingInvalidTool()
    {
        var page = new FakePage
        {
            WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
                "finish-task", "Complete the task", "http://127.0.0.1:8080",
                System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"patternProperties\":{}}"))]),
        };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("http://127.0.0.1:8080/task");
        var fallback = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var invalid = Assert.Single((await session.Page.DiscoverWebMcpAsync()).Tools);
        Assert.False(invalid.Valid);

        var route = await session.Page.RouteWebMcpOrClickAsync(invalid, new System.Text.Json.Nodes.JsonObject(), fallback.Binding);
        Assert.Equal("browser", route.Route);
        Assert.Equal(0, page.WebMcpInvokeCalls);
        Assert.Equal(1, page.ClickCalls);
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("file:///tmp/webmcp.html")]
    public async Task WebMcpUntrustedOrNonLoopbackOrigin_IsNotOffered(string url)
    {
        var page = new FakePage
        {
            WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "https://example.test", [new BrowserWebMcpToolDescriptor(
                "finish-task", "Complete the task", "https://example.test",
                System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{}}"))]),
        };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync(url);
        var catalog = await session.Page.DiscoverWebMcpAsync();
        Assert.False(catalog.Supported);
        Assert.Empty(catalog.Tools);
    }

    [Fact]
    public async Task WebMcpChangedRegistration_FallsBackWithoutCallingOldRegistration()
    {
        var page = new FakePage
        {
            WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
                "finish-task", "Complete the task", "http://127.0.0.1:8080",
                System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"))]),
        };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("http://127.0.0.1:8080/task");
        var candidate = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var discovered = (await session.Page.DiscoverWebMcpAsync()).Tools.Single();
        page.WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
            "finish-task", "Changed registration", "http://127.0.0.1:8080",
            System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"))]);

        var route = await session.Page.RouteWebMcpOrClickAsync(discovered, new System.Text.Json.Nodes.JsonObject(), candidate.Binding);
        Assert.Equal("browser", route.Route);
        Assert.Contains("changed", route.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, page.WebMcpInvokeCalls);
        Assert.Equal(1, page.ClickCalls);
        Assert.Equal("done", route.Observation.Candidates.Single(item => item.Selector == "#save").Text);
    }

    [Fact]
    public async Task WebMcpInvalidArguments_AreRejectedBeforeInvocationOrFallback()
    {
        var page = new FakePage
        {
            WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
                "finish-task", "Complete the task", "http://127.0.0.1:8080",
                System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}"))]),
        };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("http://127.0.0.1:8080/task");
        var fallback = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var tool = (await session.Page.DiscoverWebMcpAsync()).Tools.Single();
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.RouteWebMcpOrClickAsync(tool, new System.Text.Json.Nodes.JsonObject(), fallback.Binding));
        Assert.Equal(0, page.WebMcpInvokeCalls);
        Assert.Equal(0, page.ClickCalls);
    }

    [Fact]
    public async Task WebMcpExecutionFailure_IsNotRetriedAsBrowserClick()
    {
        var page = new FakePage
        {
            WebMcpSnapshot = new BrowserWebMcpPageSnapshot(true, "http://127.0.0.1:8080", [new BrowserWebMcpToolDescriptor(
                "finish-task", "Complete the task", "http://127.0.0.1:8080",
                System.Text.Json.Nodes.JsonNode.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"))]),
            WebMcpInvokeResult = new BrowserWebMcpInvokeResult(BrowserWebMcpInvokeStatus.Failed, null, "controlled failure"),
        };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        await session.GotoAsync("http://127.0.0.1:8080/task");
        var candidate = (await session.Page.ObserveAsync()).Candidates.Single(item => item.Selector == "#save");
        var tool = (await session.Page.DiscoverWebMcpAsync()).Tools.Single();
        var error = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.RouteWebMcpOrClickAsync(tool, new System.Text.Json.Nodes.JsonObject(), candidate.Binding));
        Assert.Contains("controlled failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, page.WebMcpInvokeCalls);
        Assert.Equal(0, page.ClickCalls);
    }

    [Fact]
    public async Task AttachedSession_DoesNotCloseCallerResources()
    {
        var browser = new FakeBrowser { OwnsResources = false };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(browser: browser));
        var session = await manager.OpenAsync(new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
        });

        Assert.Equal(BrowserSessionOwnership.Attached, session.Target.Ownership);
        await session.DisposeAsync();

        Assert.Equal(0, browser.CloseCalls);
    }

    [Fact]
    public async Task OwnedSession_ClosesOnlyItsResources()
    {
        var browser = new FakeBrowser { OwnsResources = true };
        await using var manager = new BrowserSessionManager(() => new FakeEngine(browser: browser));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });

        Assert.Equal(BrowserSessionOwnership.Owned, session.Target.Ownership);
        await session.DisposeAsync();

        Assert.Equal(1, browser.CloseCalls);
    }

    [Fact]
    public async Task IframeCandidate_ActsInsideItsFrame_NotTheParent()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var observation = await session.Page.ObserveAsync();
        var inside = observation.Candidates.Single(candidate => candidate.Selector == "#inside");

        Assert.Equal("iframe-0", inside.Binding.FrameId);
        await session.Page.ClickAsync(inside.Binding);

        Assert.Equal("iframe-0", page.LastClickedFrame);
    }

    [Fact]
    public async Task LiveInstalledEdge_IframeAction_DoesNotHitParentControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="parent">Save</button>
                <iframe srcdoc="<button id='inside' onclick=&quot;parent.document.getElementById('parent').textContent='inside'&quot;>Save</button>"></iframe>
                """;
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var inside = observation.Candidates.Single(candidate => candidate.Binding.FrameId == "iframe-0" && candidate.Selector == "#inside");
            var parent = observation.Candidates.Single(candidate => candidate.Binding.FrameId == "main" && candidate.Selector == "#parent");
            Assert.Equal("Save", parent.Text);
            await session.Page.ClickAsync(inside.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("inside", after.Candidates.Single(candidate => candidate.Selector == "#parent" && candidate.Binding.FrameId == "main").Text);
        }
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("iframe-0", true)]
    [InlineData("iframe-0/iframe-1", true)]
    [InlineData("iframe-0/iframe-1/iframe-2", true)]
    [InlineData("iframe-0/iframe-1/iframe-2/iframe-3", true)]
    [InlineData("iframe-0/iframe-1/iframe-2/iframe-3/iframe-4", false)]
    [InlineData("iframe-", false)]
    [InlineData("iframe-0/nope", false)]
    public void FrameId_AllowsOneNestedLevelOnly(string frameId, bool supported)
    {
        Assert.Equal(supported, BrowserFrames.IsSupported(frameId));
    }

    [Fact]
    public async Task NestedFrameId_IsPassedToTheInnerFrame()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var inside = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#inside");
        var nested = inside.Binding with { FrameId = "iframe-0/iframe-0" };
        await session.Page.ClickAsync(nested);
        Assert.Equal("iframe-0/iframe-0", page.LastClickedFrame);
    }

    [Fact]
    public async Task FrameDeeperThanFourLevels_IsRejected()
    {
        await using var manager = new BrowserSessionManager(() => new FakeEngine());
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var binding = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save").Binding
            with { FrameId = "iframe-0/iframe-0/iframe-0/iframe-0/iframe-0" };
        await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(binding));
    }

    [Fact]
    public async Task LiveInstalledEdge_NestedIframe_ActsOnInnerControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="outer">Save</button>
                <iframe id="parent-frame" srcdoc="<button id='mid'>Save</button><iframe id='child-frame' srcdoc=&quot;<button id='inner'>Save</button>&quot;></iframe>"></iframe>
                <script>
                function wire() {
                  const parent = document.getElementById('parent-frame').contentDocument;
                  const childFrame = parent && parent.getElementById('child-frame');
                  const child = childFrame && childFrame.contentDocument;
                  const inner = child && child.getElementById('inner');
                  const mid = parent && parent.getElementById('mid');
                  if (!inner || !mid) return false;
                  document.getElementById('outer').addEventListener('click', () => { document.getElementById('marker').textContent = 'outer'; });
                  mid.addEventListener('click', () => { document.getElementById('marker').textContent = 'mid'; });
                  inner.addEventListener('click', () => { document.getElementById('marker').textContent = 'inner'; });
                  document.getElementById('marker').textContent = 'ready';
                  return true;
                }
                (function wait() { if (!wire()) setTimeout(wait, 50); })();
                </script>
                """;
            BrowserCandidate? inner = null;
            string? marker = null;
            for (var attempt = 0; attempt < 40 && marker != "ready"; attempt++)
            {
                var observation = attempt == 0
                    ? await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html))
                    : await session.Page.ObserveAsync();
                marker = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#marker" && candidate.Binding.FrameId == "main")?.Text;
                inner = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#inner");
                if (marker != "ready" || inner?.Binding.FrameId != "iframe-0/iframe-0")
                    await Task.Delay(100);
            }

            Assert.Equal("ready", marker);
            Assert.NotNull(inner);
            Assert.Equal("iframe-0/iframe-0", inner.Binding.FrameId);
            var ready = await session.Page.ObserveAsync();
            Assert.Equal("main", ready.Candidates.Single(candidate => candidate.Selector == "#outer").Binding.FrameId);
            Assert.Equal("iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#mid").Binding.FrameId);
            await session.Page.ClickAsync(inner.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("inner", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#outer").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#mid").Text);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_ThirdIframe_ActsOnInnermostControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="outer">Save</button>
                <iframe id="level-1" srcdoc="<button id='mid'>Save</button><iframe id='level-2' srcdoc=&quot;<button id='inner'>Save</button><iframe id='level-3' srcdoc=&amp;quot;<button id='deep'>Save</button>&amp;quot;></iframe>&quot;></iframe>"></iframe>
                <script>
                function wire() {
                  const first = document.getElementById('level-1').contentDocument;
                  const secondFrame = first && first.getElementById('level-2');
                  const second = secondFrame && secondFrame.contentDocument;
                  const thirdFrame = second && second.getElementById('level-3');
                  const third = thirdFrame && thirdFrame.contentDocument;
                  const deep = third && third.getElementById('deep');
                  const inner = second && second.getElementById('inner');
                  const mid = first && first.getElementById('mid');
                  if (!deep || !inner || !mid) return false;
                  document.getElementById('outer').addEventListener('click', () => { document.getElementById('marker').textContent = 'outer'; });
                  mid.addEventListener('click', () => { document.getElementById('marker').textContent = 'mid'; });
                  inner.addEventListener('click', () => { document.getElementById('marker').textContent = 'inner'; });
                  deep.addEventListener('click', () => { document.getElementById('marker').textContent = 'deep'; });
                  document.getElementById('marker').textContent = 'ready';
                  return true;
                }
                (function wait() { if (!wire()) setTimeout(wait, 50); })();
                </script>
                """;
            BrowserCandidate? deep = null;
            string? marker = null;
            for (var attempt = 0; attempt < 40 && marker != "ready"; attempt++)
            {
                var observation = attempt == 0
                    ? await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html))
                    : await session.Page.ObserveAsync();
                marker = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#marker" && candidate.Binding.FrameId == "main")?.Text;
                deep = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#deep");
                if (marker != "ready" || deep?.Binding.FrameId != "iframe-0/iframe-0/iframe-0")
                    await Task.Delay(100);
            }

            Assert.Equal("ready", marker);
            Assert.NotNull(deep);
            Assert.Equal("iframe-0/iframe-0/iframe-0", deep.Binding.FrameId);
            var ready = await session.Page.ObserveAsync();
            Assert.Equal("main", ready.Candidates.Single(candidate => candidate.Selector == "#outer").Binding.FrameId);
            Assert.Equal("iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#mid").Binding.FrameId);
            Assert.Equal("iframe-0/iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#inner").Binding.FrameId);
            await session.Page.ClickAsync(deep.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("deep", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#outer").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#mid").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#inner").Text);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_FourthIframe_ActsOnInnermostControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="outer">Save</button>
                <iframe id="level-1" srcdoc="<button id='mid'>Save</button><iframe id='level-2' srcdoc=&quot;<button id='inner'>Save</button><iframe id='level-3' srcdoc=&amp;quot;<button id='deep'>Save</button><iframe id='level-4' srcdoc=&amp;amp;quot;<button id='deepest'>Save</button>&amp;amp;quot;></iframe>&amp;quot;></iframe>&quot;></iframe>"></iframe>
                <script>
                function wire() {
                  const first = document.getElementById('level-1').contentDocument;
                  const secondFrame = first && first.getElementById('level-2');
                  const second = secondFrame && secondFrame.contentDocument;
                  const thirdFrame = second && second.getElementById('level-3');
                  const third = thirdFrame && thirdFrame.contentDocument;
                  const fourthFrame = third && third.getElementById('level-4');
                  const fourth = fourthFrame && fourthFrame.contentDocument;
                  const deepest = fourth && fourth.getElementById('deepest');
                  const deep = third && third.getElementById('deep');
                  const inner = second && second.getElementById('inner');
                  const mid = first && first.getElementById('mid');
                  if (!deepest || !deep || !inner || !mid) return false;
                  document.getElementById('outer').addEventListener('click', () => { document.getElementById('marker').textContent = 'outer'; });
                  mid.addEventListener('click', () => { document.getElementById('marker').textContent = 'mid'; });
                  inner.addEventListener('click', () => { document.getElementById('marker').textContent = 'inner'; });
                  deep.addEventListener('click', () => { document.getElementById('marker').textContent = 'deep'; });
                  deepest.addEventListener('click', () => { document.getElementById('marker').textContent = 'deepest'; });
                  document.getElementById('marker').textContent = 'ready';
                  return true;
                }
                (function wait() { if (!wire()) setTimeout(wait, 50); })();
                </script>
                """;
            BrowserCandidate? deepest = null;
            string? marker = null;
            for (var attempt = 0; attempt < 40 && marker != "ready"; attempt++)
            {
                var observation = attempt == 0
                    ? await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html))
                    : await session.Page.ObserveAsync();
                marker = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#marker" && candidate.Binding.FrameId == "main")?.Text;
                deepest = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#deepest");
                if (marker != "ready" || deepest?.Binding.FrameId != "iframe-0/iframe-0/iframe-0/iframe-0")
                    await Task.Delay(100);
            }

            Assert.Equal("ready", marker);
            Assert.NotNull(deepest);
            Assert.Equal("iframe-0/iframe-0/iframe-0/iframe-0", deepest.Binding.FrameId);
            var ready = await session.Page.ObserveAsync();
            Assert.Equal("main", ready.Candidates.Single(candidate => candidate.Selector == "#outer").Binding.FrameId);
            Assert.Equal("iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#mid").Binding.FrameId);
            Assert.Equal("iframe-0/iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#inner").Binding.FrameId);
            Assert.Equal("iframe-0/iframe-0/iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#deep").Binding.FrameId);
            await session.Page.ClickAsync(deepest.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("deepest", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#outer").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#mid").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#inner").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#deep").Text);
            var fifth = deepest.Binding with { FrameId = "iframe-0/iframe-0/iframe-0/iframe-0/iframe-0" };
            await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(fifth));
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_OpenShadowInsideIframe_DoesNotClickLightOrMain()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="main-save">Save</button>
                <iframe id="frame"></iframe>
                <script>
                const frame = document.getElementById('frame');
                const doc = frame.contentDocument;
                doc.open();
                doc.write('<button id="frame-save">Save</button><div id="host"></div>');
                doc.close();
                const root = doc.getElementById('host').attachShadow({mode:'open'});
                const button = doc.createElement('button');
                button.id = 'inner';
                button.textContent = 'Save';
                button.addEventListener('click', () => { document.getElementById('marker').textContent = 'frame-shadow'; });
                root.appendChild(button);
                doc.getElementById('frame-save').addEventListener('click', () => { document.getElementById('marker').textContent = 'frame-light'; });
                document.getElementById('main-save').addEventListener('click', () => { document.getElementById('marker').textContent = 'main'; });
                document.getElementById('marker').textContent = 'ready';
                </script>
                """;
            BrowserCandidate? inner = null;
            string? marker = null;
            for (var attempt = 0; attempt < 40 && (marker != "ready" || inner is null); attempt++)
            {
                var observation = attempt == 0
                    ? await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html))
                    : await session.Page.ObserveAsync();
                marker = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "#marker")?.Text;
                inner = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "open-root:0>>#inner" && candidate.Binding.FrameId == "iframe-0");
                if (marker != "ready" || inner is null)
                    await Task.Delay(100);
            }

            Assert.Equal("ready", marker);
            Assert.NotNull(inner);
            var ready = await session.Page.ObserveAsync();
            Assert.Equal("main", ready.Candidates.Single(candidate => candidate.Selector == "#main-save").Binding.FrameId);
            Assert.Equal("iframe-0", ready.Candidates.Single(candidate => candidate.Selector == "#frame-save").Binding.FrameId);
            Assert.Equal("Save", ready.Candidates.Single(candidate => candidate.Selector == "#main-save").Text);
            Assert.Equal("Save", ready.Candidates.Single(candidate => candidate.Selector == "#frame-save").Text);
            await session.Page.ClickAsync(inner.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("frame-shadow", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#main-save").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#frame-save" && candidate.Binding.FrameId == "iframe-0").Text);

            var closedHtml = """
                <button id="main-save">Save</button>
                <iframe id="frame"></iframe>
                <script>
                const doc = document.getElementById('frame').contentDocument;
                doc.open();
                doc.write('<button id="frame-save">Save</button><div id="closed-host"></div>');
                doc.close();
                const root = doc.getElementById('closed-host').attachShadow({mode:'closed'});
                const secret = doc.createElement('button');
                secret.id = 'secret';
                secret.textContent = 'Save';
                root.appendChild(secret);
                </script>
                """;
            var closed = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(closedHtml));
            Assert.DoesNotContain(closed.Candidates, candidate => candidate.Selector.Contains("secret", StringComparison.Ordinal));
            var frameSave = closed.Candidates.Single(candidate => candidate.Selector == "#frame-save" && candidate.Binding.FrameId == "iframe-0");
            var closedBinding = frameSave.Binding with { Selector = "open-root:0>>#secret" };
            var closedError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(closedBinding));
            Assert.Contains("Closed shadow", closedError.Message);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_OpenShadowInsidePopup_DoesNotClickLightOrOpener()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="opener-save">Save</button>
                <button id="open">Open</button>
                <button id="close">Close</button>
                <script>
                document.getElementById('opener-save').addEventListener('click', () => { document.getElementById('marker').textContent = 'opener'; });
                document.getElementById('open').addEventListener('click', () => {
                  const child = window.open('about:blank', 'dcu-popup-shadow');
                  window.__popup = child;
                  const light = child.document.createElement('button');
                  light.id = 'popup-save';
                  light.textContent = 'Save';
                  light.addEventListener('click', () => { light.textContent = 'light-clicked'; });
                  child.document.body.appendChild(light);
                  const host = child.document.createElement('div');
                  host.id = 'host';
                  child.document.body.appendChild(host);
                  const root = host.attachShadow({mode:'open'});
                  const inner = child.document.createElement('button');
                  inner.id = 'inner';
                  inner.textContent = 'Save';
                  inner.addEventListener('click', () => { inner.textContent = 'shadow-clicked'; });
                  root.appendChild(inner);
                  const nested = child.document.createElement('div');
                  nested.id = 'nested-host';
                  root.appendChild(nested);
                  const nestedRoot = nested.attachShadow({mode:'open'});
                  const deep = child.document.createElement('button');
                  deep.id = 'deep';
                  deep.textContent = 'Save';
                  deep.addEventListener('click', () => { deep.textContent = 'nested-clicked'; });
                  nestedRoot.appendChild(deep);
                });
                document.getElementById('close').addEventListener('click', () => { if (window.__popup) window.__popup.close(); });
                </script>
                """;
            var opened = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            await session.Page.ClickAsync(opened.Candidates.Single(candidate => candidate.Selector == "#open").Binding);
            BrowserCandidate? inner = null;
            for (var attempt = 0; attempt < 40 && inner is null; attempt++)
            {
                var observation = await session.Page.ObserveAsync();
                inner = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "popup-page:0>>open-root:0>>#inner");
                if (inner is null)
                    await Task.Delay(100);
            }

            Assert.NotNull(inner);
            var ready = await session.Page.ObserveAsync();
            Assert.Equal("none", ready.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", ready.Candidates.Single(candidate => candidate.Selector == "#opener-save").Text);
            Assert.Equal("Save", ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#popup-save").Text);
            Assert.Equal("Save", ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>open-root:0>>#inner").Text);
            var deep = ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>open-root:0>>open-root:0>>#deep");
            Assert.Equal("Save", deep.Text);
            await session.Page.ClickAsync(deep.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("nested-clicked", after.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>open-root:0>>open-root:0>>#deep").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>open-root:0>>#inner").Text);
            Assert.Equal("none", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#opener-save").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#popup-save").Text);

            var thirdLevel = deep.Binding with { Selector = "popup-page:0>>open-root:0>>open-root:0>>open-root:0>>#deep" };
            var deepError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(thirdLevel));
            Assert.Contains("one nested", deepError.Message, StringComparison.OrdinalIgnoreCase);
            await session.Page.ClickAsync(after.Candidates.Single(candidate => candidate.Selector == "#close").Binding);

            var closedHtml = """
                <button id="opener-save">Save</button>
                <button id="open">Open</button>
                <script>
                document.getElementById('open').addEventListener('click', () => {
                  const child = window.open('about:blank', 'dcu-popup-closed');
                  const light = child.document.createElement('button');
                  light.id = 'popup-save';
                  light.textContent = 'Save';
                  child.document.body.appendChild(light);
                  const host = child.document.createElement('div');
                  host.id = 'outer-host';
                  child.document.body.appendChild(host);
                  const outerRoot = host.attachShadow({mode:'open'});
                  const innerHost = child.document.createElement('div');
                  innerHost.id = 'closed-inner-host';
                  outerRoot.appendChild(innerHost);
                  const root = innerHost.attachShadow({mode:'closed'});
                  const secret = child.document.createElement('button');
                  secret.id = 'secret';
                  secret.textContent = 'Save';
                  root.appendChild(secret);
                });
                </script>
                """;
            var closedOpen = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(closedHtml));
            await session.Page.ClickAsync(closedOpen.Candidates.Single(candidate => candidate.Selector == "#open").Binding);
            BrowserCandidate? popupSave = null;
            for (var attempt = 0; attempt < 40 && popupSave is null; attempt++)
            {
                var observation = await session.Page.ObserveAsync();
                popupSave = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "popup-page:1>>#popup-save");
                if (popupSave is null)
                    await Task.Delay(100);
            }

            Assert.NotNull(popupSave);
            var closedSeen = await session.Page.ObserveAsync();
            Assert.DoesNotContain(closedSeen.Candidates, candidate => candidate.Selector.Contains("secret", StringComparison.Ordinal));
            var closedBinding = popupSave.Binding with { Selector = "popup-page:1>>open-root:0>>open-root:0>>#secret" };
            var closedError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(closedBinding));
            Assert.Contains("Closed shadow", closedError.Message);
        }
    }

    [Fact]
    public void OpenRootSelector_ParsesCanonicalAddress()
    {
        Assert.False(BrowserOpenRoots.TryParse("#light", out _, out _));
        Assert.True(BrowserOpenRoots.TryParse("open-root:0>>#inner", out var index, out var inner));
        Assert.Equal(0, index);
        Assert.Equal("#inner", inner);
        Assert.Throws<BrowserUnsupportedException>(() => BrowserOpenRoots.TryParse("open-root:0", out _, out _));
        Assert.Throws<BrowserUnsupportedException>(() => BrowserOpenRoots.TryParse("open-root:00>>#inner", out _, out _));
        Assert.Throws<BrowserUnsupportedException>(() => BrowserOpenRoots.TryParse("open-root:1>>internal:control", out _, out _));
        Assert.True(BrowserOpenRoots.TryParse("open-root:0>>open-root:1>>#deep", out index, out inner));
        Assert.Equal(0, index);
        Assert.Equal("open-root:1>>#deep", inner);
        var deep = Assert.Throws<BrowserUnsupportedException>(() => BrowserOpenRoots.TryParse("open-root:0>>open-root:1>>open-root:2>>#deep", out _, out _));
        Assert.Contains("one nested", deep.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectorContainingShadow_IsRejected()
    {
        await using var manager = new BrowserSessionManager(() => new FakeEngine());
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var binding = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save").Binding
            with { Selector = "#shadow" };
        var error = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(binding));
        Assert.Contains("shadow", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveInstalledEdge_OpenShadowRoot_DoesNotClickLightControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var openHtml = """
                <button id="marker">none</button>
                <button id="light">Save</button>
                <div id="host"></div>
                <script>
                const root = document.getElementById('host').attachShadow({mode:'open'});
                const button = document.createElement('button');
                button.id = 'inner';
                button.textContent = 'Save';
                button.addEventListener('click', () => { document.getElementById('marker').textContent = 'shadow'; });
                root.appendChild(button);
                document.getElementById('light').addEventListener('click', () => { document.getElementById('marker').textContent = 'light'; });
                document.getElementById('marker').textContent = 'ready';
                </script>
                """;
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(openHtml));
            var light = observation.Candidates.Single(candidate => candidate.Selector == "#light");
            var inner = observation.Candidates.Single(candidate => candidate.Selector == "open-root:0>>#inner");
            Assert.Equal("Save", light.Text);
            Assert.Equal("Save", inner.Text);
            Assert.Equal("ready", observation.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.DoesNotContain(observation.Candidates, candidate => candidate.Selector == "#inner");
            await session.Page.ClickAsync(inner.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("shadow", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#light").Text);

            var missing = after.Candidates.Single(candidate => candidate.Selector == "open-root:0>>#inner").Binding with { Selector = "open-root:1>>#inner" };
            var missingError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(missing));
            Assert.Contains("not found", missingError.Message, StringComparison.OrdinalIgnoreCase);

            var closedHtml = """
                <button id="light">Save</button>
                <div id="closed-host"></div>
                <script>
                const root = document.getElementById('closed-host').attachShadow({mode:'closed'});
                const button = document.createElement('button');
                button.id = 'secret';
                button.textContent = 'Save';
                root.appendChild(button);
                </script>
                """;
            var closed = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(closedHtml));
            Assert.DoesNotContain(closed.Candidates, candidate => candidate.Selector.Contains("secret", StringComparison.Ordinal));
            var closedBinding = closed.Candidates.Single(candidate => candidate.Selector == "#light").Binding with { Selector = "open-root:0>>#secret" };
            var closedError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(closedBinding));
            Assert.Contains("Closed shadow", closedError.Message);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_NestedOpenShadow_DoesNotClickOuterOrLightControl()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="light">Save</button>
                <div id="outer-host"></div>
                <script>
                const outer = document.getElementById('outer-host').attachShadow({mode:'open'});
                const mid = document.createElement('button');
                mid.id = 'mid';
                mid.textContent = 'Save';
                mid.addEventListener('click', () => { document.getElementById('marker').textContent = 'outer-root'; });
                outer.appendChild(mid);
                const innerHost = document.createElement('div');
                innerHost.id = 'inner-host';
                outer.appendChild(innerHost);
                const inner = innerHost.attachShadow({mode:'open'});
                const deep = document.createElement('button');
                deep.id = 'deep';
                deep.textContent = 'Save';
                deep.addEventListener('click', () => { document.getElementById('marker').textContent = 'nested'; });
                inner.appendChild(deep);
                document.getElementById('light').addEventListener('click', () => { document.getElementById('marker').textContent = 'light'; });
                document.getElementById('marker').textContent = 'ready';
                </script>
                """;
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var nested = observation.Candidates.Single(candidate => candidate.Selector == "open-root:0>>open-root:0>>#deep");
            Assert.Equal("Save", observation.Candidates.Single(candidate => candidate.Selector == "#light").Text);
            Assert.Equal("Save", observation.Candidates.Single(candidate => candidate.Selector == "open-root:0>>#mid").Text);
            Assert.Equal("ready", observation.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            await session.Page.ClickAsync(nested.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("nested", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#light").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "open-root:0>>#mid").Text);

            var third = nested.Binding with { Selector = "open-root:0>>open-root:0>>open-root:0>>#deep" };
            var thirdError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(third));
            Assert.Contains("one nested", thirdError.Message, StringComparison.OrdinalIgnoreCase);

            var closedHtml = """
                <button id="light">Save</button>
                <div id="outer-host"></div>
                <script>
                const outer = document.getElementById('outer-host').attachShadow({mode:'open'});
                const innerHost = document.createElement('div');
                innerHost.id = 'closed-inner';
                outer.appendChild(innerHost);
                const closed = innerHost.attachShadow({mode:'closed'});
                const secret = document.createElement('button');
                secret.id = 'secret';
                secret.textContent = 'Save';
                closed.appendChild(secret);
                </script>
                """;
            var closed = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(closedHtml));
            Assert.DoesNotContain(closed.Candidates, candidate => candidate.Selector.Contains("secret", StringComparison.Ordinal));
            var closedBinding = closed.Candidates.Single(candidate => candidate.Selector == "#light").Binding with { Selector = "open-root:0>>open-root:0>>#secret" };
            var closedError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(closedBinding));
            Assert.Contains("Closed shadow", closedError.Message);
        }
    }

    [Fact]
    public void PopupSelector_ParsesCanonicalAddress_AndRememberKeepsFirstSeenOrder()
    {
        Assert.False(BrowserPopups.TryParse("#same", out _, out _));
        Assert.True(BrowserPopups.TryParse("popup-page:1>>#popup-save", out var index, out var inner));
        Assert.Equal(1, index);
        Assert.Equal("#popup-save", inner);
        Assert.Throws<BrowserUnsupportedException>(() => BrowserPopups.TryParse("popup-page:01>>#popup-save", out _, out _));

        var opener = new object();
        var first = new object();
        var second = new object();
        var third = new object();
        var seen = BrowserPopups.Remember([], [second, opener, first], opener);
        var again = BrowserPopups.Remember(seen, [third, second, first, opener], opener);
        Assert.Same(second, again[0]);
        Assert.Same(first, again[1]);
        Assert.Same(third, again[2]);
    }

    [Fact]
    public async Task LiveInstalledEdge_Popup_ActsOnExplicitPopupNotOpener()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="same">Save</button>
                <button id="open">Open</button>
                <script>
                document.getElementById('same').addEventListener('click', () => {
                  document.getElementById('marker').textContent = 'opener';
                });
                document.getElementById('open').addEventListener('click', () => {
                  const child = window.open('about:blank', 'dcu-popup');
                  const button = child.document.createElement('button');
                  button.id = 'popup-save';
                  button.textContent = 'Save';
                  button.addEventListener('click', () => {
                    button.textContent = 'clicked';
                    const marker = window.opener && window.opener.document.getElementById('marker');
                    if (marker) marker.textContent = 'popup';
                  });
                  child.document.body.appendChild(button);
                });
                </script>
                """;
            var opened = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            await session.Page.ClickAsync(opened.Candidates.Single(candidate => candidate.Selector == "#open").Binding);
            BrowserCandidate? popup = null;
            for (var attempt = 0; attempt < 40 && popup is null; attempt++)
            {
                var observation = await session.Page.ObserveAsync();
                popup = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "popup-page:0>>#popup-save");
                if (popup is null)
                    await Task.Delay(100);
            }

            Assert.NotNull(popup);
            var before = await session.Page.ObserveAsync();
            Assert.Equal("Save", before.Candidates.Single(candidate => candidate.Selector == "#same").Text);
            Assert.Equal("none", before.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            await session.Page.ClickAsync(popup.Binding);
            var after = await session.Page.ObserveAsync();
            Assert.Equal("clicked", after.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#popup-save").Text);
            Assert.NotEqual("opener", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("Save", after.Candidates.Single(candidate => candidate.Selector == "#same").Text);
            var missing = popup.Binding with { Selector = "popup-page:1>>#popup-save" };
            var missingError = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.ClickAsync(missing));
            Assert.Contains("not found", missingError.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DesktopStartup_RegistersBrowserLazily_AndDoesNotOpenIt()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ShadowUse", "Program.cs"));
        var tools = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "ShadowUse", "Tools", "BrowserUseTools.cs"));
        var toolNames = System.Text.RegularExpressions.Regex.Matches(tools, "McpServerTool\\(Name = \\\"([^\\\"]+)\\\"\\)")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("browser_wait", toolNames);
        Assert.Contains("browser_webmcp_discover", toolNames);
        Assert.Contains("browser_webmcp_invoke", toolNames);
        Assert.Contains("browser_jev_decide", toolNames);
        Assert.Contains("browser_jev_act", toolNames);
        Assert.Contains("browser_capture_value", toolNames);
        Assert.Contains("browser_jev_inspect", toolNames);
        Assert.Contains("browser_diagnostics", toolNames);
        Assert.Contains("browser_extract", toolNames);
        Assert.Contains("browser_remember_action_resolution", toolNames);
        Assert.Contains("browser_resolve_cached_action", toolNames);
        Assert.Contains("browser_record_task_experience", toolNames);
        Assert.Contains("browser_recall_task_experience", toolNames);
        Assert.Contains("browser_point_click", toolNames);
        Assert.Contains("browser_point_fill", toolNames);
        Assert.Equal(23, toolNames.Count);

        Assert.Contains("AddSingleton<BrowserSessionManager>()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAsync", program, StringComparison.Ordinal);
        Assert.DoesNotContain("Playwright.CreateAsync", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveInstalledEdge_ObservesMasksPassword_AndRejectsStaleBinding()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var first = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(
                "<button id=\"save\">Save</button><input id=\"pw\" type=\"password\" value=\"secret\"/>"));
            var password = Assert.Single(first.Candidates, candidate => candidate.Password);
            Assert.Null(password.Text);
            await session.Page.FillAsync(password.Binding, "synthetic-password-42!");
            await session.Page.ClickAsync(first.Candidates.Single(candidate => candidate.Selector == "#save").Binding);

            var second = await session.GotoAsync("data:text/html," + Uri.EscapeDataString("<button id=\"other\">Other</button>"));
            await Assert.ThrowsAsync<BrowserStaleException>(() => session.Page.ClickAsync(password.Binding));
            Assert.NotEqual(first.Target.DocumentEpoch, second.Target.DocumentEpoch);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [Fact]
    public async Task LiveInstalledEdge_NonHeadlessSessionDiscoversARealHostWindow()
    {
        // F42-R02: a genuinely non-headless session (real visible OS window, briefly) must
        // resolve BrowserTargetRef.HostHwnd to a real, currently-live window -- proving the CDP
        // SystemInfo.getProcessInfo + Win32 EnumWindows discovery path actually works, not just
        // that it compiles. A headless session (this suite's default elsewhere) must NOT attempt
        // discovery at all and must report HostHwnd:null.
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = false,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            Assert.NotNull(session.Target.HostHwnd);
            var parsed = Convert.ToInt64(session.Target.HostHwnd!.TrimStart('0', 'x', 'X'), 16);
            Assert.True(IsWindow((nint)parsed), $"Discovered HostHwnd {session.Target.HostHwnd} was not a live window.");
        }

        await using var headlessManager = new BrowserSessionManager();
        BrowserSession headlessSession;
        try
        {
            headlessSession = await headlessManager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }
        await using (headlessSession)
        {
            Assert.Null(headlessSession.Target.HostHwnd);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_PointClick_RealButtonClickReflectsInPageState()
    {
        // F42-R02, real dispatch: page.Mouse (CDP) lands on the SAME real page an ordinary
        // browser_click would, proving the fallback path is a genuine functioning click, not
        // just a probe that always aborts.
        using var pageServer = LoopbackPage.Start(
            "<button id=\"btn\" onclick=\"document.getElementById('status').textContent='clicked'\">Click</button>" +
            "<div id=\"status\">idle</div>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var btn = opened.Candidates.Single(c => c.Selector == "#btn");

        var result = await session.Page.PointClickAsync(btn.Binding, null, null);

        Assert.True(result.Executed);
        Assert.True(result.MoveVerified);
        Assert.True(result.PressVerified);
        Assert.Equal("element-verified", result.VerificationKind);

        var statusBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#status");
        Assert.Equal("clicked", await session.Page.ReadTextAsync(statusBinding));
    }

    [Fact]
    public async Task LiveInstalledEdge_PointClick_CanvasTarget_ReportsPointVerifiedNotElementVerified()
    {
        // F42-R02 honest scope: a canvas has no sub-elements DOM verification can prove, so the
        // result must say "point-verified", not overclaim "element-verified".
        using var pageServer = LoopbackPage.Start(
            "<canvas id=\"cv\" width=\"100\" height=\"60\"></canvas>" +
            "<div id=\"status\">idle</div>" +
            "<script>document.getElementById('cv').addEventListener('click', () => { document.getElementById('status').textContent = 'canvas-clicked'; });</script>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        await session.GotoAsync(pageServer.Url);

        var canvasBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#cv");
        var result = await session.Page.PointClickAsync(canvasBinding, null, null);

        Assert.True(result.Executed);
        Assert.True(result.PressVerified);
        Assert.Equal("point-verified", result.VerificationKind);

        var statusBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#status");
        Assert.Equal("canvas-clicked", await session.Page.ReadTextAsync(statusBinding));
    }

    [Fact]
    public async Task LiveInstalledEdge_PointClick_ObscuredOverlay_AbortsBeforeAnyPress()
    {
        // The core safety claim, proven live: an overlay covering the target must abort with
        // ZERO presses -- the real page's own onclick handler must never fire.
        using var pageServer = LoopbackPage.Start(
            "<button id=\"btn\" style=\"position:absolute;left:10px;top:10px;width:80px;height:30px;\" onclick=\"document.getElementById('status').textContent='clicked'\">Click</button>" +
            "<div id=\"status\">idle</div>" +
            "<div id=\"overlay\" style=\"position:absolute;left:0;top:0;width:300px;height:300px;z-index:5;\"></div>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var btn = opened.Candidates.Single(c => c.Selector == "#btn");

        var result = await session.Page.PointClickAsync(btn.Binding, null, null);

        Assert.False(result.Executed);
        Assert.False(result.MoveVerified);
        Assert.Contains("elementFromPoint", result.AbortReason);

        var statusBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#status");
        Assert.Equal("idle", await session.Page.ReadTextAsync(statusBinding));
    }

    [Fact]
    public async Task LiveInstalledEdge_PointClick_SubFrameSelector_ThrowsUnsupported()
    {
        // Honest scope gate: BoundingBoxAsync is main-frame-relative but a sub-frame's own
        // elementFromPoint probe needs frame-local coordinates -- deliberately refused rather
        // than silently mis-clicking with an unadjusted coordinate.
        BrowserSession session;
        await using var manager = new BrowserSessionManager();
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
            <button id="parent">Save</button>
            <iframe srcdoc="<button id='inside'>Save</button>"></iframe>
            """;
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var inside = observation.Candidates.Single(candidate => candidate.Binding.FrameId == "iframe-0" && candidate.Selector == "#inside");

            var error = await Assert.ThrowsAsync<BrowserUnsupportedException>(() => session.Page.PointClickAsync(inside.Binding));
            Assert.Contains("main frame", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_PointFill_RealInputReflectsTypedValue()
    {
        // F42-R02, real dispatch: page.Mouse places focus and page.Keyboard types real trusted
        // key events -- the page's own oninput handler must observe every keystroke, proving
        // this is genuine keyboard-driven entry, not a JS value assignment.
        using var pageServer = LoopbackPage.Start(
            "<input id=\"field\" oninput=\"document.getElementById('status').textContent = 'typed:' + this.value\">" +
            "<div id=\"status\">idle</div>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var field = opened.Candidates.Single(c => c.Selector == "#field");

        var result = await session.Page.PointFillAsync(field.Binding, "hello-42");

        Assert.True(result.Executed);
        Assert.True(result.FocusVerified);
        Assert.True(result.TypeVerified);
        Assert.Equal("hello-42", result.ObservedValue);

        var statusBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#status");
        Assert.Equal("typed:hello-42", await session.Page.ReadTextAsync(statusBinding));
    }

    [Fact]
    public async Task LiveInstalledEdge_PointFill_ClearsExistingContentBeforeTyping()
    {
        // Existing content must be replaced (Ctrl+A, Delete), not appended to.
        using var pageServer = LoopbackPage.Start("<input id=\"field\" value=\"old-value\">");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var field = opened.Candidates.Single(c => c.Selector == "#field");

        var result = await session.Page.PointFillAsync(field.Binding, "new");

        Assert.Equal("new", result.ObservedValue);
    }

    [Fact]
    public async Task LiveInstalledEdge_PointFill_ObscuredOverlay_AbortsBeforeAnyKeyEvent()
    {
        // The core safety claim, proven live: an overlay covering the target must abort with
        // ZERO key events -- the real page's own oninput handler must never fire.
        using var pageServer = LoopbackPage.Start(
            "<input id=\"field\" style=\"position:absolute;left:10px;top:10px;width:80px;height:30px;\" oninput=\"document.getElementById('status').textContent = 'typed:' + this.value\">" +
            "<div id=\"status\">idle</div>" +
            "<div id=\"overlay\" style=\"position:absolute;left:0;top:0;width:300px;height:300px;z-index:5;\"></div>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var field = opened.Candidates.Single(c => c.Selector == "#field");

        var result = await session.Page.PointFillAsync(field.Binding, "should-not-appear");

        Assert.False(result.Executed);
        Assert.False(result.FocusVerified);
        Assert.Contains("elementFromPoint", result.AbortReason);

        var statusBinding = new BrowserActionBinding(session.Target.PageId, "main", session.Target.DocumentEpoch, "#status");
        Assert.Equal("idle", await session.Page.ReadTextAsync(statusBinding));
    }

    [Fact]
    public async Task LiveInstalledEdge_ObservationExposesSupportedOperationsPerCandidate()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            // F42-R01: a caller reading this list should never be told to attempt an operation
            // that is guaranteed to fail against the real element it names.
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(
                "<button id=\"go\">Go</button>" +
                "<input id=\"name\" type=\"text\"/>" +
                "<input id=\"remember\" type=\"checkbox\"/>" +
                "<input id=\"resume\" type=\"file\"/>" +
                "<select id=\"country\"><option>US</option></select>"));

            Assert.Equal(["click"], observation.Candidates.Single(c => c.Selector == "#go").SupportedOperations);
            Assert.Equal(["fill", "click"], observation.Candidates.Single(c => c.Selector == "#name").SupportedOperations);
            Assert.Equal(["click"], observation.Candidates.Single(c => c.Selector == "#remember").SupportedOperations);
            Assert.Equal(["upload"], observation.Candidates.Single(c => c.Selector == "#resume").SupportedOperations);
            Assert.Equal(["click"], observation.Candidates.Single(c => c.Selector == "#country").SupportedOperations);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_RememberAndResolveActionResolution_RealNavigationRoundTrip()
    {
        // F31: browser_remember_action_resolution/browser_resolve_cached_action have only ever
        // been proven against a fake IBrowserEngine before this test -- this drives the exact same
        // two tools against real installed Edge to prove a hit is a genuine fresh re-resolution
        // (not a replayed candidate id) after a real navigation, and that a real origin change
        // (a structurally different page) misses cleanly rather than resolving to the wrong element.
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var tools = new BrowserUseTools(manager, new JevController(new HttpClient(), new JevOptions()), new WorkflowValueStore());
            var loginUrl = "data:text/html," + Uri.EscapeDataString("<button id=\"login\">Log in</button>");

            var first = await session.GotoAsync(loginUrl);
            var firstCandidateId = first.Candidates.Single(c => c.Selector == "#login").CandidateId;
            var remembered = tools.RememberActionResolution(session.Target.AdapterSessionId, firstCandidateId, "login_button");
            var rememberedJson = JsonSerializer.SerializeToElement(remembered);
            Assert.True(rememberedJson.GetProperty("ok").GetBoolean());

            // A real navigation back to the identical page: candidate ids are minted fresh per
            // observation, so a real hit here can only come from genuine live re-resolution.
            await session.GotoAsync(loginUrl);
            var resolved = await tools.ResolveCachedAction(session.Target.AdapterSessionId, "login_button");
            var resolvedJson = JsonSerializer.SerializeToElement(resolved);
            Assert.True(resolvedJson.GetProperty("hit").GetBoolean());
            var freshCandidateId = resolvedJson.GetProperty("candidate_id").GetString();
            Assert.NotEqual(firstCandidateId, freshCandidateId);
            // Read the observation ResolveCachedAction itself just took (LastObservation), not a
            // fresh ObserveAsync() call -- candidate ids are minted fresh per observation, so a
            // second independent observe here would invalidate freshCandidateId before we could
            // look it up.
            Assert.Equal("#login", session.Page.LastObservation!.Candidates.Single(c => c.CandidateId == freshCandidateId).Selector);

            // A real structurally-different page (F31-T02): the whole data: URL differs, so
            // BrowserTargetIdentityOf sees a different origin/identity and must miss cleanly.
            await session.GotoAsync("data:text/html," + Uri.EscapeDataString("<button id=\"login\">Log in</button><p>extra</p>"));
            var missed = await tools.ResolveCachedAction(session.Target.AdapterSessionId, "login_button");
            var missedJson = JsonSerializer.SerializeToElement(missed);
            Assert.False(missedJson.GetProperty("hit").GetBoolean());
        }
    }

    [Fact]
    public void DownloadPath_StaysInsideRoot_AndRejectsEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var saved = BrowserDownloadPaths.Resolve(root, "note.txt");
            Assert.StartsWith(Path.GetFullPath(root), saved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("note.txt", Path.GetFileName(saved));
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "note.txt"), BrowserDownloadPaths.Resolve(root, "../note.txt"));
            Assert.Throws<BrowserValidationException>(() => BrowserDownloadPaths.Resolve(root, "bad|name.txt"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AttachedSession_RejectsDownloadDirectory()
    {
        var options = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
            AllowedDownloadRoot = Path.GetTempPath(),
        };
        Assert.Throws<BrowserValidationException>(options.Validate);
    }

    [Fact]
    public async Task OwnedSession_SavesDownloadInsideCallerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var manager = new BrowserSessionManager(() => new FakeEngine());
            var session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                AllowedDownloadRoot = root,
            });
            var observation = await session.Page.ObserveAsync();
            var file = observation.Candidates.Single(candidate => candidate.Selector == "#save");
            var saved = await session.Page.DownloadAsync(file.Binding);
            Assert.StartsWith(Path.GetFullPath(root), saved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("hello-download", File.ReadAllText(saved));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_SavesOwnedDownloadInsideCallerDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var manager = new BrowserSessionManager();
            BrowserSession session;
            try
            {
                session = await manager.OpenAsync(new BrowserSessionOptions
                {
                    Mode = BrowserSessionMode.FreshIsolated,
                    Channel = "msedge",
                    Headless = true,
                    TimeoutMs = 20_000,
                    AllowedDownloadRoot = root,
                });
            }
            catch (BrowserUnavailableException ex)
            {
                Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
                return;
            }

            await using (session)
            {
                var html = """
                    <a id="file" href="#">Download</a>
                    <script>
                    document.getElementById('file').addEventListener('click', event => {
                      event.preventDefault();
                      const link = document.createElement('a');
                      link.href = URL.createObjectURL(new Blob(['hello-download'], {type:'text/plain'}));
                      link.download = 'note.txt';
                      link.click();
                    });
                    </script>
                    """;
                var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
                var file = observation.Candidates.Single(candidate => candidate.Selector == "#file");
                var saved = await session.Page.DownloadAsync(file.Binding);
                Assert.StartsWith(Path.GetFullPath(root), saved, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("note.txt", Path.GetFileName(saved));
                Assert.Equal("hello-download", File.ReadAllText(saved));
                Assert.False(File.Exists(saved + ".crdownload"));
                Assert.False(File.Exists(saved + ".partial"));
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadCompletion_ReturnsExactStableFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path, "hello-download");
        try
        {
            var completed = await BrowserDownloadCompletion.WaitAsync(path, 1_000, CancellationToken.None);
            Assert.Equal(Path.GetFullPath(path), completed);
            Assert.Equal("hello-download", File.ReadAllText(completed));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadCompletion_FailsWhenFileStaysMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        try
        {
            var error = await Assert.ThrowsAsync<BrowserValidationException>(() => BrowserDownloadCompletion.WaitAsync(path, 60, CancellationToken.None));
            Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DownloadCompletion_FailsWhenPartialSiblingRemains()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-download-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "note.txt");
        File.WriteAllText(path + ".crdownload", "partial");
        try
        {
            var error = await Assert.ThrowsAsync<BrowserValidationException>(() => BrowserDownloadCompletion.WaitAsync(path, 60, CancellationToken.None));
            Assert.Contains("partial", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UploadPath_RejectsRelativeAndOutside()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-upload-path-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "dcu-upload-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "chosen-upload.txt");
        File.WriteAllText(inside, "hello-upload");
        File.WriteAllText(outside, "outside");
        try
        {
            Assert.Equal(Path.GetFullPath(inside), BrowserUploadPaths.Resolve(root, inside));
            Assert.Throws<BrowserValidationException>(() => BrowserUploadPaths.Resolve(root, "chosen-upload.txt"));
            Assert.Throws<BrowserValidationException>(() => BrowserUploadPaths.Resolve(root, outside));
            Assert.Throws<BrowserValidationException>(() => BrowserUploadPaths.Resolve(root, Path.Combine(root, "missing.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void AttachedSession_RejectsUploadDirectory()
    {
        var options = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
            AllowedUploadRoot = Path.GetTempPath(),
        };
        Assert.Throws<BrowserValidationException>(options.Validate);
    }

    [Fact]
    public async Task AttachedSession_RejectsUpload()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page, new FakeBrowser { OwnsResources = false, Page = page }));
        var session = await manager.OpenAsync(new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
        });
        var file = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save");
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.UploadAsync(file.Binding, Path.GetFullPath("chosen-upload.txt")));
    }

    [Fact]
    public async Task OwnedSession_UploadsApprovedAbsoluteFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-upload-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "chosen-upload.txt");
        File.WriteAllText(source, "hello-upload");
        try
        {
            var page = new FakePage();
            await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
            var session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                AllowedUploadRoot = root,
            });
            var file = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save");
            var name = await session.Page.UploadAsync(file.Binding, source);
            Assert.Equal("chosen-upload.txt", name);
            Assert.Equal(Path.GetFullPath(source), page.LastUploadPath);
            await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.UploadAsync(file.Binding, "chosen-upload.txt"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_UploadsApprovedFileAndReadsSelectedName()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-upload-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "chosen-upload.txt");
        File.WriteAllText(source, "hello-upload");
        try
        {
            await using var manager = new BrowserSessionManager();
            BrowserSession session;
            try
            {
                session = await manager.OpenAsync(new BrowserSessionOptions
                {
                    Mode = BrowserSessionMode.FreshIsolated,
                    Channel = "msedge",
                    Headless = true,
                    TimeoutMs = 20_000,
                    AllowedUploadRoot = root,
                });
            }
            catch (BrowserUnavailableException ex)
            {
                Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
                return;
            }

            await using (session)
            {
                var html = """
                    <input id="file" type="file" aria-label="Upload" style="display:none">
                    <button id="chosen">none</button>
                    <script>
                    document.getElementById('file').addEventListener('change', () => {
                      const selected = document.getElementById('file').files[0];
                      document.getElementById('chosen').textContent = selected ? selected.name : 'none';
                    });
                    </script>
                    """;
                var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
                var file = observation.Candidates.Single(candidate => candidate.Selector == "#file");
                var name = await session.Page.UploadAsync(file.Binding, source);
                Assert.Equal("chosen-upload.txt", name);
                var after = await session.Page.ObserveAsync();
                Assert.Equal("chosen-upload.txt", after.Candidates.Single(candidate => candidate.Selector == "#chosen").Text);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("alert", true, null)]
    [InlineData("confirm", false, null)]
    [InlineData("prompt", true, "synthetic-password-42!")]
    [InlineData("prompt", true, "")]
    [InlineData("prompt", false, null)]
    public void DialogRequest_AcceptsNamedType(string type, bool accept, string? promptText)
    {
        Assert.Equal(type, BrowserDialogs.Validate(type, accept, promptText));
    }

    [Theory]
    [InlineData("beforeunload", true, null)]
    [InlineData("alert", true, "nope")]
    [InlineData("prompt", true, null)]
    [InlineData("prompt", false, "nope")]
    public void DialogRequest_RejectsImplicitOrMismatchedText(string type, bool accept, string? promptText)
    {
        Assert.Throws<BrowserValidationException>(() => BrowserDialogs.Validate(type, accept, promptText));
    }

    [Fact]
    public async Task AttachedSession_RejectsDialog()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page, new FakeBrowser { OwnsResources = false, Page = page }));
        var session = await manager.OpenAsync(new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
        });
        var save = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save");
        await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.DialogAsync(save.Binding, "alert", true, null));
    }

    [Fact]
    public async Task OwnedSession_RecordsExplicitDialogRequest()
    {
        var page = new FakePage();
        await using var manager = new BrowserSessionManager(() => new FakeEngine(page));
        var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated });
        var save = (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#save");
        var handled = await session.Page.DialogAsync(save.Binding, "prompt", true, "synthetic-password-42!");
        Assert.Equal("prompt", handled);
        Assert.Equal("prompt", page.LastDialogType);
        Assert.True(page.LastDialogAccept);
        Assert.Equal("synthetic-password-42!", page.LastDialogPrompt);
    }

    [Fact]
    public async Task LiveInstalledEdge_Dialog_AcceptAndDismissAreExplicit()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="do-alert">Alert</button>
                <button id="do-confirm">Confirm</button>
                <button id="do-prompt">Prompt</button>
                <button id="plain">Plain</button>
                <script>
                document.getElementById('do-alert').onclick = () => { alert('hello-alert'); document.getElementById('marker').textContent = 'alert-closed'; };
                document.getElementById('do-confirm').onclick = () => { document.getElementById('marker').textContent = confirm('hello-confirm') ? 'confirm-true' : 'confirm-false'; };
                document.getElementById('do-prompt').onclick = () => {
                  const value = prompt('hello-prompt', '');
                  document.getElementById('marker').textContent = value === null ? 'prompt-dismiss' : 'prompt-' + value;
                };
                </script>
                """;
            var observation = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            var alert = observation.Candidates.Single(candidate => candidate.Selector == "#do-alert");
            var confirm = observation.Candidates.Single(candidate => candidate.Selector == "#do-confirm");
            var prompt = observation.Candidates.Single(candidate => candidate.Selector == "#do-prompt");
            var plain = observation.Candidates.Single(candidate => candidate.Selector == "#plain");

            Assert.Equal("alert", await session.Page.DialogAsync(alert.Binding, "alert", true, null));
            Assert.Equal("alert-closed", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("alert", await session.Page.DialogAsync(alert.Binding, "alert", false, null));

            Assert.Equal("confirm", await session.Page.DialogAsync(confirm.Binding, "confirm", true, null));
            Assert.Equal("confirm-true", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("confirm", await session.Page.DialogAsync(confirm.Binding, "confirm", false, null));
            Assert.Equal("confirm-false", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);

            Assert.Equal("prompt", await session.Page.DialogAsync(prompt.Binding, "prompt", true, "synthetic-password-42!"));
            Assert.Equal("prompt-synthetic-password-42!", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.Equal("prompt", await session.Page.DialogAsync(prompt.Binding, "prompt", false, null));
            Assert.Equal("prompt-dismiss", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);

            var mismatch = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.DialogAsync(confirm.Binding, "alert", true, null));
            Assert.Contains("confirm", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("alert", mismatch.Message, StringComparison.Ordinal);
            Assert.Equal("confirm-false", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);

            var missing = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.DialogAsync(plain.Binding, "alert", true, null));
            Assert.Contains("did not open", missing.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_PopupDialog_AcceptAndDismissChangePopupOnly()
    {
        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var html = """
                <button id="marker">none</button>
                <button id="same">Save</button>
                <button id="open">Open</button>
                <script>
                document.getElementById('open').addEventListener('click', () => {
                  const child = window.open('about:blank', 'dcu-popup-dialog');
                  const marker = child.document.createElement('button');
                  marker.id = 'popup-marker';
                  marker.textContent = 'none';
                  child.document.body.appendChild(marker);
                  function add(id, label, run) {
                    const button = child.document.createElement('button');
                    button.id = id;
                    button.textContent = label;
                    button.addEventListener('click', run);
                    child.document.body.appendChild(button);
                  }
                  add('do-alert', 'Alert', () => { child.alert('hello-alert'); marker.textContent = 'alert-closed'; });
                  add('do-confirm', 'Confirm', () => { marker.textContent = child.confirm('hello-confirm') ? 'confirm-true' : 'confirm-false'; });
                  add('do-prompt', 'Prompt', () => {
                    const value = child.prompt('hello-prompt', '');
                    marker.textContent = value === null ? 'prompt-dismiss' : 'prompt-' + value;
                  });
                });
                </script>
                """;
            var opened = await session.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            await session.Page.ClickAsync(opened.Candidates.Single(candidate => candidate.Selector == "#open").Binding);
            BrowserCandidate? confirm = null;
            for (var attempt = 0; attempt < 40 && confirm is null; attempt++)
            {
                var observation = await session.Page.ObserveAsync();
                confirm = observation.Candidates.SingleOrDefault(candidate => candidate.Selector == "popup-page:0>>#do-confirm");
                if (confirm is null)
                    await Task.Delay(100);
            }

            Assert.NotNull(confirm);
            var ready = await session.Page.ObserveAsync();
            var alert = ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#do-alert");
            var prompt = ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#do-prompt");
            confirm = ready.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#do-confirm");

            Assert.Equal("alert", await session.Page.DialogAsync(alert.Binding, "alert", true, null));
            Assert.Equal("alert-closed", PopupMarker(await session.Page.ObserveAsync()));
            Assert.Equal("none", OpenerMarker(await session.Page.ObserveAsync()));

            Assert.Equal("confirm", await session.Page.DialogAsync(confirm.Binding, "confirm", true, null));
            Assert.Equal("confirm-true", PopupMarker(await session.Page.ObserveAsync()));
            Assert.Equal("confirm", await session.Page.DialogAsync(confirm.Binding, "confirm", false, null));
            Assert.Equal("confirm-false", PopupMarker(await session.Page.ObserveAsync()));

            Assert.Equal("prompt", await session.Page.DialogAsync(prompt.Binding, "prompt", true, "synthetic-password-42!"));
            Assert.Equal("prompt-synthetic-password-42!", PopupMarker(await session.Page.ObserveAsync()));
            Assert.Equal("prompt", await session.Page.DialogAsync(prompt.Binding, "prompt", false, null));
            Assert.Equal("prompt-dismiss", PopupMarker(await session.Page.ObserveAsync()));
            Assert.Equal("none", OpenerMarker(await session.Page.ObserveAsync()));
            Assert.Equal("Save", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#same").Text);

            var mismatch = await Assert.ThrowsAsync<BrowserValidationException>(() => session.Page.DialogAsync(confirm.Binding, "alert", true, null));
            Assert.Contains("confirm", mismatch.Message, StringComparison.Ordinal);
            Assert.Contains("alert", mismatch.Message, StringComparison.Ordinal);
            Assert.Equal("confirm-false", PopupMarker(await session.Page.ObserveAsync()));
            Assert.Equal("none", OpenerMarker(await session.Page.ObserveAsync()));
        }
    }

    private static string PopupMarker(BrowserObservation observation) =>
        observation.Candidates.Single(candidate => candidate.Selector == "popup-page:0>>#popup-marker").Text!;

    private static string OpenerMarker(BrowserObservation observation) =>
        observation.Candidates.Single(candidate => candidate.Selector == "#marker").Text!;

    [Fact]
    public async Task LiveInstalledEdge_ObservationTextReflectsFilledInputValue_NotEmptyInnerText()
    {
        // Regression: an <input>/<textarea>/<select> has no inner text, so a candidate's
        // Text must come from the DOM value property, not InnerTextAsync -- otherwise every
        // caller reading a filled field back (browser_capture_value, F40 cross-app transfer)
        // would silently see an empty string no matter what was actually entered.
        using var pageServer = LoopbackPage.Start(
            "<input id=\"account\" type=\"text\" value=\"\" />" +
            "<textarea id=\"notes\"></textarea>" +
            "<select id=\"plan\"><option value=\"free\">free</option><option value=\"pro\">pro</option></select>");
        await using var manager = new BrowserSessionManager();
        await using var session = await manager.OpenAsync(new BrowserSessionOptions { Mode = BrowserSessionMode.FreshIsolated, Channel = "msedge", Headless = true, TimeoutMs = 20_000 });
        var opened = await session.GotoAsync(pageServer.Url);
        var account = opened.Candidates.Single(candidate => candidate.Selector == "#account");
        var notes = opened.Candidates.Single(candidate => candidate.Selector == "#notes");
        var plan = opened.Candidates.Single(candidate => candidate.Selector == "#plan");
        Assert.Equal(string.Empty, account.Text);
        Assert.Equal(string.Empty, notes.Text);
        Assert.Equal("free", plan.Text);

        await session.Page.FillAsync(account.Binding, "acct-88213-fixture");
        await session.Page.FillAsync(notes.Binding, "multi-line field value");

        var after = await session.Page.ObserveAsync();
        Assert.Equal("acct-88213-fixture", after.Candidates.Single(candidate => candidate.Selector == "#account").Text);
        Assert.Equal("multi-line field value", after.Candidates.Single(candidate => candidate.Selector == "#notes").Text);
    }

    [Fact]
    public async Task LiveInstalledEdge_ReopensDedicatedProfile_AndReadsSavedValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-profile-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "edge-profile");
        var otherProfile = Path.Combine(root, "other-profile");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(otherProfile);
        using var pageServer = LoopbackPage.Start("""
            <button id="marker">boot</button>
            <input id="note" aria-label="Note">
            <button id="save">Save</button>
            <script>
            const marker = document.getElementById('marker');
            const note = document.getElementById('note');
            const saved = localStorage.getItem('note');
            marker.textContent = saved === null ? 'empty' : saved;
            document.getElementById('save').addEventListener('click', () => {
              localStorage.setItem('note', note.value);
              marker.textContent = note.value;
            });
            </script>
            """);
        try
        {
            await using (var manager = new BrowserSessionManager())
            await using (var session = await OpenPersistentEdge(manager, profile))
            {
                var opened = await session.GotoAsync(pageServer.Url);
                Assert.Equal("empty", opened.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
                await session.Page.FillAsync(opened.Candidates.Single(candidate => candidate.Selector == "#note").Binding, "profile-note-42");
                await session.Page.ClickAsync(opened.Candidates.Single(candidate => candidate.Selector == "#save").Binding);
                Assert.Equal("profile-note-42", (await session.Page.ObserveAsync()).Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            }

            await using (var manager = new BrowserSessionManager())
            await using (var session = await OpenPersistentEdge(manager, profile))
            {
                var reopened = await session.GotoAsync(pageServer.Url);
                Assert.Equal("profile-note-42", reopened.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            }

            await using (var manager = new BrowserSessionManager())
            await using (var session = await OpenPersistentEdge(manager, otherProfile))
            {
                var isolated = await session.GotoAsync(pageServer.Url);
                Assert.Equal("empty", isolated.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            }
        }
        finally
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(root, true);
                    break;
                }
                catch (IOException) when (attempt < 9)
                {
                    await Task.Delay(200);
                }
            }
        }
    }

    [Fact]
    public async Task LiveInstalledEdge_MultiPageLoginWithOneTimeCode_ObservesRealNavigationAndVerifies()
    {
        // F38-R02: a real two-PAGE login flow (a genuine second HTTP navigation, not a same-page
        // JS reveal) -- credentials on page 1, a one-time-code field on page 2, reached only after
        // page 1's real navigation completes.
        using var otpPage = LoopbackPage.Start("""
            <input id="otp" aria-label="One-time code">
            <button id="verify">Verify</button>
            <button id="status">awaiting-code</button>
            <script>
            document.getElementById('verify').addEventListener('click', () => {
              document.getElementById('status').textContent =
                document.getElementById('otp').value === '123456' ? 'authenticated' : 'rejected';
            });
            </script>
            """);
        using var loginPage = LoopbackPage.Start($$"""
            <input id="username" aria-label="Username">
            <input id="password" aria-label="Password" type="password">
            <button id="signin">Sign in</button>
            <script>
            document.getElementById('signin').addEventListener('click', () => {
              window.location.href = {{System.Text.Json.JsonSerializer.Serialize(otpPage.Url)}};
            });
            </script>
            """);

        await using var manager = new BrowserSessionManager();
        BrowserSession session;
        try
        {
            session = await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (session)
        {
            var page1 = await session.GotoAsync(loginPage.Url);
            var username = page1.Candidates.Single(item => item.Selector == "#username");
            var password = page1.Candidates.Single(item => item.Selector == "#password");
            var signin = page1.Candidates.Single(item => item.Selector == "#signin");
            await session.Page.FillAsync(username.Binding, "fixture-user");
            await session.Page.FillAsync(password.Binding, "synthetic-password-42!");
            await session.Page.ClickAsync(signin.Binding);

            var nav = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.NavigationCommitted, null, null, otpPage.Url, 5_000));
            Assert.True(nav.Satisfied, $"expected a real navigation to the one-time-code page: {nav.Reason}; actual={nav.Url}");
            // Real navigation replaced the whole page -- page 1's own elements must be gone, not
            // merely hidden by a same-page JS reveal (which is what would make this NOT a real
            // multi-page flow).
            Assert.DoesNotContain(nav.Observation.Candidates, item => item.Selector is "#username" or "#password" or "#signin");

            var otp = nav.Observation.Candidates.Single(item => item.Selector == "#otp");
            var verify = nav.Observation.Candidates.Single(item => item.Selector == "#verify");
            var status = nav.Observation.Candidates.Single(item => item.Selector == "#status");
            Assert.Equal("awaiting-code", status.Text);

            await session.Page.FillAsync(otp.Binding, "123456");
            await session.Page.ClickAsync(verify.Binding);
            var verified = await session.Page.WaitAsync(new BrowserWaitRequest(BrowserWaitCondition.TextEquals, status.Binding, "authenticated", null, 3_000));
            Assert.True(verified.Satisfied, $"expected the one-time code to be accepted: {verified.Reason}");
        }
    }

    [Fact]
    public void Attach_RejectsDebuggingPort()
    {
        var options = new BrowserSessionOptions
        {
            Mode = BrowserSessionMode.AttachApprovedExisting,
            CdpEndpoint = "http://127.0.0.1:9222",
            DebuggingPort = 9223,
        };
        Assert.Throws<BrowserValidationException>(options.Validate);
    }

    [Fact]
    public async Task LiveInstalledEdge_AttachedClose_LeavesOwnedPageOpen()
    {
        var port = FreeLoopbackPort();
        await using var ownedManager = new BrowserSessionManager();
        BrowserSession owned;
        try
        {
            owned = await ownedManager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.FreshIsolated,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
                DebuggingPort = port,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            return;
        }

        await using (owned)
        {
            var html = "<button id=\"marker\">still-open</button>";
            var opened = await owned.GotoAsync("data:text/html," + Uri.EscapeDataString(html));
            Assert.Equal("still-open", opened.Candidates.Single(candidate => candidate.Selector == "#marker").Text);

            await using var attachedManager = new BrowserSessionManager();
            var attached = await attachedManager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.AttachApprovedExisting,
                CdpEndpoint = $"http://127.0.0.1:{port}",
                TimeoutMs = 20_000,
            });
            var seen = await attached.Page.ObserveAsync();
            Assert.Equal("still-open", seen.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            Assert.False(attached.OwnsResources);
            await attachedManager.DisposeAsync();

            var after = await owned.Page.ObserveAsync();
            Assert.Equal("still-open", after.Candidates.Single(candidate => candidate.Selector == "#marker").Text);
            await owned.Page.ClickAsync(after.Candidates.Single(candidate => candidate.Selector == "#marker").Binding);
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<BrowserSession> OpenPersistentEdge(BrowserSessionManager manager, string profile)
    {
        try
        {
            return await manager.OpenAsync(new BrowserSessionOptions
            {
                Mode = BrowserSessionMode.DedicatedPersistent,
                Channel = "msedge",
                Headless = true,
                TimeoutMs = 20_000,
                ProfilePath = profile,
            });
        }
        catch (BrowserUnavailableException ex)
        {
            Assert.Fail("Installed Edge channel was unavailable: " + ex.Message);
            throw;
        }
    }

    private sealed class LoopbackPage : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public string Url { get; }

        private LoopbackPage(System.Net.Sockets.TcpListener listener, string url)
        {
            _listener = listener;
            Url = url;
        }

        public static LoopbackPage Start(string html)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var page = new LoopbackPage(listener, $"http://127.0.0.1:{port}/note.html");
            var body = System.Text.Encoding.UTF8.GetBytes(html);
            var header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            _ = Task.Run(async () =>
            {
                while (!page._stop.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(page._stop.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        await using (var stream = client.GetStream())
                        {
                            var request = new byte[2048];
                            await stream.ReadAsync(request).ConfigureAwait(false);
                            await stream.WriteAsync(header).ConfigureAwait(false);
                            await stream.WriteAsync(body).ConfigureAwait(false);
                        }
                    });
                }
            });
            return page;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }

    private static string RepositoryRoot()
    {
        var current = Environment.CurrentDirectory;
        if (File.Exists(Path.Combine(current, "src", "ShadowUse", "Program.cs")))
            return Path.GetFullPath(current);
        var metadataRoot = System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(System.Reflection.Assembly.GetExecutingAssembly())
            .FirstOrDefault(attribute => attribute.Key == "DcuRepositoryRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(metadataRoot) && File.Exists(Path.Combine(metadataRoot, "src", "ShadowUse", "Program.cs")))
            return Path.GetFullPath(metadataRoot);
        var configured = Environment.GetEnvironmentVariable("DCU_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "src", "ShadowUse", "Program.cs")))
            return Path.GetFullPath(configured);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "ShadowUse", "Program.cs")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("DCU repository root was not found.");
    }

    private sealed class FakeEngine(FakePage? page = null, FakeBrowser? browser = null) : IBrowserEngine
    {
        private readonly FakeBrowser _browser = browser ?? new FakeBrowser { Page = page ?? new FakePage(), OwnsResources = true };
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IControlledBrowser> OpenAsync(BrowserSessionOptions options, CancellationToken cancellationToken)
        {
            options.Validate();
            return Task.FromResult<IControlledBrowser>(_browser);
        }
    }

    private sealed class FakeBrowser : IControlledBrowser
    {
        public bool OwnsResources { get; set; }
        public string ContextId { get; } = "ctx-test";
        public FakePage Page { get; init; } = new();
        IControlledPage IControlledBrowser.Page => Page;
        public string? HostHwnd => null;
        public int CloseCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            if (OwnsResources)
                CloseCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePage : IControlledPage
    {
        private string _url = "about:blank";
        private bool _webMcpDone;
        public string? LastFilledValue { get; private set; }
        public string Url => _url;
        public BrowserWebMcpPageSnapshot WebMcpSnapshot { get; set; } = new(false, null, []);
        public BrowserWebMcpInvokeResult WebMcpInvokeResult { get; set; } = new(BrowserWebMcpInvokeStatus.Executed, System.Text.Json.Nodes.JsonValue.Create("done"));
        public int WebMcpInvokeCalls { get; private set; }
        public int ClickCalls { get; private set; }
        public bool PointerClickReady { get; set; } = true;
        public bool PointerFillReady { get; set; } = true;
        public BrowserReadinessResult? ForcedReadiness { get; set; }

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            _url = url;
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(string selector, string frameId, CancellationToken cancellationToken)
            => Task.FromResult(selector == "#inside" && frameId is "iframe-0" or "iframe-0/iframe-0" ? 1 : selector == "button" && frameId == "main" ? 2 : selector is "#save" or "#password" or "#field" or "#target" or "#disabled" or "#readonly" or "#marker" ? 1 : 0);

        public Task<BrowserReadinessResult> CheckReadinessAsync(string selector, string frameId, BrowserActionKind operation, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(ForcedReadiness ?? (operation == BrowserActionKind.Click && !PointerClickReady
                ? new BrowserReadinessResult(false, BrowserReadinessFailure.Obscured)
                : operation == BrowserActionKind.Fill && !PointerFillReady
                    ? new BrowserReadinessResult(false, BrowserReadinessFailure.Obscured)
                    : new BrowserReadinessResult(true)));

        public Task<BrowserWebMcpPageSnapshot> DiscoverWebMcpAsync(CancellationToken cancellationToken)
            => Task.FromResult(WebMcpSnapshot);

        public Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<BrowserDiagnosticEntry>>([]);

        public Task<BrowserWebMcpInvokeResult> InvokeWebMcpAsync(BrowserWebMcpCandidate tool, System.Text.Json.Nodes.JsonObject arguments, CancellationToken cancellationToken)
        {
            WebMcpInvokeCalls++;
            if (WebMcpInvokeResult.Status == BrowserWebMcpInvokeStatus.Executed)
                _webMcpDone = true;
            return Task.FromResult(WebMcpInvokeResult);
        }

        public Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            LastClickedFrame = frameId;
            ClickCalls++;
            if (selector == "#save")
                _webMcpDone = true;
            return Task.CompletedTask;
        }

        public Func<string, string, BrowserPointClickResult>? PointClickHandler { get; set; }
        public int PointClickCalls { get; private set; }

        public Task<BrowserPointClickResult> PointClickAsync(string selector, string frameId, double? offsetX, double? offsetY, int timeoutMs, CancellationToken cancellationToken)
        {
            PointClickCalls++;
            if (frameId != "main")
                throw new BrowserUnsupportedException("Point-click fallback is only supported for the main frame in this version.");
            if (PointClickHandler is not null)
                return Task.FromResult(PointClickHandler(selector, frameId));
            if (selector == "#save")
                _webMcpDone = true;
            return Task.FromResult(new BrowserPointClickResult(true, true, true, "element-verified", 10, 10, null));
        }

        public Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            LastFilledValue = value;
            LastFilledFrame = frameId;
            return Task.CompletedTask;
        }

        public Func<string, string, string, BrowserPointFillResult>? PointFillHandler { get; set; }
        public int PointFillCalls { get; private set; }

        public Task<BrowserPointFillResult> PointFillAsync(string selector, string frameId, string value, int timeoutMs, CancellationToken cancellationToken)
        {
            PointFillCalls++;
            if (frameId != "main")
                throw new BrowserUnsupportedException("Point-fill fallback is only supported for the main frame in this version.");
            if (PointFillHandler is not null)
                return Task.FromResult(PointFillHandler(selector, frameId, value));
            LastFilledValue = value;
            LastFilledFrame = frameId;
            return Task.FromResult(new BrowserPointFillResult(true, true, true, 10, 10, value, null));
        }

        public Task PressAsync(string selector, string key, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScrollIntoViewAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> TextAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(selector == "#password" ? "secret" : selector == "#save" && _webMcpDone ? "done" : selector == "#field" && LastFilledValue is not null ? LastFilledValue : selector == "#readonly" ? "fixed" : selector == "#marker" ? "untouched" : "Save");
        public Task<string?> AttributeAsync(string selector, string name, string frameId, CancellationToken cancellationToken)
            => Task.FromResult(name == "type" && selector == "#password" ? "password" : null);
        public Task<bool> EnabledAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> VisibleAsync(string selector, string frameId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<string> DownloadAsync(string selector, string frameId, string downloadRoot, int timeoutMs, CancellationToken cancellationToken)
        {
            var path = BrowserDownloadPaths.Resolve(downloadRoot, "note.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "hello-download");
            return Task.FromResult(path);
        }

        public Task<string> UploadAsync(string selector, string frameId, string uploadRoot, string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            var approved = BrowserUploadPaths.Resolve(uploadRoot, filePath);
            LastUploadPath = approved;
            return Task.FromResult(Path.GetFileName(approved));
        }

        public bool WaitValueResult { get; set; } = true;
        public bool WaitTextResult { get; set; } = true;
        public bool WaitDisappearResult { get; set; } = true;
        public bool WaitNavigationResult { get; set; } = true;
        public Task<bool> WaitValueEqualsAsync(string selector, string frameId, string expectedValue, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(WaitValueResult);
        public Task<bool> WaitTextEqualsAsync(string selector, string frameId, string expectedText, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(WaitTextResult);
        public Task<bool> WaitElementDisappearsAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(WaitDisappearResult);
        public Task<bool> WaitNavigationAsync(string beforeUrl, string expectedUrl, int timeoutMs, CancellationToken cancellationToken)
            => Task.FromResult(WaitNavigationResult);

        public Task<string> DialogAsync(string selector, string frameId, string dialogType, bool accept, string? promptText, int timeoutMs, CancellationToken cancellationToken)
        {
            LastDialogType = BrowserDialogs.Validate(dialogType, accept, promptText);
            LastDialogAccept = accept;
            LastDialogPrompt = promptText;
            LastClickedFrame = frameId;
            return Task.FromResult(LastDialogType);
        }

        public string? LastDialogType { get; private set; }
        public bool? LastDialogAccept { get; private set; }
        public string? LastDialogPrompt { get; private set; }

        public string? LastUploadPath { get; private set; }

        public string? LastClickedFrame { get; private set; }
        public string? LastFilledFrame { get; private set; }

        public Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ObservedElement>>([
            new("#save", "button", "Save", true, true, false, _webMcpDone ? "done" : "Save", "main"),
            new("#password", "input", "Password", true, true, true, "secret", "main"),
            new("#inside", "button", "Inside", true, true, false, "Inside", "iframe-0"),
            new("#field", "input", "Field", true, true, false, null, "main"),
            new("#target", "button", "Target", true, true, false, "Target", "main"),
            new("#disabled", "button", "Disabled", false, true, false, "Disabled", "main"),
            new("#readonly", "input", "Readonly", true, true, false, "fixed", "main"),
            new("#marker", "div", "", true, true, false, "untouched", "main"),
        ]);
    }
}
