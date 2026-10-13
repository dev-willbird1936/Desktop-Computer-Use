// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Automation;

/// <summary>
/// Foreground input backend contract. Main uses normal Windows SendInput;
/// the interface also lets tests record accepted/rejected dispatches safely.
/// Implementations return failure rather than silently claiming accepted input.
/// </summary>
public interface IInputBackend
{
    /// <summary>Human-readable name for this backend, for diagnostics and error messages.</summary>
    string Name { get; }

    /// <summary>True when the backend is available and initialised (driver loaded, device connected).</summary>
    bool IsAvailable { get; }

    /// <summary>Move the cursor to absolute screen coordinates.</summary>
    InputResult MoveTo(int x, int y);

    /// <summary>Move the cursor by a relative delta (what a real mouse produces).</summary>
    InputResult MoveRelative(int dx, int dy);

    /// <summary>Press or release a mouse button at the current cursor position.</summary>
    InputResult SetButton(MouseButton button, bool down);

    /// <summary>Send a wheel or horizontal-wheel notch.</summary>
    InputResult Wheel(int delta, bool horizontal);

    /// <summary>Press or release a key by virtual-key code. The backend chooses scan-code vs
    /// virtual-code delivery based on its capabilities.</summary>
    InputResult SetKey(int vk, bool up, IntPtr layout);

    /// <summary>Type a single Unicode character (surrogate pair handled as two calls).</summary>
    InputResult TypeChar(char ch);
}