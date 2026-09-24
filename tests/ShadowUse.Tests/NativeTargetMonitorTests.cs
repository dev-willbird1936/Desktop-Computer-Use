using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ShadowUse.Automation;
using ShadowUse.Native;

namespace ShadowUse.Tests;

public sealed class NativeTargetMonitorTests
{
    [Fact]
    public void NativeTargetMonitor_ReceivesDestroyForOwnedNativeWindow()
    {
        using var window = new OwnedNativeWindow();
        var registry = new TargetRegistry("monitor-test");
        var target = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            window.Handle));
        using var monitor = new NativeTargetMonitor(registry);

        Assert.True(monitor.TryTrack(target, out var error), error);
        Assert.True(monitor.IsActive);

        window.Destroy();

        Assert.True(
            SpinWait.SpinUntil(
                () => !registry.IsCurrent(target),
                TimeSpan.FromSeconds(5)),
            "The asynchronous EVENT_OBJECT_DESTROY callback did not invalidate the tracked target.");
        Assert.False(registry.TryGet(target.TargetId, out _));
    }

    [Fact]
    public void NativeTargetMonitor_AmbiguousDestroyForLiveHwndQuarantinesWithoutInvalidation()
    {
        using var window = new OwnedNativeWindow();
        var registry = new TargetRegistry("monitor-ambiguous-destroy-test");
        var target = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            window.Handle));
        using var monitor = new NativeTargetMonitor(registry);

        Assert.True(monitor.TryTrack(target, out var trackingError), trackingError);

        var replacement = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            2,
            window.Handle));
        Assert.True(monitor.TryTrack(replacement, out var replacementError), replacementError);

        InvokeDestroyCallback(monitor, window.Handle, window.ThreadId);

        Assert.True(
            SpinWait.SpinUntil(() => monitor.State == NativeTargetMonitorState.Failed, TimeSpan.FromSeconds(5)),
            "The monitor did not quarantine after an ambiguous live-HWND destroy callback.");
        Assert.False(monitor.IsActive);
        Assert.False(registry.IsCurrent(target));
        Assert.True(registry.IsCurrent(replacement));
        Assert.False(monitor.TryTrack(replacement, out var refusalError));
        Assert.Contains("failed", refusalError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeTargetMonitor_RecreatedOwnedWindowGetsNewEpoch()
    {
        var registry = new TargetRegistry("monitor-recreated-test");
        using var monitor = new NativeTargetMonitor(registry);
        using var firstWindow = new OwnedNativeWindow();
        var first = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            firstWindow.Handle));
        Assert.True(monitor.TryTrack(first, out var firstError), firstError);

        firstWindow.Destroy();
        Assert.True(
            SpinWait.SpinUntil(() => !registry.IsCurrent(first), TimeSpan.FromSeconds(5)),
            "The destroyed owned target was not retired before recreation.");

        using var secondWindow = new OwnedNativeWindow();
        var second = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            secondWindow.Handle));
        Assert.True(monitor.TryTrack(second, out var secondError), secondError);
        Assert.NotEqual(first.TargetId, second.TargetId);
        Assert.True(second.LifetimeEpoch > first.LifetimeEpoch);
        Assert.False(registry.IsCurrent(first));
        Assert.True(registry.IsCurrent(second));
    }

    [Fact]
    public void NativeTargetMonitor_PrunesRetiredReferencesPerHwnd()
    {
        using var window = new OwnedNativeWindow();
        var registry = new TargetRegistry("monitor-prune-test");
        using var monitor = new NativeTargetMonitor(registry);
        NativeTargetReference latest = default;

        for (var token = 1; token <= 32; token++)
        {
            latest = registry.Register(new NativeTargetIdentity(
                Environment.ProcessId,
                token,
                window.Handle));
            Assert.True(monitor.TryTrack(latest, out var error), error);
        }

        var trackedByHwnd = typeof(NativeTargetMonitor)
            .GetField("_trackedByHwnd", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackedByHwnd);
        var tracked = Assert.IsAssignableFrom<System.Collections.IDictionary>(trackedByHwnd.GetValue(monitor));
        var perWindow = Assert.IsAssignableFrom<System.Collections.IList>(tracked[window.Handle]);
        Assert.Single(perWindow);
        Assert.True(registry.IsCurrent(latest));
    }

    [Fact]
    public void NativeTargetMonitor_FailedRegistrationPrunesStaleReferencesAcrossHwnds()
    {
        using var staleWindow = new OwnedNativeWindow();
        using var liveWindow = new OwnedNativeWindow();
        var registry = new TargetRegistry("monitor-global-prune-test");
        using var monitor = new NativeTargetMonitor(registry);
        var stale = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            staleWindow.Handle));
        var live = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            liveWindow.Handle));

        Assert.True(monitor.TryTrack(stale, out var staleError), staleError);
        Assert.True(monitor.TryTrack(live, out var liveError), liveError);
        Assert.True(registry.Invalidate(stale));

        var unavailable = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            (IntPtr)0x7FFF0001));
        Assert.False(monitor.TryTrack(unavailable, out var unavailableError));
        Assert.Contains("unavailable", unavailableError, StringComparison.OrdinalIgnoreCase);
        Assert.True(registry.IsCurrent(live));

        var trackedByHwnd = typeof(NativeTargetMonitor)
            .GetField("_trackedByHwnd", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(trackedByHwnd);
        var tracked = Assert.IsAssignableFrom<System.Collections.IDictionary>(trackedByHwnd.GetValue(monitor));
        Assert.False(tracked.Contains(staleWindow.Handle));
        Assert.True(tracked.Contains(liveWindow.Handle));
    }

    [Fact]
    public async Task UiaService_ListAppsRegistersActualTargetIdentityAndMonitor()
    {
        using var window = new OwnedNativeWindow();
        window.ShowWithoutActivation();
        using var uiaThread = new UiaThread();
        var registry = new TargetRegistry("service-wiring-test");
        using var monitor = new NativeTargetMonitor(registry);
        using var service = new UiaService(uiaThread, registry, monitor);

        AppTarget? listed = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && listed == null)
        {
            listed = (await service.ListAppsAsync()).SingleOrDefault(app => app.Hwnd == window.Handle);
            if (listed == null)
                await Task.Delay(25);
        }

        Assert.NotNull(listed);
        Assert.NotNull(listed.TargetReference);
        Assert.True(listed.TargetReference.Value.IsValid);
        Assert.True(listed.LifetimeEpoch > 0);
        Assert.True(service.TryValidateTarget(listed, out var validationError), validationError);
        Assert.True(monitor.IsActive);
        Assert.True(registry.IsCurrent(listed.TargetReference.Value));
    }

    [Fact]
    public void NativeTargetMonitor_DisposalIsIdempotentAndStopsFutureInvalidation()
    {
        using var window = new OwnedNativeWindow();
        var registry = new TargetRegistry("monitor-disposal-test");
        var target = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            window.Handle));
        var monitor = new NativeTargetMonitor(registry);

        Assert.True(monitor.TryTrack(target, out var registrationError), registrationError);
        monitor.Dispose();
        monitor.Dispose();

        Assert.False(monitor.IsActive);
        Assert.Equal(NativeTargetMonitorState.Disposed, monitor.State);
        Assert.False(monitor.TryTrack(target, out var disposedError));
        Assert.Contains("disposed", disposedError, StringComparison.OrdinalIgnoreCase);

        window.Destroy();
        Thread.Sleep(250);
        Assert.True(registry.IsCurrent(target));
    }

    [Fact]
    public void NativeTargetMonitor_RefusesInvalidAndForeignReferencesExplicitly()
    {
        var registry = new TargetRegistry("monitor-registration-test");
        using var monitor = new NativeTargetMonitor(registry);
        var foreignRegistry = new TargetRegistry("other-session");
        var foreignTarget = foreignRegistry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            (IntPtr)1234));

        Assert.False(monitor.TryTrack(default, out var invalidError));
        Assert.Contains("invalid", invalidError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NativeTargetMonitorState.NotStarted, monitor.State);

        Assert.False(monitor.TryTrack(foreignTarget, out var foreignError));
        Assert.Contains("not active", foreignError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NativeTargetMonitorState.NotStarted, monitor.State);
    }

    [Fact]
    public void NativeTargetMonitor_ReportsUnavailableWindowRegistration()
    {
        var registry = new TargetRegistry("monitor-window-registration-test");
        using var monitor = new NativeTargetMonitor(registry);
        var target = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            1,
            (IntPtr)0x7FFF0001));

        Assert.False(monitor.TryTrack(target, out var error));
        Assert.Contains("unavailable", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(monitor.IsActive);
        Assert.Null(monitor.Failure);
    }

    private static void InvokeDestroyCallback(
        NativeTargetMonitor monitor,
        IntPtr hwnd,
        uint ownerThreadId)
    {
        var callback = typeof(NativeTargetMonitor).GetMethod(
            "HandleWinEvent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(callback);
        callback.Invoke(monitor, [
            IntPtr.Zero,
            NativeMethods.EVENT_OBJECT_DESTROY,
            hwnd,
            NativeMethods.OBJID_WINDOW,
            NativeMethods.CHILDID_SELF,
            ownerThreadId,
            0u,
        ]);
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
        private readonly string _className = $"ShadowUse.NativeTargetMonitorTests.{Guid.NewGuid():N}";
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
                Name = "ShadowUse.NativeTargetMonitorTestWindow",
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

        public uint ThreadId
        {
            get
            {
                lock (_gate)
                    return _threadId;
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
                    "ShadowUse owned native monitor test window",
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
