namespace ShadowUse.Browser;

public enum BrowserWaitCondition
{
    ValueEquals,
    TextEquals,
    ElementDisappears,
    NavigationCommitted,
    DownloadCompleted,
}

public sealed record BrowserWaitRequest(
    BrowserWaitCondition Condition,
    BrowserActionBinding? Binding,
    string? ExpectedValue,
    string? ExpectedUrl,
    int TimeoutMs);

public sealed record BrowserWaitResult(
    string Condition,
    bool Satisfied,
    bool TimedOut,
    string? Reason,
    string Url,
    long DocumentEpoch,
    BrowserObservation Observation);

public enum BrowserActionKind
{
    Click,
    Fill,
    Press,
    Scroll,
    FileUpload,
}

public enum BrowserReadinessFailure
{
    Detached,
    Hidden,
    Disabled,
    ReadOnly,
    Obscured,
    Moving,
    Actionability,
}

public sealed record BrowserReadinessResult(bool Ready, BrowserReadinessFailure? Failure = null, string? Detail = null);

public static class BrowserNavigation
{
    public static bool Matches(string currentUrl, string expected)
    {
        if (string.Equals(currentUrl, expected, StringComparison.Ordinal))
            return true;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current))
            return false;
        if (Uri.TryCreate(expected, UriKind.Absolute, out var absolute))
            return string.Equals(current.AbsoluteUri, absolute.AbsoluteUri, StringComparison.Ordinal);
        return string.Equals(current.AbsolutePath, expected, StringComparison.Ordinal)
            || string.Equals(current.PathAndQuery, expected, StringComparison.Ordinal);
    }
}
