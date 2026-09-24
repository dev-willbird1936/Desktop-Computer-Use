using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ShadowUse.Automation;
using ShadowUse.Native;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class NativeIdentityIntegrationTests
{
    [Fact]
    public void ProcessCreationToken_ComesFromCurrentProcessIdentity()
    {
        Assert.True(
            UiaService.TryGetProcessCreationToken(Environment.ProcessId, out var token));
        Assert.True(token > 0);
    }

    [Fact]
    public void UnknownProcessCreationIdentity_FailsClosed()
    {
        var identity = new NativeTargetIdentity(Environment.ProcessId, 0, (IntPtr)1);

        Assert.False(identity.IsValid);
        var registry = new TargetRegistry("unknown-identity-test");
        Assert.False(registry.TryRegister(identity, out _));
    }

    [Fact]
    public void TryValidateTarget_RereadsIdentityAfterMonitorTracking()
    {
        using var window = new NativeTestWindow();
        Assert.True(
            UiaService.TryGetProcessCreationToken(Environment.ProcessId, out var processToken));
        var registry = new TargetRegistry("post-track-identity-test");
        var reference = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            processToken,
            window.Handle));
        using var monitor = new NativeTargetMonitor(registry);
        using var uiaThread = new UiaThread();
        int reads = 0;
        using var service = new UiaService(
            uiaThread,
            registry,
            monitor,
            (pid, hwnd) =>
            {
                var token = Interlocked.Increment(ref reads) == 1
                    ? processToken
                    : processToken + 1;
                return new NativeTargetIdentity(pid, token, hwnd);
            });
        var target = new AppTarget
        {
            Pid = Environment.ProcessId,
            ProcessName = "testhost",
            Hwnd = window.Handle,
            Title = "post-track identity",
            TargetReference = reference,
        };

        Assert.False(service.TryValidateTarget(target, out var error));
        Assert.Equal(2, reads);
        Assert.Contains("stale", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryValidateTarget_RefusesMonitorQuarantineBetweenTrackingAndDispatch()
    {
        using var window = new NativeTestWindow();
        Assert.True(
            UiaService.TryGetProcessCreationToken(Environment.ProcessId, out var processToken));
        var registry = new TargetRegistry("monitor-quarantine-dispatch-test");
        var reference = registry.Register(new NativeTargetIdentity(
            Environment.ProcessId,
            processToken,
            window.Handle));
        using var monitor = new NativeTargetMonitor(registry);
        using var uiaThread = new UiaThread();
        int reads = 0;
        using var service = new UiaService(
            uiaThread,
            registry,
            monitor,
            (pid, hwnd) =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    InvokeDestroyCallback(monitor, hwnd, window.ThreadId);
                    if (!SpinWait.SpinUntil(
                            () => monitor.State == NativeTargetMonitorState.Failed,
                            TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The monitor did not quarantine during identity validation.");
                }

                return new NativeTargetIdentity(pid, processToken, hwnd);
            });
        var target = new AppTarget
        {
            Pid = Environment.ProcessId,
            ProcessName = "testhost",
            Hwnd = window.Handle,
            Title = "monitor quarantine",
            TargetReference = reference,
        };

        Assert.False(service.TryValidateTarget(target, out var error));
        Assert.Equal(2, reads);
        Assert.Contains("failed", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(registry.IsCurrent(reference));
    }

    [Fact]
    public void McpAppSelectorMetadataAdvertisesWindowAndTargetIds()
    {
        var appTools = typeof(ComputerUseTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetParameters().Any(parameter => parameter.Name == "app"))
            .ToArray();

        Assert.NotEmpty(appTools);
        foreach (var method in appTools)
        {
            var methodDescription = GetDescription(method);
            var appDescription = GetDescription(
                method.GetParameters().Single(parameter => parameter.Name == "app"));
            Assert.Contains("window_id", methodDescription, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("target_id", methodDescription, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("window_id", appDescription, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("target_id", appDescription, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("window_id", GetDescription(typeof(ComputerUseTools).GetMethod(nameof(ComputerUseTools.ListApps))!), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target_id", GetDescription(typeof(ComputerUseTools).GetMethod(nameof(ComputerUseTools.ListApps))!), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDescription(ICustomAttributeProvider member)
    {
        var attribute = member.GetCustomAttributes(inherit: true)
            .Single(attribute => attribute.GetType().Name == "DescriptionAttribute");
        return attribute.GetType().GetProperty("Description")?.GetValue(attribute) as string
            ?? throw new InvalidOperationException($"DescriptionAttribute on {member} has no Description value.");
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

    private sealed class NativeTestWindow : IDisposable
    {
        private const uint WM_APP_DESTROY = 0x8001;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private readonly WndProc _wndProc = WindowProc;
        private readonly string _className = $"ShadowUse.NativeIdentityIntegration.{Guid.NewGuid():N}";
        private IntPtr _handle;
        private uint _threadId;
        private ushort _classAtom;
        private IntPtr _module;
        private Exception? _error;

        public NativeTestWindow()
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "ShadowUse.NativeIdentityIntegrationWindow",
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The native identity test window did not start.");
            if (_error != null)
                throw new InvalidOperationException("The native identity test window failed to start.", _error);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("The native identity test window has no HWND.");
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

        public void Dispose()
        {
            uint threadId;
            lock (_gate)
                threadId = _threadId;
            if (threadId != 0 && _thread.IsAlive)
            {
                if (!PostThreadMessageW(threadId, WM_APP_DESTROY, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the native identity test window.");
                if (!_thread.Join(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The native identity test window did not stop.");
            }
            _ready.Dispose();
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
                    LpfnWndProc = _wndProc,
                    HInstance = _module,
                    LpszClassName = _className,
                };
                _classAtom = RegisterClassW(ref windowClass);
                if (_classAtom == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassW failed.");

                var handle = CreateWindowExW(
                    WS_EX_TOOLWINDOW,
                    _className,
                    "ShadowUse native identity integration test window",
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
                    var result = GetMessageW(out var message, IntPtr.Zero, 0, 0);
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
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint minFilter, uint maxFilter);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    }
}
