namespace ShadowUse.Browser;

public static class BrowserUploadPaths
{
    public static string Resolve(string uploadRoot, string filePath)
    {
        if (string.IsNullOrWhiteSpace(uploadRoot) || !Path.IsPathRooted(uploadRoot))
            throw new BrowserValidationException("An owned upload requires an absolute upload directory.");
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath))
            throw new BrowserValidationException("The upload file must be an absolute path.");
        var root = Path.GetFullPath(uploadRoot);
        var full = Path.GetFullPath(filePath);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new BrowserValidationException("The upload file is outside the approved directory.");
        if (!File.Exists(full))
            throw new BrowserValidationException("The upload file was not found inside the approved directory.");
        return full;
    }
}
