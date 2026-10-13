using ShadowUse.Native;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;

namespace ShadowUse.Automation;

public static partial class InteractiveInput
{
    public const int MaxHoldMs = 60_000;

    public static int[] ParseKeySet(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) throw new ArgumentException("key must not be empty");
        var result = new List<int>();
        foreach (var name in keys.Split('+', StringSplitOptions.TrimEntries))
        {
            int mouse = name.ToLowerInvariant() switch { "lmb" => 1, "rmb" => 2, "mmb" => 4, _ => 0 };
            if (mouse != 0) { result.Add(mouse); continue; }
            var (vk, _, mods) = BackgroundInput.ParseKey(name);
            result.AddRange(mods);
            result.Add(vk);
        }
        return result.Distinct().ToArray();
    }

    private static INPUT KeyOrButton(int vk, bool up, IntPtr layout) => vk switch
    {
        1 => Pure(up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN),
        2 => Pure(up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN),
        4 => Pure(up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN),
        _ => ScanKey(vk, up, layout)
    };

    public static Task<InputResult> GameKeyAsync(IntPtr hwnd, int[] keys, string action, int durationMs, int intervalMs, bool humanize, CancellationToken ct)
    {
        if (action is not ("tap" or "hold" or "repeat"))
            throw new ArgumentException("action must be tap, hold or repeat; use a bounded hold instead of leaving keys down between agent calls");
        if (keys.Length == 0 || durationMs is < 0 or > MaxHoldMs || intervalMs is < 15 or > 10_000)
            throw new ArgumentException("Invalid keys, duration_ms (0-60000) or interval_ms (15-10000)");
        return RunHumanAsync(hwnd, "sendinput_game_key_" + action, ct, r =>
        {
            var layout = GetKeyboardLayout(r.ThreadId);
            string? Tap(int duration)
            {
                var pressed = new List<int>();
                try
                {
                    foreach (int key in keys)
                    {
                        if (!r.HasForeground()) return ForegroundLost;
                        if (!Send(KeyOrButton(key, false, layout))) return "Windows rejected key/button press";
                        pressed.Add(key);
                        if (humanize) r.Wait(r.P(4, 30));
                    }
                    return r.WaitWhileForeground(duration) ? null : ForegroundLost;
                }
                finally { foreach (int key in pressed.AsEnumerable().Reverse()) Send(KeyOrButton(key, true, layout)); }
            }
            int hold = humanize ? HumanPlanner.KeyHoldMs(r.Rng, InputPace.Fast) : InputPace.Ms(40);
            if (action == "hold") return Tap(durationMs);
            if (action == "tap") return Tap(hold);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < durationMs)
            {
                if (Tap(hold) is { } error) return error;
                r.Wait(Math.Max(0, InputPace.Ms(intervalMs) - hold));
            }
            return null;
        }, string.Join('+', keys));
    }

    public static Task<InputResult> GameMoveAsync(IntPtr hwnd, int x, int y, bool relative, HumanizedOptions? human, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_game_move", ct, r =>
        {
            if (!r.HasForeground()) return ForegroundLost;
            if (human != null) return relative ? GlideOpenLoop(r, human, x, y, 60) : Glide(r, human, x, y,24);
            if (relative)
            {
                if (!Send(Relative(x, y))) return "Windows rejected mouse motion";
            }
            else MoveTo(x, y);
            return null;
        }, relative ? $"delta ({x},{y})" : $"to ({x},{y})", restoreCursor: !relative);
}
