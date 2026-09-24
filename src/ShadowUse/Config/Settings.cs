// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
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

    /// <summary>Jev mode: "off" (default), "assist", or "fast_loop". Never a permission gate — only a decision-speed path.</summary>
    public string JevMode { get; set; } = "off";

    /// <summary>Jev model name sent with each request.</summary>
    public string JevModel { get; set; } = "jev-latest";

    /// <summary>TypeSafe/Jev endpoint. The API key itself is read from TYPESAFE_API_KEY, never stored here.</summary>
    public string JevBaseUrl { get; set; } = "https://api.typesafe.ai/v1/systemone";

    /// <summary>Per-call Jev timeout in milliseconds.</summary>
    public int JevTimeoutMs { get; set; } = 8_000;

    /// <summary>Record a local JSON-lines run timeline (F48). Off by default: not a required hot-path cost.</summary>
    public bool EnableRunTimeline { get; set; } = false;

    /// <summary>Run timeline file path, used only when EnableRunTimeline is true.</summary>
    public string RunTimelinePath { get; set; } = Path.Combine(ApplicationDataFolder(), "shadow-use", "run-timeline.jsonl");

    /// <summary>F44: optional remote MCP transport. Null/empty (default) means local stdio only --
    /// no network listener, no ASP.NET Core dependency invoked at runtime (F44-T02). A non-empty
    /// URL (e.g. "http://127.0.0.1:8642") explicitly opts into hosting the SAME full desktop/browser
    /// tool set over HTTP instead of stdio (F44-R01/R02) -- not a reduced "safe" subset. Transport
    /// authentication (VPN, reverse proxy, firewall) is the operator's responsibility, not DCU's;
    /// binding to a non-loopback address exposes full computer control to that network.</summary>
    public string? RemoteTransportUrl { get; set; }

    /// <summary>F59-R02: catalogue-constrained interface composition stays behind this flag,
    /// off by default. While disabled, render_composition/dispatch_composition_action return an
    /// explicit blocked status rather than silently no-oping.</summary>
    public bool EnableCatalogueComposition { get; set; } = false;

    public static ShadowSettings Load()
    {
        foreach (var path in Candidates())
        {
            try
            {
                if (File.Exists(path))
                {
                    var settings = JsonSerializer.Deserialize<ShadowSettings>(File.ReadAllText(path)) ?? new ShadowSettings();
                    settings.Normalize();
                    return settings;
                }
            }
            catch { /* bad json → defaults */ }
        }
        return new ShadowSettings();
    }

    // Environment.GetFolderPath(ApplicationData) resolves via the Windows Known Folder API, which
    // does NOT honor a process-level APPDATA environment variable override -- it always returns the
    // real logged-on user's roaming profile folder regardless of what APPDATA is set to. Reading the
    // APPDATA env var directly first (falling back to GetFolderPath only when genuinely unset) lets
    // a caller redirect settings/run-timeline storage by setting APPDATA, matching every other
    // Windows convention where APPDATA is authoritative.
    internal static string ApplicationDataFolder()
        => Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData
            ? appData
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string[] ValidJevModes = ["off", "assist", "fast_loop"];

    private void Normalize()
    {
        PostActionDelayMs = Math.Clamp(PostActionDelayMs, 0, 5_000);
        JevTimeoutMs = Math.Clamp(JevTimeoutMs, 1_000, 60_000);
        if (!ValidJevModes.Contains(JevMode))
            JevMode = "off";
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "settings.json");
        yield return Path.Combine(ApplicationDataFolder(), "shadow-use", "settings.json");
    }
}
