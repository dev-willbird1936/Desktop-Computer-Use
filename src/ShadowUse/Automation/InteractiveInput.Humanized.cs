// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShadowUse.Native;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// background_mode=humanized: the isolated_interactive path (foreground, real cursor, SendInput) played
/// back from <see cref="HumanPlanner"/> plans so it reads like a person at the machine, including inside
/// games. Cursor motion is sent as small relative deltas at 200-500 Hz (what a mouse produces, so games
/// reading Raw Input see real WM_INPUT deltas and games polling GetCursorPos see a real cursor), steered
/// by the actual cursor position. Buttons and the wheel are sent without coordinates. A cursor the game
/// locks or recentres (mouse-look) is detected and never steered by position. Every held button or key
/// is released on cancellation or failure, and input stops if the target loses the foreground.
/// </summary>
public static partial class InteractiveInput
{
    private const string ForegroundLost = "target window lost the foreground; humanized input stopped";

    /// <summary>Optional cursor-lock probe override used by controlled input tests.</summary>
    public static Func<IntPtr, bool>? CursorLockProvider { get; set; }

    private sealed class Run(IntPtr root, uint threadId, CancellationToken ct)
    {
        public readonly Random Rng = new();
        public readonly uint ThreadId = threadId;
        public CancellationToken CancellationToken => ct;
        private bool? locked;

        /// <summary>True when a 1 px nudge does not move the cursor: the game recentres or locks it.</summary>
        public bool CursorLocked => locked ??= CursorLockProvider?.Invoke(root) ?? ProbeCursorLocked(this);

        /// <summary>Random pause in [lo, hi] ms, scaled down in fast pace.</summary>
        public int P(int lo, int hi) => InputPace.Fast
            ? HumanPlanner.Between(Math.Max(3, (int)(lo * 0.3)), Math.Max(6, (int)(hi * 0.35)), Rng)
            : HumanPlanner.Between(lo, hi, Rng);

        public void Wait(int ms)
        {
            if (ms <= 0) return;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }
        }

        /// <summary>Waits ms while the target keeps the foreground, checking every 25 ms. False when it lost the foreground.</summary>
        public bool WaitWhileForeground(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!HasForeground()) return false;
                long left = ms - sw.ElapsedMilliseconds;
                if (left <= 0) return true;
                Wait((int)Math.Min(25, left));
            }
        }

        public bool HasForeground()
        {
            var fg = ForegroundWindowProvider();
            return fg != IntPtr.Zero && GetAncestor(fg, GA_ROOT) == root;
        }
    }

    private static Task<InputResult> RunHumanAsync(IntPtr hwnd, string method, CancellationToken ct, Func<Run, string?> body, string detail,
        bool activateTarget = true, bool restoreCursor = true)
        => RunAsync(hwnd, method, ct, () =>
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            var threadId = NativeMethods.GetWindowThreadProcessId(root, out _);
            timeBeginPeriod(1);
            try { return body(new Run(root, threadId, ct)); }
            finally { timeEndPeriod(1); }
        }, detail, activateTarget, restoreCursor);

    public static Task<InputResult> HumanClickAsync(IntPtr hwnd, int x, int y, ElementFrame? frame, MouseButton button, int clickCount, HumanizedOptions o, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_human_click", ct, r =>
        {
            GetCursorPos(out var cursor);
            var (tx, ty, width) = (x, y, 24.0);
            if (frame is { } f)
            {
                (tx, ty) = HumanPlanner.PickPoint(f.ScreenX, f.ScreenY, f.Width, f.Height, (cursor.X, cursor.Y), r.Rng);
                width = Math.Min(f.Width, f.Height);
            }
            r.Wait(r.P(40, 140));
            if (r.CursorLocked) return "cursor_locked_click_refused";
            if (Glide(r, o, tx, ty, width) is { } lost) return lost;
            if (Dwell(r, r.P(120, 600)) is { } lostAfterDwell) return lostAfterDwell;
            uint down = ButtonFlags(button).Down;
            int buttonVk = button switch { MouseButton.Right => VkRButton, MouseButton.Middle => VkMButton, _ => VkLButton };
            for (int i = 0; i < clickCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!r.HasForeground()) return ForegroundLost;
                if (CursorPosition() is not { } settledCursor
                    || Math.Abs((long)settledCursor.X - tx) > 2 || Math.Abs((long)settledCursor.Y - ty) > 2)
                    return "cursor_did_not_reach_click_target";
                bool pressed = false;
                string? clickError = null, releaseError = null;
                OperationCanceledException? cancellation = null;
                try
                {
                    if (!Send(Pure(down))) clickError = "SendInput rejected the button press";
                    else
                    {
                        pressed = true;
                        if (!r.WaitWhileForeground(r.P(50, 180))) clickError = ForegroundLost;
                    }
                }
                catch (OperationCanceledException ex) { cancellation = ex; }
                finally
                {
                    if (pressed) releaseError = ReleaseInputWithRecovery(buttonVk, hwnd, IntPtr.Zero);
                }
                if (releaseError is not null)
                    return cancellation is null ? releaseError : $"Input was cancelled; {releaseError}";
                if (cancellation is not null)
                    throw new OperationCanceledException(cancellation.Message, cancellation, ct);
                if (clickError is not null) return clickError;
                if (i + 1 < clickCount) r.Wait(r.P(60, 170));
            }
            r.Wait(r.P(30, 250));
            return null;
        }, $"{button} x{clickCount} at ({x},{y})");

    public static Task<InputResult> HumanDragAsync(IntPtr hwnd, int fromX, int fromY, int toX, int toY, HumanizedOptions o, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_human_drag", ct, r =>
        {
            r.Wait(r.P(40, 140));
            if (Glide(r, o, fromX, fromY, 24) is { } lost) return lost;
            if (Dwell(r, r.P(120, 320)) is { } lostAfterDwell) return lostAfterDwell;
            if (!r.HasForeground()) return ForegroundLost;
            bool pressed = false;
            try
            {
                if (!Send(Pure(MOUSEEVENTF_LEFTDOWN))) return "SendInput rejected the button press";
                pressed = true;
                r.Wait(r.P(70, 280));
                var dragLost = r.CursorLocked
                    ? GlideOpenLoop(r, o, toX - fromX, toY - fromY, 30)
                    : Glide(r, o, toX, toY, 30);
                if (dragLost != null) return dragLost;
                r.Wait(r.P(40, 200));
                return null;
            }
            finally { if (pressed) Send(Pure(MOUSEEVENTF_LEFTUP)); }
        }, $"({fromX},{fromY})→({toX},{toY})");

    /// <summary>Wheel input at a jittered point inside the given box (element frame or window centre).</summary>
    public static Task<InputResult> HumanScrollAsync(IntPtr hwnd, int left, int top, int width, int height, string direction, double pages, HumanizedOptions o, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_human_wheel", ct, r =>
        {
            GetCursorPos(out var cursor);
            var (px, py) = HumanPlanner.PickPoint(left, top, width, height, (cursor.X, cursor.Y), r.Rng);
            r.Wait(r.P(30, 180));
            if (Glide(r, o, px, py, 40) is { } lost) return lost;
            if (Dwell(r, r.P(60, 350)) is { } lostAfterDwell) return lostAfterDwell;
            int notches = Math.Max(1, (int)Math.Ceiling(pages * 3));
            uint flag = direction is "left" or "right" ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
            int delta = (direction is "up" or "right" ? 1 : -1) * NativeMethods.WHEEL_DELTA;
            var delays = HumanPlanner.PlanWheelDelays(notches, r.Rng, InputPace.Fast);
            for (int i = 0; i < notches; i++)
            {
                if (!r.HasForeground()) return ForegroundLost;
                if (!Send(Pure(flag, (uint)delta))) return "SendInput rejected the wheel event";
                r.Wait(delays[i]);
            }
            return null;
        }, $"{direction} {pages} pages");

    public static Task<InputResult> HumanPressKeyAsync(IntPtr hwnd, string key, CancellationToken ct)
    {
        var (vk, _, mods) = BackgroundInput.ParseKey(key);
        return RunHumanAsync(hwnd, "sendinput_human_key", ct, r =>
        {
            r.Wait(r.P(60, 220));
            if (!r.HasForeground()) return ForegroundLost;
            var layout = GetKeyboardLayout(r.ThreadId);
            var held = new List<int>();
            int? keyDown = null;
            try
            {
                foreach (var m in mods)
                {
                    if (!Send(ScanKey(m, false, layout))) return "SendInput rejected a modifier press";
                    held.Add(m);
                    r.Wait(r.P(18, 100));
                }
                if (!Send(ScanKey(vk, false, layout))) return "SendInput rejected the key press";
                keyDown = vk;
                r.Wait(HumanPlanner.KeyHoldMs(r.Rng, InputPace.Fast));
                Send(ScanKey(vk, true, layout));
                keyDown = null;
                while (held.Count > 0)
                {
                    r.Wait(r.P(12, 75));
                    Send(ScanKey(held[^1], true, layout));
                    held.RemoveAt(held.Count - 1);
                }
                return null;
            }
            finally
            {
                if (keyDown is { } k) Send(ScanKey(k, true, layout));
                for (int i = held.Count - 1; i >= 0; i--) Send(ScanKey(held[i], true, layout));
            }
        }, key);
    }

    public static Task<InputResult> HumanTypeTextAsync(IntPtr hwnd, string text, HumanizedOptions o, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_human_type", ct, r =>
        {
            r.Wait(r.P(100, 500));
            var steps = HumanPlanner.PlanTyping(text, o.TypingSpeed, o.TypoRate, r.Rng, InputPace.Fast);
            var layout = GetKeyboardLayout(r.ThreadId);
            foreach (var step in steps)
            {
                if (!r.HasForeground()) return ForegroundLost;
                if (TapGlyph(r, step.Glyph, step.HoldMs, layout) is { } error) return error;
                r.Wait(step.DelayAfterMs);
            }
            return null;
        }, $"{text.Length} chars");

    // Cursor visible and responsive: relative deltas along the plan, corrected against the real position.
    // Locked or recentred cursor (mouse-look): it has no screen position to steer to, so no move is made.
    private static string? Glide(Run r, HumanizedOptions o, int x, int y, double targetWidth)
    {
        if (r.CursorLocked) return null;
        GetCursorPos(out var from);
        var plan = HumanPlanner.PlanPath((from.X, from.Y), (x, y), targetWidth, o.MouseSpeed, r.Rng, InputPace.Fast);
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int right = left + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1, bottom = top + GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1;
        // Relative moves go through pointer acceleration, so the position is re-read every few events.
        (int X, int Y) expected = (from.X, from.Y);
        var clock = Stopwatch.StartNew();
        double squeeze = InputPace.MaxLatencyMs > 0 && plan.DurationMs > InputPace.MaxLatencyMs ? InputPace.MaxLatencyMs / (double)plan.DurationMs : 1;
        for (int i = 1; i < plan.Points.Count; i++)
        {
            var p = plan.Points[i];
            r.Wait((int)(p.TMs * squeeze) - (int)clock.ElapsedMilliseconds);
            if (!r.HasForeground()) return ForegroundLost;
            if (i % 4 == 0) { GetCursorPos(out var at); expected = (at.X, at.Y); }
            var goal = (X: Math.Clamp(p.X, left, right), Y: Math.Clamp(p.Y, top, bottom));
            if (goal != expected) Send(Relative(goal.X - expected.X, goal.Y - expected.Y));
            expected = goal;
        }
        for (int i = 0; i < 6; i++)
        {
            r.Wait(r.P(4, 10));
            GetCursorPos(out var at);
            if (at.X == x && at.Y == y) break;
            Send(Relative(x - at.X, y - at.Y));
        }
        return null;
    }

    private static string? GlideOpenLoop(Run r, HumanizedOptions o, int dx, int dy, double targetWidth)
    {
        var plan = HumanPlanner.PlanPath((0, 0), (dx, dy), targetWidth, o.MouseSpeed, r.Rng, InputPace.Fast);
        var last = (X: 0, Y: 0);
        var clock = Stopwatch.StartNew();
        double squeeze = InputPace.MaxLatencyMs > 0 && plan.DurationMs > InputPace.MaxLatencyMs ? InputPace.MaxLatencyMs / (double)plan.DurationMs : 1;
        for (int i = 1; i < plan.Points.Count; i++)
        {
            var p = plan.Points[i];
            r.Wait((int)(p.TMs * squeeze) - (int)clock.ElapsedMilliseconds);
            if (!r.HasForeground()) return ForegroundLost;
            if (p.X != last.X || p.Y != last.Y) Send(Relative(p.X - last.X, p.Y - last.Y));
            last = (p.X, p.Y);
        }
        return null;
    }

    // Resting hand: rare 1 px nudges that return to the start. Skipped for a locked cursor, where they would turn the camera.
    private static string? Dwell(Run r, int ms)
    {
        if (r.CursorLocked || ms < 100) { r.Wait(ms); return null; }
        var clock = Stopwatch.StartNew();
        foreach (var (t, dx, dy) in HumanPlanner.PlanTremor(ms, r.Rng))
        {
            r.Wait(t - (int)clock.ElapsedMilliseconds);
            if (!r.HasForeground()) return ForegroundLost;
            Send(Relative(dx, dy));
        }
        r.Wait(ms - (int)clock.ElapsedMilliseconds);
        return null;
    }

    private static bool ProbeCursorLocked(Run r)
    {
        GetCursorPos(out var before);
        int right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1;
        int step = before.X >= right ? -1 : 1;
        Send(Relative(step, 0));
        r.Wait(HumanPlanner.Between(30, 70, r.Rng)); // long enough for a game that recentres once per frame to have done so
        GetCursorPos(out var after);
        Send(Relative(-step, 0));
        return after.X == before.X;
    }

    private static string? TapGlyph(Run r, string glyph, int holdMs, IntPtr layout)
    {
        switch (glyph)
        {
            case "\b": return TapKey(r, 0x08, false, holdMs, layout);
            case "\n": return TapKey(r, 0x0D, false, holdMs, layout);
            case "\t": return TapKey(r, 0x09, false, holdMs, layout);
        }
        if (glyph.Length == 1)
        {
            short scan = VkKeyScanExW(glyph[0], layout);
            int vk = scan & 0xFF, state = (scan >> 8) & 0xFF;
            // AltGr/Ctrl layers and unmapped characters fall through to Unicode injection
            if (scan != -1 && vk != 0xFF && (state & 0b110) == 0)
            {
                bool shift = (state & 1) != 0;
                if (char.IsLetter(glyph[0]) && (GetKeyState(0x14) & 1) != 0) shift = !shift; // Caps Lock inverts letters
                return TapKey(r, vk, shift, holdMs, layout);
            }
        }
        foreach (var unit in glyph)
        {
            bool pressed = false;
            try
            {
                if (!Send(Unicode(unit, up: false))) return "SendInput rejected a character";
                pressed = true;
                r.Wait(holdMs);
            }
            finally { if (pressed) Send(Unicode(unit, up: true)); }
        }
        return null;
    }

    private static string? TapKey(Run r, int vk, bool shift, int holdMs, IntPtr layout)
    {
        bool shiftDown = false, keyDown = false;
        try
        {
            if (shift)
            {
                if (!Send(ScanKey(0x10, false, layout))) return "SendInput rejected the Shift press";
                shiftDown = true;
                r.Wait(r.P(10, 35));
            }
            if (!Send(ScanKey(vk, false, layout))) return "SendInput rejected the key press";
            keyDown = true;
            r.Wait(holdMs);
            keyDown = false;
            Send(ScanKey(vk, true, layout));
            return null;
        }
        finally
        {
            if (keyDown) Send(ScanKey(vk, true, layout));
            if (shiftDown) Send(ScanKey(0x10, true, layout));
        }
    }

    // Buttons and the wheel carry no coordinates, like a real device: they act where the cursor already is.
    private static INPUT Pure(uint flags, uint data = 0) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { mouseData = data, dwFlags = flags } },
    };

    private static INPUT Relative(int dx, int dy) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
    };

    // Scan code for the physical key in the target window's keyboard layout (VK_A is a different key on AZERTY).
    private static INPUT ScanKey(int vk, bool up, IntPtr layout)
    {
        var input = Key(vk, up);
        if (layout != IntPtr.Zero) input.u.ki.wScan = (ushort)MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);
        return input;
    }

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr hkl);

    [LibraryImport("user32.dll")]
    private static partial short VkKeyScanExW(ushort ch, IntPtr hkl);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint idThread);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int nVirtKey);

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint uPeriod);
}
