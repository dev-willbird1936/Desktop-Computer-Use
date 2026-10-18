// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShadowUse.Native;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// background_mode=isolated_interactive: real hardware-level input for apps that ignore posted
/// messages (games reading Raw Input, the real cursor, or GetAsyncKeyState). Unlike
/// <see cref="BackgroundInput"/>, this DOES bring the target to the foreground, move the real
/// cursor, and inject input through the configured <see cref="IInputBackend"/>. By default the
/// backend is <see cref="SendInputBackend"/> (detectable by anti-cheat via LLMHF_INJECTED and
/// hDevice=0). Set <see cref="Backend"/> to a stealth backend (kernel driver, hardware device,
/// or memory write) to bypass those markers.
///
/// Every public method accepts an optional backend override; pass null (default) to use
/// <see cref="Backend"/>, which in turn defaults to <see cref="SendInputBackend"/>.
/// </summary>
public static partial class InteractiveInput
{
    /// <summary>The active input backend for this session. Swap this at any time.
    /// Defaults to the basic SendInput path. Set via <see cref="InputBackendFactory.Create"/>.</summary>
    public static IInputBackend DefaultBackend { get; set; } = new SendInputBackend();

    /// <summary>Initialise the default backend from configuration. Call once at startup.</summary>
    public static void ConfigureBackend(InputBackendConfig cfg)
    {
        DefaultBackend = cfg.Mode switch
        {
            InputBackendKind.Auto => InputBackendFactory.CreateAuto(cfg),
            _ => InputBackendFactory.Create(cfg),
        };
    }
    // Games sample input once per frame and many drop it on focus loss, so the target keeps
    // foreground briefly before and after the input (a few frames at 30-60 fps).
    private const int ActivationSettleMs = 80;
    private const int HoldAfterInputMs = 120;
    /// <summary>Foreground window lookup. Tests replace it so owned fixtures never take the real foreground.</summary>
    public static Func<IntPtr> ForegroundWindowProvider { get; set; } = NativeMethods.GetForegroundWindow;

    public const string InterruptionForegroundLost = "foreground_lost";
    public const string InterruptionCancelled = "cancelled";
    public const string InterruptionInputFailed = "input_failed";

    private sealed class InputDispatchState { public int SentCount; }
    private static readonly AsyncLocal<InputDispatchState?> DispatchState = new();

    public static Task<InputResult> ClickAsync(IntPtr hwnd, int x, int y, MouseButton button, int clickCount, CancellationToken ct)
        => RunAsync(hwnd, "sendinput_click", ct, () =>
        {
            var root = GetAncestor(hwnd, GA_ROOT);
            uint down = ButtonFlags(button).Down;
            int buttonVk = button switch { MouseButton.Right => VkRButton, MouseButton.Middle => VkMButton, _ => VkLButton };
            ct.ThrowIfCancellationRequested();
            MoveTo(x, y);
            Thread.Sleep(InputPace.Ms(30)); // hover: lets placement previews and hover states follow the cursor
            for (int i = 0; i < clickCount; i++)
            {
                // Task.Run cancellation does not interrupt a running SendInput delegate; recheck at the final boundary.
                ct.ThrowIfCancellationRequested();
                var foreground = ForegroundWindowProvider();
                if (foreground == IntPtr.Zero || GetAncestor(foreground, GA_ROOT) != root) return ForegroundLost;
                if (CursorPosition() is not { } settledCursor
                    || Math.Abs((long)settledCursor.X - x) > 2 || Math.Abs((long)settledCursor.Y - y) > 2)
                    return "cursor_did_not_reach_click_target";
                bool pressed = false;
                string? clickError = null, releaseError = null;
                try
                {
                    if (!Send(Pure(down))) clickError = "SendInput rejected the button press";
                    else
                    {
                        pressed = true;
                        releaseError = ReleaseInputWithRecovery(buttonVk, hwnd, IntPtr.Zero);
                        pressed = false;
                    }
                }
                finally
                {
                    if (pressed) releaseError ??= ReleaseInputWithRecovery(buttonVk, hwnd, IntPtr.Zero);
                }
                if (releaseError is not null) return releaseError;
                if (clickError is not null) return clickError;
                if (i + 1 < clickCount) Thread.Sleep(InputPace.Ms(60));
            }
            return null;
        }, $"{button} x{clickCount} at ({x},{y})");

    public static Task<InputResult> DragAsync(IntPtr hwnd, int fromX, int fromY, int toX, int toY, CancellationToken ct)
        => RunAsync(hwnd, "sendinput_drag", ct, () =>
        {
            MoveTo(fromX, fromY);
            Thread.Sleep(InputPace.Ms(30));
            if (!Send(Mouse(fromX, fromY, MOUSEEVENTF_LEFTDOWN))) return "SendInput rejected the button press";
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                MoveTo(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
                Thread.Sleep(InputPace.Ms(16));
            }
            return Send(Mouse(toX, toY, MOUSEEVENTF_LEFTUP)) ? null : "SendInput rejected the button release";
        }, $"({fromX},{fromY})→({toX},{toY})");

    public static Task<InputResult> ScrollAsync(IntPtr hwnd, int x, int y, string direction, double pages, CancellationToken ct)
        => RunAsync(hwnd, "sendinput_wheel", ct, () =>
        {
            MoveTo(x, y);
            int notches = Math.Max(1, (int)Math.Ceiling(pages * 3));
            uint flag = direction is "left" or "right" ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
            int delta = (direction is "up" or "right" ? 1 : -1) * NativeMethods.WHEEL_DELTA;
            for (int i = 0; i < notches; i++)
            {
                if (!Send(Mouse(x, y, flag, (uint)delta))) return "SendInput rejected the wheel event";
                Thread.Sleep(InputPace.Ms(30));
            }
            return null;
        }, $"{direction} {pages} pages");

    /// <summary>Key press as scan codes, which DirectInput and Raw Input games read.</summary>
    public static Task<InputResult> PressKeyAsync(IntPtr hwnd, string key, CancellationToken ct)
    {
        var (vk, _, mods) = BackgroundInput.ParseKey(key);
        return RunAsync(hwnd, "sendinput_key", ct, () =>
        {
            bool ok = true;
            foreach (var m in mods) ok &= Send(Key(m, up: false));
            ok &= Send(Key(vk, up: false));
            Thread.Sleep(InputPace.Ms(40)); // held long enough for per-frame key polling to see it
            ok &= Send(Key(vk, up: true));
            foreach (var m in mods.Reverse()) ok &= Send(Key(m, up: true));
            return ok ? null : "SendInput rejected one or more key events";
        }, key);
    }

    public static Task<InputResult> TypeTextAsync(IntPtr hwnd, string text, CancellationToken ct)
        => RunAsync(hwnd, "sendinput_unicode", ct, () =>
        {
            foreach (var ch in text)
            {
                if (!Send(Unicode(ch, up: false)) || !Send(Unicode(ch, up: true))) return "SendInput rejected a character";
                Thread.Sleep(InputPace.Ms(10));
            }
            return null;
        }, $"{text.Length} chars");

    /// <summary>Game mode: the game owns the foreground and the cursor. Input skips the pre-input settle when
    /// the window already has the foreground, holds the window for less time afterwards, and never puts the
    /// cursor back (a mouse-look game would turn that jump into camera motion).</summary>
    public static bool GameProfile { get; set; }

    private static (int X, int Y)? ReadNativeCursorPosition()
        => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    /// <summary>Current screen cursor position. Tests can replace the native reader to model clipping.</summary>
    public static Func<(int X, int Y)?> CursorPositionProvider { get; set; } = ReadNativeCursorPosition;

    public static (int X, int Y)? CursorPosition() => CursorPositionProvider();

    /// <summary>Cross-process one-controller-per-window lease. Claimed before activation or real input while <see cref="GameProfile"/> is on.</summary>
    public static GameInputOwnership Ownership { get; set; } = new(GameInputOwnership.DefaultLeaseDirectory(), heldInputCount: HeldInputCountForRoot);

    private static int HeldInputCountForRoot(GameInputWindowIdentity window)
    {
        lock (HeldLock) return Held.Values.Count(h => h.Root == window.RootHwnd);
    }

    public static IntPtr RootWindow(IntPtr hwnd) => GetAncestor(hwnd, GA_ROOT);

    /// <summary>Claims the window for this controller and starts one operation under the claim. Dispose the result when the operation ends.
    /// Not required (granted without a lease) when <see cref="GameProfile"/> is off.</summary>
    public static Task<GameInputClaim> BeginInputOwnershipAsync(IntPtr hwnd, CancellationToken ct)
        => GameProfile ? Ownership.TryBeginAsync(GetAncestor(hwnd, GA_ROOT), ct) : Task.FromResult(GameInputOwnership.NotRequiredClaim());

    /// <summary>Shutdown: release held keys first, then every ownership lease.</summary>
    public static int ShutdownInput()
    {
        ReleaseAllHeldKeys();
        return Ownership.ReleaseAll();
    }

    private static async Task<InputResult> RunAsync(IntPtr hwnd, string method, CancellationToken ct, Func<string?> input, string detail,
        bool activateTarget = true, bool restoreCursor = true)
    {
        var started = Stopwatch.GetTimestamp();
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero || !NativeMethods.IsWindow(root))
            return new InputResult(false, method, "target window no longer exists", ElapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        using var claim = await BeginInputOwnershipAsync(root, ct).ConfigureAwait(false);
        if (!claim.Granted)
            return new InputResult(false, method, claim.Status.Describe(), ElapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        GetCursorPos(out var cursor);
        var state = new InputDispatchState();
        var previousState = DispatchState.Value;
        DispatchState.Value = state;
        try
        {
            bool alreadyForeground = ForegroundWindowProvider() == root;
            if (!alreadyForeground && !activateTarget)
                return new InputResult(false, method, "target window is not foreground; activation was not requested", ElapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (activateTarget && !await ActivateAsync(root, ct).ConfigureAwait(false))
                return new InputResult(false, method, "Windows refused to bring the target window to the foreground", ElapsedMs: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (activateTarget && !(GameProfile && alreadyForeground))
                await Task.Delay(InputPace.Ms(GameProfile ? 40 : ActivationSettleMs), ct).ConfigureAwait(false);
            string? error;
            try { error = await Task.Run(input, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && Volatile.Read(ref state.SentCount) > 0)
            {
                // The input body's finally blocks have already released every pressed key and button.
                return new InputResult(false, method, "cancelled after input was sent; pressed keys and buttons were released", true,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, InterruptionReason: InterruptionCancelled);
            }
            await Task.Delay(InputPace.Ms(GameProfile ? 20 : HoldAfterInputMs), CancellationToken.None).ConfigureAwait(false);
            bool sent = Volatile.Read(ref state.SentCount) > 0;
            bool cursorLocked = error == "cursor_locked";
            string? interruption = error == null || cursorLocked ? null
                : error == ForegroundLost ? InterruptionForegroundLost
                : sent ? InterruptionInputFailed : null;
            return new InputResult(error == null || cursorLocked, method, cursorLocked ? "cursor_locked" : error ?? detail, sent,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, InterruptionReason: interruption);
        }
        finally
        {
            DispatchState.Value = previousState;
            if (restoreCursor && !GameProfile) SetCursorPos(cursor.X, cursor.Y);
        }
    }

    /// <summary>Brings the window's root to the foreground (restoring it when minimised). True when it holds the foreground afterwards.</summary>
    public static async Task<bool> EnsureForegroundAsync(IntPtr hwnd, CancellationToken ct)
    {
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero || !NativeMethods.IsWindow(root)) return false;
        using var claim = await BeginInputOwnershipAsync(root, ct).ConfigureAwait(false);
        return claim.Granted && await ActivateAsync(root, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ActivateAsync(IntPtr root, CancellationToken ct)
    {
        if (ForegroundWindowProvider() == root) return true;
        if (NativeMethods.IsIconic(root)) NativeMethods.ShowWindow(root, SW_RESTORE);
        // SetForegroundWindow succeeds only for a thread attached to the current foreground
        // owner's input queue (same trick FocusGuard uses to restore).
        uint cur = NativeMethods.GetCurrentThreadId();
        var fg = ForegroundWindowProvider();
        uint fgTid = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
        bool attached = fgTid != 0 && fgTid != cur && NativeMethods.AttachThreadInput(cur, fgTid, true);
        try
        {
            BringWindowToTop(root);
            NativeMethods.SetForegroundWindow(root);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(cur, fgTid, false);
        }
        for (int i = 0; i < 10 && ForegroundWindowProvider() != root; i++)
            await Task.Delay(20, ct).ConfigureAwait(false);
        return ForegroundWindowProvider() == root;
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
    };

    // An absolute SendInput move generates both a cursor move and Raw Input motion, so it reaches
    // games that poll the cursor as well as games that read WM_INPUT.
    private static void MoveTo(int x, int y)
    {
        var result = DefaultBackend.MoveTo(x, y);
        if (result.Success && DispatchState.Value is { } state) Interlocked.Increment(ref state.SentCount);
    }

    /// <summary>Send one INPUT struct through the default backend.</summary>
    private static bool Send(INPUT input)
    {
        // Fast path for the built-in SendInput backend (avoids dispatch overhead)
        bool success = DefaultBackend is SendInputBackend
            ? SendInputPInvoke(input) == 1
            : DispatchThroughBackend(input, DefaultBackend).Success;
        if (success && DispatchState.Value is { } state) Interlocked.Increment(ref state.SentCount);
        return success;
    }

    private static InputResult DispatchThroughBackend(INPUT input, IInputBackend backend)
    {
        uint type = input.type;
        if (type == INPUT_MOUSE)
        {
            var mi = input.u.mi;
            uint flags = mi.dwFlags;
            bool moved = false;
            if ((flags & MOUSEEVENTF_MOVE) != 0)
            {
                InputResult moveResult;
                if ((flags & MOUSEEVENTF_ABSOLUTE) != 0)
                {
                    int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                    int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
                    int x = (int)Math.Round(mi.dx * (width - 1) / 65535.0 + left);
                    int y = (int)Math.Round(mi.dy * (height - 1) / 65535.0 + top);
                    moveResult = backend.MoveTo(x, y);
                }
                else
                    moveResult = backend.MoveRelative(mi.dx, mi.dy);
                if (!moveResult.Success) return moveResult;
                moved = true;
            }
            // A SendInput mouse packet may combine absolute movement with a button or wheel event.
            if ((flags & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP | MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP |
                        MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP)) != 0)
            {
                MouseButton btn = (flags & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP)) != 0 ? MouseButton.Left
                    : (flags & (MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP)) != 0 ? MouseButton.Right
                    : MouseButton.Middle;
                bool down = (flags & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_MIDDLEDOWN)) != 0;
                return backend.SetButton(btn, down);
            }
            if ((flags & (MOUSEEVENTF_WHEEL | MOUSEEVENTF_HWHEEL)) != 0)
                return backend.Wheel((int)mi.mouseData, (flags & MOUSEEVENTF_HWHEEL) != 0);
            if (moved) return new InputResult(true, backend.Name, "");
        }
        else if (type == INPUT_KEYBOARD)
        {
            var ki = input.u.ki;
            if ((ki.dwFlags & KEYEVENTF_UNICODE) != 0)
            {
                return backend.TypeChar((char)ki.wScan);
            }
            else
            {
                int vk = (int)MapVirtualKeyW(ki.wScan, MAPVK_VSC_TO_VK);
                bool up = (ki.dwFlags & KEYEVENTF_KEYUP) != 0;
                return backend.SetKey(vk, up, IntPtr.Zero);
            }
        }
        // Fallback: use raw SendInput
        return SendInputPInvoke(input) == 1
            ? new InputResult(true, backend.Name, "")
            : new InputResult(false, backend.Name, "SendInput fallback failed");
    }

    private static uint SendInputPInvoke(INPUT input)
    {
        Span<INPUT> inputs = [input];
        return SendInput(1, ref inputs[0], Marshal.SizeOf<INPUT>());
    }

    private static INPUT Mouse(int x, int y, uint flags, uint data = 0)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                    dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                    mouseData = data,
                    dwFlags = flags | MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
    }

    private static INPUT Key(int vk, bool up)
    {
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C)
            flags |= KEYEVENTF_EXTENDEDKEY; // PgUp/PgDn/End/Home/arrows/Ins/Del/Win share scan codes with the numpad
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC), dwFlags = flags } },
        };
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    // Replaced by the above Send(input) that dispatches through DefaultBackend

    // ---------- interop (kept here: NativeMethods deliberately excludes SendInput) ----------

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0, MAPVK_VSC_TO_VK = 1;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint GA_ROOT = 2;
    private const int SW_RESTORE = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint cInputs, ref INPUT pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativeMethods.POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint uCode, uint uMapType);
}
