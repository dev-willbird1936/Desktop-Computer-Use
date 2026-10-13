using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace ShadowUse.Browser;

public sealed class PlaywrightBrowserEngine : IBrowserEngine
{
    private IPlaywright? _playwright;
    private int _disposed;

    public async Task<IControlledBrowser> OpenAsync(BrowserSessionOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
            return options.Mode switch
            {
                BrowserSessionMode.FreshIsolated => await LaunchFreshAsync(_playwright, options, cancellationToken).ConfigureAwait(false),
                BrowserSessionMode.DedicatedPersistent => await LaunchPersistentAsync(_playwright, options, cancellationToken).ConfigureAwait(false),
                BrowserSessionMode.AttachApprovedExisting => await AttachAsync(_playwright, options, cancellationToken).ConfigureAwait(false),
                _ => throw new BrowserValidationException($"Unsupported browser session mode '{options.Mode}'."),
            };
        }
        catch (Exception ex) when (ex is not BrowserValidationException and not BrowserUnavailableException and not OperationCanceledException)
        {
            throw new BrowserUnavailableException("The selected browser runtime is unavailable. DCU did not download a browser. " + ex.Message, ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;
        _playwright?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<IControlledBrowser> LaunchFreshAsync(IPlaywright playwright, BrowserSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var browser = await playwright.Chromium.LaunchAsync(LaunchOptions(options)).ConfigureAwait(false);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { AcceptDownloads = true }).ConfigureAwait(false);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
        var hostHwnd = await BrowserHostWindow.TryDiscoverAsync(browser, options.Headless, cancellationToken).ConfigureAwait(false);
        return new PlaywrightControlledBrowser(browser, context, page, ownsResources: true, $"ctx-{Guid.NewGuid():N}", hostHwnd);
    }

    private static async Task<IControlledBrowser> LaunchPersistentAsync(IPlaywright playwright, BrowserSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(options.ProfilePath!, PersistentOptions(options)).ConfigureAwait(false);
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false);
        var hostHwnd = await BrowserHostWindow.TryDiscoverAsync(context.Browser, options.Headless, cancellationToken).ConfigureAwait(false);
        return new PlaywrightControlledBrowser(context.Browser, context, page, ownsResources: true, $"ctx-{Guid.NewGuid():N}", hostHwnd);
    }

    private static async Task<IControlledBrowser> AttachAsync(IPlaywright playwright, BrowserSessionOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(options.CdpEndpoint!, new BrowserTypeConnectOverCDPOptions
        {
            NoDefaults = true,
            Timeout = options.TimeoutMs,
        }).ConfigureAwait(false);
        var context = browser.Contexts.FirstOrDefault()
            ?? throw new BrowserValidationException("The attached browser has no existing context. DCU will not create one in an attached session.");
        var page = context.Pages.FirstOrDefault()
            ?? throw new BrowserValidationException("The attached browser has no existing page. Register a page explicitly before attaching.");
        // An attached browser is never launched headless by DCU (it is somebody else's already-running
        // browser) -- always attempt discovery rather than trusting options.Headless, which describes
        // DCU's own launch preference and is meaningless for an external process.
        var hostHwnd = await BrowserHostWindow.TryDiscoverAsync(browser, headless: false, cancellationToken).ConfigureAwait(false);
        return new PlaywrightControlledBrowser(browser, context, page, ownsResources: false, $"ctx-{Guid.NewGuid():N}", hostHwnd);
    }

    private static BrowserTypeLaunchOptions LaunchOptions(BrowserSessionOptions options) => new()
    {
        Channel = options.Channel,
        ExecutablePath = options.ExecutablePath,
        Headless = options.Headless,
        Timeout = options.TimeoutMs,
        Args = DebuggingArgs(options),
    };

    private static BrowserTypeLaunchPersistentContextOptions PersistentOptions(BrowserSessionOptions options) => new()
    {
        Channel = options.Channel,
        ExecutablePath = options.ExecutablePath,
        Headless = options.Headless,
        Timeout = options.TimeoutMs,
        AcceptDownloads = true,
        Args = DebuggingArgs(options, "--no-first-run", "--no-default-browser-check", "--disable-sync"),
    };

    private static string[] DebuggingArgs(BrowserSessionOptions options, params string[] extra)
    {
        if (options.DebuggingPort is not int port)
            return extra;
        return [..extra, $"--remote-debugging-port={port}"];
    }

    private sealed class PlaywrightControlledBrowser(
        IBrowser? browser,
        IBrowserContext context,
        IPage page,
        bool ownsResources,
        string contextId,
        string? hostHwnd) : IControlledBrowser
    {
        private int _disposed;
        public bool OwnsResources { get; } = ownsResources;
        public string ContextId { get; } = contextId;
        public IControlledPage Page { get; } = new PlaywrightControlledPage(page);
        public string? HostHwnd { get; } = hostHwnd;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (!OwnsResources)
                return;
            await context.CloseAsync().ConfigureAwait(false);
            if (browser is not null)
                await browser.CloseAsync().ConfigureAwait(false);
        }
    }

    private sealed class PlaywrightControlledPage(IPage page) : IControlledPage
    {
        private const int CandidateLimit = BrowserSessionManager.MaxObservationCandidates + 1;
        private const int FailedRequestLimit = 50;
        private static readonly string[] Roles = ["button", "a", "input", "textarea", "select"];
        private readonly ConcurrentQueue<BrowserDiagnosticEntry> _failedRequests = HookFailedRequests(page);

        public string Url => page.Url;

        // Console/page-error history is buffered by Playwright itself (last 200); only failed
        // requests need a manual bounded subscription since there is no built-in accessor for them.
        private static ConcurrentQueue<BrowserDiagnosticEntry> HookFailedRequests(IPage page)
        {
            var queue = new ConcurrentQueue<BrowserDiagnosticEntry>();
            void Enqueue(BrowserDiagnosticEntry entry)
            {
                queue.Enqueue(entry);
                while (queue.Count > FailedRequestLimit)
                    queue.TryDequeue(out _);
            }
            page.RequestFailed += (_, request) => Enqueue(new BrowserDiagnosticEntry("request_failed", request.Failure ?? "Request failed.", request.Url, null, DateTimeOffset.UtcNow));
            page.Response += (_, response) =>
            {
                if (response.Status >= 400)
                    Enqueue(new BrowserDiagnosticEntry("request_failed", $"HTTP {response.Status}", response.Url, response.Status, DateTimeOffset.UtcNow));
            };
            return queue;
        }

        public async Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var console = await page.ConsoleMessagesAsync().ConfigureAwait(false);
            var pageErrors = await page.PageErrorsAsync().ConfigureAwait(false);
            var entries = new List<BrowserDiagnosticEntry>();
            entries.AddRange(console
                .Where(message => message.Type == "error")
                .Select(message => new BrowserDiagnosticEntry("console_error", message.Text, page.Url, null, DateTimeOffset.UtcNow)));
            entries.AddRange(pageErrors.Select(error => new BrowserDiagnosticEntry("page_exception", error, page.Url, null, DateTimeOffset.UtcNow)));
            entries.AddRange(_failedRequests);
            return entries;
        }

        public async Task<BrowserWebMcpPageSnapshot> DiscoverWebMcpAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string script = "async () => { const mc = document.modelContext; if (!mc || typeof mc.getTools !== 'function' || typeof mc.executeTool !== 'function') return JSON.stringify({ supported: false, origin: location.origin, tools: [] }); let tools; try { tools = await mc.getTools(); } catch (error) { return JSON.stringify({ supported: true, origin: location.origin, tools: [], error: String(error && error.message || error) }); } return JSON.stringify({ supported: true, origin: location.origin, tools: tools.filter(tool => tool && tool.window === window && tool.origin === location.origin).map(tool => ({ name: tool.name, description: tool.description, origin: tool.origin, inputSchema: tool.inputSchema ?? null })) }); }";
            var json = await page.EvaluateAsync<string>(script).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var supported = root.GetProperty("supported").GetBoolean();
                var origin = root.TryGetProperty("origin", out var originNode) && originNode.ValueKind == JsonValueKind.String ? originNode.GetString() : null;
                var snapshotError = root.TryGetProperty("error", out var errorNode) && errorNode.ValueKind == JsonValueKind.String ? errorNode.GetString() : null;
                var tools = new List<BrowserWebMcpToolDescriptor>();
                if (supported && root.TryGetProperty("tools", out var toolList) && toolList.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in toolList.EnumerateArray().Take(64))
                    {
                        string? ReadString(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                        var schema = item.TryGetProperty("inputSchema", out var schemaElement) && schemaElement.ValueKind != JsonValueKind.Null
                            ? JsonNode.Parse(schemaElement.GetRawText())
                            : null;
                        tools.Add(new BrowserWebMcpToolDescriptor(ReadString("name"), ReadString("description"), ReadString("origin"), schema));
                    }
                }
                return new(supported, origin, tools, snapshotError);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new BrowserUnavailableException("WebMCP discovery returned an invalid page snapshot.", ex);
            }
        }

        public async Task<BrowserWebMcpInvokeResult> InvokeWebMcpAsync(BrowserWebMcpCandidate tool, JsonObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new { name = tool.Name, description = tool.Description, origin = tool.Origin, schema = BrowserWebMcp.Canonical(tool.InputSchema), arguments };
            const string script = "async ({ name, description, origin, schema, arguments }) => { const mc = document.modelContext; if (!mc || typeof mc.getTools !== 'function' || typeof mc.executeTool !== 'function') return JSON.stringify({ status: 'unavailable' }); const stable = value => value && typeof value === 'object' ? Array.isArray(value) ? '[' + value.map(stable).join(',') + ']' : '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + stable(value[key])).join(',') + '}' : JSON.stringify(value); let tools; try { tools = await mc.getTools(); } catch (error) { return JSON.stringify({ status: 'unavailable', error: String(error && error.message || error) }); } let tool; try { tool = tools.find(item => item && item.window === window && item.origin === origin && item.origin === location.origin && item.name === name && item.description === description && stable(item.inputSchema ?? null) === schema); } catch { return JSON.stringify({ status: 'unavailable' }); } if (!tool) return JSON.stringify({ status: 'changed' }); try { const result = await mc.executeTool(tool, arguments); let serialized; try { serialized = JSON.stringify(result); } catch { return JSON.stringify({ status: 'failed', error: 'The WebMCP result is not JSON-serializable.' }); } return JSON.stringify({ status: 'executed', result: serialized === undefined ? null : JSON.parse(serialized), hasResult: serialized !== undefined }); } catch (error) { return JSON.stringify({ status: 'failed', error: String(error && error.message || error) }); } }";
            var json = await page.EvaluateAsync<string>(script, payload).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var status = root.GetProperty("status").GetString();
                return status switch
                {
                    "executed" => new(BrowserWebMcpInvokeStatus.Executed, root.TryGetProperty("hasResult", out var hasResult) && hasResult.GetBoolean() && root.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null ? JsonNode.Parse(result.GetRawText()) : null),
                    "changed" => new(BrowserWebMcpInvokeStatus.RegistrationChanged, null),
                    "unavailable" => new(BrowserWebMcpInvokeStatus.Unavailable, null),
                    "failed" => new(BrowserWebMcpInvokeStatus.Failed, null, root.TryGetProperty("error", out var error) ? error.GetString() : "WebMCP tool execution failed."),
                    _ => new(BrowserWebMcpInvokeStatus.Unavailable, null),
                };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                throw new BrowserUnavailableException("WebMCP execution returned an invalid page result.", ex);
            }
        }

        public Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return page.GotoAsync(url, new PageGotoOptions { Timeout = timeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded });
        }

        public async Task<int> CountAsync(string selector, string frameId, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            return await locator.CountAsync().ConfigureAwait(false);
        }

        public async Task<BrowserReadinessResult> CheckReadinessAsync(string selector, string frameId, BrowserActionKind operation, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            if (await locator.CountAsync().ConfigureAwait(false) != 1)
                return new(false, BrowserReadinessFailure.Detached);
            var isFileInput = operation == BrowserActionKind.FileUpload
                && await locator.EvaluateAsync<bool>("element => element instanceof HTMLInputElement && element.type === 'file'").ConfigureAwait(false);
            if (operation is (BrowserActionKind.Click or BrowserActionKind.Fill or BrowserActionKind.Press or BrowserActionKind.FileUpload)
                && !isFileInput && !await locator.IsVisibleAsync().ConfigureAwait(false))
                return new(false, BrowserReadinessFailure.Hidden);
            if (operation is (BrowserActionKind.Click or BrowserActionKind.Fill or BrowserActionKind.Press or BrowserActionKind.FileUpload)
                && !await locator.IsEnabledAsync().ConfigureAwait(false))
                return new(false, BrowserReadinessFailure.Disabled);
            if (operation is BrowserActionKind.Fill or BrowserActionKind.Press)
            {
                var readOnly = await locator.EvaluateAsync<bool>("element => 'readOnly' in element && element.readOnly").ConfigureAwait(false);
                if (readOnly)
                    return new(false, BrowserReadinessFailure.ReadOnly);
            }
            if (operation != BrowserActionKind.Click)
                return new(true);
            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs, Trial = true })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new(true);
            }
            catch (TimeoutException ex)
            {
                return new(false, ClassifyActionability(ex.Message), ex.Message);
            }
            catch (PlaywrightException ex)
            {
                return new(false, ClassifyActionability(ex.Message), ex.Message);
            }
        }

        private static BrowserReadinessFailure ClassifyActionability(string message)
        {
            var text = message.ToLowerInvariant();
            if (text.Contains("detached", StringComparison.Ordinal) || text.Contains("not attached", StringComparison.Ordinal))
                return BrowserReadinessFailure.Detached;
            if (text.Contains("not visible", StringComparison.Ordinal) || text.Contains("hidden", StringComparison.Ordinal))
                return BrowserReadinessFailure.Hidden;
            if (text.Contains("disabled", StringComparison.Ordinal))
                return BrowserReadinessFailure.Disabled;
            if (text.Contains("intercepts pointer events", StringComparison.Ordinal) || text.Contains("receives pointer events", StringComparison.Ordinal))
                return BrowserReadinessFailure.Obscured;
            if (text.Contains("stable", StringComparison.Ordinal) || text.Contains("moving", StringComparison.Ordinal))
                return BrowserReadinessFailure.Moving;
            return BrowserReadinessFailure.Actionability;
        }

        public async Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs }).ConfigureAwait(false);
        }

        // F42-R02: coordinate-bridge fallback. Uses page.Mouse (real CDP Input.dispatchMouseEvent,
        // isTrusted events, no OS cursor movement) instead of Playwright's DOM/accessibility click
        // path, gated by a structural pointer-move probe run BEFORE any press -- per architecture
        // review, this makes a silently-wrong click structurally impossible rather than merely
        // unlikely: a probe mismatch aborts with zero presses sent, every time, not just usually.
        public async Task<BrowserPointClickResult> PointClickAsync(string selector, string frameId, double? offsetX, double? offsetY, int timeoutMs, CancellationToken cancellationToken)
        {
            if (frameId != "main")
                throw new BrowserUnsupportedException("Point-click fallback is only supported for the main frame in this version (BoundingBoxAsync is main-frame-relative, but a sub-frame's own elementFromPoint probe needs frame-local coordinates -- that translation is not implemented).");
            cancellationToken.ThrowIfCancellationRequested();
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            if (await locator.CountAsync().ConfigureAwait(false) != 1)
                return new(false, false, false, "element-verified", 0, 0, "Selector is not uniquely present; no click dispatched.");

            var box = await locator.BoundingBoxAsync(new LocatorBoundingBoxOptions { Timeout = timeoutMs }).ConfigureAwait(false);
            if (box is null)
                return new(false, false, false, "element-verified", 0, 0, "Element has no rendered bounding box (hidden or not laid out); no click dispatched.");

            var px = box.X + (offsetX ?? box.Width / 2);
            var py = box.Y + (offsetY ?? box.Height / 2);

            var isCanvas = await locator.EvaluateAsync<bool>("element => element.tagName === 'CANVAS'").ConfigureAwait(false);
            var verificationKind = isCanvas ? "point-verified" : "element-verified";

            // Static pre-check: catches stale layout, scroll drift, and overlays without any
            // pointer event at all.
            var staticOk = await locator.EvaluateAsync<bool>(
                "(element, [x, y]) => { const el = document.elementFromPoint(x, y); return el === element || element.contains(el); }",
                new object[] { px, py }).ConfigureAwait(false);
            if (!staticOk)
                return new(false, false, false, verificationKind, px, py, "elementFromPoint at the target coordinates did not resolve to this element; no click dispatched.");

            // One-shot capture-phase listeners record what the page itself saw, so the probe
            // result comes from the browser's own event target resolution, not a second (possibly
            // inconsistent) coordinate calculation on this side.
            await locator.EvaluateAsync(
                "element => { window.__dcuProbe = { moveOk: false, moveX: null, moveY: null, downOk: false }; " +
                "const onMove = e => { window.__dcuProbe.moveOk = (e.target === element || element.contains(e.target)); window.__dcuProbe.moveX = e.clientX; window.__dcuProbe.moveY = e.clientY; }; " +
                "const onDown = e => { window.__dcuProbe.downOk = (e.target === element || element.contains(e.target)); }; " +
                "window.addEventListener('pointermove', onMove, { capture: true, once: true }); " +
                "window.addEventListener('pointerdown', onDown, { capture: true, once: true }); }").ConfigureAwait(false);

            await page.Mouse.MoveAsync((float)px, (float)py).ConfigureAwait(false);
            var moveProbe = await locator.EvaluateAsync<JsonElement>("() => window.__dcuProbe").ConfigureAwait(false);
            var moveOk = moveProbe.GetProperty("moveOk").GetBoolean();
            var moveX = moveProbe.GetProperty("moveX").GetDouble();
            var moveY = moveProbe.GetProperty("moveY").GetDouble();
            if (!moveOk || Math.Abs(moveX - px) > 1.0 || Math.Abs(moveY - py) > 1.0)
                return new(false, false, false, verificationKind, px, py,
                    $"Pointer-move probe mismatch (target_matched={moveOk}, recorded=[{moveX},{moveY}], requested=[{px},{py}]); aborted before any press.");

            // Probe passed -- press immediately, no other awaited work in between.
            await page.Mouse.DownAsync().ConfigureAwait(false);
            await page.Mouse.UpAsync().ConfigureAwait(false);
            var pressProbe = await locator.EvaluateAsync<JsonElement>("() => window.__dcuProbe").ConfigureAwait(false);
            var pressOk = pressProbe.GetProperty("downOk").GetBoolean();

            return new(true, true, pressOk, verificationKind, px, py,
                pressOk ? null : "A press was dispatched, but the recorded pointerdown target did not match the probed element (layout may have shifted between probe and press).");
        }

        public async Task<BrowserPointFillResult> PointFillAsync(string selector, string frameId, string value, int timeoutMs, CancellationToken cancellationToken)
        {
            if (frameId != "main")
                throw new BrowserUnsupportedException("Point-fill fallback is only supported for the main frame in this version (same limitation as point-click).");
            cancellationToken.ThrowIfCancellationRequested();
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            if (await locator.CountAsync().ConfigureAwait(false) != 1)
                return new(false, false, false, 0, 0, null, "Selector is not uniquely present; no fill dispatched.");

            var box = await locator.BoundingBoxAsync(new LocatorBoundingBoxOptions { Timeout = timeoutMs }).ConfigureAwait(false);
            if (box is null)
                return new(false, false, false, 0, 0, null, "Element has no rendered bounding box (hidden or not laid out); no fill dispatched.");
            // double, not float: a boxed float is not correctly serialized as a JS argument by
            // Playwright's EvaluateAsync (a real bug found live -- "non-finite" on the JS side --
            // not a guess), whereas PointClickAsync's px/py are already double via the offsetX/Y
            // double? coalescing above it.
            double px = box.X + box.Width / 2;
            double py = box.Y + box.Height / 2;

            var staticOk = await locator.EvaluateAsync<bool>(
                "(element, [x, y]) => { const el = document.elementFromPoint(x, y); return el === element || element.contains(el); }",
                new object[] { px, py }).ConfigureAwait(false);
            if (!staticOk)
                return new(false, false, false, px, py, null, "elementFromPoint at the target coordinates did not resolve to this element; no fill dispatched.");

            await page.Mouse.MoveAsync((float)px, (float)py).ConfigureAwait(false);
            await page.Mouse.DownAsync().ConfigureAwait(false);
            await page.Mouse.UpAsync().ConfigureAwait(false);

            var focusOk = await locator.EvaluateAsync<bool>("element => document.activeElement === element").ConfigureAwait(false);
            if (!focusOk)
                return new(true, false, false, px, py, null, "A click was dispatched to place focus, but document.activeElement did not become this element; no key events sent.");

            var isPassword = await locator.EvaluateAsync<bool>("element => element instanceof HTMLInputElement && element.type === 'password'").ConfigureAwait(false);

            await page.Keyboard.PressAsync("Control+A").ConfigureAwait(false);
            await page.Keyboard.PressAsync("Delete").ConfigureAwait(false);
            await page.Keyboard.TypeAsync(value).ConfigureAwait(false);

            var observed = await locator.EvaluateAsync<string>(
                "element => element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement ? element.value : element.textContent").ConfigureAwait(false);
            var typeVerified = observed == value;

            return new(true, true, typeVerified, px, py, isPassword ? null : observed,
                typeVerified ? null : $"Typed, but the observed value after typing did not match what was sent (observed length {observed?.Length ?? 0}).");
        }

        public async Task<bool> WaitValueEqualsAsync(string selector, string frameId, string expectedValue, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            var kind = await locator.EvaluateAsync<string>("element => element instanceof HTMLInputElement ? (element.type === 'password' ? 'password' : element.type === 'file' ? 'unsupported' : 'value') : element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement ? 'value' : 'unsupported'").ConfigureAwait(false);
            if (kind == "password")
                throw new BrowserValidationException("Value-equals waits are not supported for password controls.");
            if (kind != "value")
                throw new BrowserValidationException("Value-equals waits require a non-password input, textarea, or select.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await locator.WaitForFunctionAsync("(element, expected) => element.value === expected", expectedValue,
                    new LocatorWaitForFunctionOptions { Timeout = timeoutMs }).WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<bool> WaitTextEqualsAsync(string selector, string frameId, string expectedText, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            var type = await locator.GetAttributeAsync("type").ConfigureAwait(false);
            if (string.Equals(type, "password", StringComparison.OrdinalIgnoreCase))
                throw new BrowserValidationException("Text-equals waits are not supported for password controls.");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await locator.WaitForFunctionAsync("(element, expected) => element.innerText === expected", expectedText,
                    new LocatorWaitForFunctionOptions { Timeout = timeoutMs }).WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<bool> WaitElementDisappearsAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = timeoutMs })
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<bool> WaitNavigationAsync(string beforeUrl, string expectedUrl, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentUrl = page.Url;
            if (!string.Equals(currentUrl, beforeUrl, StringComparison.Ordinal) && BrowserNavigation.Matches(currentUrl, expectedUrl))
                return true;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrameNavigated(object? _, IFrame frame)
            {
                if (ReferenceEquals(frame, page.MainFrame) && BrowserNavigation.Matches(frame.Url, expectedUrl))
                    completed.TrySetResult();
            }
            page.FrameNavigated += OnFrameNavigated;
            try
            {
                currentUrl = page.Url;
                if (!string.Equals(currentUrl, beforeUrl, StringComparison.Ordinal) && BrowserNavigation.Matches(currentUrl, expectedUrl))
                    return true;
                await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            finally
            {
                page.FrameNavigated -= OnFrameNavigated;
            }
        }

        public async Task<string> DialogAsync(string selector, string frameId, string dialogType, bool accept, string? promptText, int timeoutMs, CancellationToken cancellationToken)
        {
            var expected = BrowserDialogs.Validate(dialogType, accept, promptText);
            var (target, locator) = await DialogTargetAsync(selector, frameId, cancellationToken).ConfigureAwait(false);
            var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<IDialog>? handler = null;
            handler = (_, dialog) =>
            {
                target.Dialog -= handler;
                _ = AnswerAsync(dialog);
            };
            target.Dialog += handler;
            try
            {
                await locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs }).ConfigureAwait(false);
                if (!result.Task.IsCompleted)
                    throw new BrowserValidationException("The selected control did not open the requested JavaScript dialog.");
                return await result.Task.ConfigureAwait(false);
            }
            finally
            {
                target.Dialog -= handler;
            }

            async Task AnswerAsync(IDialog dialog)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!string.Equals(dialog.Type, expected, StringComparison.Ordinal))
                    {
                        await dialog.DismissAsync().ConfigureAwait(false);
                        result.TrySetException(new BrowserValidationException(
                            $"Dialog type '{dialog.Type}' did not match the requested type '{expected}'.{BrowserDialogs.MismatchDismissed}"));
                        return;
                    }
                    if (accept)
                        await dialog.AcceptAsync(expected == "prompt" ? promptText : null).ConfigureAwait(false);
                    else
                        await dialog.DismissAsync().ConfigureAwait(false);
                    result.TrySetResult(expected);
                }
                catch (Exception ex) when (ex is not BrowserValidationException)
                {
                    try { await dialog.DismissAsync().ConfigureAwait(false); } catch { /* the dialog may already be closed */ }
                    result.TrySetException(ex);
                }
            }
        }

        private async Task<(IPage Target, ILocator Locator)> DialogTargetAsync(string selector, string frameId, CancellationToken cancellationToken)
        {
            if (!BrowserPopups.TryParse(selector, out var popupIndex, out var popupSelector))
                return (page, await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false));
            if (!string.IsNullOrEmpty(frameId) && frameId != "main")
                throw new BrowserUnsupportedException("Popup pages do not yet accept iframe or shadow selectors.");
            if (BrowserOpenRoots.TryParse(popupSelector, out _, out _))
                throw new BrowserUnsupportedException("Shadow roots inside popups are deferred.");
            RememberPopups();
            if ((uint)popupIndex >= (uint)_popupPages.Count || _popupPages[popupIndex].IsClosed)
                throw new BrowserUnsupportedException(BrowserPopups.MissingMessage);
            var target = _popupPages[popupIndex];
            return (target, target.Locator("css:light=" + popupSelector));
        }

        public async Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            await locator.FillAsync(value, new LocatorFillOptions { Timeout = timeoutMs }).ConfigureAwait(false);
        }

        public async Task PressAsync(string selector, string key, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            await locator.PressAsync(key, new LocatorPressOptions { Timeout = timeoutMs }).ConfigureAwait(false);
        }

        public async Task ScrollIntoViewAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = timeoutMs }).ConfigureAwait(false);
        }

        public async Task<string> TextAsync(string selector, string frameId, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            // Same InnerText-vs-value distinction as CollectRolesAsync: an input/textarea/select
            // has no meaningful inner text, its content lives in the DOM value property.
            var isFormField = await locator.EvaluateAsync<bool>(
                "element => element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement").ConfigureAwait(false);
            return isFormField
                ? await locator.InputValueAsync().ConfigureAwait(false)
                : await locator.InnerTextAsync().ConfigureAwait(false);
        }

        public async Task<string?> AttributeAsync(string selector, string name, string frameId, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            return await locator.GetAttributeAsync(name).ConfigureAwait(false);
        }

        public async Task<bool> EnabledAsync(string selector, string frameId, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            return await locator.IsEnabledAsync().ConfigureAwait(false);
        }

        public async Task<bool> VisibleAsync(string selector, string frameId, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            return await locator.IsVisibleAsync().ConfigureAwait(false);
        }

        public async Task<string> DownloadAsync(string selector, string frameId, string downloadRoot, int timeoutMs, CancellationToken cancellationToken)
        {
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            var download = await page.RunAndWaitForDownloadAsync(
                () => locator.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs }),
                new PageRunAndWaitForDownloadOptions { Timeout = timeoutMs }).ConfigureAwait(false);
            var destination = BrowserDownloadPaths.Resolve(downloadRoot, download.SuggestedFilename);
            Directory.CreateDirectory(Path.GetFullPath(downloadRoot));
            await download.SaveAsAsync(destination).ConfigureAwait(false);
            return destination;
        }

        public async Task<string> UploadAsync(string selector, string frameId, string uploadRoot, string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var approved = BrowserUploadPaths.Resolve(uploadRoot, filePath);
            var locator = await LocateAsync(frameId, selector, cancellationToken).ConfigureAwait(false);
            await locator.SetInputFilesAsync(approved, new LocatorSetInputFilesOptions { Timeout = timeoutMs }).ConfigureAwait(false);
            return Path.GetFileName(approved);
        }

        private readonly List<IPage> _popupPages = [];

        private async Task<ILocator> LocateAsync(string frameId, string selector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BrowserPopups.TryParse(selector, out var popupIndex, out var popupSelector))
                return await LocatePopupAsync(frameId, popupIndex, popupSelector, cancellationToken).ConfigureAwait(false);
            var scope = FrameScope(frameId);
            if (!BrowserOpenRoots.TryParse(selector, out var rootIndex, out var inner))
                return scope("css:light=" + selector);
            var hosts = await OpenHostsAsync(scope, cancellationToken).ConfigureAwait(false);
            if (rootIndex >= hosts.Count)
                throw new BrowserUnsupportedException(hosts.Count == 0 ? BrowserOpenRoots.MissingMessage : "The open shadow root was not found.");
            var host = hosts[rootIndex];
            if (!BrowserOpenRoots.TryParse(inner, out var nestedIndex, out var leaf))
                return host.Locator(inner);
            var nestedHosts = await OpenHostsInsideAsync(host, cancellationToken).ConfigureAwait(false);
            if (nestedIndex >= nestedHosts.Count)
                throw new BrowserUnsupportedException(nestedHosts.Count == 0 ? BrowserOpenRoots.MissingMessage : "The open shadow root was not found.");
            return nestedHosts[nestedIndex].Locator(leaf);
        }

        private async Task<ILocator> LocatePopupAsync(string frameId, int popupIndex, string selector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(frameId) && frameId != "main")
                throw new BrowserUnsupportedException("Popup pages do not yet accept iframe or shadow selectors.");
            RememberPopups();
            if ((uint)popupIndex >= (uint)_popupPages.Count || _popupPages[popupIndex].IsClosed)
                throw new BrowserUnsupportedException(BrowserPopups.MissingMessage);
            var popup = _popupPages[popupIndex];
            if (!BrowserOpenRoots.TryParse(selector, out var rootIndex, out var inner))
                return popup.Locator("css:light=" + selector);
            var hosts = await OpenHostsAsync(item => popup.Locator(item), cancellationToken).ConfigureAwait(false);
            if (rootIndex >= hosts.Count)
                throw new BrowserUnsupportedException(hosts.Count == 0 ? BrowserOpenRoots.MissingMessage : "The open shadow root was not found.");
            var host = hosts[rootIndex];
            if (!BrowserOpenRoots.TryParse(inner, out var nestedIndex, out var leaf))
                return host.Locator(inner);
            var nestedHosts = await OpenHostsInsideAsync(host, cancellationToken).ConfigureAwait(false);
            if (nestedIndex >= nestedHosts.Count)
                throw new BrowserUnsupportedException(nestedHosts.Count == 0 ? BrowserOpenRoots.MissingMessage : "The open shadow root was not found.");
            return nestedHosts[nestedIndex].Locator(leaf);
        }

        private void RememberPopups()
        {
            var updated = BrowserPopups.Remember(_popupPages, page.Context.Pages, page);
            _popupPages.Clear();
            _popupPages.AddRange(updated);
        }

        private Func<string, ILocator> FrameScope(string frameId)
        {
            if (string.IsNullOrEmpty(frameId) || frameId == "main")
                return selector => page.Locator(selector);
            if (!BrowserFrames.TryRead(frameId, out var indexes) || indexes.Length == 0)
                throw new BrowserUnsupportedException(BrowserFrames.UnsupportedMessage);
            IFrameLocator frame = page.Locator("css:light=iframe").Nth(indexes[0]).ContentFrame;
            for (var depth = 1; depth < indexes.Length; depth++)
                frame = frame.Locator("css:light=iframe").Nth(indexes[depth]).ContentFrame;
            return selector => frame.Locator(selector);
        }

        private static async Task<List<ILocator>> OpenHostsAsync(Func<string, ILocator> scope, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hosts = scope("css:light=[id]");
            var root = scope("css:light=html");
            var indexes = await root.EvaluateAsync<int[]>("""
                (element, maxElements) => {
                  const nodes = element.ownerDocument.querySelectorAll('[id]');
                  const open = [];
                  for (let index = 0; index < nodes.length && index < maxElements && open.length < 8; index++)
                    if (nodes[index].shadowRoot?.mode === 'open') open.push(index);
                  return open;
                }
                """, CandidateLimit).ConfigureAwait(false);
            return indexes.Select(index => hosts.Nth(index)).ToList();
        }

        private static async Task<List<ILocator>> OpenHostsInsideAsync(ILocator host, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await host.EvaluateAsync<string[]>("""
                element => {
                  const root = element.shadowRoot;
                  if (!root || root.mode !== 'open') return [];
                  return [...root.querySelectorAll('[id]')]
                    .filter(node => node.shadowRoot && node.shadowRoot.mode === 'open')
                    .map(node => node.id);
                }
                """).ConfigureAwait(false);
            var open = new List<ILocator>();
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (open.Count >= 8 || string.IsNullOrWhiteSpace(id) || id.IndexOfAny([' ', '.', '#', ':', '[', ']', '>', '+', '~', '\'']) >= 0)
                    continue;
                open.Add(host.Locator("#" + id));
            }
            return open;
        }

        private const int MaxFrameDepth = 4;

        public async Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken)
        {
            var found = new List<ObservedElement>();
            await CollectFramesAsync(found, selector => page.Locator(selector), null, 0, cancellationToken).ConfigureAwait(false);
            RememberPopups();
            for (var popup = 0; popup < _popupPages.Count && !CandidateCollectionFull(found); popup++)
            {
                if (_popupPages[popup].IsClosed)
                    continue;
                var popupPage = _popupPages[popup];
                await CollectRolesAsync(found, role => popupPage.Locator("css:light=" + role), "main", $"popup-page:{popup}>>", cancellationToken).ConfigureAwait(false);
                var hosts = await OpenHostsAsync(item => popupPage.Locator(item), cancellationToken).ConfigureAwait(false);
                for (var hostIndex = 0; hostIndex < hosts.Count && !CandidateCollectionFull(found); hostIndex++)
                {
                    var host = hosts[hostIndex];
                    await CollectOpenRootSurfaceAsync(found, host, $"popup-page:{popup}>>open-root:{hostIndex}>>", cancellationToken).ConfigureAwait(false);
                    var nestedHosts = await OpenHostsInsideAsync(host, cancellationToken).ConfigureAwait(false);
                    for (var nestedIndex = 0; nestedIndex < nestedHosts.Count && !CandidateCollectionFull(found); nestedIndex++)
                        await CollectOpenRootSurfaceAsync(found, nestedHosts[nestedIndex], $"popup-page:{popup}>>open-root:{hostIndex}>>open-root:{nestedIndex}>>", cancellationToken).ConfigureAwait(false);
                }
            }
            return found;
        }

        private async Task CollectFramesAsync(List<ObservedElement> found, Func<string, ILocator> scope, string? framePath, int depth, CancellationToken cancellationToken)
        {
            await CollectAsync(found, scope, framePath ?? "main", cancellationToken).ConfigureAwait(false);
            if (depth >= MaxFrameDepth || CandidateCollectionFull(found))
                return;
            var frames = scope("css:light=iframe");
            var count = await frames.CountAsync().ConfigureAwait(false);
            for (var index = 0; index < count && !CandidateCollectionFull(found); index++)
            {
                var childFrame = frames.Nth(index).ContentFrame;
                var childPath = framePath is null ? $"iframe-{index}" : $"{framePath}/iframe-{index}";
                await CollectFramesAsync(found, selector => childFrame.Locator(selector), childPath, depth + 1, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CollectAsync(List<ObservedElement> found, Func<string, ILocator> scope, string frameId, CancellationToken cancellationToken)
        {
            await CollectRolesAsync(found, role => scope("css:light=" + role), frameId, null, cancellationToken).ConfigureAwait(false);
            var hosts = await OpenHostsAsync(scope, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < hosts.Count && !CandidateCollectionFull(found); index++)
            {
                var host = hosts[index];
                await CollectRolesAsync(found, role => host.Locator(role), frameId, $"open-root:{index}>>", cancellationToken).ConfigureAwait(false);
                var nestedHosts = await OpenHostsInsideAsync(host, cancellationToken).ConfigureAwait(false);
                for (var child = 0; child < nestedHosts.Count && !CandidateCollectionFull(found); child++)
                {
                    var nestedHost = nestedHosts[child];
                    await CollectRolesAsync(found, role => nestedHost.Locator(role), frameId, $"open-root:{index}>>open-root:{child}>>", cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private const string CandidateSnapshotScript = """
            (elements, limits) => {
              const result = [];
              let visibleCount = 0;
              let hiddenUploadCount = 0;
              for (let index = 0; index < elements.length; index++) {
                if (visibleCount >= limits.visible && (limits.role !== 'input' || hiddenUploadCount >= limits.hiddenUploads)) break;
                const element = elements[index];
                const role = element.tagName.toLowerCase();
                const type = element.getAttribute('type');
                const style = getComputedStyle(element);
                const visible = style.visibility !== 'hidden' && style.visibility !== 'collapse' && element.getClientRects().length > 0;
                const rect = element.getBoundingClientRect();
                const x = rect.left + rect.width / 2;
                const y = rect.top + rect.height / 2;
                const hit = x >= 0 && y >= 0 && x < window.innerWidth && y < window.innerHeight ? document.elementFromPoint(x, y) : null;
                const pointerReady = visible && !!hit && (hit === element || element.contains(hit));
                if (visible) {
                  if (visibleCount >= limits.visible) continue;
                  visibleCount++;
                } else {
                  if (role !== 'input' || (type || '').toLowerCase() !== 'file' || hiddenUploadCount >= limits.hiddenUploads) continue;
                  hiddenUploadCount++;
                }
                const password = (type || '').toLowerCase() === 'password';
                const field = role === 'input' || role === 'textarea' || role === 'select';
                const id = element.getAttribute('id');
                const ariaLabel = element.getAttribute('aria-label');
                const name = element.getAttribute('name');
                let ariaDisabled = false;
                for (let parent = element; parent; parent = parent.parentElement)
                  if ((parent.getAttribute('aria-disabled') || '').toLowerCase() === 'true') { ariaDisabled = true; break; }
                const selector = id
                  ? `#${CSS.escape(id)}`
                  : ariaLabel
                    ? `${role}[aria-label=${CSS.escape(ariaLabel)}]`
                    : name
                      ? `${role}[name=${CSS.escape(name)}]`
                      : `${role} >> nth=${index}`;
                result.push({
                  index,
                  id,
                  selector,
                  role,
                  type,
                  ariaLabel,
                  name,
                  enabled: !element.matches(':disabled') && !ariaDisabled,
                  visible,
                  pointerReady,
                  password,
                  text: password ? null : field ? element.value : element.innerText
                });
              }
              return JSON.stringify(result);
            }
            """;

        private async Task CollectOpenRootSurfaceAsync(List<ObservedElement> found, ILocator host, string selectorPrefix, CancellationToken cancellationToken)
        {
            const string script = "element => { const root = element.shadowRoot; if (!root || root.mode !== 'open') return []; return [...root.querySelectorAll('button,a,input,textarea,select')].map(node => node.id).filter(Boolean); }";
            var ids = await host.EvaluateAsync<string[]>(script).ConfigureAwait(false);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny([' ', '.', '#', ':', '[', ']', '>', '+', '~', '\'']) >= 0)
                    continue;
                var item = host.Locator("#" + id);
                var type = await item.GetAttributeAsync("type").ConfigureAwait(false);
                var role = await item.EvaluateAsync<string>("node => node.tagName.toLowerCase()").ConfigureAwait(false);
                var visible = await item.IsVisibleAsync().ConfigureAwait(false);
                bool uploadInput = role == "input" && string.Equals(type, "file", StringComparison.OrdinalIgnoreCase);
                if (!visible && !uploadInput)
                    continue;
                if (visible ? VisibleCandidateCount(found) >= CandidateLimit : HiddenUploadCount(found) >= CandidateLimit)
                    continue;
                var password = string.Equals(type, "password", StringComparison.OrdinalIgnoreCase);
                var name = await item.GetAttributeAsync("aria-label").ConfigureAwait(false)
                    ?? await item.GetAttributeAsync("name").ConfigureAwait(false);
                string? text = password
                    ? null
                    : role is "input" or "textarea" or "select"
                        ? await item.InputValueAsync().ConfigureAwait(false)
                        : await item.InnerTextAsync().ConfigureAwait(false);
                found.Add(new ObservedElement(
                    selectorPrefix + "#" + id,
                    role,
                    name,
                    await item.IsEnabledAsync().ConfigureAwait(false),
                    visible,
                    password,
                    text,
                    "main",
                    BrowserCapabilities.SupportedOperationsFor(role, type)));
            }
        }

        private static int VisibleCandidateCount(List<ObservedElement> found) => found.Count(element => element.Visible);
        private static int HiddenUploadCount(List<ObservedElement> found) => found.Count(element => !element.Visible && element.SupportedOperations?.Contains("upload", StringComparer.Ordinal) == true);
        private static bool CandidateCollectionFull(List<ObservedElement> found)
            => VisibleCandidateCount(found) >= CandidateLimit && HiddenUploadCount(found) >= CandidateLimit;

        private async Task CollectRolesAsync(List<ObservedElement> found, Func<string, ILocator> matches, string frameId, string? selectorPrefix, CancellationToken cancellationToken)
        {
            foreach (var role in Roles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CandidateCollectionFull(found))
                    return;
                var roleMatches = matches(role);
                var json = await roleMatches.EvaluateAllAsync<string>(CandidateSnapshotScript, new
                {
                    role,
                    visible = Math.Max(0, CandidateLimit - VisibleCandidateCount(found)),
                    hiddenUploads = Math.Max(0, CandidateLimit - HiddenUploadCount(found)),
                }).ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = element.GetProperty("index").GetInt32();
                    var roleName = element.GetProperty("role").GetString() ?? role;
                    var type = NullableString(element, "type");
                    var ariaLabel = NullableString(element, "ariaLabel");
                    var htmlName = NullableString(element, "name");
                    var name = ariaLabel ?? htmlName;
                    var password = element.GetProperty("password").GetBoolean();
                    var text = NullableString(element, "text");
                    var visible = element.GetProperty("visible").GetBoolean();
                    var pointerReady = element.GetProperty("pointerReady").GetBoolean();
                    var selector = NullableString(element, "selector") ?? $"{role} >> nth={index}";
                    found.Add(new ObservedElement(
                        selectorPrefix is null ? selector : selectorPrefix + selector,
                        roleName,
                        name,
                        element.GetProperty("enabled").GetBoolean(),
                        visible,
                        password,
                        text,
                        frameId,
                        BrowserCapabilities.SupportedOperationsFor(roleName, type))
                    {
                        PointerReady = pointerReady,
                    });
                }
            }
        }

        private static string? NullableString(JsonElement element, string property)
            => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    }
}
