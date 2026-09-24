namespace ShadowUse.Browser;

/// <summary>F42-R01: what a caller can actually do with a candidate through the existing browser_*
/// tools, derived from the real HTML tag/type rather than asserted.</summary>
public static class BrowserCapabilities
{
    /// <summary>"select" is click-only because Playwright's generic FillAsync rejects &lt;select&gt;
    /// elements -- reporting "fill" there would be a false capability claim, not an honest one.</summary>
    public static string[] SupportedOperationsFor(string? role, string? type) => role switch
    {
        "button" or "a" or "select" => ["click"],
        "input" => type?.ToLowerInvariant() switch
        {
            "checkbox" or "radio" or "submit" or "button" or "reset" or "image" => ["click"],
            "file" => ["upload"],
            _ => ["fill", "click"],
        },
        "textarea" => ["fill", "click"],
        _ => ["click"],
    };
}
