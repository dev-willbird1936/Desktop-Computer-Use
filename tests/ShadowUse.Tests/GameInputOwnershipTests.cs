using System.Diagnostics;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

[Collection("InteractiveInputStatics")]
public sealed class GameInputOwnershipTests : IDisposable
{
    private static readonly GameInputWindowIdentity Window = new(new IntPtr(0x1234), 4242, 133_000_000_000_000_000);

    private readonly string directory = Path.Combine(Path.GetTempPath(), "dcu-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly List<GameInputOwnership> contenders = [];

    private GameInputOwnership Contender(Func<GameInputWindowIdentity, int>? held = null, Func<IntPtr, GameInputWindowIdentity?>? resolve = null)
    {
        var ownership = new GameInputOwnership(directory, resolve ?? (_ => Window), held);
        contenders.Add(ownership);
        return ownership;
    }

    public void Dispose()
    {
        foreach (var contender in contenders) contender.Dispose();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WindowKey_IsStableAndBoundToRootPidAndCreationToken()
    {
        Assert.Equal(Window.Key, new GameInputWindowIdentity(new IntPtr(0x1234), 4242, 133_000_000_000_000_000).Key);
        Assert.NotEqual(Window.Key, (Window with { RootHwnd = new IntPtr(0x1235) }).Key);
        Assert.NotEqual(Window.Key, (Window with { Pid = 4243 }).Key);
        Assert.NotEqual(Window.Key, (Window with { ProcessCreationToken = Window.ProcessCreationToken + 1 }).Key);
        Assert.True(UiaService.TryGetProcessCreationToken(Environment.ProcessId, out var first));
        Assert.True(UiaService.TryGetProcessCreationToken(Environment.ProcessId, out var second));
        Assert.Equal(first, second);
        Assert.False(new GameInputWindowIdentity(IntPtr.Zero, 1, 1).IsValid);
        Assert.False(new GameInputWindowIdentity(new IntPtr(1), 0, 1).IsValid);
        Assert.False(new GameInputWindowIdentity(new IntPtr(1), 1, 0).IsValid);
        Assert.Null(GameInputOwnership.ResolveNative(IntPtr.Zero));
    }

    [Fact]
    public async Task IndependentContenders_OneOwnsAndTheOtherSeesConflictWithOwnerPid()
    {
        var first = Contender();
        var second = Contender();
        Assert.Equal(GameInputOwnershipState.Unowned, first.Inspect(Window).State);

        using var owned = await first.TryBeginAsync(Window);
        Assert.True(owned.Granted);
        Assert.Equal(GameInputOwnershipState.Owner, owned.Status.State);

        using var refused = await second.TryBeginAsync(Window);
        Assert.False(refused.Granted);
        Assert.Equal(GameInputOwnershipState.Conflict, refused.Status.State);
        Assert.Equal("held_by_other_controller", refused.Status.Reason);
        Assert.Equal(Environment.ProcessId, refused.Status.Controller?.Pid);
        Assert.Equal(first.Controller.ControllerId, refused.Status.Controller?.ControllerId);
        Assert.Equal(Window, refused.Status.Window);

        var inspected = second.Inspect(Window);
        Assert.Equal(GameInputOwnershipState.Conflict, inspected.State);
        Assert.Equal(first.Controller.ControllerId, inspected.Controller?.ControllerId);
        Assert.Equal(GameInputOwnershipState.Owner, first.Inspect(Window).State);
    }

    [Fact]
    public async Task OwnerReenters_AcrossCallsWithoutReleasingBetween()
    {
        var owner = Contender();
        using (var call1 = await owner.TryBeginAsync(Window)) Assert.True(call1.Granted);
        using var call2 = await owner.TryBeginAsync(Window);
        using var call3 = await owner.TryBeginAsync(Window);
        Assert.True(call2.Granted && call3.Granted);
        Assert.Equal(2, owner.Held().Single().ActiveOperations);
    }

    [Fact]
    public async Task Release_RefusedWhileInputInFlightOrKeysHeld_ThenReacquirableByAnotherController()
    {
        int heldInputs = 1;
        var owner = Contender(held: _ => heldInputs);
        var other = Contender();

        var claim = await owner.TryBeginAsync(Window);
        var inFlight = owner.Release(Window);
        Assert.False(inFlight.Released);
        Assert.Equal("input_in_flight", inFlight.Reason);
        claim.Dispose();

        var held = owner.Release(Window);
        Assert.False(held.Released);
        Assert.Equal("keys_or_buttons_held", held.Reason);
        Assert.Equal(GameInputOwnershipState.Owner, held.Status.State);
        Assert.False((await other.TryBeginAsync(Window)).Granted);

        heldInputs = 0;
        var released = owner.Release(Window);
        Assert.True(released.Released);
        Assert.Equal(GameInputOwnershipState.Unowned, released.Status.State);
        Assert.Empty(owner.Held());

        using var reacquired = await other.TryBeginAsync(Window);
        Assert.True(reacquired.Granted);
        Assert.Equal(GameInputOwnershipState.Owner, reacquired.Status.State);
    }

    [Fact]
    public async Task Release_NeverTouchesAnotherControllersLease()
    {
        var owner = Contender();
        var other = Contender();
        using var claim = await owner.TryBeginAsync(Window);

        var attempt = other.Release(Window);
        Assert.False(attempt.Released);
        Assert.Equal("owned_by_another_controller", attempt.Reason);
        Assert.Equal(GameInputOwnershipState.Owner, owner.Inspect(Window).State);

        Assert.Equal("not_owned", Contender().Release(Window with { Pid = 9 }).Reason);
    }

    [Fact]
    public async Task ReleaseAll_AndDispose_FreeEveryLeaseForShutdown()
    {
        var owner = Contender();
        var secondWindow = Window with { RootHwnd = new IntPtr(0x9999) };
        (await owner.TryBeginAsync(Window)).Dispose();
        (await owner.TryBeginAsync(secondWindow)).Dispose();

        Assert.Equal(2, owner.ReleaseAll());
        Assert.Empty(owner.Held());
        var other = Contender();
        Assert.Equal(GameInputOwnershipState.Unowned, other.Inspect(Window).State);
        using var first = await other.TryBeginAsync(Window);
        using var second = await other.TryBeginAsync(secondWindow);
        Assert.True(first.Granted && second.Granted);
    }

    [Fact]
    public async Task ReleaseStale_DropsLeaseWhenWindowIdentityChangedAndNothingRuns()
    {
        GameInputWindowIdentity? current = Window;
        var owner = Contender(resolve: _ => current);
        var claim = await owner.TryBeginAsync(Window);

        current = Window with { ProcessCreationToken = Window.ProcessCreationToken + 5 };
        Assert.Equal(0, owner.ReleaseStale());
        claim.Dispose();

        Assert.Equal(1, owner.ReleaseStale());
        Assert.Empty(owner.Held());
        Assert.False(File.Exists(owner.Inspect(Window).LeasePath));
    }

    [Fact]
    public async Task UnresolvableWindow_IsUnknownAndFailsClosed()
    {
        var owner = Contender(resolve: _ => null);
        using var claim = await owner.TryBeginAsync(new IntPtr(77));
        Assert.False(claim.Granted);
        Assert.Equal(GameInputOwnershipState.Unknown, claim.Status.State);
        Assert.Equal("window_identity_unavailable", claim.Status.Reason);
        Assert.Equal(GameInputOwnershipState.Unknown, owner.Inspect(new IntPtr(77)).State);
        Assert.False(owner.Release(new IntPtr(77)).Released);
    }

    [Fact]
    public async Task LeaseAccessFailure_IsUnknownAndFailsClosed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        await File.WriteAllTextAsync(directory, "a file where the lease directory should be");
        try
        {
            var owner = new GameInputOwnership(directory, _ => Window);
            using var claim = await owner.TryBeginAsync(Window);
            Assert.False(claim.Granted);
            Assert.Equal(GameInputOwnershipState.Unknown, claim.Status.State);
            Assert.Equal("lease_access_failed", claim.Status.Reason);
            Assert.NotNull(claim.Status.Error);
        }
        finally { File.Delete(directory); }
    }

    [Fact]
    public async Task ProfileOff_DoesNotRequireOwnership()
    {
        bool previous = InteractiveInput.GameProfile;
        InteractiveInput.GameProfile = false;
        try
        {
            using var claim = await InteractiveInput.BeginInputOwnershipAsync(new IntPtr(1), CancellationToken.None);
            Assert.True(claim.Granted);
            Assert.Equal(GameInputOwnership.NotRequired, claim.Status.Reason);
        }
        finally { InteractiveInput.GameProfile = previous; }
    }

    [Fact]
    public async Task CrossProcess_FixtureOwnerBlocksThenCrashFreesTheLease()
    {
        var contender = Contender();
        var leasePath = contender.Inspect(Window).LeasePath!;
        Directory.CreateDirectory(directory);
        using var fixture = StartOwnerFixture(leasePath);
        try
        {
            using var refused = await contender.TryBeginAsync(Window);
            Assert.False(refused.Granted);
            Assert.Equal(GameInputOwnershipState.Conflict, refused.Status.State);
            Assert.Equal("held_by_other_controller", refused.Status.Reason);
            Assert.Equal(fixture.Id, refused.Status.Controller?.Pid);
            Assert.NotEqual(Environment.ProcessId, refused.Status.Controller?.Pid);
            Assert.Equal(Window, refused.Status.Window);
            Assert.Equal(GameInputOwnershipState.Conflict, contender.Inspect(Window).State);

            var release = contender.Release(Window);
            Assert.False(release.Released);
            Assert.Equal("owned_by_another_controller", release.Reason);
            Assert.True(File.Exists(leasePath));
        }
        finally
        {
            fixture.Kill(entireProcessTree: true);
            fixture.WaitForExit(10_000);
        }

        GameInputClaim? claim = null;
        for (int i = 0; i < 50 && claim is not { Granted: true }; i++)
        {
            claim?.Dispose();
            claim = await contender.TryBeginAsync(Window);
            if (!claim.Granted) await Task.Delay(100);
        }
        using (claim)
        {
            Assert.True(claim!.Granted, claim.Status.Describe());
            Assert.Equal(GameInputOwnershipState.Owner, claim.Status.State);
            Assert.Equal(Environment.ProcessId, claim.Status.Controller?.Pid);
        }
    }

    private static Process StartOwnerFixture(string leasePath)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $stream = [IO.File]::Open('{LEASE}', 'OpenOrCreate', 'ReadWrite', 'Read')
            $json = '{"Version":1,"ControllerId":"fixture-controller","ControllerPid":' + $PID + ',"ControllerProcessCreationToken":7,"WindowHwnd":4660,"WindowPid":4242,"WindowProcessCreationToken":133000000000000000,"ClaimedUtc":"2026-01-01T00:00:00+00:00"}'
            $bytes = [Text.Encoding]::UTF8.GetBytes($json)
            $stream.SetLength(0)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
            [Console]::Out.WriteLine('READY')
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var info = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script.Replace("{LEASE}", leasePath)));
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
            info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(30)) || ready.Result != "READY")
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("ownership fixture process did not report READY");
        }
        return process;
    }
}
