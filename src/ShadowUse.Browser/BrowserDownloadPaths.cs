namespace ShadowUse.Browser;

public static class BrowserDownloadPaths
{
    public static string Resolve(string downloadRoot, string? suggestedName)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot) || !Path.IsPathRooted(downloadRoot))
            throw new BrowserValidationException("An owned download requires an absolute download directory.");
        var root = Path.GetFullPath(downloadRoot);
        var name = Path.GetFileName(suggestedName ?? "");
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new BrowserValidationException("The browser download name is not a safe file name.");
        var destination = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new BrowserValidationException("The download path escapes the approved directory.");
        if (!File.Exists(destination))
            return destination;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var suffix = 2; suffix < 1000; suffix++)
        {
            destination = Path.GetFullPath(Path.Combine(root, $"{stem}-{suffix}{extension}"));
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new BrowserValidationException("The download path escapes the approved directory.");
            if (!File.Exists(destination))
                return destination;
        }
        throw new BrowserValidationException("The approved download directory has no free file name.");
    }
}
