using System.Diagnostics;
using System.Runtime.InteropServices;
using ShadowUse.Native;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>Explicit foreground input through the normal Windows SendInput API.</summary>
public static partial class InteractiveInput
{
    public static IInputBackend DefaultBackend { get; set; } = new SendInputBackend();
    public static Func<IntPtr> ForegroundWindowProvider { get; set; } = NativeMethods.GetForegroundWindow;
    public static bool GameProfile { get; set; }
    public const string InterruptionForegroundLost = "foreground_lost";
    public const string InterruptionCancelled = "cancelled";
    public const string InterruptionInputFailed = "input_failed";
    private sealed class InputDispatchState { public int SentCount; public bool ReleaseFailed; }
    private static readonly AsyncLocal<InputDispatchState?> DispatchState = new();

    public static (int X, int Y)? CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : null;
    public static IntPtr RootWindow(IntPtr hwnd) => GetAncestor(hwnd, GA_ROOT);

    public static Task<InputResult> ClickAsync(IntPtr hwnd, int x, int y, MouseButton button, int count, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_click", ct, r =>
        {
            MoveTo(x, y);
            var (down, up) = ButtonFlags(button);
            for (int i = 0; i < count; i++)
            {
                if (!r.HasForeground()) return ForegroundLost;
                bool pressed = false;
                try
                {
                    if (!Send(Pure(down))) return "SendInput rejected the button press";
                    pressed = true;
                    if (!r.WaitWhileForeground(InputPace.Ms(35))) return ForegroundLost;
                }
                finally { if (pressed) Send(Pure(up)); }
                if (i + 1 < count) r.Wait(InputPace.Ms(60));
            }
            return null;
        }, $"{button} x{count} at ({x},{y})");

    public static Task<InputResult> DragAsync(IntPtr hwnd, int fromX, int fromY, int toX, int toY, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_drag", ct, r =>
        {
            MoveTo(fromX, fromY);
            bool pressed = false;
            try
            {
                if (!Send(Pure(MOUSEEVENTF_LEFTDOWN))) return "SendInput rejected the button press";
                pressed = true;
                for (int i = 1; i <= 20; i++)
                {
                    if (!r.HasForeground()) return ForegroundLost;
                    MoveTo(fromX + (toX - fromX) * i / 20, fromY + (toY - fromY) * i / 20);
                    r.Wait(InputPace.Ms(16));
                }
                return null;
            }
            finally { if (pressed) Send(Pure(MOUSEEVENTF_LEFTUP)); }
        }, $"({fromX},{fromY}) -> ({toX},{toY})");

    public static Task<InputResult> ScrollAsync(IntPtr hwnd, int x, int y, string direction, double pages, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_wheel", ct, r =>
        {
            MoveTo(x, y);
            uint flag = direction is "left" or "right" ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
            int delta = (direction is "up" or "right" ? 1 : -1) * NativeMethods.WHEEL_DELTA;
            for (int i = 0; i < Math.Ceiling(pages * 3); i++)
            {
                if (!r.HasForeground()) return ForegroundLost;
                if (!Send(Pure(flag, (uint)delta))) return "SendInput rejected the wheel event";
                r.Wait(InputPace.Ms(30));
            }
            return null;
        }, $"{direction} {pages} pages");

    public static Task<InputResult> PressKeyAsync(IntPtr hwnd, string key, CancellationToken ct)
        => GameKeyAsync(hwnd, ParseKeySet(key), "tap", 40, 100, false, ct);

    public static Task<InputResult> TypeTextAsync(IntPtr hwnd, string text, CancellationToken ct)
        => RunHumanAsync(hwnd, "sendinput_unicode", ct, r =>
        {
            foreach (char ch in text)
            {
                if (!r.HasForeground()) return ForegroundLost;
                bool pressed = false;
                try
                {
                    if (!Send(Unicode(ch, false))) return "SendInput rejected a character";
                    pressed = true;
                    r.Wait(InputPace.Ms(10));
                }
                finally { if (pressed) Send(Unicode(ch, true)); }
            }
            return null;
        }, $"{text.Length} chars");

    private static async Task<InputResult> RunAsync(IntPtr hwnd, string method, CancellationToken ct, Func<string?> input, string detail,
        bool activateTarget = true, bool restoreCursor = true)
    {
        var started = Stopwatch.GetTimestamp();
        var root = RootWindow(hwnd);
        if (root == IntPtr.Zero || !NativeMethods.IsWindow(root))
            return new(false, method, "target window no longer exists");
        GetCursorPos(out var cursor);
        var previous = DispatchState.Value;
        var state = new InputDispatchState();
        DispatchState.Value = state;
        try
        {
            bool alreadyForeground = ForegroundWindowProvider() == root;
            if (!alreadyForeground && !activateTarget)
                return new(false, method, "target is not foreground; activation was not requested");
            if (activateTarget && !await ActivateAsync(root, ct).ConfigureAwait(false))
                return new(false, method, "Windows refused foreground activation; check desktop and elevation");
            if (!alreadyForeground) await Task.Delay(InputPace.Ms(GameProfile ? 40 : 80), ct).ConfigureAwait(false);
            string? error;
            try { error = await Task.Run(input, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && state.SentCount > 0)
            {
                return new(false, method, state.ReleaseFailed
                    ? "cancelled after input was sent; Windows rejected some key/button releases"
                    : "cancelled after input was sent; pressed keys/buttons were released", true,
                    (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, InterruptionReason: InterruptionCancelled);
            }
            if (ForegroundWindowProvider() == root)
                await Task.Delay(InputPace.Ms(GameProfile ? 20 : 120), CancellationToken.None).ConfigureAwait(false);
            bool sent = state.SentCount > 0;
            if (state.ReleaseFailed) error = "Windows rejected key/button cleanup; release the input physically and check desktop access";
            if (error == null && !sent) error = "Windows accepted no input; check elevation, desktop access and target compatibility";
            return new(error == null, method, error ?? detail, sent,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                InterruptionReason: error == ForegroundLost ? InterruptionForegroundLost : error != null && sent ? InterruptionInputFailed : null);
        }
        finally
        {
            DispatchState.Value = previous;
            if (restoreCursor && !GameProfile && ForegroundWindowProvider() == root) SetCursorPos(cursor.X, cursor.Y);
        }
    }

    public static Task<bool> EnsureForegroundAsync(IntPtr hwnd, CancellationToken ct)
    {
        var root = RootWindow(hwnd);
        return root == IntPtr.Zero || !NativeMethods.IsWindow(root) ? Task.FromResult(false) : ActivateAsync(root, ct);
    }

    private static async Task<bool> ActivateAsync(IntPtr root, CancellationToken ct)
    {
        if (ForegroundWindowProvider() == root) return true;
        if (NativeMethods.IsIconic(root)) NativeMethods.ShowWindow(root, SW_RESTORE);
        uint current = NativeMethods.GetCurrentThreadId();
        var foreground = ForegroundWindowProvider();
        uint foregroundThread = foreground != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(foreground, out _) : 0;
        bool attached = foregroundThread != 0 && foregroundThread != current && NativeMethods.AttachThreadInput(current, foregroundThread, true);
        try { BringWindowToTop(root); NativeMethods.SetForegroundWindow(root); }
        finally { if (attached) NativeMethods.AttachThreadInput(current, foregroundThread, false); }
        for (int i = 0; i < 10 && ForegroundWindowProvider() != root; i++) await Task.Delay(20, ct).ConfigureAwait(false);
        return ForegroundWindowProvider() == root;
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)
    };

    private static void MoveTo(int x, int y)
    {
        if (!DefaultBackend.MoveTo(x, y).Success) throw new InvalidOperationException("Windows rejected cursor movement");
        if (DispatchState.Value is { } state) Interlocked.Increment(ref state.SentCount);
    }

    private static bool Send(INPUT input)
    {
        bool success;
        if (DefaultBackend is SendInputBackend)
        {
            if (input.type == INPUT_MOUSE) input.u.mi.dwExtraInfo = (IntPtr)Environment.ProcessId;
            else input.u.ki.dwExtraInfo = (IntPtr)Environment.ProcessId;
            success = SendInput(1, ref input, Marshal.SizeOf<INPUT>()) == 1;
        }
        else if (input.type == INPUT_MOUSE)
        {
            var m = input.u.mi;
            uint f = m.dwFlags;
            success = (f & MOUSEEVENTF_MOVE) != 0 ? DefaultBackend.MoveRelative(m.dx, m.dy).Success
                : (f & (MOUSEEVENTF_WHEEL | MOUSEEVENTF_HWHEEL)) != 0 ? DefaultBackend.Wheel((int)m.mouseData, (f & MOUSEEVENTF_HWHEEL) != 0).Success
                : DefaultBackend.SetButton((f & (MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP)) != 0 ? MouseButton.Right
                    : (f & (MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP)) != 0 ? MouseButton.Middle : MouseButton.Left,
                    (f & (MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_MIDDLEDOWN)) != 0).Success;
        }
        else
        {
            var k = input.u.ki;
            success = (k.dwFlags & KEYEVENTF_UNICODE) != 0
                ? (k.dwFlags & KEYEVENTF_KEYUP) != 0 || DefaultBackend.TypeChar((char)k.wScan).Success
                : DefaultBackend.SetKey((int)MapVirtualKeyW(k.wScan, MAPVK_VSC_TO_VK), (k.dwFlags & KEYEVENTF_KEYUP) != 0, IntPtr.Zero).Success;
        }
        if (DispatchState.Value is { } state)
        {
            if (success) Interlocked.Increment(ref state.SentCount);
            else if (input.type == INPUT_KEYBOARD && (input.u.ki.dwFlags & KEYEVENTF_KEYUP) != 0
                || input.type == INPUT_MOUSE && (input.u.mi.dwFlags & (MOUSEEVENTF_LEFTUP | MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_MIDDLEUP)) != 0)
                state.ReleaseFailed = true;
        }
        return success;
    }

    private static INPUT Key(int vk, bool up)
    {
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C) flags |= KEYEVENTF_EXTENDEDKEY;
        return new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC), dwFlags = flags } } };
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } }
    };

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
        MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0, MAPVK_VSC_TO_VK = 1, GA_ROOT = 2;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79, SW_RESTORE = 9;
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint cInputs, ref INPUT pInputs, int cbSize);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out NativeMethods.POINT point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BringWindowToTop(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] private static partial uint MapVirtualKeyW(uint code, uint type);
}
