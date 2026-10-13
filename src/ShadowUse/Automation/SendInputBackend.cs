// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Runtime.InteropServices;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// Baseline input backend using Win32 SendInput. Every event carries the
/// LLMHF_INJECTED / LLKHF_INJECTED flag visible to low-level hooks,
/// hDevice = 0 in RAWINPUTHEADER, and is subject to UIPI integrity-level
/// filtering. These markers are detectable by any anti-cheat that installs
/// a hook or reads Raw Input at ring-0.
///
/// This is the only shipped injection backend on main; alternative injection remains experimental.
/// </summary>
public sealed class SendInputBackend : IInputBackend
{
    public string Name => "SendInput";
    public bool IsAvailable => true;

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

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

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint SendInput(uint cInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr hkl);

    public InputResult MoveTo(int x, int y)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
                    dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                },
            },
        };
        return SendOne(input);
    }

    public InputResult MoveRelative(int dx, int dy)
        => SendOne(new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } },
        });

    public InputResult SetButton(MouseButton button, bool down)
    {
        uint flags = button switch
        {
            MouseButton.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButton.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        };
        return SendOne(new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } },
        });
    }

    public InputResult Wheel(int delta, bool horizontal)
        => SendOne(new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT { mouseData = (uint)delta, dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL } },
        });

    public InputResult SetKey(int vk, bool up, IntPtr layout)
    {
        uint flags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0);
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C)
            flags |= KEYEVENTF_EXTENDEDKEY;

        ushort scan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        if (layout != IntPtr.Zero)
            scan = (ushort)MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);

        return SendOne(new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } },
        });
    }

    public InputResult TypeChar(char ch)
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } },
        };
        var up = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
        };
        down.u.ki.dwExtraInfo = up.u.ki.dwExtraInfo = (IntPtr)Environment.ProcessId;
        Span<INPUT> inputs = [down, up];
        bool ok = SendInput(2, ref inputs[0], Marshal.SizeOf<INPUT>()) == 2;
        return ok
            ? new InputResult(true, Name, $"char U+{(uint)ch:X4}")
            : new InputResult(false, Name, "SendInput rejected Unicode character");
    }

    private static InputResult SendOne(INPUT input)
    {
        if (input.type == INPUT_MOUSE) input.u.mi.dwExtraInfo = (IntPtr)Environment.ProcessId;
        else input.u.ki.dwExtraInfo = (IntPtr)Environment.ProcessId;
        Span<INPUT> inputs = [input];
        return SendInput(1, ref inputs[0], Marshal.SizeOf<INPUT>()) == 1
            ? new InputResult(true, "SendInput", "")
            : new InputResult(false, "SendInput", "SendInput returned 0");
    }
}