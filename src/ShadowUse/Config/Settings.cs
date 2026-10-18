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

    /// <summary>Jev mode: "off" (default), "assist", or "fast_loop". A decision-mode setting, not sufficient authorization for experimental game input.</summary>
    public string JevMode { get; set; } = "off";

    /// <summary>Decision backend for native, browser, and game Jev tools: "jev" (TypeSafe Choice/Noul; experimental game actions may use image+Box) or "openai" (Responses API with optional image input).</summary>
    public string DecisionBackend { get; set; } = "jev";

    /// <summary>OpenAI Responses model. The API key is read from OPENAI_API_KEY and is never stored in settings.</summary>
    public string OpenAiModel { get; set; } = "gpt-4o-mini";

    /// <summary>OpenAI decision request timeout in milliseconds.</summary>
    public int OpenAiTimeoutMs { get; set; } = 8_000;

    /// <summary>Jev model name sent with each request.</summary>
    public string JevModel { get; set; } = "jev-latest";

    /// <summary>TypeSafe/Jev endpoint. The API key itself is read from TYPESAFE_API_KEY, never stored here.</summary>
    public string JevBaseUrl { get; set; } = "https://api.typesafe.ai/v1/systemone";

    /// <summary>Per-call Jev timeout in milliseconds.</summary>
    public int JevTimeoutMs { get; set; } = 8_000;

    /// <summary>Experimental game image+Box action coordinator. Off by default; only enable in an isolated experimental MCP instance.</summary>
    public bool EnableExperimentalGameJevAct { get; set; } = false;

    /// <summary>Minimum Choice/operation/target/applicability score for experimental game Jev actions (0-1; 0 disables this minimum).</summary>
    public double ExperimentalGameJevActChoiceThreshold { get; set; } = 0.85;

    /// <summary>Minimum target-presence Noul and Box confidence for experimental click actions (0-1; 0 disables this minimum).</summary>
    public double ExperimentalGameJevActClickThreshold { get; set; } = 0.90;

    /// <summary>Refuse top-two scores closer than this margin (0-1; 0 disables the ambiguity-margin check).</summary>
    public double ExperimentalGameJevActAmbiguityMargin { get; set; } = 0.10;

    /// <summary>Optional local detector Python executable; requires torch, torchvision, numpy and Pillow.</summary>
    public string? LocalUiYoloPythonExecutable { get; set; }

    /// <summary>Separately obtained MIT icon_detect_v3/model.pt path. No weights are bundled or downloaded.</summary>
    public string? LocalUiYoloWeightsPath { get; set; }

    /// <summary>Local YOLO input resolution; higher values improve small-target recall at added cost.</summary>
    public int LocalUiYoloImageSize { get; set; } = 1280;

    /// <summary>Local YOLO device: auto, cpu, or cuda.</summary>
    public string LocalUiYoloDevice { get; set; } = "auto";

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

    /// <summary>tools/list exposure: "core" (default) lists the desktop loop plus search_tools/describe_tool/call_tool
    /// and any tool a session loads; every tool stays callable by name. "all" lists the full catalogue.</summary>
    public string ToolExposure { get; set; } = "core";

    /// <summary>Game mode (use when playing a game or the window has no UIA tree): turns on the game_* tools (screenshot, click, move, drag, scroll, key hold/repeat, OCR, HUD bars,
    /// wait), makes an omitted background_mode mean real input (humanized when HumanizedInput is also on), applies the
    /// InputBackendMode settings below, and uses the fast game input profile (the game keeps the foreground and cursor).
    /// Independent of HumanizedInput and of JevMode.</summary>
    public bool GameMode { get; set; } = false;

    /// <summary>Fast pace across DCU (use when lower latency matters; usually wanted for games): shorter waits, key and click holds, settle delays, and tighter humanized paths and typing
    /// (timing stays randomised, just lower). Game tools can override it per call with pace.</summary>
    public bool FastInput { get; set; } = false;

    /// <summary>Human-simulated input (use when input must look human; usually wanted for games): run click, type_text, press_key, scroll, and drag humanized (background_mode=humanized) whenever the
    /// caller passes no background_mode. Humanized input is real SendInput: it takes the foreground and the real cursor.</summary>
    public bool HumanizedInput { get; set; } = false;

    /// <summary>Humanized cursor speed multiplier (0.25-4).</summary>
    public double HumanizedMouseSpeed { get; set; } = 1.0;

    /// <summary>Humanized typing speed multiplier (0.25-4).</summary>
    public double HumanizedTypingSpeed { get; set; } = 1.0;

    /// <summary>Per-letter chance of a self-corrected typo in humanized typing (0-0.2). Text from value_ref/value_env never gets typos.</summary>
    public double HumanizedTypoRate { get; set; } = 0.015;

    // ---------- Input injection backend (anti-detection) ----------

    /// <summary>Input injection backend kind, applied only when GameMode is on. SendInput (detectable) | KernelDriver | HardwareDevice | MemoryWrite | Auto.</summary>
    public string InputBackendMode { get; set; } = "SendInput";

    /// <summary>Kernel driver model: Interception or WdfHidMinidriver.</summary>
    public string KernelDriverKind { get; set; } = "Interception";

    /// <summary>Serial port for hardware-device backend (e.g. COM3).</summary>
    public string? HardwareComPort { get; set; }

    /// <summary>Baud rate for hardware-device serial connection.</summary>
    public int HardwareBaudRate { get; set; } = 115200;

    /// <summary>Target PID for memory-write backend (0 = disabled).</summary>
    public int MemoryWriteTargetPid { get; set; } = 0;

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

    internal static string LocalApplicationDataFolder()
        => Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } localAppData
            ? localAppData
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } path
                ? path
                : ApplicationDataFolder();

    private static readonly string[] ValidJevModes = ["off", "assist", "fast_loop"];
    private static readonly string[] ValidDecisionBackends = ["jev", "openai"];

    private void Normalize()
    {
        PostActionDelayMs = Math.Clamp(PostActionDelayMs, 0, 5_000);
        JevTimeoutMs = Math.Clamp(JevTimeoutMs, 1_000, 60_000);
        OpenAiTimeoutMs = Math.Clamp(OpenAiTimeoutMs, 1_000, 60_000);
        if (!ValidDecisionBackends.Contains(DecisionBackend)) DecisionBackend = "jev";
        if (string.IsNullOrWhiteSpace(OpenAiModel) || OpenAiModel.Length > 100) OpenAiModel = "gpt-4o-mini";
        if (LocalUiYoloDevice is not ("auto" or "cpu" or "cuda")) LocalUiYoloDevice = "auto";
        if (!ValidJevModes.Contains(JevMode))
            JevMode = "off";
        if (ToolExposure is not ("core" or "all"))
            ToolExposure = "core";
        HumanizedMouseSpeed = double.IsFinite(HumanizedMouseSpeed) ? Math.Clamp(HumanizedMouseSpeed, 0.25, 4) : 1.0;
        HumanizedTypingSpeed = double.IsFinite(HumanizedTypingSpeed) ? Math.Clamp(HumanizedTypingSpeed, 0.25, 4) : 1.0;
        HumanizedTypoRate = double.IsFinite(HumanizedTypoRate) ? Math.Clamp(HumanizedTypoRate, 0, 0.2) : 0.015;
        ExperimentalGameJevActChoiceThreshold = double.IsFinite(ExperimentalGameJevActChoiceThreshold) ? Math.Clamp(ExperimentalGameJevActChoiceThreshold, 0, 1) : 0.85;
        ExperimentalGameJevActClickThreshold = double.IsFinite(ExperimentalGameJevActClickThreshold) ? Math.Clamp(ExperimentalGameJevActClickThreshold, 0, 1) : 0.90;
        ExperimentalGameJevActAmbiguityMargin = double.IsFinite(ExperimentalGameJevActAmbiguityMargin) ? Math.Clamp(ExperimentalGameJevActAmbiguityMargin, 0, 1) : 0.10;
        HardwareBaudRate = Math.Clamp(HardwareBaudRate, 9600, 2_000_000);
        MemoryWriteTargetPid = Math.Max(0, MemoryWriteTargetPid);
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "settings.json");
        yield return Path.Combine(ApplicationDataFolder(), "shadow-use", "settings.json");
    }
}
