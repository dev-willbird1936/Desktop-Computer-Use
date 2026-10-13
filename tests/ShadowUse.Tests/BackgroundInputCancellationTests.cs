// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShadowUse.Automation;
using Xunit;

namespace ShadowUse.Tests;

/// <summary>Regression coverage for a real bug found during a bug-hunt pass: BackgroundInput's
/// per-character typing loops (Tier 2 render-widget keys, Tier 4 WM_CHAR stream) passed a
/// CancellationToken to Task.Run, which only prevents the delegate from STARTING if already
/// cancelled -- it does not stop a delegate already running. A caller cancelling/timing out a
/// long type_text call would keep silently typing the entire remaining text regardless.</summary>
public sealed class BackgroundInputCancellationTests
{
    [Fact]
    public async Task TypeTextAsync_StopsPromptlyOnCancellation_InsteadOfFinishingTheWholeText()
    {
        using var window = new OwnedNativeWindow();
        window.ShowWithoutActivation();
        using var uiaThread = new UiaThread();
        var input = new BackgroundInput(uiaThread);

        // Long enough (8ms/char in Tier 4) that finishing unabated would take ~16s; cancelling
        // after ~100ms should stop the loop almost immediately if the fix works.
        var longText = new string('x', 2000);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => input.TypeTextAsync(window.Handle, longText, cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"cancellation should stop the per-character loop promptly, not run to completion (elapsed={stopwatch.Elapsed})");
    }

    private sealed class OwnedNativeWindow : IDisposable
    {
        private const uint WM_APP_DESTROY = 0x8001;
        private const uint WM_APP_SHOW = 0x8002;
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;

        private readonly object _gate = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private readonly string _className = $"ShadowUse.BackgroundInputCancellationTests.{Guid.NewGuid():N}";
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
                Name = "ShadowUse.BackgroundInputCancellationTestWindow",
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
                    "ShadowUse owned background-input cancellation test window",
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
