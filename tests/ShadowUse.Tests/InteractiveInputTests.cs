using System.Collections.Concurrent;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

[CollectionDefinition("InteractiveInput", DisableParallelization = true)]
public sealed class InteractiveInputCollection { }

[Collection("InteractiveInput")]
public sealed class InteractiveInputTests
{
    [Fact]
    public async Task CancellingARealInputHoldReleasesEveryAcceptedKey()
    {
        using var window = new HiddenWindow();
        var backend = new RecordingBackend();
        var previousBackend = InteractiveInput.DefaultBackend;
        var previousForeground = InteractiveInput.ForegroundWindowProvider;
        bool previousProfile = InteractiveInput.GameProfile;
        try
        {
            InteractiveInput.DefaultBackend = backend;
            InteractiveInput.ForegroundWindowProvider = () => window.Handle;
            InteractiveInput.GameProfile = true; // Do not restore the user's physical cursor in a fake-backend test.
            using var cancellation = new CancellationTokenSource();
            var input = InteractiveInput.GameKeyAsync(window.Handle, [0x10, 0x57], "hold", 60000, 100, false, cancellation.Token);
            await backend.TwoPresses.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            var result = await input;
            Assert.False(result.Success);
            Assert.True(result.InputSent);
            Assert.Equal(InteractiveInput.InterruptionCancelled, result.InterruptionReason);
            Assert.Contains((0x10, true), backend.Keys);
            Assert.Contains((0x57, true), backend.Keys);
        }
        finally
        {
            InteractiveInput.DefaultBackend = previousBackend;
            InteractiveInput.ForegroundWindowProvider = previousForeground;
            InteractiveInput.GameProfile = previousProfile;
        }
    }

    [Fact]
    public async Task PartialChordFailureReleasesItsAlreadyPressedModifier()
    {
        using var window = new HiddenWindow();
        var backend = new RecordingBackend { FailSecondPress = true };
        var previousBackend = InteractiveInput.DefaultBackend;
        var previousForeground = InteractiveInput.ForegroundWindowProvider;
        bool previousProfile = InteractiveInput.GameProfile;
        try
        {
            InteractiveInput.DefaultBackend = backend;
            InteractiveInput.ForegroundWindowProvider = () => window.Handle;
            InteractiveInput.GameProfile = true;
            var result = await InteractiveInput.GameKeyAsync(window.Handle, [0x10, 0x57], "tap", 100, 100, false, default);
            Assert.False(result.Success);
            Assert.True(result.InputSent);
            Assert.Contains((0x10, true), backend.Keys);
        }
        finally
        {
            InteractiveInput.DefaultBackend = previousBackend;
            InteractiveInput.ForegroundWindowProvider = previousForeground;
            InteractiveInput.GameProfile = previousProfile;
        }
    }

    [Fact]
    public async Task RejectedCleanupIsReportedRatherThanClaimingSuccess()
    {
        using var window = new HiddenWindow();
        var previousBackend = InteractiveInput.DefaultBackend;
        var previousForeground = InteractiveInput.ForegroundWindowProvider;
        bool previousProfile = InteractiveInput.GameProfile;
        try
        {
            InteractiveInput.DefaultBackend = new RecordingBackend { FailReleases = true };
            InteractiveInput.ForegroundWindowProvider = () => window.Handle;
            InteractiveInput.GameProfile = true;
            var result = await InteractiveInput.GameKeyAsync(window.Handle, [0x57], "tap", 100, 100, false, default);
            Assert.False(result.Success);
            Assert.Contains("cleanup", result.Detail);
        }
        finally
        {
            InteractiveInput.DefaultBackend = previousBackend;
            InteractiveInput.ForegroundWindowProvider = previousForeground;
            InteractiveInput.GameProfile = previousProfile;
        }
    }

    [Theory]
    [InlineData("shift+w", 2)]
    [InlineData("rmb+lmb", 2)]
    [InlineData("ctrl+alt+delete", 3)]
    public void KeyChordsIncludeStandaloneModifiersAndMouseButtons(string text, int count)
        => Assert.Equal(count, InteractiveInput.ParseKeySet(text).Length);

    [Fact]
    public async Task UnboundedDownCannotLeaveAnotherAgentBlocked()
        => await Assert.ThrowsAsync<ArgumentException>(async () => await InteractiveInput.GameKeyAsync((IntPtr)1, [0x57], "down", 100, 100, false, default));

    private sealed class RecordingBackend : IInputBackend
    {
        public string Name => "test";
        public bool IsAvailable => true;
        public bool FailSecondPress;
        public bool FailReleases;
        public ConcurrentQueue<(int Key, bool Up)> Keys = new();
        public TaskCompletionSource TwoPresses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int presses;
        private BackgroundInput.InputResult Ok() => new(true, Name);
        public BackgroundInput.InputResult MoveTo(int x, int y) => Ok();
        public BackgroundInput.InputResult MoveRelative(int dx, int dy) => Ok();
        public BackgroundInput.InputResult SetButton(BackgroundInput.MouseButton button, bool down) => Ok();
        public BackgroundInput.InputResult Wheel(int delta, bool horizontal) => Ok();
        public BackgroundInput.InputResult TypeChar(char ch) => Ok();
        public BackgroundInput.InputResult SetKey(int vk, bool up, IntPtr layout)
        {
            if (!up && Interlocked.Increment(ref presses) == 2)
            {
                TwoPresses.TrySetResult();
                if (FailSecondPress) return new(false, Name, "rejected");
            }
            Keys.Enqueue((vk, up));
            return up && FailReleases ? new(false, Name, "release rejected") : Ok();
        }
    }

    private sealed class HiddenWindow : IDisposable
    {
        private readonly ManualResetEventSlim ready = new();
        private readonly Thread thread;
        private System.Windows.Forms.Form form = null!;
        public IntPtr Handle { get; private set; }
        public HiddenWindow()
        {
            thread = new Thread(() =>
            {
                form = new System.Windows.Forms.Form { ShowInTaskbar = false };
                Handle = form.Handle; // Create a hidden owned HWND; never activate it.
                ready.Set();
                System.Windows.Forms.Application.Run();
                form.Dispose();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(2)));
        }
        public void Dispose()
        {
            form.BeginInvoke((Action)(() => System.Windows.Forms.Application.ExitThread()));
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
            ready.Dispose();
        }
    }
}
