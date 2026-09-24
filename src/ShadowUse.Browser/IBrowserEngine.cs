using System.Text.Json.Nodes;

namespace ShadowUse.Browser;

public interface IBrowserEngine : IAsyncDisposable
{
    Task<IControlledBrowser> OpenAsync(BrowserSessionOptions options, CancellationToken cancellationToken);
}

public interface IControlledBrowser : IAsyncDisposable
{
    bool OwnsResources { get; }
    string ContextId { get; }
    IControlledPage Page { get; }

    /// <summary>F42-R02: the real OS window handle backing this session, formatted as "0x...",
    /// when one genuinely exists and could be discovered (a non-headless launch/attach). Null for
    /// headless sessions (there is no OS window to bridge to) or when discovery failed -- never a
    /// guess, only ever a confirmed handle a native coordinate-bridge fallback could act on.</summary>
    string? HostHwnd { get; }
}

public interface IControlledPage
{
    string Url { get; }
    Task GotoAsync(string url, int timeoutMs, CancellationToken cancellationToken);
    Task<int> CountAsync(string selector, string frameId, CancellationToken cancellationToken);
    Task<BrowserReadinessResult> CheckReadinessAsync(string selector, string frameId, BrowserActionKind operation, int timeoutMs, CancellationToken cancellationToken);
    Task ClickAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken);
    Task FillAsync(string selector, string value, string frameId, int timeoutMs, CancellationToken cancellationToken);
    Task PressAsync(string selector, string key, string frameId, int timeoutMs, CancellationToken cancellationToken);
    Task ScrollIntoViewAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken);
    Task<string> TextAsync(string selector, string frameId, CancellationToken cancellationToken);
    Task<string?> AttributeAsync(string selector, string name, string frameId, CancellationToken cancellationToken);
    Task<bool> EnabledAsync(string selector, string frameId, CancellationToken cancellationToken);
    Task<bool> VisibleAsync(string selector, string frameId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ObservedElement>> ElementsAsync(CancellationToken cancellationToken);
    Task<string> DownloadAsync(string selector, string frameId, string downloadRoot, int timeoutMs, CancellationToken cancellationToken);
    Task<string> UploadAsync(string selector, string frameId, string uploadRoot, string filePath, int timeoutMs, CancellationToken cancellationToken);
    Task<string> DialogAsync(string selector, string frameId, string dialogType, bool accept, string? promptText, int timeoutMs, CancellationToken cancellationToken);
    Task<bool> WaitValueEqualsAsync(string selector, string frameId, string expectedValue, int timeoutMs, CancellationToken cancellationToken);
    Task<bool> WaitTextEqualsAsync(string selector, string frameId, string expectedText, int timeoutMs, CancellationToken cancellationToken);
    Task<bool> WaitElementDisappearsAsync(string selector, string frameId, int timeoutMs, CancellationToken cancellationToken);
    Task<bool> WaitNavigationAsync(string beforeUrl, string expectedUrl, int timeoutMs, CancellationToken cancellationToken);
    Task<BrowserWebMcpPageSnapshot> DiscoverWebMcpAsync(CancellationToken cancellationToken);
    Task<BrowserWebMcpInvokeResult> InvokeWebMcpAsync(BrowserWebMcpCandidate tool, JsonObject arguments, CancellationToken cancellationToken);
    Task<IReadOnlyList<BrowserDiagnosticEntry>> DiagnosticsAsync(CancellationToken cancellationToken);

    /// <summary>F42-R02 coordinate-bridge fallback: click via real CDP mouse dispatch at the
    /// element's fresh bounding-box point instead of Playwright's DOM/accessibility click path.
    /// Main frame only (frameId must be "main") -- BoundingBoxAsync is main-frame-relative but a
    /// sub-frame's own elementFromPoint probe needs frame-local coordinates, an unresolved
    /// translation left out of this version rather than guessed. Never moves the OS cursor: this
    /// dispatches through Chromium's own input pipeline (isTrusted CDP events), not the native
    /// desktop input path.</summary>
    Task<BrowserPointClickResult> PointClickAsync(string selector, string frameId, double? offsetX, double? offsetY, int timeoutMs, CancellationToken cancellationToken);
    Task<BrowserPointFillResult> PointFillAsync(string selector, string frameId, string value, int timeoutMs, CancellationToken cancellationToken);
}

public sealed record BrowserDiagnosticEntry(string Kind, string Text, string Url, int? Status, DateTimeOffset At);

public sealed record ObservedElement(
    string Selector,
    string Role,
    string? Name,
    bool Enabled,
    bool Visible,
    bool Password,
    string? Text,
    string FrameId = "main",
    IReadOnlyList<string>? SupportedOperations = null);
