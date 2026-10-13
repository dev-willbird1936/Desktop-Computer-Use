// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.IO.Ports;
using System.Runtime.InteropServices;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// Input backend that sends structured commands over a serial connection to a
/// microcontroller (Arduino Leonardo/Micro, Pi Pico PIO-USB, Teensy 3/4) that
/// enumerates as a standard USB HID keyboard and mouse. Because the OS recognises
/// the microcontroller as real hardware, ALL input events are delivered without
/// LLMHF_INJECTED / LLKHF_INJECTED flags, hDevice in RAWINPUTHEADER is a valid
/// non-zero handle, and UIPI integrity-level restrictions do not apply.
///
/// Protocol (bidirectional, ASCII, terminated by ';'):
///   M x y;        — Mouse move to absolute (x,y) on [0,65535]²
///   R dx dy;      — Mouse relative move (dx,dy)
///   B button s;   — Button: button=L|R|M, s=0(up)|1(down)
///   W d;          — Wheel: d = signed delta (multiples of 120)
///   K scan s;     — Key: scan=HID usage code, s=0(up)|1(down)
///   T char;       — Type: char=Unicode scalar
///   R baud;       — Handshake: "R 115200;" → "OK;"
///
/// Firmware source for common boards is documented in docs/stealth-hardware/.
/// </summary>
public sealed class HardwareDeviceBackend : IInputBackend, IDisposable
{
    private SerialPort? _port;
    private readonly string _comPort;
    private readonly int _baudRate;
    private readonly object _lock = new();
    private bool _available;

    public string Name => $"HID-{_comPort}";
    public bool IsAvailable => _available;

    /// <param name="comPort">Serial port name, e.g. "COM3" or "/dev/ttyACM0".</param>
    /// <param name="baudRate">Baud rate matching the microcontroller firmware (default 115200).</param>
    public HardwareDeviceBackend(string comPort, int baudRate = 115200)
    {
        _comPort = comPort;
        _baudRate = baudRate;
        try { Connect(); }
        catch { _available = false; }
    }

    private void Connect()
    {
        _port = new SerialPort(_comPort, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 100,
            NewLine = ";",
        };
        _port.Open();
        _port.DiscardInBuffer();

        // Handshake: send a ping, expect OK back
        _port.WriteLine("R 115200;");
        string? ack = _port.ReadLine();
        _available = ack != null && ack.TrimEnd(';').Trim() == "OK";
    }

    public InputResult MoveTo(int x, int y)
    {
        int hidX = Math.Clamp(x * 65535 / Math.Max(1, GetSystemMetrics(78)), 0, 65535);
        int hidY = Math.Clamp(y * 65535 / Math.Max(1, GetSystemMetrics(79)), 0, 65535);
        return Send($"M {hidX} {hidY};");
    }

    public InputResult MoveRelative(int dx, int dy)
        => Send($"R {Math.Clamp(dx, short.MinValue, short.MaxValue)} {Math.Clamp(dy, short.MinValue, short.MaxValue)};");

    public InputResult SetButton(MouseButton button, bool down)
    {
        char b = button switch
        {
            MouseButton.Left => 'L',
            MouseButton.Right => 'R',
            MouseButton.Middle => 'M',
            _ => 'L',
        };
        return Send($"B {b} {(down ? '1' : '0')};");
    }

    public InputResult Wheel(int delta, bool horizontal)
        => horizontal ? Send($"W H{delta};") : Send($"W {delta};");

    public InputResult SetKey(int vk, bool up, IntPtr layout)
    {
        ushort scan = (ushort)MapVirtualKeyW((uint)vk, MAPVK_VK_TO_VSC);
        if (layout != IntPtr.Zero)
            scan = (ushort)MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC, layout);

        ushort hidCode = VkToHidUsage(vk, scan);
        string suffix = VkHasE0Prefix(vk) ? " E0" : "";
        return Send($"K {hidCode} {(up ? '0' : '1')}{suffix};");
    }

    public InputResult TypeChar(char ch)
        => Send($"T {(uint)ch};");

    private InputResult Send(string command)
    {
        lock (_lock)
        {
            if (_port == null || !_port.IsOpen)
                return new InputResult(false, Name, "serial port not open");

            try
            {
                _port.WriteLine(command);
                string? response = _port.ReadLine();
                if (response == null)
                    return new InputResult(false, Name, "no response from device");
                string trimmed = response.TrimEnd(';').Trim();
                return trimmed == "OK"
                    ? new InputResult(true, Name, command.TrimEnd(';'))
                    : new InputResult(false, Name, $"device NAK: {trimmed}");
            }
            catch (TimeoutException)
            {
                return new InputResult(false, Name, "device timed out");
            }
            catch (Exception ex)
            {
                return new InputResult(false, Name, ex.Message);
            }
        }
    }

    // ---------- HID usage translation ----------

    private static ushort VkToHidUsage(int vk, ushort scanCode)
    {
        return vk switch
        {
            >= 0x30 and <= 0x39 => (ushort)(0x1E + (vk - 0x30)),
            0x0D => 0x28,
            0x1B => 0x29,
            0x08 => 0x2A,
            0x09 => 0x2B,
            0x20 => 0x2C,
            0x2D => 0x2D,
            0xBB => 0x2E,
            0xDB => 0x2F,
            0xDD => 0x30,
            0x5B or 0x5C => 0xE3,
            >= 0x41 and <= 0x5A => (ushort)(0x04 + (vk - 0x41)),
            _ => vk switch
            {
                0x25 => 0x50, 0x27 => 0x4F, 0x26 => 0x52, 0x28 => 0x51,
                0x21 => 0x4B, 0x22 => 0x4E, 0x24 => 0x4A, 0x23 => 0x4D,
                0x2E => 0x4C,
                0x10 => 0xE1, 0x11 => 0xE0, 0x12 => 0xE2, 0x14 => 0x53,
                >= 0x70 and <= 0x7A => (ushort)(0x3A + (vk - 0x70)),
                0x7B => 0x45, 0x7C => 0x46, 0x7D => 0x47, 0x7E => 0x48,
                0x60 => 0x62,
                >= 0x61 and <= 0x69 => (ushort)(0x59 + (vk - 0x61)),
                0x6A => 0x55, 0x6B => 0x57, 0x6D => 0x56, 0x6E => 0x58, 0x6F => 0x54,
                _ => scanCode,
            },
        };
    }

    private static bool VkHasE0Prefix(int vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2D or 0x2E or 0x5B or 0x5C;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MapVirtualKeyExW(uint uCode, uint uMapType, IntPtr hkl);

    private const uint MAPVK_VK_TO_VSC = 0;

    public void Dispose()
    {
        _port?.Dispose();
        _port = null;
        _available = false;
    }
}