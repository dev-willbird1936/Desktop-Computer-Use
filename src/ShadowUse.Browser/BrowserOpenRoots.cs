namespace ShadowUse.Browser;

public static class BrowserOpenRoots
{
    public const string Prefix = "open-root:";
    public const string MissingMessage = "The open shadow root was not found. Closed shadow roots are not supported.";
    public const string FormatMessage = "An open shadow selector must be open-root:N>>selector or open-root:N>>open-root:M>>selector.";
    public const string TooDeepMessage = "Only one nested open shadow root is supported.";

    public static bool TryParse(string selector, out int index, out string inner)
    {
        index = -1;
        inner = "";
        if (!selector.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        if (!TrySegment(selector, out index, out inner))
            throw new BrowserUnsupportedException(FormatMessage);
        if (!inner.StartsWith(Prefix, StringComparison.Ordinal))
            return true;
        if (!TrySegment(inner, out _, out var leaf))
            throw new BrowserUnsupportedException(FormatMessage);
        if (leaf.StartsWith(Prefix, StringComparison.Ordinal))
            throw new BrowserUnsupportedException(TooDeepMessage);
        return true;
    }

    private static bool TrySegment(string selector, out int index, out string inner)
    {
        index = -1;
        inner = "";
        var split = selector.IndexOf(">>", StringComparison.Ordinal);
        if (split < 0)
            return false;
        var number = selector.AsSpan(Prefix.Length, split - Prefix.Length);
        if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) || index < 0)
            return false;
        if (!selector.AsSpan(0, split).SequenceEqual((Prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).AsSpan()))
            return false;
        inner = selector[(split + 2)..];
        if (string.IsNullOrWhiteSpace(inner))
            return false;
        if (inner.StartsWith(Prefix, StringComparison.Ordinal))
            return true;
        return !inner.Contains("shadow", StringComparison.OrdinalIgnoreCase)
            && !inner.Contains("internal:", StringComparison.OrdinalIgnoreCase);
    }
}
