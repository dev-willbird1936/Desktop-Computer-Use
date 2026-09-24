namespace ShadowUse.Browser;

public static class BrowserDownloadCompletion
{
    private static readonly string[] PartialSuffixes = [".crdownload", ".partial", ".download"];

    public static async Task<string> WaitAsync(string destination, int timeoutMs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination))
            throw new BrowserValidationException("A completed download requires the exact absolute saved path.");
        if (timeoutMs < 1)
            throw new BrowserValidationException("A download completion wait requires a positive timeout.");
        var full = Path.GetFullPath(destination);
        var deadline = Environment.TickCount64 + timeoutMs;
        long? previous = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stable = TryReadStable(full, out var length);
            if (stable && previous == length)
                return full;
            previous = stable ? length : null;
            if (Environment.TickCount64 >= deadline)
                break;
            var remaining = (int)Math.Min(25, deadline - Environment.TickCount64);
            if (remaining > 0)
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
        if (TryReadStable(full, out var finalLength) && previous == finalLength)
            return full;
        throw new BrowserValidationException(IsPartial(full)
            ? "The download is still partial when the timeout ended."
            : "The download file was missing when the timeout ended.");
    }

    private static bool TryReadStable(string path, out long length)
    {
        length = 0;
        if (IsPartial(path) || !File.Exists(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = stream.Length;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsPartial(string path)
    {
        if (PartialSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;
        return PartialSuffixes.Any(suffix => File.Exists(path + suffix));
    }
}
