namespace ShadowUse.Browser;

public static class BrowserFrames
{
    public const string UnsupportedMessage = "Only the main frame, iframe-N, iframe-N/iframe-M, iframe-N/iframe-M/iframe-P, and iframe-N/iframe-M/iframe-P/iframe-Q targets are supported.";

    public static bool IsSupported(string? frameId) =>
        frameId == "main" || (frameId is not null && TryRead(frameId, out _));

    public static bool TryRead(string frameId, out int[] indexes)
    {
        indexes = [];
        if (frameId == "main")
            return true;
        var parts = frameId.Split('/');
        if (parts.Length is < 1 or > 4)
            return false;
        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryIndex(parts[i], out parsed[i]))
                return false;
        }
        indexes = parsed;
        return true;
    }

    private static bool TryIndex(string part, out int index)
    {
        index = -1;
        const string prefix = "iframe-";
        if (!part.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var number = part.AsSpan(prefix.Length);
        if (number.IsEmpty || !int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) || index < 0)
            return false;
        return part == prefix + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
