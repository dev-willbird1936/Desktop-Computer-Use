// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>Regression coverage for a real, low-severity finding from a bug-hunt pass (documented
/// in docs/benchmarks.md, class E): BackgroundInput._addressBarInputs kept an entry for a window
/// forever if the address bar was selected (Ctrl+L) but Return was never sent, including after
/// the window closed -- a small unbounded-in-theory leak keyed by window handle. Fixed with
/// opportunistic pruning of dead-window entries on the two entry points that touch the dictionary.</summary>
public sealed class BackgroundInputAddressBarLeakTests
{
    private static FieldInfo AddressBarInputsField() =>
        typeof(BackgroundInput).GetField("_addressBarInputs", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("BackgroundInput._addressBarInputs not found");

    [Fact]
    public async Task PressKeyAsync_PrunesDeadWindowEntries_FromAnUnrelatedLiveCall()
    {
        using var dead = new OwnedNativeWindow();
        var deadHandle = dead.Handle;
        dead.Dispose(); // destroys the window; deadHandle now refers to nothing
        Assert.False(IsWindow(deadHandle));

        using var live = new OwnedNativeWindow();
        live.ShowWithoutActivation();
        using var uiaThread = new UiaThread();
        var input = new BackgroundInput(uiaThread);

        var field = AddressBarInputsField();
        var dictionary = (IDictionary)field.GetValue(input)!;
        var addressBarInputType = typeof(BackgroundInput).GetNestedType("AddressBarInput", BindingFlags.NonPublic)!;
        var ctor = addressBarInputType.GetConstructor([typeof(string)])!;
        dictionary[deadHandle] = ctor.Invoke([null]);
        Assert.True(dictionary.Contains(deadHandle), "test setup: the dead-window entry must exist before the call");

        // Any real call through either entry point should opportunistically prune it -- Tab has
        // no address-bar meaning, so this exercises the normal postmessage_key fallback path.
        await input.PressKeyAsync(live.Handle, "Tab", default);

        Assert.False(dictionary.Contains(deadHandle),
            "a dead window's address-bar entry must be pruned on the next call through either entry point, not linger forever");
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    private sealed class OwnedNativeWindow : IDisposable
    {
        private const uint WM_APP_DESTROY = 0x8001;
        private const uint WM_APP_SHOW = 0x8002;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private readonly object _gate = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private readonly string _className = $"ShadowUse.BackgroundInputAddressBarLeakTests.{Guid.NewGuid():N}";
        private IntPtr _handle;
        private uint _threadId;
        private ushort _classAtom;
        private IntPtr _module;
        private Exception? _error;

        public OwnedNativeWindow()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ShadowUse.BackgroundInputAddressBarLeakTestWindow",
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The owned native test window did not start.");
            if (_error != null)
                throw new InvalidOperationException("The owned native test window failed to start.", _error);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("The owned native test window has no HWND.");
        }

        public IntPtr Handle
        {
            get
            {
                lock (_gate)
                    return _handle;
            }
        }

        public void ShowWithoutActivation()
        {
            uint threadId;
            lock (_gate)
                threadId = _threadId;

            if (threadId == 0 || !_thread.IsAlive)
                throw new InvalidOperationException("The owned native test window is not running.");
            if (!PostThreadMessageW(threadId, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not show the owned native test window.");
        }

        public void Destroy()
        {
            uint threadId;
            lock (_gate)
                threadId = _threadId;

            if (threadId == 0 || !_thread.IsAlive)
                return;
            if (!PostThreadMessageW(threadId, WM_APP_DESTROY, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the owned native test window.");
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The owned native test window did not stop.");
        }

        private void Run()
        {
            try
            {
                lock (_gate)
                {
                    _threadId = GetCurrentThreadId();
                    _module = GetModuleHandleW(null);
                }

                var windowClass = new WNDCLASS
                {
                    LpfnWndProc = WindowProc,
                    HInstance = _module,
                    LpszClassName = _className,
                };
                _classAtom = RegisterClassW(ref windowClass);
                if (_classAtom == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassW failed.");

                var handle = CreateWindowExW(
                    WS_EX_TOOLWINDOW,
                    _className,
                    "ShadowUse owned address-bar leak test window",
                    0,
                    0,
                    0,
                    64,
                    64,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _module,
                    IntPtr.Zero);
                if (handle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");

                lock (_gate)
                    _handle = handle;
                _ready.Set();

                while (true)
                {
                    int result = GetMessageW(out var message, IntPtr.Zero, 0, 0);
                    if (result == -1)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessageW failed.");
                    if (result == 0)
                        break;

                    if (message.Message == WM_APP_DESTROY)
                    {
                        IntPtr current;
                        lock (_gate)
                            current = _handle;
                        if (current != IntPtr.Zero)
                        {
                            DestroyWindow(current);
                            lock (_gate)
                                _handle = IntPtr.Zero;
                        }
                        PostQuitMessage(0);
                        continue;
                    }

                    if (message.Message == WM_APP_SHOW)
                    {
                        IntPtr current;
                        lock (_gate)
                            current = _handle;
                        if (current != IntPtr.Zero)
                            ShowWindow(current, SW_SHOWNOACTIVATE);
                        continue;
                    }

                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
            catch (Exception ex)
            {
                _error = ex;
                _ready.Set();
            }
            finally
            {
                IntPtr current;
                lock (_gate)
                    current = _handle;
                if (current != IntPtr.Zero)
                    DestroyWindow(current);
                lock (_gate)
                    _handle = IntPtr.Zero;
                if (_classAtom != 0)
                    UnregisterClassW(_className, _module);
                _ready.Set();
            }
        }

        private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
            => DefWindowProcW(hwnd, message, wParam, lParam);

        public void Dispose()
        {
            Destroy();
            _ready.Dispose();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint Style;
            public WndProc? LpfnWndProc;
            public int ClsExtra;
            public int WndExtra;
            public IntPtr HInstance;
            public IntPtr HIcon;
            public IntPtr HCursor;
            public IntPtr HbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? LpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string LpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public POINT Point;
            public uint Private;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint minFilter, uint maxFilter);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    }
}
