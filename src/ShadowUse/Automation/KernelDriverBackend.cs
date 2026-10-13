// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// Input backend that communicates with a signed kernel driver via IOCTL to inject HID
/// reports into the Windows input pipeline BELOW the flagging layer. Events delivered through
/// this path do NOT carry LLMHF_INJECTED / LLKHF_INJECTED in low-level hooks, and the
/// RAWINPUTHEADER.hDevice field is a non-zero handle from the real device stack.
///
/// Two driver models are supported:
///   1. Interception (https://github.com/oblitum/Interception) — the de facto standard
///      for dual-use input injection, controlled via IOCTL on \\.\interception.
///      Reports back as a real keyboard/mouse with proper device handles.
///   2. WDF HID minidriver — a custom signed driver that appears as a standard HID
///      device and forwards HID reports from user-mode IOCTL calls into the input subsystem.
///
/// The driver binary must be signed and installed separately; this backend opens the device
/// at startup and falls back to SendInput if the driver is not loaded.
/// </summary>
public sealed class KernelDriverBackend : IInputBackend, IDisposable
{
    private readonly KernelDriverKind _kind;
    private SafeFileHandle? _deviceHandle;
    private readonly object _lock = new();
    private bool _available;
    private int _lastCursorX;
    private int _lastCursorY;

    public string Name => _kind switch
    {
        KernelDriverKind.Interception => "Interception",
        KernelDriverKind.WdfHidMinidriver => "WDF-HID",
        _ => "KernelDriver",
    };

    public bool IsAvailable => _available;

    public KernelDriverBackend(KernelDriverKind kind = KernelDriverKind.Interception)
    {
        _kind = kind;
        try { Connect(); }
        catch { _available = false; }
    }

    private void Connect()
    {
        string path = _kind switch
        {
            KernelDriverKind.Interception => @"\\.\interception",
            KernelDriverKind.WdfHidMinidriver => @"\\.\HidInject",
            _ => throw new ArgumentOutOfRangeException(nameof(_kind)),
        };
        _deviceHandle = CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE,
            (uint)(FileShare.Read | FileShare.Write),
            IntPtr.Zero,
            FileMode.Open,
            FileAttributes.Normal,
            IntPtr.Zero);

        if (_deviceHandle.IsInvalid)
        {
            _deviceHandle = null;
            _available = false;
            return;
        }
        _available = true;
    }

    // ---------- Interception protocol ----------

    private const uint IOCTL_INTERCEPTION_SEND = 0x00220014;
    private const ushort INTERCEPTION_KEY_DOWN = 0x00;
    private const ushort INTERCEPTION_KEY_UP = 0x01;
    private const ushort INTERCEPTION_KEY_E0 = 0x02;
    private const ushort INTERCEPTION_MOUSE_LEFT_BUTTON_DOWN = 0x001;
    private const ushort INTERCEPTION_MOUSE_LEFT_BUTTON_UP = 0x002;
    private const ushort INTERCEPTION_MOUSE_RIGHT_BUTTON_DOWN = 0x004;
    private const ushort INTERCEPTION_MOUSE_RIGHT_BUTTON_UP = 0x008;
    private const ushort INTERCEPTION_MOUSE_MIDDLE_BUTTON_DOWN = 0x010;
    private const ushort INTERCEPTION_MOUSE_MIDDLE_BUTTON_UP = 0x020;
    private const ushort INTERCEPTION_MOUSE_MOVE = 0x040;
    private const ushort INTERCEPTION_MOUSE_WHEEL = 0x080;
    private const ushort INTERCEPTION_MOUSE_HWHEEL = 0x100;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InterceptionKeyStroke { public ushort Code; public ushort State; public uint Information; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct InterceptionMouseStroke { public ushort State; public short X; public short Y; public short Rolling; public uint Information; }

    private const uint MAPVK_VK_TO_VSC = 0;
    private const int GENERIC_READ = unchecked((int)0x80000000);
    private const int GENERIC_WRITE = 0x40000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        int dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        uint nInBufferSize,
        byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr hkl);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short VkKeyScanExW(ushort ch, IntPtr hkl);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    public InputResult MoveTo(int x, int y)
    {
        int dx = x - _lastCursorX;
        int dy = y - _lastCursorY;
        _lastCursorX = x;
        _lastCursorY = y;
        return SendMouse(INTERCEPTION_MOUSE_MOVE, dx, dy, 0);
    }

    public InputResult MoveRelative(int dx, int dy)
        => SendMouse(INTERCEPTION_MOUSE_MOVE, dx, dy, 0);

    public InputResult SetButton(MouseButton button, bool down)
    {
        ushort state = (button, down) switch
        {
            (MouseButton.Left, true) => INTERCEPTION_MOUSE_LEFT_BUTTON_DOWN,
            (MouseButton.Left, false) => INTERCEPTION_MOUSE_LEFT_BUTTON_UP,
            (MouseButton.Right, true) => INTERCEPTION_MOUSE_RIGHT_BUTTON_DOWN,
            (MouseButton.Right, false) => INTERCEPTION_MOUSE_RIGHT_BUTTON_UP,
            (MouseButton.Middle, true) => INTERCEPTION_MOUSE_MIDDLE_BUTTON_DOWN,
            (MouseButton.Middle, false) => INTERCEPTION_MOUSE_MIDDLE_BUTTON_UP,
            _ => 0,
        };
        return SendMouse(state, 0, 0, 0);
    }

    public InputResult Wheel(int delta, bool horizontal)
        => SendMouse(horizontal ? INTERCEPTION_MOUSE_HWHEEL : INTERCEPTION_MOUSE_WHEEL, 0, 0, (short)delta);

    public InputResult SetKey(int vk, bool up, IntPtr layout)
    {
        ushort scan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        if (layout != IntPtr.Zero)
            scan = (ushort)MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);

        ushort state = up ? INTERCEPTION_KEY_UP : INTERCEPTION_KEY_DOWN;
        if (vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C)
            state |= INTERCEPTION_KEY_E0;

        return SendKey(scan, state);
    }

    public InputResult TypeChar(char ch)
    {
        short scan = VkKeyScanExW(ch, GetKeyboardLayout(0));
        if (scan == -1)
            return new InputResult(false, Name, $"no scan code for U+{(uint)ch:X4} in current layout");

        int vk = scan & 0xFF;
        int state = (scan >> 8) & 0xFF;
        bool shift = (state & 1) != 0;

        if (shift)
        {
            var r = SendKey((ushort)MapVirtualKeyW(0x10, MAPVK_VK_TO_VSC), INTERCEPTION_KEY_DOWN);
            if (!r.Success) return r;
        }

        ushort sc = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        var down = SendKey(sc, INTERCEPTION_KEY_DOWN);
        if (!down.Success) return down;
        var up = SendKey(sc, INTERCEPTION_KEY_UP);
        if (!up.Success) return up;

        if (shift)
            SendKey((ushort)MapVirtualKeyW(0x10, MAPVK_VK_TO_VSC), INTERCEPTION_KEY_UP);

        return up;
    }

    private InputResult SendMouse(ushort state, int dx, int dy, short rolling)
    {
        lock (_lock)
        {
            if (_deviceHandle == null) return new InputResult(false, Name, "driver not connected");

            var stroke = new InterceptionMouseStroke
            {
                State = state,
                X = (short)Math.Clamp(dx, short.MinValue, short.MaxValue),
                Y = (short)Math.Clamp(dy, short.MinValue, short.MaxValue),
                Rolling = rolling,
            };
            int sz = Marshal.SizeOf<InterceptionMouseStroke>();
            byte[] buf = new byte[sz];
            Marshal.StructureToPtr(stroke, Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0), false);
            bool ok = DeviceIoControl(_deviceHandle, IOCTL_INTERCEPTION_SEND, buf, (uint)sz, null, 0, out _, IntPtr.Zero);
            return ok ? new InputResult(true, Name, "") : new InputResult(false, Name, "IOCTL send failed (mouse)");
        }
    }

    private InputResult SendKey(ushort code, ushort state)
    {
        lock (_lock)
        {
            if (_deviceHandle == null) return new InputResult(false, Name, "driver not connected");

            var stroke = new InterceptionKeyStroke { Code = code, State = state };
            int sz = Marshal.SizeOf<InterceptionKeyStroke>();
            byte[] buf = new byte[sz];
            Marshal.StructureToPtr(stroke, Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0), false);
            bool ok = DeviceIoControl(_deviceHandle, IOCTL_INTERCEPTION_SEND, buf, (uint)sz, null, 0, out _, IntPtr.Zero);
            return ok ? new InputResult(true, Name, "") : new InputResult(false, Name, "IOCTL send failed (keyboard)");
        }
    }

    public void Dispose()
    {
        _deviceHandle?.Dispose();
        _deviceHandle = null;
        _available = false;
    }
}

public enum KernelDriverKind
{
    Interception,
    WdfHidMinidriver,
}