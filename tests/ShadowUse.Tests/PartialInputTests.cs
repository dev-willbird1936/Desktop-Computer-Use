using System.Runtime.InteropServices;
using ShadowUse.Automation;
using ShadowUse.Tools;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;
using MouseButton = ShadowUse.Automation.BackgroundInput.MouseButton;

namespace ShadowUse.Tests;

/// <summary>Partial-input safety for game holds. Input goes to a recording backend and the foreground comes from a fake provider,
/// so no real key, button, or focus change ever reaches the desktop or a game.</summary>
[Collection("InteractiveInputStatics")]
public sealed class PartialInputTests : IDisposable
{
    private const int KeyW = 0x57, KeyD = 0x44;
    private readonly IInputBackend _backend = InteractiveInput.DefaultBackend;
    private readonly Func<IntPtr> _foreground = InteractiveInput.ForegroundWindowProvider;
    private readonly Func<(int X, int Y)?> _cursorPositionProvider = InteractiveInput.CursorPositionProvider;
    private readonly Func<IntPtr, bool>? _cursorLockProvider = InteractiveInput.CursorLockProvider;
    private readonly bool _gameProfile = InteractiveInput.GameProfile;
    private readonly OwnedWindow _window = new();
    private readonly RecordingBackend _recorder = new();
    private volatile bool _foregroundLost;

    public PartialInputTests()
    {
        InteractiveInput.DefaultBackend = _recorder;
        InteractiveInput.GameProfile = false;
        InteractiveInput.ForegroundWindowProvider = () => _foregroundLost ? IntPtr.Zero : _window.Handle;
        InteractiveInput.CursorPositionProvider = () => _recorder.CursorPosition;
        InteractiveInput.CursorLockProvider = null;
    }

    public void Dispose()
    {
        InteractiveInput.DefaultBackend = _backend;
        InteractiveInput.GameProfile = _gameProfile;
        InteractiveInput.ForegroundWindowProvider = _foreground;
        InteractiveInput.CursorPositionProvider = _cursorPositionProvider;
        InteractiveInput.CursorLockProvider = _cursorLockProvider;
        _window.Dispose();
    }

    private Task<InputResult> Hold(int[] keys, int durationMs, bool humanize, CancellationToken ct = default)
        => InteractiveInput.GameKeyAsync(_window.Handle, keys, "hold", durationMs, 100, humanize, ct);

    [Fact]
    public async Task Click_SuccessfulPositionSendsButtonDownAndUp()
    {
        var result = await InteractiveInput.ClickAsync(_window.Handle, 100, 100, MouseButton.Left, 1, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.True(result.InputSent);
        Assert.Equal(["Left:down", "Left:up"], _recorder.ButtonLog);
    }

    [Fact]
    public async Task Click_FailedReleaseIsRetriedInFinally()
    {
        _recorder.RejectButtonUpCount = 1;

        var result = await InteractiveInput.ClickAsync(_window.Handle, 100, 100, MouseButton.Left, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionInputFailed, result.InterruptionReason);
        Assert.Equal(["Left:down", "Left:up", "Left:up"], _recorder.ButtonLog);
    }

    [Fact]
    public async Task HumanizedClick_FailedReleaseRetriesAndReturnsFailure()
    {
        InteractiveInput.CursorLockProvider = _ => false;
        _recorder.CursorPosition = (100, 100);
        _recorder.RejectButtonUpCount = 1;

        var result = await InteractiveInput.HumanClickAsync(_window.Handle, 100, 100, null,
            MouseButton.Left, 1, new HumanizedOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Contains("recovery release succeeded", result.Detail);
        Assert.Equal(["Left:down", "Left:up", "Left:up"], _recorder.ButtonLog);
    }

    [Fact]
    public async Task GameKeyTap_FailedReleaseRetriesAndReturnsFailure()
    {
        _recorder.RejectKeyUpCount = 1;

        var result = await InteractiveInput.GameKeyAsync(_window.Handle, [KeyW], "tap", 0, 100, humanize: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Contains("recovery release succeeded", result.Detail);
        Assert.Equal(["down:57", "up:57", "up:57"], _recorder.Log);
    }

    [Fact]
    public async Task GameKeyTap_ChordReleasesEveryKeyAfterAReleaseFailure()
    {
        _recorder.RejectKeyUpCount = 1;

        var result = await InteractiveInput.GameKeyAsync(_window.Handle, [KeyW, KeyD], "tap", 0, 100, humanize: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionInputFailed, result.InterruptionReason);
        Assert.Equal(["down:57", "down:44", "up:44", "up:44", "up:57"], _recorder.Log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameKeyTap_ForegroundLossDuringChordSetupReleasesBeforeNextKey(bool humanize)
    {
        _recorder.OnEvent = e => { if (e == "down:57") _foregroundLost = true; };

        var result = await InteractiveInput.GameKeyAsync(_window.Handle, [KeyW, KeyD], "tap", 0, 100, humanize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameKeyTap_SameProcessForegroundSwitchDuringChordSetupReleasesBeforeNextKey(bool humanize)
    {
        using var otherWindow = new OwnedWindow();
        InteractiveInput.ForegroundWindowProvider = () => _foregroundLost ? otherWindow.Handle : _window.Handle;
        _recorder.OnEvent = e => { if (e == "down:57") _foregroundLost = true; };

        var result = await InteractiveInput.GameKeyAsync(_window.Handle, [KeyW, KeyD], "tap", 0, 100, humanize, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
    }

    [Fact]
    public async Task GameKeyTap_RefusesDifferentTopLevelWindowInSameProcess()
    {
        using var otherWindow = new OwnedWindow();
        int foregroundReads = 0;
        InteractiveInput.ForegroundWindowProvider = () => ++foregroundReads <= 2 ? _window.Handle : otherWindow.Handle;

        var result = await InteractiveInput.GameKeyAsync(_window.Handle, [KeyW], "tap", 0, 100, humanize: false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Empty(_recorder.Log);
    }

    [Fact]
    public async Task Click_CursorClippedOutsideRequestedPoint_RefusesButtonDown()
    {
        _recorder.MoveOffset = (20, 20);

        var result = await InteractiveInput.ClickAsync(_window.Handle, 100, 100, MouseButton.Left, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal("cursor_did_not_reach_click_target", result.Detail);
        Assert.Empty(_recorder.ButtonLog);
    }

    [Fact]
    public async Task Click_CancelledDuringHover_RefusesButtonDown()
    {
        using var cts = new CancellationTokenSource();
        _recorder.OnMove = cts.Cancel;

        var result = await InteractiveInput.ClickAsync(_window.Handle, 100, 100, MouseButton.Left, 1, cts.Token);

        Assert.False(result.Success);
        Assert.True(result.InputSent); // cursor moved, but no click was sent
        Assert.Equal(InteractiveInput.InterruptionCancelled, result.InterruptionReason);
        Assert.Empty(_recorder.ButtonLog);
    }

    [Fact]
    public async Task Click_ForegroundLostDuringHover_RefusesButtonDown()
    {
        _recorder.OnMove = () => _foregroundLost = true;

        var result = await InteractiveInput.ClickAsync(_window.Handle, 100, 100, MouseButton.Left, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent); // cursor moved, but no click was sent
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Empty(_recorder.ButtonLog);
    }

    [Fact]
    public async Task HumanizedClick_CursorRemainsOutsideTarget_RefusesButtonDown()
    {
        InteractiveInput.CursorLockProvider = _ => false;
        _recorder.CursorPosition = (120, 120);

        var result = await InteractiveInput.HumanClickAsync(_window.Handle, 100, 100, null,
            MouseButton.Left, 1, new HumanizedOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("cursor_did_not_reach_click_target", result.Detail);
        Assert.Empty(_recorder.ButtonLog);
    }

    [Fact]
    public async Task HumanizedClick_CursorLocked_RefusesButtonDown()
    {
        var result = await InteractiveInput.HumanClickAsync(_window.Handle, 100, 100, null,
            MouseButton.Left, 1, new HumanizedOptions(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("cursor_locked_click_refused", result.Detail);
        Assert.Empty(_recorder.ButtonLog);
    }

    [Fact]
    public async Task Hold_ReportsActualHeldDurationAndReleasesInReverseOrder()
    {
        var result = await Hold([KeyW, KeyD], 300, humanize: false);

        Assert.True(result.Success, result.Detail);
        Assert.True(result.InputSent);
        Assert.Null(result.InterruptionReason);
        Assert.InRange(result.ElapsedHoldMs ?? -1, 280, 1500);
        Assert.Equal(["down:57", "down:44", "up:44", "up:57"], _recorder.Log);
    }

    [Fact]
    public async Task Hold_ForegroundLostMidHold_ReportsPartialHoldAndReleases()
    {
        _recorder.OnEvent = e => { if (e == "down:57") _ = Task.Delay(100).ContinueWith(_ => _foregroundLost = true); };

        var result = await Hold([KeyW], 5000, humanize: false);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Contains("lost the foreground", result.Detail);
        Assert.InRange(result.ElapsedHoldMs ?? -1, 80, 2000);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
        Assert.False(GameTools.CanRetryForegroundLoss(result, 1));
    }

    [Fact]
    public async Task HumanizedHold_ForegroundLostDuringChordSetup_ReleasesPressedKeys()
    {
        _recorder.OnEvent = e => { if (e == "down:57") _foregroundLost = true; };

        var result = await Hold([KeyW, KeyD], 2000, humanize: true);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.NotNull(result.ElapsedHoldMs);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
        Assert.False(GameTools.CanRetryForegroundLoss(result, 1));
    }

    [Fact]
    public async Task Hold_CancelledAfterPress_ReturnsPartialInputInsteadOfThrowing()
    {
        using var cts = new CancellationTokenSource();
        _recorder.OnEvent = e => { if (e == "down:57") _ = Task.Delay(100).ContinueWith(_ => cts.Cancel()); };
        int dispatches = 0;

        var (result, _) = await GameTools.RetryOnForegroundLoss(() => { dispatches++; return Hold([KeyW], 5000, humanize: false, cts.Token); }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionCancelled, result.InterruptionReason);
        Assert.InRange(result.ElapsedHoldMs ?? -1, 80, 2000);
        Assert.Equal(1, dispatches);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
    }

    [Fact]
    public async Task HumanizedHold_CancelledDuringChordSetup_CountsSetupInHeldDurationAndReleases()
    {
        using var cts = new CancellationTokenSource();
        _recorder.OnEvent = e => { if (e == "down:57") cts.Cancel(); };

        var result = await Hold([KeyW, KeyD], 2000, humanize: true, cts.Token);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionCancelled, result.InterruptionReason);
        Assert.NotNull(result.ElapsedHoldMs);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
    }

    [Fact]
    public async Task Hold_CancelledBeforeAnyInput_KeepsCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Hold([KeyW], 300, humanize: false, cts.Token));

        Assert.Empty(_recorder.Log);
    }

    [Fact]
    public async Task Hold_RejectedChordPress_ReportsPartialHoldAndReleasesAcceptedKeys()
    {
        _recorder.RejectDown = KeyD;

        var result = await Hold([KeyW, KeyD], 300, humanize: false);

        Assert.False(result.Success);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionInputFailed, result.InterruptionReason);
        Assert.NotNull(result.ElapsedHoldMs);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
        Assert.False(GameTools.CanRetryForegroundLoss(result, 1));
    }

    [Fact]
    public async Task Retry_PreInputForegroundLossRepeatsOnce_ThenStartedHoldIsNeverReplayed()
    {
        int reads = 0, dispatches = 0;
        InteractiveInput.ForegroundWindowProvider = () =>
        {
            if (dispatches == 1) return ++reads <= 2 ? _window.Handle : IntPtr.Zero; // activation checks pass, the first foreground check fails
            return _foregroundLost ? IntPtr.Zero : _window.Handle;
        };
        _recorder.OnEvent = e => { if (e == "down:57") _ = Task.Delay(100).ContinueWith(_ => _foregroundLost = true); };

        var (result, _) = await GameTools.RetryOnForegroundLoss(() => { dispatches++; return Hold([KeyW], 5000, humanize: false); }, CancellationToken.None);

        Assert.Equal(2, dispatches);
        Assert.Equal(2, result.Attempts);
        Assert.True(result.InputSent);
        Assert.Equal(InteractiveInput.InterruptionForegroundLost, result.InterruptionReason);
        Assert.Equal(["down:57", "up:57"], _recorder.Log);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task Retry_IsBoundedAndStopsOnceInputWasSent(bool inputSent, int expectedDispatches)
    {
        int dispatches = 0;
        var lost = new InputResult(false, "sendinput_game_key_hold", "target window lost the foreground; humanized input stopped", inputSent);

        var (result, _) = await GameTools.RetryOnForegroundLoss(() => { dispatches++; return Task.FromResult(lost); }, CancellationToken.None);

        Assert.Equal(expectedDispatches, dispatches);
        Assert.Equal(expectedDispatches, result.Attempts);
    }

    private sealed class RecordingBackend : IInputBackend
    {
        private readonly object _gate = new();
        private readonly List<string> _log = [];
        private readonly List<string> _buttonLog = [];
        public Action<string>? OnEvent { get; set; }
        public Action? OnMove { get; set; }
        public int RejectDown { get; set; } = -1;
        public int RejectKeyUpCount { get; set; }
        public int RejectButtonUpCount { get; set; }
        public (int X, int Y) MoveOffset { get; set; }
        public (int X, int Y)? CursorPosition { get; set; }
        public string Name => "recording-test-backend";
        public bool IsAvailable => true;
        public IReadOnlyList<string> Log { get { lock (_gate) return [.. _log]; } }
        public IReadOnlyList<string> ButtonLog { get { lock (_gate) return [.. _buttonLog]; } }

        public InputResult SetKey(int vk, bool up, IntPtr layout)
        {
            if (!up && vk == RejectDown) return new InputResult(false, Name, "rejected");
            var entry = (up ? "up:" : "down:") + vk.ToString("X2");
            lock (_gate) _log.Add(entry);
            OnEvent?.Invoke(entry);
            if (up && RejectKeyUpCount > 0)
            {
                RejectKeyUpCount--;
                return new InputResult(false, Name, "rejected key release");
            }
            return new InputResult(true, Name);
        }

        public InputResult MoveTo(int x, int y)
        {
            CursorPosition = (x + MoveOffset.X, y + MoveOffset.Y);
            OnMove?.Invoke();
            return new InputResult(true, Name);
        }
        public InputResult MoveRelative(int dx, int dy) => new(true, Name);
        public InputResult SetButton(MouseButton button, bool down)
        {
            lock (_gate) _buttonLog.Add($"{button}:{(down ? "down" : "up")}");
            if (!down && RejectButtonUpCount > 0)
            {
                RejectButtonUpCount--;
                return new InputResult(false, Name, "rejected button release");
            }
            return new InputResult(true, Name);
        }
        public InputResult Wheel(int delta, bool horizontal) => new(true, Name);
        public InputResult TypeChar(char ch) => new(true, Name);
    }

    private sealed class OwnedWindow : IDisposable
    {
        private readonly ManualResetEventSlim _created = new();
        private readonly ManualResetEventSlim _close = new();
        private readonly Thread _thread;

        public IntPtr Handle { get; private set; }

        public OwnedWindow()
        {
            _thread = new Thread(() =>
            {
                Handle = CreateWindowExW(0, "STATIC", "ShadowUse partial-input test window", 0, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                _created.Set();
                _close.Wait();
                if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            }) { IsBackground = true };
            _thread.Start();
            _created.Wait();
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create the owned test window.");
        }

        public void Dispose()
        {
            _close.Set();
            _thread.Join(TimeSpan.FromSeconds(5));
            _created.Dispose();
            _close.Dispose();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
    }
}
