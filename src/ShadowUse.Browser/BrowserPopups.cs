namespace ShadowUse.Browser;

public static class BrowserPopups
{
    public const string Prefix = "popup-page:";
    public const string MissingMessage = "The popup page was not found. Choose an explicit popup-page:N selector.";

    public static bool TryParse(string selector, out int index, out string inner)
    {
        index = -1;
        inner = "";
        if (!selector.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var split = selector.IndexOf(">>", StringComparison.Ordinal);
        if (split < 0)
            throw new BrowserUnsupportedException("A popup selector must be popup-page:N>>selector.");
        var number = selector.AsSpan(Prefix.Length, split - Prefix.Length);
        if (!int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) || index < 0)
            throw new BrowserUnsupportedException("A popup selector must be popup-page:N>>selector.");
        if (!selector.AsSpan(0, split).SequenceEqual((Prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).AsSpan()))
            throw new BrowserUnsupportedException("A popup selector must be popup-page:N>>selector.");
        inner = selector[(split + 2)..];
        if (string.IsNullOrWhiteSpace(inner) || inner.Contains("shadow", StringComparison.OrdinalIgnoreCase) || inner.Contains("internal:", StringComparison.OrdinalIgnoreCase))
            throw new BrowserUnsupportedException("A popup selector must be popup-page:N>>selector.");
        return true;
    }

    public static List<T> Remember<T>(IReadOnlyList<T> seen, IEnumerable<T> current, T opener) where T : class
    {
        var list = new List<T>(seen);
        foreach (var page in current)
        {
            if (ReferenceEquals(page, opener) || list.Contains(page))
                continue;
            list.Add(page);
        }
        return list;
    }
}
