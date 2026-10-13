// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;

namespace ShadowUse.Automation;

/// <summary>Bounded, atomic JSON persistence for small user-local DCU knowledge stores.</summary>
internal static class LocalStateFile
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static T? Read<T>(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaxBytes) return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return default;
        }
    }

    public static bool Write<T>(string? path, T value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        string? temp = null;
        try
        {
            string json = JsonSerializer.Serialize(value, Json);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
            {
                error = $"Local state exceeds the {MaxBytes}-byte persistence limit.";
                return false;
            }
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, fullPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            error = $"Unable to persist local DCU state: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (temp is not null)
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
