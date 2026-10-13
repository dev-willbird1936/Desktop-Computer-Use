// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShadowUse.Native;

namespace ShadowUse.Automation;

public enum GameInputOwnershipState { Unowned, Owner, Conflict, Unknown }

/// <summary>Stable window identity: root HWND plus the owning process id and creation token.
/// Independent of DCU's per-process target registry ids, so every DCU process derives the same key.</summary>
public readonly record struct GameInputWindowIdentity(IntPtr RootHwnd, int Pid, long ProcessCreationToken)
{
    public bool IsValid => RootHwnd != IntPtr.Zero && Pid > 0 && ProcessCreationToken > 0;
    public string Key => $"{RootHwnd.ToInt64():X}:{Pid}:{ProcessCreationToken}";
}

public sealed record GameInputControllerInfo(string ControllerId, int Pid, long ProcessCreationToken, DateTimeOffset ClaimedUtc);

public sealed record GameInputOwnershipStatus(
    GameInputOwnershipState State,
    string Reason,
    GameInputWindowIdentity? Window,
    GameInputControllerInfo? Controller,
    string? LeasePath,
    int ActiveOperations = 0,
    string? Error = null)
{
    public string Describe()
    {
        var text = $"game input ownership {State.ToString().ToLowerInvariant()} ({Reason})";
        if (Controller is { } c) text += $"; controller pid {c.Pid}";
        if (Window is { } w) text += $"; window {w.Key}";
        if (Error != null) text += $"; {Error}";
        return text;
    }
}

public sealed record GameInputReleaseResult(bool Released, string Reason, GameInputOwnershipStatus Status);

/// <summary>One in-flight operation under a lease. Disposing it ends the operation but keeps the lease.</summary>
public sealed class GameInputClaim : IDisposable
{
    private Action? end;
    internal GameInputClaim(GameInputOwnershipStatus status, Action? end)
    {
        Status = status;
        this.end = end;
        Granted = end != null || status.Reason == GameInputOwnership.NotRequired;
    }
    public GameInputOwnershipStatus Status { get; }
    public bool Granted { get; }
    public void Dispose() => Interlocked.Exchange(ref end, null)?.Invoke();
}

/// <summary>
/// Cross-process control lease for one game window. The lease is an open OS file handle that denies other writers
/// (FileShare.Read), so it needs no named Mutex, has no thread affinity across awaits, and the OS drops it when the
/// owning process exits or crashes. Metadata written into the file lets a contender report who holds it. Ownership
/// is never stolen: a contender gets Conflict or Unknown and no input is sent.
/// </summary>
public sealed class GameInputOwnership : IDisposable
{
    public const string NotRequired = "not_required";
    private const int ClaimAttempts = 3;
    private const int ClaimRetryMs = 20;
    private const int MetadataReadAttempts = 3;
    private const int MetadataRetryMs = 10;

    private sealed record LeaseMetadata(int Version, string ControllerId, int ControllerPid, long ControllerProcessCreationToken,
        long WindowHwnd, int WindowPid, long WindowProcessCreationToken, DateTimeOffset ClaimedUtc);

    private sealed class Lease(GameInputWindowIdentity window, FileStream stream, string path, GameInputControllerInfo controller)
    {
        public GameInputWindowIdentity Window { get; } = window;
        public FileStream Stream { get; } = stream;
        public string Path { get; } = path;
        public GameInputControllerInfo Controller { get; } = controller;
        public int ActiveOperations;
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Lease> leases = [];
    private readonly string leaseDirectory;
    private readonly Func<IntPtr, GameInputWindowIdentity?> resolveWindow;
    private readonly Func<GameInputWindowIdentity, int> heldInputCount;
    private readonly GameInputControllerInfo controllerTemplate;

    public GameInputOwnership(string leaseDirectory, Func<IntPtr, GameInputWindowIdentity?>? resolveWindow = null,
        Func<GameInputWindowIdentity, int>? heldInputCount = null)
    {
        this.leaseDirectory = leaseDirectory;
        this.resolveWindow = resolveWindow ?? ResolveNative;
        this.heldInputCount = heldInputCount ?? (_ => 0);
        int pid = Environment.ProcessId;
        UiaService.TryGetProcessCreationToken(pid, out var token);
        controllerTemplate = new GameInputControllerInfo(Guid.NewGuid().ToString("N"), pid, token, DateTimeOffset.UtcNow);
    }

    /// <summary>Leases live under the real per-user LocalApplicationData, never an isolated APPDATA, so every DCU
    /// process of the user sees the same leases.</summary>
    public static string DefaultLeaseDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "shadow-use", "game-input-ownership");

    public static GameInputClaim NotRequiredClaim()
        => new(new GameInputOwnershipStatus(GameInputOwnershipState.Unowned, NotRequired, null, null, null), null);

    public string LeaseDirectory => leaseDirectory;
    public GameInputControllerInfo Controller => controllerTemplate;

    public static GameInputWindowIdentity? ResolveNative(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero || !NativeMethods.IsWindow(rootHwnd)) return null;
        if (NativeMethods.GetWindowThreadProcessId(rootHwnd, out var pid) == 0 || pid == 0) return null;
        return UiaService.TryGetProcessCreationToken((int)pid, out var token)
            ? new GameInputWindowIdentity(rootHwnd, (int)pid, token)
            : null;
    }

    public GameInputWindowIdentity? Resolve(IntPtr rootHwnd) => resolveWindow(rootHwnd);

    private string LeasePath(GameInputWindowIdentity window)
        => Path.Combine(leaseDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(window.Key)))[..32].ToLowerInvariant() + ".lease");

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private GameInputOwnershipStatus Unknown(GameInputWindowIdentity? window, string reason, string? error, string? path = null)
        => new(GameInputOwnershipState.Unknown, reason, window, null, path, Error: error);

    /// <summary>Claims the window (or re-enters this controller's claim) and starts one operation under it.
    /// Dispose the result when the operation ends; the lease itself stays until released.</summary>
    public async Task<GameInputClaim> TryBeginAsync(IntPtr rootHwnd, CancellationToken ct = default)
    {
        if (resolveWindow(rootHwnd) is not { IsValid: true } window)
            return new GameInputClaim(Unknown(null, "window_identity_unavailable", "window root, process id or process creation token could not be read"), null);
        return await TryBeginAsync(window, ct).ConfigureAwait(false);
    }

    public async Task<GameInputClaim> TryBeginAsync(GameInputWindowIdentity window, CancellationToken ct = default)
    {
        ReleaseStale();
        GameInputClaim claim = Attempt(window);
        for (int i = 1; !claim.Granted && (claim.Status.State == GameInputOwnershipState.Conflict || claim.Status.Reason == "lease_access_failed") && i < ClaimAttempts; i++)
        {
            await Task.Delay(ClaimRetryMs, ct).ConfigureAwait(false);
            claim = Attempt(window);
        }
        return claim;
    }

    private GameInputClaim Attempt(GameInputWindowIdentity window)
    {
        if (!window.IsValid) return new GameInputClaim(Unknown(window, "window_identity_unavailable", "window identity is incomplete"), null);
        string path = LeasePath(window);
        lock (gate)
        {
            if (leases.TryGetValue(window.Key, out var held))
            {
                held.ActiveOperations++;
                return new GameInputClaim(OwnerStatus(held), () => EndOperation(held));
            }
            FileStream stream;
            try
            {
                Directory.CreateDirectory(leaseDirectory);
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.None);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return new GameInputClaim(ReadConflict(window, path), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return new GameInputClaim(Unknown(window, "lease_access_failed", $"{ex.GetType().Name}: {ex.Message}", path), null);
            }

            var controller = controllerTemplate with { ClaimedUtc = DateTimeOffset.UtcNow };
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new LeaseMetadata(1, controller.ControllerId, controller.Pid, controller.ProcessCreationToken,
                    window.RootHwnd.ToInt64(), window.Pid, window.ProcessCreationToken, controller.ClaimedUtc));
                stream.SetLength(0);
                stream.Write(bytes);
                stream.Flush(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stream.Dispose();
                return new GameInputClaim(Unknown(window, "lease_access_failed", $"{ex.GetType().Name}: {ex.Message}", path), null);
            }
            var lease = new Lease(window, stream, path, controller) { ActiveOperations = 1 };
            leases[window.Key] = lease;
            return new GameInputClaim(OwnerStatus(lease), () => EndOperation(lease));
        }
    }

    private void EndOperation(Lease lease)
    {
        lock (gate) if (lease.ActiveOperations > 0) lease.ActiveOperations--;
    }

    private static GameInputOwnershipStatus OwnerStatus(Lease lease)
        => new(GameInputOwnershipState.Owner, "owned_by_this_controller", lease.Window, lease.Controller, lease.Path, lease.ActiveOperations);

    private GameInputOwnershipStatus ReadConflict(GameInputWindowIdentity window, string path)
    {
        LeaseMetadata? metadata = null;
        string? readError = null;
        for (int i = 0; i < MetadataReadAttempts && metadata == null; i++)
        {
            if (i > 0) Thread.Sleep(MetadataRetryMs);
            try
            {
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (reader.Length == 0) continue;
                metadata = JsonSerializer.Deserialize<LeaseMetadata>(reader);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                readError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        if (metadata == null || metadata.WindowHwnd != window.RootHwnd.ToInt64() || metadata.WindowPid != window.Pid || metadata.WindowProcessCreationToken != window.ProcessCreationToken)
            return new GameInputOwnershipStatus(GameInputOwnershipState.Conflict, "held_by_other_controller_metadata_unavailable", window, null, path, Error: readError);
        return new GameInputOwnershipStatus(GameInputOwnershipState.Conflict, "held_by_other_controller", window,
            new GameInputControllerInfo(metadata.ControllerId, metadata.ControllerPid, metadata.ControllerProcessCreationToken, metadata.ClaimedUtc), path);
    }

    public GameInputOwnershipStatus Inspect(IntPtr rootHwnd)
        => resolveWindow(rootHwnd) is { IsValid: true } window
            ? Inspect(window)
            : Unknown(null, "window_identity_unavailable", "window root, process id or process creation token could not be read");

    /// <summary>Reports unowned, owner, conflict or unknown without claiming. The probe briefly opens the lease file
    /// exclusively, so it can only succeed when no controller holds it.</summary>
    public GameInputOwnershipStatus Inspect(GameInputWindowIdentity window)
    {
        if (!window.IsValid) return Unknown(window, "window_identity_unavailable", "window identity is incomplete");
        string path = LeasePath(window);
        for (int attempt = 0; ; attempt++)
        {
            lock (gate)
            {
                if (leases.TryGetValue(window.Key, out var held)) return OwnerStatus(held);
                try
                {
                    if (!File.Exists(path)) return new GameInputOwnershipStatus(GameInputOwnershipState.Unowned, "unowned", window, null, path);
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return new GameInputOwnershipStatus(GameInputOwnershipState.Unowned, "unowned", window, null, path);
                }
                catch (FileNotFoundException) { return new GameInputOwnershipStatus(GameInputOwnershipState.Unowned, "unowned", window, null, path); }
                catch (DirectoryNotFoundException) { return new GameInputOwnershipStatus(GameInputOwnershipState.Unowned, "unowned", window, null, path); }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    if (attempt >= ClaimAttempts - 1) return ReadConflict(window, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return Unknown(window, "lease_access_failed", $"{ex.GetType().Name}: {ex.Message}", path);
                }
            }
            Thread.Sleep(ClaimRetryMs);
        }
    }

    /// <summary>Leases held by this controller.</summary>
    public IReadOnlyList<GameInputOwnershipStatus> Held()
    {
        lock (gate) return [.. leases.Values.Select(OwnerStatus)];
    }

    /// <summary>Releases this controller's lease for the window only when no operation is running and no key or button
    /// is held. Another controller's lease is never touched.</summary>
    public GameInputReleaseResult Release(IntPtr rootHwnd)
        => resolveWindow(rootHwnd) is { IsValid: true } window
            ? Release(window)
            : new GameInputReleaseResult(false, "window_identity_unavailable", Unknown(null, "window_identity_unavailable", "window root, process id or process creation token could not be read"));

    public GameInputReleaseResult Release(GameInputWindowIdentity window)
    {
        lock (gate)
        {
            if (leases.TryGetValue(window.Key, out var lease))
            {
                var status = OwnerStatus(lease);
                if (lease.ActiveOperations > 0) return new GameInputReleaseResult(false, "input_in_flight", status);
                if (heldInputCount(window) > 0) return new GameInputReleaseResult(false, "keys_or_buttons_held", status);
                CloseLease(lease);
                return new GameInputReleaseResult(true, "released", status with { State = GameInputOwnershipState.Unowned, Reason = "released", ActiveOperations = 0 });
            }
        }
        var observed = Inspect(window);
        string reason = observed.State switch
        {
            GameInputOwnershipState.Unowned => "not_owned",
            GameInputOwnershipState.Conflict => "owned_by_another_controller",
            _ => observed.Reason,
        };
        return new GameInputReleaseResult(false, reason, observed);
    }

    /// <summary>Drops leases whose window is gone or whose process identity changed, when nothing is running or held.</summary>
    public int ReleaseStale()
    {
        lock (gate)
        {
            var stale = leases.Values
                .Where(l => l.ActiveOperations == 0 && heldInputCount(l.Window) == 0 && resolveWindow(l.Window.RootHwnd) != l.Window)
                .ToArray();
            foreach (var lease in stale) CloseLease(lease);
            return stale.Length;
        }
    }

    /// <summary>Shutdown: closes every lease this controller holds. Release held keys first.</summary>
    public int ReleaseAll()
    {
        lock (gate)
        {
            var all = leases.Values.ToArray();
            foreach (var lease in all) CloseLease(lease);
            return all.Length;
        }
    }

    private void CloseLease(Lease lease)
    {
        leases.Remove(lease.Window.Key);
        try { lease.Stream.SetLength(0); } catch (IOException) { /* the handle closing releases ownership regardless */ }
        lease.Stream.Dispose();
        try { File.Delete(lease.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a contender already reopened it */ }
    }

    public void Dispose() => ReleaseAll();
}
