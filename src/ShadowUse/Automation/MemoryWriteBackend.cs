// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;
using Native = ShadowUse.Native.NativeMethods;

namespace ShadowUse.Automation;

/// <summary>
/// Input backend that writes keyboard and mouse state directly into the target
/// process's memory, bypassing the OS input pipeline entirely. No SendInput, no
/// driver, no hardware — the game reads key/mouse state from its own memory and
/// we overwrite those values before the game reads them.
///
/// This is inherently GAME-SPECIFIC. Each engine needs its own adapter that knows:
///   - Which memory addresses hold key state (e.g. GetAsyncKeyState shadow table)
///   - Where the mouse position / delta buffer lives
///   - How the engine polls input (polled vs. event-driven, which frame phase)
///
/// The adapters are registered as <see cref="GameAdapter"/> instances, keyed by
/// process name. If no adapter matches, this backend falls back to not-available.
///
/// Detection risk: anti-cheat integrity checks (VAC, BattlEye, EAC) scan for
/// writes to their input memory regions. This backend works best on games WITHOUT
/// kernel-level anti-cheat, or where the memory region is not CRC-checked per-frame.
/// </summary>
public sealed class MemoryWriteBackend : IInputBackend, IDisposable
{
    private readonly FrozenDictionary<string, GameAdapter> _adapters;
    private readonly object _lock = new();
    private Process? _target;
    private IntPtr _hProcess;
    private GameAdapter? _activeAdapter;
    private bool _available;

    /// <summary>
    /// Create the backend with a set of known game adapters.
    /// Call <see cref="Attach(int)"/> before use, or let the first call attach automatically.
    /// </summary>
    public MemoryWriteBackend(IReadOnlyDictionary<string, GameAdapter>? adapters = null)
    {
        _adapters = (adapters ?? Defaults()).ToFrozenDictionary();
    }

    public string Name => _activeAdapter?.GameName ?? "MemWrite";
    public bool IsAvailable => _available && _activeAdapter != null;

    /// <summary>Attach to a target process by PID. Detects the adapter from the process name.</summary>
    public bool Attach(int pid)
    {
        lock (_lock)
        {
            Detach();

            try
            {
                _target = Process.GetProcessById(pid);
                _hProcess = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, (uint)pid);
                if (_hProcess == IntPtr.Zero)
                {
                    _available = false;
                    return false;
                }

                string name = _target.ProcessName;
                foreach (var (pattern, adapter) in _adapters)
                {
                    if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase))
                    {
                        _activeAdapter = adapter;
                        _available = adapter.Init(_hProcess, _target);
                        return _available;
                    }
                }
                _available = false;
                return false;
            }
            catch
            {
                _available = false;
                return false;
            }
        }
    }

    private void Detach()
    {
        _activeAdapter?.Cleanup();
        _activeAdapter = null;
        if (_hProcess != IntPtr.Zero)
        {
            CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        _target = null;
        _available = false;
    }

    // ---------- IInputBackend ----------

    public InputResult MoveTo(int x, int y)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.MoveTo(x, y);
    }

    public InputResult MoveRelative(int dx, int dy)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.MoveRelative(dx, dy);
    }

    public InputResult SetButton(MouseButton button, bool down)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.SetButton(button, down);
    }

    public InputResult Wheel(int delta, bool horizontal)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.Wheel(delta, horizontal);
    }

    public InputResult SetKey(int vk, bool up, IntPtr layout)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.SetKey(vk, up);
    }

    public InputResult TypeChar(char ch)
    {
        if (_activeAdapter == null) return new InputResult(false, Name, "no game adapter loaded");
        return _activeAdapter.TypeChar(ch);
    }

    // ---------- built-in adapters ----------

    private static Dictionary<string, GameAdapter> Defaults() => new()
    {
        // Example: Unity engine games that poll Input.GetKey / Input.GetMouseButton.
        // The internal "KeyCode" table in mono's mscorlib/NativeInputSystem is at
        // an offset from a scanned .data pointer. This is a TEMPLATE — real addresses
        // must be reverse-engineered per game version.
        //
        // ["^UnityGame$"] = new UnityInputAdapter(...),
        //
        // Example: Unreal Engine 4/5 games that read FKeyStateMap via
        // FSlateApplication::Get().GetKeyStates(). The map is a TMap with
        // pointer-keyed FKeyState entries. Pattern-scan for the polling loop.
        //
        // ["^UE4Game$"] = new UnrealInputAdapter(...),
        //
        // Example: Source/Valve engine games reading k_pubKeyState from
        // CInputSystem::PollInputState. A known signature in engine.dll.
        //
        // ["^hl2_linux$"] = new SourceInputAdapter(...),
    };

    // ---------- interop ----------

    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    public void Dispose() => Detach();
}

/// <summary>
/// One game/adapter knows its input memory layout. Define a concrete subclass
/// for each target game/engine.
/// </summary>
public abstract class GameAdapter
{
    public abstract string GameName { get; }

    /// <summary>Called once when attaching. Resolve addresses, create hooks, etc.</summary>
    public virtual bool Init(IntPtr hProcess, Process process) => true;

    /// <summary>Called on detach / disposal.</summary>
    public virtual void Cleanup() { }

    public abstract InputResult MoveTo(int x, int y);
    public abstract InputResult MoveRelative(int dx, int dy);
    public abstract InputResult SetButton(MouseButton button, bool down);
    public abstract InputResult Wheel(int delta, bool horizontal);
    public abstract InputResult SetKey(int vk, bool up);
    public abstract InputResult TypeChar(char ch);
}

/// <summary>
/// Example adapter for Unity Engine games that poll Input.GetKey and Input.mousePosition.
///
/// Unity stores key state in a table accessible through the mono module's
/// native-to-managed bridge. The exact offset changes per Unity version and
/// il2cpp vs mono scripting backend. This class documents the pattern —
/// real offsets require game-specific reverse engineering.
///
/// The adapter is registered in <see cref="MemoryWriteBackend.Defaults"/> after
/// the correct pattern offsets are identified.
/// </summary>
public sealed class UnityInputAdapter : GameAdapter
{
    public override string GameName => "Unity";

    // These addresses must be resolved at Init time via pattern scanning.
    private IntPtr _keyStateTable;   // byte[512] of current key states
    private IntPtr _mousePosX;       // float for cursor X
    private IntPtr _mousePosY;       // float for cursor Y
    private IntPtr _mouseButtonState; // int bitmask: bit0=left, bit1=right, bit2=middle
    private readonly int _keyStateTableOffset;
    private readonly int _mouseBaseOffset;
    private IntPtr _hProcess;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short VkKeyScanW(ushort ch);

    /// <param name="keyStateTableOffset">Offset from UnityPlayer.dll + known pattern
    /// to the key state byte array.</param>
    /// <param name="mouseBaseOffset">Offset from UnityPlayer.dll + known pattern to
    /// the mouse state struct.</param>
    public UnityInputAdapter(int keyStateTableOffset = 0x12345, int mouseBaseOffset = 0x67890)
    {
        _keyStateTableOffset = keyStateTableOffset;
        _mouseBaseOffset = mouseBaseOffset;
    }

    public override bool Init(IntPtr hProcess, Process process)
    {
        _hProcess = hProcess;

        // Pattern scan for UnityPlayer!GetKeyState table and mouse state.
        // This is a placeholder — real implementation must:
        //   1. Find UnityPlayer.dll base address
        //   2. Sigscan for the key state pointer / offset
        //   3. Resolve _keyStateTable and _mouseBase
        //
        // Example sig for Unity 2020+ mono: "48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75"
        //   → lea rcx, [rip+KeyStateTable]  then call GetKey; test al, al; jnz
        //
        // For now, return false — the user must supply concrete offsets.
        return _keyStateTableOffset != 0 && _mouseBaseOffset != 0;
    }

    public override InputResult MoveRelative(int dx, int dy)
        => MoveTo(_lastX + dx, _lastY + dy);

    private int _lastX, _lastY;

    public override InputResult MoveTo(int x, int y)
    {
        _lastX = x; _lastY = y;
        if (_mouseBaseOffset == IntPtr.Zero)
            return new InputResult(false, GameName, "mouse base not resolved");
        // Write two floats
        byte[] xBytes = BitConverter.GetBytes((float)x);
        byte[] yBytes = BitConverter.GetBytes((float)y);
        bool okX = WriteProcessMemory(_hProcess, _mousePosX, xBytes, 4, out _);
        bool okY = WriteProcessMemory(_hProcess, _mousePosY, yBytes, 4, out _);
        return okX && okY
            ? new InputResult(true, GameName, $"mouse pos ({x},{y})")
            : new InputResult(false, GameName, "failed to write mouse position");
    }

    public override InputResult SetButton(MouseButton button, bool down)
    {
        if (_mouseButtonState == IntPtr.Zero)
            return new InputResult(false, GameName, "mouse button state not resolved");
        int bit = button switch { MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0 };
        byte[] buf = new byte[4];
        ReadProcessMemory(_hProcess, _mouseButtonState, buf, 4, out _);
        int state = BitConverter.ToInt32(buf, 0);
        if (down) state |= (1 << bit);
        else state &= ~(1 << bit);
        byte[] write = BitConverter.GetBytes(state);
        return WriteProcessMemory(_hProcess, _mouseButtonState, write, 4, out _)
            ? new InputResult(true, GameName, $"{button} {(down ? "down" : "up")}")
            : new InputResult(false, GameName, "failed to write mouse button state");
    }

    public override InputResult Wheel(int delta, bool horizontal)
    {
        // Unity exposes wheel delta via Input.mouseScrollDelta which is read
        // from a float[2] in the mouse state struct. Write directly.
        // horizontal → [1], vertical → [0]
        int offset = horizontal ? 4 : 0;
        byte[] buf = BitConverter.GetBytes((float)delta / 120f);
        return WriteProcessMemory(_hProcess, IntPtr.Add(_mouseBaseOffset + 0x10, offset), buf, 4, out _)
            ? new InputResult(true, GameName, $"wheel {delta}")
            : new InputResult(false, GameName, "failed to write wheel delta");
    }

    public override InputResult SetKey(int vk, bool up)
    {
        if (_keyStateTable == IntPtr.Zero)
            return new InputResult(false, GameName, "key state table not resolved");
        // Unity key table: byte per VK (actually per KeyCode enum value).
        // 0 = not pressed (up), != 0 = pressed (down).
        // Write 0 for up, 1 for down.
        byte[] val = [up ? (byte)0 : (byte)1];
        IntPtr addr = IntPtr.Add(_keyStateTable, vk);
        return WriteProcessMemory(_hProcess, addr, val, 1, out _)
            ? new InputResult(true, GameName, $"{vk} {(up ? "up" : "down")}")
            : new InputResult(false, GameName, $"failed to write key {vk}");
    }

    public override InputResult TypeChar(char ch)
    {
        // Unity: send WM_CHAR equivalent through key state writes.
        // This is the most complex path — best handled by:
        //   - Setting the VK for the character (if available)
        //   - Writing the char into the input string buffer the game reads
        //   - Or falling back to SetKey sequences

        // Simple fallback: use VkKeyScan to find the VK and write keydown/keyup.
        short scan = VkKeyScanW((ushort)ch);
        if (scan == -1) return new InputResult(false, GameName, $"no VK for U+{(uint)ch:X4}");
        int vk = scan & 0xFF;
        bool shift = (scan & 0x100) != 0;

        if (shift) SetKey(0x10, false);
        var down = SetKey(vk, false);
        if (!down.Success) return down;
        var up = SetKey(vk, true);
        if (!up.Success) return up;
        if (shift) SetKey(0x10, true);

        return up;
    }
}