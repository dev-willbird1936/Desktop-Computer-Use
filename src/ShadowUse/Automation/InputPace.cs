// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Automation;

/// <summary>
/// Fast pace: shorter waits, hold times, settle delays and randomisation ranges across DCU. Set globally
/// from the FastInput setting; a single call can override it (game tools' pace parameter) for the
/// duration of that call. Timing stays randomised in fast pace, just within narrower, lower ranges.
/// </summary>
public static class InputPace
{
    private const double FastFactor = 0.35;
    private static readonly AsyncLocal<bool?> CallOverride = new();
    private static readonly AsyncLocal<int> CallMaxLatencyMs = new();

    public static bool GlobalFast { get; set; }

    public static bool Fast => CallOverride.Value ?? GlobalFast;

    /// <summary>Upper bound for one cursor move, 0 for none. Longer planned moves are compressed to fit.</summary>
    public static int MaxLatencyMs => CallMaxLatencyMs.Value;

    /// <summary>Scales a fixed wait. Normal pace returns it unchanged.</summary>
    // Never below 15 ms (about one frame at 60 fps), so a press is still seen by per-frame polling.
    public static int Ms(int ms) => Fast && ms > 0 ? Math.Max(Math.Min(ms, 15), (int)Math.Round(ms * FastFactor)) : ms;

    /// <summary>Applies a per-call pace ("fast", "human" or null for the global setting) until the returned scope is disposed.</summary>
    public static IDisposable Scope(string? pace, int maxLatencyMs = 0)
    {
        var previousPace = CallOverride.Value;
        var previousLatency = CallMaxLatencyMs.Value;
        CallOverride.Value = pace?.ToLowerInvariant() switch { "fast" => true, "human" or "normal" => false, _ => previousPace };
        CallMaxLatencyMs.Value = Math.Max(0, maxLatencyMs);
        return new Restore(() => { CallOverride.Value = previousPace; CallMaxLatencyMs.Value = previousLatency; });
    }

    public static bool IsValidPace(string? pace) => pace is null or "" or "fast" or "human" or "normal";

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
