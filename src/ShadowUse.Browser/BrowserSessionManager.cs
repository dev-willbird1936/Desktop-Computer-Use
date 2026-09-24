using System.Text.Json.Nodes;

namespace ShadowUse.Browser;

public sealed class BrowserSessionManager : IAsyncDisposable
{
    private readonly Func<IBrowserEngine> _engineFactory;
    private readonly List<BrowserSession> _sessions = [];
    private readonly string _dcuSessionId = $"dcu-{Guid.NewGuid():N}";
    private int _opened;

    public BrowserSessionManager() : this(static () => new PlaywrightBrowserEngine()) { }

    public BrowserSessionManager(Func<IBrowserEngine> engineFactory)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    }

    public bool HasOpened => Volatile.Read(ref _opened) != 0;
    public string DcuSessionId => _dcuSessionId;

    public async Task<BrowserSession> OpenAsync(BrowserSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var engine = _engineFactory();
        try
        {
            var browser = await engine.OpenAsync(options, cancellationToken).ConfigureAwait(false);
            var ownership = browser.OwnsResources ? BrowserSessionOwnership.Owned : BrowserSessionOwnership.Attached;
            // Prefer a genuinely discovered handle (BrowserHostWindow, real CDP process lookup +
            // window enumeration) over the caller-supplied hint, which no current caller populates.
            var target = BrowserTargetRef.Create(_dcuSessionId, browser.ContextId, ownership, browser.HostHwnd ?? options.HostHwnd);
            var session = new BrowserSession(engine, browser, target, options.TimeoutMs, options.AllowedDownloadRoot, options.AllowedUploadRoot);
            lock (_sessions)
                _sessions.Add(session);
            Interlocked.Exchange(ref _opened, 1);
            return session;
        }
        catch
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        BrowserSession[] sessions;
        lock (_sessions)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }
        foreach (var session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public BrowserSession? Find(string adapterSessionId)
    {
        if (string.IsNullOrWhiteSpace(adapterSessionId))
            return null;
        lock (_sessions)
            return _sessions.FirstOrDefault(session => session.Target.AdapterSessionId == adapterSessionId);
    }

    public async Task<bool> CloseAsync(string adapterSessionId)
    {
        BrowserSession? session;
        lock (_sessions)
        {
            session = _sessions.FirstOrDefault(item => item.Target.AdapterSessionId == adapterSessionId);
            if (session is not null)
                _sessions.Remove(session);
        }
        if (session is null)
            return false;
        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }
}

public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IBrowserEngine _engine;
    private readonly IControlledBrowser _browser;
    private int _disposed;

    internal BrowserSession(IBrowserEngine engine, IControlledBrowser browser, BrowserTargetRef target, int timeoutMs, string? downloadRoot, string? uploadRoot)
    {
        _engine = engine;
        _browser = browser;
        Target = target;
        DownloadRoot = browser.OwnsResources ? downloadRoot : null;
        UploadRoot = browser.OwnsResources ? uploadRoot : null;
        Page = new BrowserPageSession(browser.Page, target, timeoutMs, browser.OwnsResources, DownloadRoot, UploadRoot);
    }

    public BrowserTargetRef Target { get; private set; }
    public BrowserPageSession Page { get; }
    public bool OwnsResources => _browser.OwnsResources;
    public string? DownloadRoot { get; }
    public string? UploadRoot { get; }

    public async Task<BrowserObservation> GotoAsync(string url, CancellationToken cancellationToken = default)
    {
        var observation = await Page.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        Target = observation.Target;
        return observation;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _browser.DisposeAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class BrowserPageSession
{
    private readonly IControlledPage _page;
    private readonly int _timeoutMs;
    private BrowserTargetRef _target;
    private string _url;

    private readonly bool _ownsResources;
    private readonly string? _downloadRoot;
    private readonly string? _uploadRoot;
    private string? _lastDownloadPath;

    internal BrowserPageSession(IControlledPage page, BrowserTargetRef target, int timeoutMs, bool ownsResources, string? downloadRoot, string? uploadRoot)
    {
        _page = page;
        _target = target;
        _timeoutMs = timeoutMs;
        _ownsResources = ownsResources;
        _downloadRoot = downloadRoot;
        _uploadRoot = uploadRoot;
        _url = page.Url;
    }

    public BrowserTargetRef Target => _target;
    public BrowserObservation? LastObservation { get; private set; }

    public async Task<BrowserObservation> GotoAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new BrowserValidationException("A browser navigation URL is required.");
        await _page.GotoAsync(url, _timeoutMs, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
        return await ObserveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrowserObservation> ObserveAsync(CancellationToken cancellationToken = default)
    {
        NoteUrl(_page.Url);
        var elements = await _page.ElementsAsync(cancellationToken).ConfigureAwait(false);
        var candidates = elements.Select(element => new BrowserCandidate(
            $"cand-{Guid.NewGuid():N}",
            element.Selector,
            element.Role,
            element.Name,
            element.Enabled,
            element.Visible,
            element.Password,
            element.Password ? null : element.Text,
            new BrowserActionBinding(_target.PageId, element.FrameId, _target.DocumentEpoch, element.Selector),
            element.SupportedOperations ?? [])).ToArray();
        LastObservation = new BrowserObservation(_target, _url, candidates.Length < 40, candidates.Length >= 40 ? "candidate_limit" : null, candidates);
        return LastObservation;
    }

    public Task ClickAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
        => ActAsync(binding, BrowserActionKind.Click, (selector, frame, token) => _page.ClickAsync(selector, frame, _timeoutMs, token), cancellationToken);

    /// <summary>F42-R02: exposes the same readiness check ClickAsync uses internally, without
    /// dispatching anything, so a caller can decide up front whether the DOM click path applies
    /// -- the same structural pre-check pattern the native adapter's SupportedOperations already
    /// uses, not exception-message parsing.</summary>
    public async Task<BrowserReadinessResult> CheckClickReadinessAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
    {
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        return await _page.CheckReadinessAsync(binding.Selector, binding.FrameId, BrowserActionKind.Click, _timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>F42-R02 coordinate-bridge fallback dispatch. Deliberately skips RequireReadyAsync's
    /// full click-readiness check (that is precisely what just failed) but still requires the
    /// binding to resolve to exactly one live element before attempting anything.</summary>
    public async Task<BrowserPointClickResult> PointClickAsync(BrowserActionBinding binding, double? offsetX = null, double? offsetY = null, CancellationToken cancellationToken = default)
    {
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        var result = await _page.PointClickAsync(binding.Selector, binding.FrameId, offsetX, offsetY, _timeoutMs, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
        return result;
    }

    public Task FillAsync(BrowserActionBinding binding, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ActAsync(binding, BrowserActionKind.Fill, (selector, frame, token) => _page.FillAsync(selector, value, frame, _timeoutMs, token), cancellationToken);
    }

    /// <summary>F42-R02: same structural pre-check pattern as CheckClickReadinessAsync, for the
    /// fill operation.</summary>
    public async Task<BrowserReadinessResult> CheckFillReadinessAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
    {
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        return await _page.CheckReadinessAsync(binding.Selector, binding.FrameId, BrowserActionKind.Fill, _timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>F42-R02 coordinate-bridge fallback dispatch for text entry. Deliberately skips
    /// RequireReadyAsync's full fill-readiness check (that is precisely what just failed) but
    /// still requires the binding to resolve to exactly one live element before attempting
    /// anything.</summary>
    public async Task<BrowserPointFillResult> PointFillAsync(BrowserActionBinding binding, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        var result = await _page.PointFillAsync(binding.Selector, binding.FrameId, value, _timeoutMs, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
        return result;
    }

    public Task PressAsync(BrowserActionBinding binding, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new BrowserValidationException("A key is required.");
        return ActAsync(binding, BrowserActionKind.Press, (selector, frame, token) => _page.PressAsync(selector, key, frame, _timeoutMs, token), cancellationToken);
    }

    public Task ScrollIntoViewAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
        => ActAsync(binding, BrowserActionKind.Scroll, (selector, frame, token) => _page.ScrollIntoViewAsync(selector, frame, _timeoutMs, token), cancellationToken);

    public async Task<string> DownloadAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
    {
        if (!_ownsResources)
            throw new BrowserValidationException("Attached browser sessions do not save downloads.");
        if (string.IsNullOrWhiteSpace(_downloadRoot))
            throw new BrowserValidationException("An owned download requires an absolute download directory.");
        await RequireReadyAsync(binding, BrowserActionKind.Click, cancellationToken).ConfigureAwait(false);
        var saved = await _page.DownloadAsync(binding.Selector, binding.FrameId, _downloadRoot, _timeoutMs, cancellationToken).ConfigureAwait(false);
        var completed = await BrowserDownloadCompletion.WaitAsync(saved, _timeoutMs, cancellationToken).ConfigureAwait(false);
        _lastDownloadPath = completed;
        NoteUrl(_page.Url);
        return completed;
    }

    public async Task<string> UploadAsync(BrowserActionBinding binding, string filePath, CancellationToken cancellationToken = default)
    {
        if (!_ownsResources)
            throw new BrowserValidationException("Attached browser sessions do not upload files.");
        if (string.IsNullOrWhiteSpace(_uploadRoot))
            throw new BrowserValidationException("An owned upload requires an absolute upload directory.");
        var approved = BrowserUploadPaths.Resolve(_uploadRoot, filePath);
        await RequireReadyAsync(binding, BrowserActionKind.FileUpload, cancellationToken).ConfigureAwait(false);
        var fileName = await _page.UploadAsync(binding.Selector, binding.FrameId, _uploadRoot, approved, _timeoutMs, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
        return fileName;
    }

    public async Task<BrowserWaitResult> WaitAsync(BrowserWaitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TimeoutMs is < 1 or > 120_000)
            throw new BrowserValidationException("Browser wait timeout must be between 1 and 120000 milliseconds.");
        var beforeUrl = LastObservation?.Url ?? _url;
        NoteUrl(_page.Url);
        string? selector = null;
        var frame = "main";
        if (request.Condition is BrowserWaitCondition.ValueEquals or BrowserWaitCondition.TextEquals or BrowserWaitCondition.ElementDisappears)
        {
            var binding = request.Binding ?? throw new BrowserValidationException("This browser wait condition requires a current candidate binding.");
            if (request.Condition == BrowserWaitCondition.ElementDisappears)
            {
                RejectDeferredSelector(binding.Selector);
                if (binding.PageId != _target.PageId || binding.DocumentEpoch != _target.DocumentEpoch)
                    throw new BrowserStaleException("The browser observation is stale. Re-observe the current page before acting.");
                if (!BrowserFrames.IsSupported(binding.FrameId))
                    throw new BrowserUnsupportedException(BrowserFrames.UnsupportedMessage);
                var matches = await _page.CountAsync(binding.Selector, binding.FrameId, cancellationToken).ConfigureAwait(false);
                if (matches > 1)
                    throw new BrowserAmbiguousException($"Selector '{binding.Selector}' matched {matches} elements. Use a unique selector.");
                if (matches == 0)
                {
                    var alreadyGone = await ObserveAsync(cancellationToken).ConfigureAwait(false);
                    return new(request.Condition.ToString(), true, false, null, alreadyGone.Url, alreadyGone.Target.DocumentEpoch, alreadyGone);
                }
            }
            else
            {
                await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
            }
            selector = binding.Selector;
            frame = binding.FrameId;
        }
        else if (request.Binding is not null)
        {
            await RequireUniqueAsync(request.Binding, cancellationToken).ConfigureAwait(false);
        }
        if (request.Condition == BrowserWaitCondition.ValueEquals && request.ExpectedValue is null)
            throw new BrowserValidationException("Value-equals wait requires an exact expected value.");
        if (request.Condition == BrowserWaitCondition.TextEquals && request.ExpectedValue is null)
            throw new BrowserValidationException("Text-equals wait requires an exact expected value.");
        if (request.Condition == BrowserWaitCondition.NavigationCommitted && string.IsNullOrWhiteSpace(request.ExpectedUrl))
            throw new BrowserValidationException("Navigation-committed wait requires the expected URL or path.");
        if (request.Condition == BrowserWaitCondition.DownloadCompleted && !_ownsResources)
            throw new BrowserValidationException("Attached browser sessions cannot wait for a DCU-owned download.");
        if (request.Condition == BrowserWaitCondition.DownloadCompleted && string.IsNullOrWhiteSpace(_lastDownloadPath))
            throw new BrowserValidationException("No owned download is available to check for completion.");

        bool satisfied;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.TimeoutMs);
        try
        {
            satisfied = request.Condition switch
            {
                BrowserWaitCondition.ValueEquals => await _page.WaitValueEqualsAsync(selector!, frame, request.ExpectedValue!, request.TimeoutMs, timeout.Token).ConfigureAwait(false),
                BrowserWaitCondition.TextEquals => await _page.WaitTextEqualsAsync(selector!, frame, request.ExpectedValue!, request.TimeoutMs, timeout.Token).ConfigureAwait(false),
                BrowserWaitCondition.ElementDisappears => await _page.WaitElementDisappearsAsync(selector!, frame, request.TimeoutMs, timeout.Token).ConfigureAwait(false),
                BrowserWaitCondition.NavigationCommitted => await _page.WaitNavigationAsync(beforeUrl, request.ExpectedUrl!, request.TimeoutMs, timeout.Token).ConfigureAwait(false)
                    && BrowserNavigation.Matches(_page.Url, request.ExpectedUrl!),
                BrowserWaitCondition.DownloadCompleted => await WaitForLastDownloadAsync(request.TimeoutMs, timeout.Token).ConfigureAwait(false),
                _ => throw new BrowserValidationException($"Unsupported browser wait condition '{request.Condition}'."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            satisfied = false;
        }
        NoteUrl(_page.Url);
        var observation = await ObserveAsync(cancellationToken).ConfigureAwait(false);
        return new BrowserWaitResult(request.Condition.ToString(), satisfied, !satisfied, satisfied ? null : "The condition was not observed before timeout; outcome is unknown.", observation.Url, observation.Target.DocumentEpoch, observation);
    }

    private async Task<bool> WaitForLastDownloadAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            await BrowserDownloadCompletion.WaitAsync(_lastDownloadPath!, timeoutMs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BrowserValidationException)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken = default)
        => _page.DiagnosticsAsync(cancellationToken);

    public async Task<BrowserWebMcpCatalog> DiscoverWebMcpAsync(CancellationToken cancellationToken = default)
    {
        NoteUrl(_page.Url);
        BrowserWebMcpPageSnapshot snapshot;
        try
        {
            snapshot = await _page.DiscoverWebMcpAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserUnavailableException ex)
        {
            return new(false, BrowserWebMcp.GetSecureOrigin(_page.Url), [], ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, BrowserWebMcp.GetSecureOrigin(_page.Url), [], "WebMCP discovery failed: " + ex.Message);
        }
        var currentOrigin = BrowserWebMcp.GetSecureOrigin(_page.Url);
        var tools = new List<BrowserWebMcpCandidate>();
        if (snapshot.Supported && snapshot.Error is null && currentOrigin is not null && string.Equals(snapshot.Origin, currentOrigin, StringComparison.OrdinalIgnoreCase))
        {
            var descriptors = snapshot.Tools.Take(64).ToArray();
            var duplicateNames = descriptors.Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(tool => tool.Name!, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var tool in descriptors)
            {
                var schemaError = BrowserWebMcp.ValidateSchema(tool.InputSchema);
                var valid = !string.IsNullOrWhiteSpace(tool.Name)
                    && tool.Name.Length <= 128
                    && tool.Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
                    && !string.IsNullOrWhiteSpace(tool.Description)
                    && tool.Description.Length <= 2048
                    && string.Equals(tool.Origin, currentOrigin, StringComparison.OrdinalIgnoreCase)
                    && schemaError is null
                    && !duplicateNames.Contains(tool.Name ?? "");
                var validationError = duplicateNames.Contains(tool.Name ?? "")
                    ? "The WebMCP registration name is duplicated on this page."
                    : schemaError ?? (valid ? null : "The WebMCP registration is not bound to this page origin or has invalid metadata.");
                tools.Add(new BrowserWebMcpCandidate(
                    BrowserWebMcp.CandidateId(tool, _target.PageId, _target.DocumentEpoch), tool.Name ?? "", tool.Description ?? "", tool.Origin ?? "",
                    tool.InputSchema?.DeepClone(), valid, validationError,
                    _target.PageId, _target.DocumentEpoch));
            }
        }
        return new BrowserWebMcpCatalog(snapshot.Supported && currentOrigin is not null, currentOrigin, tools, snapshot.Error);
    }

    public async Task<BrowserWebMcpInvokeResult> InvokeWebMcpAsync(BrowserWebMcpCandidate tool, JsonObject arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        NoteUrl(_page.Url);
        if (tool.PageId != _target.PageId || tool.DocumentEpoch != _target.DocumentEpoch)
            return new(BrowserWebMcpInvokeStatus.RegistrationChanged, null);
        if (!tool.Valid || BrowserWebMcp.ValidateSchema(tool.InputSchema) is not null)
            return new(BrowserWebMcpInvokeStatus.Unavailable, null);
        var current = await DiscoverWebMcpAsync(cancellationToken).ConfigureAwait(false);
        var fresh = current.Tools.FirstOrDefault(candidate => candidate.Valid && candidate.CandidateId == tool.CandidateId);
        if (fresh is null || !string.Equals(BrowserWebMcp.Canonical(fresh.InputSchema), BrowserWebMcp.Canonical(tool.InputSchema), StringComparison.Ordinal)
            || !string.Equals(fresh.Origin, tool.Origin, StringComparison.OrdinalIgnoreCase))
            return new(BrowserWebMcpInvokeStatus.RegistrationChanged, null);
        var argumentError = BrowserWebMcp.ValidateArguments(tool.InputSchema!, arguments);
        if (argumentError is not null)
            throw new BrowserValidationException(argumentError);
        return await _page.InvokeWebMcpAsync(fresh, arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrowserRoutedAction> RouteWebMcpOrClickAsync(BrowserWebMcpCandidate? tool, JsonObject? arguments, BrowserActionBinding fallback, CancellationToken cancellationToken = default)
    {
        if (tool is not null && arguments is not null)
        {
            var result = await InvokeWebMcpAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            if (result.Status == BrowserWebMcpInvokeStatus.Executed)
                return new("webmcp", "A fresh page-bound WebMCP registration matched the validated arguments.", result.Result, await ObserveAsync(cancellationToken).ConfigureAwait(false));
            if (result.Status == BrowserWebMcpInvokeStatus.Failed)
                throw new BrowserValidationException("The WebMCP tool was invoked but failed: " + result.Error);
            var reason = result.Status == BrowserWebMcpInvokeStatus.RegistrationChanged
                ? "The page tool registration changed after discovery; used the supplied browser action instead."
                : "WebMCP is unavailable or its registration is invalid; used the supplied browser action instead.";
            await ClickAsync(fallback, cancellationToken).ConfigureAwait(false);
            return new("browser", reason, null, await ObserveAsync(cancellationToken).ConfigureAwait(false));
        }
        await ClickAsync(fallback, cancellationToken).ConfigureAwait(false);
        return new("browser", "No usable WebMCP tool was supplied; used the supplied browser action.", null, await ObserveAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<string> DialogAsync(BrowserActionBinding binding, string dialogType, bool accept, string? promptText, CancellationToken cancellationToken = default)
    {
        if (!_ownsResources)
            throw new BrowserValidationException("Attached browser sessions do not answer JavaScript dialogs.");
        BrowserDialogs.Validate(dialogType, accept, promptText);
        await RequireReadyAsync(binding, BrowserActionKind.Click, cancellationToken).ConfigureAwait(false);
        var handled = await _page.DialogAsync(binding.Selector, binding.FrameId, dialogType, accept, promptText, _timeoutMs, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
        return handled;
    }

    public async Task<string?> ReadAttributeAsync(BrowserActionBinding binding, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new BrowserValidationException("An attribute name is required.");
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        return await _page.AttributeAsync(binding.Selector, name, binding.FrameId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadTextAsync(BrowserActionBinding binding, CancellationToken cancellationToken = default)
    {
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        var type = await _page.AttributeAsync(binding.Selector, "type", binding.FrameId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(type, "password", StringComparison.OrdinalIgnoreCase))
            throw new BrowserValidationException("Password values are not returned. Fill can still submit the exact caller value.");
        return await _page.TextAsync(binding.Selector, binding.FrameId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ActAsync(BrowserActionBinding binding, BrowserActionKind operation, Func<string, string, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        await RequireReadyAsync(binding, operation, cancellationToken).ConfigureAwait(false);
        await action(binding.Selector, binding.FrameId, cancellationToken).ConfigureAwait(false);
        NoteUrl(_page.Url);
    }

    private async Task RequireReadyAsync(BrowserActionBinding binding, BrowserActionKind operation, CancellationToken cancellationToken)
    {
        await RequireUniqueAsync(binding, cancellationToken).ConfigureAwait(false);
        var readiness = await _page.CheckReadinessAsync(binding.Selector, binding.FrameId, operation, _timeoutMs, cancellationToken).ConfigureAwait(false);
        if (!readiness.Ready)
            throw ToReadinessException(binding.Selector, readiness);
    }

    private static Exception ToReadinessException(string selector, BrowserReadinessResult readiness) => readiness.Failure switch
    {
        BrowserReadinessFailure.Detached => new BrowserStaleException($"Selector '{selector}' detached before the action; no action was dispatched."),
        BrowserReadinessFailure.Hidden => new BrowserValidationException($"Selector '{selector}' is not visible; no action was dispatched."),
        BrowserReadinessFailure.Disabled => new BrowserValidationException($"Selector '{selector}' is disabled; no action was dispatched."),
        BrowserReadinessFailure.ReadOnly => new BrowserValidationException($"Selector '{selector}' is read-only; no action was dispatched."),
        BrowserReadinessFailure.Obscured => new BrowserAmbiguousException($"Selector '{selector}' is obscured or does not receive pointer events; no click was dispatched."),
        BrowserReadinessFailure.Moving => new BrowserAmbiguousException($"Selector '{selector}' did not become stable before the readiness deadline; no action was dispatched."),
        _ => new BrowserUnavailableException($"Selector '{selector}' failed browser readiness checks; no action was dispatched. {readiness.Detail}"),
    };

    private async Task RequireUniqueAsync(BrowserActionBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        RejectDeferredSelector(binding.Selector);
        NoteUrl(_page.Url);
        if (binding.PageId != _target.PageId || binding.DocumentEpoch != _target.DocumentEpoch)
            throw new BrowserStaleException("The browser observation is stale. Re-observe the current page before acting.");
        if (!BrowserFrames.IsSupported(binding.FrameId))
            throw new BrowserUnsupportedException(BrowserFrames.UnsupportedMessage);
        var count = await _page.CountAsync(binding.Selector, binding.FrameId, cancellationToken).ConfigureAwait(false);
        if (count == 0)
            throw new BrowserStaleException($"Selector '{binding.Selector}' no longer matches the current page.");
        if (count != 1)
            throw new BrowserAmbiguousException($"Selector '{binding.Selector}' matched {count} elements. Use a unique selector.");
    }

    private void NoteUrl(string url)
    {
        if (string.Equals(_url, url, StringComparison.Ordinal))
            return;
        _url = url;
        _target = _target with { DocumentEpoch = _target.DocumentEpoch + 1 };
    }

    internal static void RejectDeferredSelector(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            throw new BrowserValidationException("A selector is required.");
        if (selector.Contains("internal:", StringComparison.OrdinalIgnoreCase)
            || selector.Contains("shadow", StringComparison.OrdinalIgnoreCase))
            throw new BrowserUnsupportedException("Selectors containing shadow or internal: are rejected. Closed shadow roots are not supported.");
    }
}
