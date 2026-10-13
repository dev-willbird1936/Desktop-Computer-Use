namespace ShadowUse.Browser;

public enum BrowserSessionMode
{
    AttachApprovedExisting,
    DedicatedPersistent,
    FreshIsolated,
}

public enum BrowserSessionOwnership
{
    Attached,
    Owned,
}

public sealed record BrowserTargetRef(
    string Schema,
    string DcuSessionId,
    string AdapterSessionId,
    string ContextId,
    string PageId,
    string FrameId,
    long DocumentEpoch,
    BrowserSessionOwnership Ownership,
    string? HostHwnd)
{
    public const string CurrentSchema = "dcu.browser-target.v1";

    public static BrowserTargetRef Create(
        string dcuSessionId,
        string contextId,
        BrowserSessionOwnership ownership,
        string? hostHwnd = null,
        long documentEpoch = 1)
    {
        if (string.IsNullOrWhiteSpace(dcuSessionId) || string.IsNullOrWhiteSpace(contextId))
            throw new BrowserValidationException("Browser target identity requires a DCU session and context id.");
        if (documentEpoch < 1)
            throw new BrowserValidationException("Document epoch must start at 1.");
        return new(
            CurrentSchema,
            dcuSessionId,
            $"browser-{Guid.NewGuid():N}",
            contextId,
            $"page-{Guid.NewGuid():N}",
            "main",
            documentEpoch,
            ownership,
            hostHwnd);
    }
}

public sealed record BrowserActionBinding(
    string PageId,
    string FrameId,
    long DocumentEpoch,
    string Selector);

public sealed record BrowserCandidate(
    string CandidateId,
    string Selector,
    string Role,
    string? Name,
    bool Enabled,
    bool Visible,
    bool Password,
    string? Text,
    BrowserActionBinding Binding,
    IReadOnlyList<string> SupportedOperations)
{
    public bool PointerReady { get; init; } = true;
}

public sealed record BrowserObservation(
    BrowserTargetRef Target,
    string Url,
    bool Complete,
    string? Truncation,
    IReadOnlyList<BrowserCandidate> Candidates)
{
    public string ObservationId { get; init; } = $"obs-{Guid.NewGuid():N}";
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public long CaptureDurationMs { get; init; }
}

/// <summary>F42-R02 coordinate-bridge fallback result. A structural pointer-move probe runs
/// BEFORE any press: <see cref="MoveVerified"/> false means zero presses were sent, ever --
/// a wrong click is refused rather than silently executed. <see cref="PressVerified"/> false
/// (with <see cref="Executed"/> true) means a press WAS sent but the recorded pointerdown
/// target did not match, e.g. a layout shift between probe and press -- reported, not hidden.
/// <see cref="VerificationKind"/> is "point-verified" for canvas targets (only the dispatch
/// point is provable, not which drawn object was there) or "element-verified" otherwise.</summary>
public sealed record BrowserPointClickResult(
    bool Executed,
    bool MoveVerified,
    bool PressVerified,
    string VerificationKind,
    double RequestedX,
    double RequestedY,
    string? AbortReason);

/// <summary>F42-R02 coordinate-bridge fallback result for text entry. A real CDP click places
/// focus first, verified structurally via document.activeElement -- <see cref="FocusVerified"/>
/// false means zero key events were sent, ever, mirroring BrowserPointClickResult's
/// abort-before-press guarantee for the keyboard case. Existing content is cleared (Ctrl+A,
/// Delete) and the value is typed as real trusted key events, never JS-assigned. <see
/// cref="ObservedValue"/> is a post-type readback (null for a masked password field) so a caller
/// never has to trust the dispatch alone.</summary>
public sealed record BrowserPointFillResult(
    bool Executed,
    bool FocusVerified,
    bool TypeVerified,
    double RequestedX,
    double RequestedY,
    string? ObservedValue,
    string? AbortReason);

public sealed record BrowserSessionOptions
{
    public BrowserSessionMode Mode { get; init; }
    public string? Channel { get; init; } = "msedge";
    public string? ExecutablePath { get; init; }
    public string? CdpEndpoint { get; init; }
    public string? ProfilePath { get; init; }
    public string? AllowedDownloadRoot { get; init; }
    public string? AllowedUploadRoot { get; init; }
    public bool Headless { get; init; } = true;
    public int TimeoutMs { get; init; } = 15_000;
    public int? DebuggingPort { get; init; }
    public string? HostHwnd { get; init; }

    public void Validate()
    {
        if (TimeoutMs is < 1 or > 120_000)
            throw new BrowserValidationException("Browser timeout must be between 1 and 120000 milliseconds.");
        if (DebuggingPort is < 1 or > 65535)
            throw new BrowserValidationException("A debugging port must be between 1 and 65535.");
        ValidateOptionalRoot(ProfilePath, "Profile path");
        ValidateOptionalRoot(AllowedDownloadRoot, "Download root");
        ValidateOptionalRoot(AllowedUploadRoot, "Upload root");
        switch (Mode)
        {
            case BrowserSessionMode.AttachApprovedExisting:
                if (string.IsNullOrWhiteSpace(CdpEndpoint) || !IsLoopback(CdpEndpoint))
                    throw new BrowserValidationException("Attached browser sessions require an explicit loopback CDP endpoint.");
                if (!string.IsNullOrWhiteSpace(AllowedDownloadRoot))
                    throw new BrowserValidationException("Attached browser sessions do not accept a download directory.");
                if (!string.IsNullOrWhiteSpace(AllowedUploadRoot))
                    throw new BrowserValidationException("Attached browser sessions do not accept an upload directory.");
                if (DebuggingPort is not null)
                    throw new BrowserValidationException("An attached browser session does not open a debugging port.");
                break;
            case BrowserSessionMode.DedicatedPersistent:
                if (string.IsNullOrWhiteSpace(ProfilePath))
                    throw new BrowserValidationException("A dedicated persistent session requires an explicit profile path.");
                RequireBrowserExecutable();
                break;
            case BrowserSessionMode.FreshIsolated:
                RequireBrowserExecutable();
                break;
            default:
                throw new BrowserValidationException($"Unsupported browser session mode '{Mode}'.");
        }
    }

    private void RequireBrowserExecutable()
    {
        if (string.IsNullOrWhiteSpace(Channel) && string.IsNullOrWhiteSpace(ExecutablePath))
            throw new BrowserValidationException("An owned browser session requires an installed channel or executable path. Bundled browser download is disabled.");
    }

    private static void ValidateOptionalRoot(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!Path.IsPathRooted(path))
            throw new BrowserValidationException($"{label} must be an absolute path.");
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetPathRoot(full)!, StringComparison.OrdinalIgnoreCase))
            throw new BrowserValidationException($"{label} is not a usable absolute path.");
    }

    public static bool IsLoopback(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0)
            return false;
        var isWebSocket = uri.Scheme is "ws" or "wss";
        if (!isWebSocket && uri.Scheme is not ("http" or "https"))
            return false;
        if (!isWebSocket && uri.AbsolutePath != "/")
            return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }
}

public class BrowserValidationException(string message) : InvalidOperationException(message);
public class BrowserUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public class BrowserStaleException(string message) : InvalidOperationException(message);
public class BrowserAmbiguousException(string message) : InvalidOperationException(message);
public class BrowserUnsupportedException(string message) : InvalidOperationException(message);
