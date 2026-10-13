// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Native;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;

namespace ShadowUse.Automation;

/// <summary>Game-mode primitives that the click/type/press paths lack: held keys, fixed-rate key repeat,
/// hover and relative mouse moves. All run through the same foreground activation, backend and
/// foreground-loss checks as the rest of <see cref="InteractiveInput"/>.</summary>
public static partial class InteractiveInput
{
    public const int MaxHoldMs = 60_000;

    private sealed record HeldKey(IntPtr Hwnd, IntPtr Root, uint Pid, IntPtr Layout, CancellationTokenSource Cancellation);
    private static readonly object HeldLock = new();
    private static readonly Dictionary<int, HeldKey> Held = [];

    public static IReadOnlyList<int> HeldKeys { get { lock (HeldLock) return [.. Held.Keys]; } }

    public static IReadOnlyList<int> HeldKeysForTarget(IntPtr hwnd)
    {
        lock (HeldLock) return Held.Where(p => p.Value.Hwnd == hwnd).Select(p => p.Key).ToArray();
    }

    private const int VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04;

    private static int MouseButtonVk(string name) => name.Trim().ToLowerInvariant() switch
    {
        "lmb" or "mouse1" or "leftbutton" => VkLButton,
        "rmb" or "mouse2" or "rightbutton" => VkRButton,
        "mmb" or "mouse3" or "middlebutton" => VkMButton,
        _ => 0,
    };

    /// <summary>Key names joined by '+' become one simultaneous set ("w+shift", "ctrl+w", "rmb+lmb"); each name is one key, modifier,
    /// or mouse button (lmb, rmb, mmb).</summary>
    public static int[] ParseKeySet(string keys)
    {
        var vks = new List<int>();
        foreach (var name in System.Text.RegularExpressions.Regex.Split(keys.Trim(), "[+](?=.)"))
        {
            if (MouseButtonVk(name) is var button and not 0)
            {
                if (!vks.Contains(button)) vks.Add(button);
                continue;
            }
            var (vk, _, mods) = BackgroundInput.ParseKey(name);
            foreach (var m in mods) if (!vks.Contains(m)) vks.Add(m);
            if (!vks.Contains(vk)) vks.Add(vk);
        }
        return [.. vks];
    }

    // Mouse buttons ride the same press/release path as keys; VK 1, 2 and 4 are never keyboard keys.
    private static INPUT KeyOrButton(int vk, bool up, IntPtr layout) => vk switch
    {
        VkLButton => Pure(up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN),
        VkRButton => Pure(up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN),
        VkMButton => Pure(up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN),
        _ => ScanKey(vk, up, layout),
    };

    /// <summary>action: tap (press and release), hold (press, wait durationMs, release), down (press and keep), up (release),
    /// repeat (tap every intervalMs for durationMs).</summary>
    public static async Task<InputResult> GameKeyAsync(IntPtr hwnd, int[] vks, string action, int durationMs, int intervalMs, bool humanize, CancellationToken ct)
    {
        // Hold duration runs from the first accepted press until every press is released, so it also covers chord setup and cancellation.
        long? elapsedHoldMs = null;
        var result = await RunHumanAsync(hwnd, "sendinput_game_key_" + action, ct, r =>
        {
            if (action != "up" && !r.HasForeground()) return ForegroundLost;
            var layout = GetKeyboardLayout(r.ThreadId);
            switch (action)
            {
                case "up":
                    foreach (var vk in vks.Reverse()) ReleaseHeld(vk, hwnd);
                    return null;
                case "down":
                {
                    var tracked = new List<int>();
                    bool completed = false;
                    try
                    {
                        foreach (var vk in vks)
                        {
                            if (!Send(KeyOrButton(vk, false, layout))) return "SendInput rejected the key press";
                            TrackHeld(vk, hwnd, layout);
                            tracked.Add(vk);
                            if (humanize && !r.WaitWhileForeground(r.P(6, 40))) return ForegroundLost;
                        }
                        completed = true;
                        return null;
                    }
                    finally
                    {
                        if (!completed)
                            foreach (var vk in tracked) ReleaseHeld(vk, hwnd);
                    }
                }
            }

            var pressed = new List<int>();
            System.Diagnostics.Stopwatch? heldClock = null;
            try
            {
                if (action == "repeat")
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    for (int n = 0; clock.ElapsedMilliseconds < durationMs; n++)
                    {
                        if (!r.HasForeground()) return ForegroundLost;
                        if (TapSet(r, vks, layout, hold: InputPace.Ms(35), humanize) is { } tapError) return tapError;
                        int gap = Math.Max(0, intervalMs - 35);
                        r.Wait(humanize ? (int)(gap * (0.85 + 0.3 * r.Rng.NextDouble())) : gap);
                    }
                    return null;
                }
                if (action == "tap")
                    return TapSet(r, vks, layout, humanize ? HumanPlanner.KeyHoldMs(r.Rng, InputPace.Fast) : 40, humanize);

                foreach (var vk in vks)
                {
                    if (!Send(KeyOrButton(vk, false, layout))) return "SendInput rejected the key press";
                    heldClock ??= System.Diagnostics.Stopwatch.StartNew();
                    pressed.Add(vk);
                    if (humanize && !r.WaitWhileForeground(r.P(6, 40))) return ForegroundLost;
                }
                int total = humanize ? (int)(durationMs * (0.97 + 0.06 * r.Rng.NextDouble())) : durationMs;
                return r.WaitWhileForeground(total) ? null : ForegroundLost;
            }
            finally
            {
                for (int i = pressed.Count - 1; i >= 0; i--) Send(KeyOrButton(pressed[i], true, layout));
                if (heldClock != null) elapsedHoldMs = heldClock.ElapsedMilliseconds;
            }
        }, string.Join('+', vks.Select(v => "0x" + v.ToString("X2")))).ConfigureAwait(false);
        return result with { ElapsedHoldMs = elapsedHoldMs };
    }

    private static string? TapSet(Run r, int[] vks, IntPtr layout, int hold, bool humanize)
    {
        var pressed = new List<int>();
        try
        {
            foreach (var vk in vks)
            {
                if (!Send(KeyOrButton(vk, false, layout))) return "SendInput rejected the key press";
                pressed.Add(vk);
                if (humanize) r.Wait(r.P(4, 30));
            }
            return r.WaitWhileForeground(hold) ? null : ForegroundLost;
        }
        finally
        {
            for (int i = pressed.Count - 1; i >= 0; i--) Send(KeyOrButton(pressed[i], true, layout));
        }
    }

    // A key left down by "down" is released automatically after MaxHoldMs so a crashed or forgetful caller cannot leave a game running.
    private static void TrackHeld(int vk, IntPtr hwnd, IntPtr layout)
    {
        var root = GetAncestor(hwnd, GA_ROOT);
        uint pid = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId) == 0 ? 0 : processId;
        var held = new HeldKey(hwnd, root, pid, layout, new CancellationTokenSource());
        lock (HeldLock)
        {
            if (Held.Remove(vk, out var previous))
            {
                previous.Cancellation.Cancel();
                previous.Cancellation.Dispose();
            }
            Held[vk] = held;
        }
        _ = MonitorHeldKeyAsync(vk, held);
    }

    private static async Task MonitorHeldKeyAsync(int vk, HeldKey held)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var cancellationToken = held.Cancellation.Token;
        try
        {
            while (elapsed.ElapsedMilliseconds < MaxHoldMs)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                uint pid = NativeMethods.GetWindowThreadProcessId(held.Hwnd, out var currentPid) == 0 ? 0 : currentPid;
                var foreground = NativeMethods.GetForegroundWindow();
                bool focused = foreground != IntPtr.Zero && GetAncestor(foreground, GA_ROOT) == held.Root;
                if (!NativeMethods.IsWindow(held.Hwnd) || GetAncestor(held.Hwnd, GA_ROOT) != held.Root || pid != held.Pid || !focused)
                    break;
            }
        }
        catch (OperationCanceledException) { return; }
        ReleaseHeld(vk, held);
    }

    private static void ReleaseHeld(int vk, IntPtr hwnd)
    {
        HeldKey? held;
        lock (HeldLock)
        {
            if (!Held.TryGetValue(vk, out held) || held.Hwnd != hwnd) return;
            Held.Remove(vk);
            held.Cancellation.Cancel();
        }
        Send(KeyOrButton(vk, true, held.Layout));
        held.Cancellation.Dispose();
    }

    private static void ReleaseHeld(int vk, HeldKey expected)
    {
        lock (HeldLock)
        {
            if (!Held.TryGetValue(vk, out var held) || !ReferenceEquals(held, expected)) return;
            Held.Remove(vk);
            held.Cancellation.Cancel();
        }
        Send(KeyOrButton(vk, true, expected.Layout));
        expected.Cancellation.Dispose();
    }

    /// <summary>Releases every key left down by GameKeyAsync("down"). Returns the count released.</summary>
    public static int ReleaseAllHeldKeys()
    {
        KeyValuePair<int, HeldKey>[] held;
        lock (HeldLock)
        {
            held = [.. Held];
            Held.Clear();
            foreach (var (_, item) in held) item.Cancellation.Cancel();
        }
        foreach (var (vk, item) in held)
        {
            Send(KeyOrButton(vk, true, item.Layout));
            item.Cancellation.Dispose();
        }
        return held.Length;
    }

    /// <summary>Releases keys owned by one target. Returns the count released.</summary>
    public static int ReleaseHeldKeys(IntPtr hwnd) => ReleaseHeldKeys(hwnd, HeldKeys);

    public static int ReleaseHeldKeys(IntPtr hwnd, IEnumerable<int> keys)
    {
        var requested = keys.ToHashSet();
        KeyValuePair<int, HeldKey>[] held;
        lock (HeldLock)
        {
            held = Held.Where(p => p.Value.Hwnd == hwnd && requested.Contains(p.Key)).ToArray();
            foreach (var p in held) { Held.Remove(p.Key); p.Value.Cancellation.Cancel(); }
        }
        foreach (var (vk, item) in held)
        {
            Send(KeyOrButton(vk, true, item.Layout));
            item.Cancellation.Dispose();
        }
        return held.Length;
    }

    /// <summary>Absolute move (hover) or, with relative true, a raw delta (mouse-look). No button is pressed.</summary>
    public static Task<InputResult> GameMoveAsync(IntPtr hwnd, int x, int y, bool relative, HumanizedOptions? human, CancellationToken ct)
        => RunHumanAsync(hwnd, relative ? "sendinput_game_move_rel" : "sendinput_game_move", ct, r =>
        {
            if (!r.HasForeground()) return ForegroundLost;
            if (human != null)
            {
                if (!relative && r.CursorLocked) return "cursor_locked";
                return relative ? GlideOpenLoop(r, human, x, y, 60) : Glide(r, human, x, y, 24);
            }
            if (relative) Send(Relative(x, y));
            else MoveTo(x, y);
            return null;
        }, relative ? $"delta ({x},{y})" : $"to ({x},{y})");

    /// <summary>Probe cursor locking without activating or restoring the target. Used before bounded cursor-follow.</summary>
    public static Task<InputResult> CheckCursorLockForegroundAsync(IntPtr hwnd, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_game_cursor_lock_check", ct, r =>
        {
            if (!r.HasForeground()) return ForegroundLost;
            return r.CursorLocked ? "cursor_locked" : null;
        }, "cursor lock check", activateTarget: false, restoreCursor: false);

    /// <summary>Move only while the target already owns foreground. Never activates the target or restores a prior cursor point.</summary>
    public static Task<InputResult> MoveCursorForegroundOnlyAsync(IntPtr hwnd, int x, int y, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_game_follow_move", ct, r =>
        {
            if (!r.HasForeground()) return ForegroundLost;
            MoveTo(x, y);
            return null;
        }, $"follow to ({x},{y})", activateTarget: false, restoreCursor: false);
}
