using System.Text.Json;

namespace ShadowUse.Config;

/// <summary>
/// User-tunable behavior. Loaded once from settings.json next to the exe
/// (falling back to %APPDATA%\shadow-use\settings.json). All trust gates are
/// OFF by default — the tool acts on anything it's pointed at, including
/// password fields. Flip to true only if you want the training wheels back.
/// </summary>
public sealed class ShadowSettings
{
    /// <summary>Allow UIA ValuePattern append as a typing fallback (can briefly foreground some apps).</summary>
    public bool AllowUiaTextFallback { get; set; } = true;

    /// <summary>Refuse coordinate input when the window moved since the last snapshot.</summary>
    public bool EnableBoundsGuard { get; set; } = false;

    /// <summary>Refuse to act when the lock screen / secure desktop is active.</summary>
    public bool EnableDesktopCheck { get; set; } = false;

    /// <summary>Show the cosmetic virtual cursor during actions.</summary>
    public bool ShowVirtualCursor { get; set; } = true;

    /// <summary>Restore the user's foreground window + caret after any action that grabs them.</summary>
    public bool EnableFocusGuard { get; set; } = true;

    /// <summary>Extra delay (ms) after each action before the follow-up snapshot.</summary>
    public int PostActionDelayMs { get; set; } = 150;

    public static ShadowSettings Load()
    {
        foreach (var path in Candidates())
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<ShadowSettings>(File.ReadAllText(path)) ?? new ShadowSettings();
            }
            catch { /* bad json → defaults */ }
        }
        return new ShadowSettings();
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "settings.json");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "shadow-use", "settings.json");
    }
}
