// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Config;

namespace ShadowUse.Automation;

/// <summary>
/// Selects and creates the <see cref="IInputBackend"/> based on <see cref="InputBackendConfig"/>.
///
/// Resolution order:
///   1. Kernel driver — if configured, loaded, and connected
///   2. Hardware device — if configured, COM port open, and responsive
///   3. Memory write — if configured and target process supports it
///   4. SendInput — always available as baseline
/// </summary>
public static class InputBackendFactory
{
    /// <summary>Create the best available backend per the user's configuration.</summary>
    public static IInputBackend Create(InputBackendConfig cfg)
    {
        // Attempt the preferred backend first
        switch (cfg.Mode)
        {
            case InputBackendKind.KernelDriver:
                if (TryCreateKernelDriver(cfg, out var kd)) return kd;
                break;

            case InputBackendKind.HardwareDevice:
                if (TryCreateHardware(cfg, out var hw)) return hw;
                break;

            case InputBackendKind.MemoryWrite:
                if (TryCreateMemoryWrite(cfg, out var mw)) return mw;
                break;
        }

        // Fallback: SendInput — always works, always detectable
        return new SendInputBackend();
    }

    /// <summary>Create a fallback chain so the best AVAILABLE backend is used (kernel > hardware > sendinput).</summary>
    public static IInputBackend CreateAuto(InputBackendConfig cfg)
    {
        if (TryCreateKernelDriver(cfg, out var kd)) return kd;
        if (cfg.HardwareComPort != null && TryCreateHardware(cfg, out var hw)) return hw;
        if (cfg.AutoAttachPid > 0 && TryCreateMemoryWrite(cfg, out var mw)) return mw;
        return new SendInputBackend();
    }

    private static bool TryCreateKernelDriver(InputBackendConfig cfg, out IInputBackend backend)
    {
        var b = new KernelDriverBackend(cfg.DriverKind);
        if (b.IsAvailable) { backend = b; return true; }
        backend = null!;
        return false;
    }

    private static bool TryCreateHardware(InputBackendConfig cfg, out IInputBackend backend)
    {
        if (cfg.HardwareComPort == null) { backend = null!; return false; }
        var b = new HardwareDeviceBackend(cfg.HardwareComPort, cfg.HardwareBaudRate);
        if (b.IsAvailable) { backend = b; return true; }
        b.Dispose();
        backend = null!;
        return false;
    }

    private static bool TryCreateMemoryWrite(InputBackendConfig cfg, out IInputBackend backend)
    {
        if (cfg.AutoAttachPid <= 0) { backend = null!; return false; }
        // Use default adapters; user can extend at runtime
        var adapters = cfg.CustomAdapters ?? new Dictionary<string, GameAdapter>();
        var b = new MemoryWriteBackend(adapters);
        if (b.Attach(cfg.AutoAttachPid))
        {
            backend = b;
            return true;
        }
        backend = null!;
        return false;
    }
}

/// <summary>Which input injection strategy to use.</summary>
public enum InputBackendKind
{
    /// <summary>Win32 SendInput — baseline, always detectable (LLMHF_INJECTED flag, hDevice=0).</summary>
    SendInput,

    /// <summary>Signed kernel driver injection below the flagging layer.</summary>
    KernelDriver,

    /// <summary>Microcontroller as USB HID device (Arduino/Pi Pico/Teensy).</summary>
    HardwareDevice,

    /// <summary>Game-specific memory writes bypassing the OS input stack entirely.</summary>
    MemoryWrite,

    /// <summary>Auto-select: kernel driver → hardware → sendinput.</summary>
    Auto,
}

/// <summary>Configuration for input backend selection, loaded from settings.json.</summary>
public sealed record InputBackendConfig
{
    /// <summary>
    /// Preferred injection mode. Defaults to SendInput (detectable but zero setup).
    /// Set to KernelDriver/Auto for anti-cheat environments.
    /// </summary>
    public InputBackendKind Mode { get; init; } = InputBackendKind.SendInput;

    /// <summary>Which kernel driver model to use (Interception is the default).</summary>
    public KernelDriverKind DriverKind { get; init; } = KernelDriverKind.Interception;

    /// <summary>Serial port for hardware-device backend, e.g. "COM3" or "/dev/ttyACM0".</summary>
    public string? HardwareComPort { get; init; }

    /// <summary>Baud rate for the hardware-device serial connection.</summary>
    public int HardwareBaudRate { get; init; } = 115200;

    /// <summary>
    /// PID of a target process for memory-write backend. If 0, memory-write
    /// auto-attach is disabled (manual Attach() call required).
    /// </summary>
    public int AutoAttachPid { get; init; }

    /// <summary>Custom game adapters for memory-write backend (process-name pattern → adapter).</summary>
    public IReadOnlyDictionary<string, GameAdapter>? CustomAdapters { get; init; }
}