// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Interop.UIAutomationClient;
using ShadowUse.Native;

namespace ShadowUse.Automation;

/// <summary>One interactive element in a snapshot, addressable by label id.</summary>
public sealed class ElementInfo
{
    public required string Id;              // action-compatible session id e.g. "e42"
    public int[] RuntimeId = [];
    public string AutomationId = "";
    public string Name = "";
    public string ControlType = "";
    public string ClassName = "";
    public string Value = "";
    public string AcceleratorKey = "";
    public string AccessKey = "";
    public int NativeWindowHandle;
    public int X, Y, Width, Height;         // window-relative frame
    public int ScreenX, ScreenY;            // absolute frame origin
    public string[] Actions = [];

    // Observation metadata is additive. Nullable values preserve an unreadable UIA
    // property as unknown instead of turning it into a false capability claim.
    public bool? Enabled;
    public bool? ReadOnly;
    public string[] SupportedOperations = [];
    public bool? ActionableNow;
}

/// <summary>Completeness of the explicitly declared native UIA projection.</summary>
public sealed class NativeSnapshotCompleteness
{
    public bool ProjectionComplete { get; set; } = true;
    public bool NodeTruncated { get; set; }
    public bool DepthTruncated { get; set; }
    public bool TreeTextTruncated { get; set; }
    public bool PropertyErrors { get; set; }
    public int CollectedElements { get; set; }
    public int MaxNodes { get; set; }
    public int MaxDepth { get; set; }
    public int TextLimit { get; set; }
}

public enum ElementResolutionStatus
{
    Found,
    NotFound,
    Ambiguous,
    Incomplete,
}

public readonly record struct ElementResolutionSelection(
    ElementResolutionStatus Status,
    int CandidateIndex = -1);

public readonly record struct ElementResolutionResult(
    ElementResolutionStatus Status,
    IUIAutomationElement? Element = null);

public readonly record struct ElementFrame(
    int ScreenX,
    int ScreenY,
    int Width,
    int Height,
    int NativeWindowHandle)
{
    public int CenterX => ScreenX + Width / 2;
    public int CenterY => ScreenY + Height / 2;
}

public sealed class Snapshot
{
    public required int Revision;
    public required string App;
    public required int Pid;
    public required IntPtr Hwnd;
    public required string Title;
    public required NativeMethods.RECT Bounds;
    public NativeTargetReference? TargetReference;
    public List<ElementInfo> Elements = [];
    public string TreeText = "";
    public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;

    // F05 visual-grounding fallback: set only when structured UIA evidence was inadequate for
    // THIS snapshot. Mutated in place after SnapshotAsync returns so click_visual_candidate can
    // read it back off the same cached object via GetSnapshot.
    public ShadowUse.Vision.VisualObservation? Visual { get; set; }
    public bool VisualGroundingRequired { get; set; }
    public bool VisualGroundingAttempted { get; set; }
    public string? VisualGroundingUnsupportedReason { get; set; }

    // Observation identity is separate from the legacy action-compatible eN labels.
    public string ObservationId { get; set; } = $"obs-{Guid.NewGuid():N}";
    public NativeSnapshotCompleteness NativeCompleteness { get; set; } = new();

    // Alias retained for contract-oriented callers that refer to the native projection
    // simply as snapshot completeness.
    public NativeSnapshotCompleteness Completeness
    {
        get => NativeCompleteness;
        set => NativeCompleteness = value ?? new NativeSnapshotCompleteness();
    }
}

public enum AppSelectionKind
{
    LegacyProcessName,
    ProcessId,
    Title,
    ExplicitWindowId,
    ExplicitTargetId,
}

/// <summary>Resolved target app.</summary>
public sealed class AppTarget
{
    public required int Pid;
    public required string ProcessName;
    public required IntPtr Hwnd;
    public required string Title;
    public NativeTargetReference? TargetReference;
    public AppSelectionKind SelectionKind = AppSelectionKind.LegacyProcessName;
    public string? NativeIdentityError;
    public string WindowId => $"{Pid}:0x{Hwnd.ToInt64():X}";
    public string? TargetId => TargetReference is { } reference
        ? UiaService.FormatTargetId(reference.TargetId)
        : null;
    public long? LifetimeEpoch => TargetReference?.LifetimeEpoch;
}

internal sealed class SnapshotCache
{
    // GetWindow command GW_OWNER: follow only the real Win32 owner chain, not a common root.
    private const uint GetWindowOwnerCommand = 4;

    private readonly TargetRegistry _registry;
    private readonly Func<IntPtr, bool> _isWindow;
    private readonly Func<IntPtr, IntPtr, bool> _isOwnerChainRelated;
    private readonly ConcurrentDictionary<IntPtr, Snapshot> _byWindow = new();
    private readonly object _continuationGate = new();
    private readonly Dictionary<string, ContinuationEntry> _continuations = new(StringComparer.Ordinal);

    // A window's element ids move across snapshots as its content is rebuilt (e.g. a
    // duplicate-control replacement). Without this bounded per-window history, an id from
    // the immediately superseded snapshot of the SAME still-live window would incorrectly
    // report "no cached snapshot" instead of going through live re-resolution (which
    // correctly reports NotFound/Ambiguous/Found) -- this history never bypasses that
    // re-resolution, it only recovers the identifying properties (RuntimeId/AutomationId/
    // Name/ControlType) needed to attempt it.
    private const int HistoryDepthPerWindow = 8;
    private readonly Dictionary<IntPtr, List<Snapshot>> _historyByWindow = new();

    private sealed record ContinuationEntry(
        Snapshot Snapshot,
        int NextIndex,
        string ObservationId,
        string SessionId,
        string TargetId,
        long LifetimeEpoch);

    public SnapshotCache()
        : this(new TargetRegistry(), NativeMethods.IsWindow)
    {
    }

    public SnapshotCache(TargetRegistry registry)
        : this(registry, NativeMethods.IsWindow)
    {
    }

    public SnapshotCache(TargetRegistry registry, Func<IntPtr, bool> isWindow)
        : this(registry, isWindow, IsOwnerChainRelated)
    {
    }

    // The relation predicate is a narrow contract-test seam. Production callers use the
    // default Win32 owner-chain walk above; tests can supply an explicit relation without
    // pretending that synthetic HWND values exercise the operating system.
    public SnapshotCache(
        TargetRegistry registry,
        Func<IntPtr, bool> isWindow,
        Func<IntPtr, IntPtr, bool> isOwnerChainRelated)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _isWindow = isWindow ?? throw new ArgumentNullException(nameof(isWindow));
        _isOwnerChainRelated = isOwnerChainRelated ?? throw new ArgumentNullException(nameof(isOwnerChainRelated));
    }

    public void Store(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_continuationGate)
        {
            if (_byWindow.TryGetValue(snapshot.Hwnd, out var previous)
                && !ReferenceEquals(previous, snapshot))
                RemoveContinuationsNoLock(previous);
            _byWindow[snapshot.Hwnd] = snapshot;

            if (!_historyByWindow.TryGetValue(snapshot.Hwnd, out var history))
            {
                history = new List<Snapshot>();
                _historyByWindow[snapshot.Hwnd] = history;
            }
            history.Add(snapshot);
            if (history.Count > HistoryDepthPerWindow)
                history.RemoveAt(0);
        }
    }

    public void RemoveInvalidWindows(Func<IntPtr, bool> isWindow)
    {
        ArgumentNullException.ThrowIfNull(isWindow);
        lock (_continuationGate)
        {
            foreach (var hwnd in _byWindow.Keys)
            {
                if (isWindow(hwnd))
                    continue;
                if (_byWindow.TryRemove(hwnd, out var snapshot))
                    RemoveContinuationsNoLock(snapshot);
                _historyByWindow.Remove(hwnd);
            }
            foreach (var hwnd in _historyByWindow.Keys.Where(hwnd => !isWindow(hwnd)).ToArray())
                _historyByWindow.Remove(hwnd);
        }
    }

    /// <summary>
    /// Creates an opaque response-page token bound to one immutable cached snapshot.
    /// Native traversal is never resumed by this token.
    /// </summary>
    public string CreateContinuation(Snapshot snapshot, int nextIndex)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (nextIndex < 0 || nextIndex > snapshot.Elements.Count)
            throw new ArgumentOutOfRangeException(nameof(nextIndex));
        if (string.IsNullOrWhiteSpace(snapshot.ObservationId)
            || snapshot.TargetReference is not { } reference
            || !reference.IsValid)
            throw new InvalidOperationException("A continuation requires a lifetime-bound observation.");

        lock (_continuationGate)
        {
            if (!_byWindow.TryGetValue(snapshot.Hwnd, out var cached)
                || !ReferenceEquals(cached, snapshot)
                || !_registry.IsCurrent(reference)
                || !_isWindow(snapshot.Hwnd))
                throw new InvalidOperationException("The cached observation is no longer current.");

            var existing = _continuations.FirstOrDefault(pair =>
                ReferenceEquals(pair.Value.Snapshot, snapshot)
                && pair.Value.NextIndex == nextIndex);
            if (!string.IsNullOrEmpty(existing.Key))
                return existing.Key;

            string token;
            do
            {
                token = $"cont-{Guid.NewGuid():N}";
            }
            while (_continuations.ContainsKey(token));

            _continuations[token] = new ContinuationEntry(
                snapshot,
                nextIndex,
                snapshot.ObservationId,
                reference.SessionId,
                reference.TargetId,
                reference.LifetimeEpoch);
            return token;
        }
    }

    /// <summary>Returns a cached response cursor only while its exact target lifetime remains current.</summary>
    public bool TryGetContinuation(
        string token,
        out Snapshot snapshot,
        out int nextIndex)
    {
        snapshot = null!;
        nextIndex = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        lock (_continuationGate)
        {
            if (!_continuations.TryGetValue(token, out var continuation))
                return false;

            var candidate = continuation.Snapshot;
            var reference = candidate.TargetReference;
            bool valid = reference is { } currentReference
                && string.Equals(continuation.ObservationId, candidate.ObservationId, StringComparison.Ordinal)
                && string.Equals(continuation.SessionId, currentReference.SessionId, StringComparison.Ordinal)
                && string.Equals(continuation.TargetId, currentReference.TargetId, StringComparison.Ordinal)
                && continuation.LifetimeEpoch == currentReference.LifetimeEpoch
                && _byWindow.TryGetValue(candidate.Hwnd, out var cached)
                && ReferenceEquals(cached, candidate)
                && _registry.IsCurrent(currentReference)
                && _isWindow(candidate.Hwnd)
                && continuation.NextIndex >= 0
                && continuation.NextIndex <= candidate.Elements.Count;
            if (!valid)
            {
                _continuations.Remove(token);
                return false;
            }

            snapshot = candidate;
            nextIndex = continuation.NextIndex;
            return true;
        }
    }

    private void RemoveContinuationsNoLock(Snapshot snapshot)
    {
        foreach (var token in _continuations
                     .Where(pair => ReferenceEquals(pair.Value.Snapshot, snapshot))
                     .Select(pair => pair.Key)
                     .ToArray())
            _continuations.Remove(token);
    }

    public Snapshot? GetForTarget(AppTarget target)
    {
        if (target.TargetReference is not { } targetReference
            || !_registry.IsCurrent(targetReference)
            || !_isWindow(target.Hwnd)
            || !_byWindow.TryGetValue(target.Hwnd, out var snapshot)
            || snapshot.Pid != target.Pid
            || snapshot.TargetReference is not { } snapshotReference
            || snapshotReference != targetReference
            || !_registry.IsCurrent(snapshotReference))
            return null;

        return snapshot;
    }

    public Snapshot? GetForElement(AppTarget target, string elementId)
    {
        var targetSnapshot = GetForTarget(target);
        if (targetSnapshot != null)
        {
            if (targetSnapshot.Elements.Any(element => element.Id == elementId))
                return targetSnapshot;
            return FindInWindowHistory(target, elementId);
        }

        // Only a legacy process-name selection may retain the historical transient
        // replacement behaviour. Explicit window/target IDs and PID/title selectors must
        // not use an element from an arbitrary same-PID sibling.
        if (target.SelectionKind != AppSelectionKind.LegacyProcessName
            || target.TargetReference is not { } targetReference
            || !_registry.IsCurrent(targetReference)
            || !_isWindow(target.Hwnd))
            return null;

        var matches = _byWindow.Values
            .Where(snapshot =>
                snapshot.Pid == target.Pid
                && snapshot.TargetReference is { } snapshotReference
                && snapshotReference.ProcessCreationToken == targetReference.ProcessCreationToken
                && _registry.IsCurrent(snapshotReference)
                && _isWindow(snapshot.Hwnd)
                && _isOwnerChainRelated(target.Hwnd, snapshot.Hwnd)
                && snapshot.Elements.Any(element => element.Id == elementId))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    // Same live window, same lifetime, older content: this recovers the id's identifying
    // properties for live re-resolution, it never substitutes for that re-resolution.
    private Snapshot? FindInWindowHistory(AppTarget target, string elementId)
    {
        if (target.TargetReference is not { } targetReference || !_registry.IsCurrent(targetReference))
            return null;
        lock (_continuationGate)
        {
            if (!_historyByWindow.TryGetValue(target.Hwnd, out var history))
                return null;
            for (var i = history.Count - 1; i >= 0; i--)
            {
                var candidate = history[i];
                if (candidate.Pid == target.Pid
                    && candidate.TargetReference is { } candidateReference
                    && candidateReference == targetReference
                    && candidate.Elements.Any(element => element.Id == elementId))
                    return candidate;
            }
            return null;
        }
    }

    private static bool IsOwnerChainRelated(IntPtr first, IntPtr second)
        => IsOwnerInChain(first, second) || IsOwnerInChain(second, first);

    private static bool IsOwnerInChain(IntPtr hwnd, IntPtr expectedOwner)
    {
        var visited = new HashSet<IntPtr>();
        var current = hwnd;
        while (current != IntPtr.Zero && visited.Add(current))
        {
            current = NativeMethods.GetWindow(current, GetWindowOwnerCommand);
            if (current == expectedOwner)
                return true;
        }
        return false;
    }
}

/// <summary>
/// UI Automation engine: app resolution, accessibility tree snapshots with
/// Set-of-Marks labeling, and element re-resolution. All COM calls run on <see cref="UiaThread"/>.
/// </summary>
public sealed class UiaService : IDisposable
{
    public const string TargetIdPrefix = "dcu-target-";

    // Resolution is read-only, so a slow provider must produce an explicit reobserve
    // result rather than leaving a caller waiting behind an unbounded candidate scan.
    private const int MaxResolutionCandidates = 4096;
    private static readonly TimeSpan ElementResolutionTimeout = TimeSpan.FromSeconds(2);

    private readonly UiaThread _uia;
    private readonly TargetRegistry _registry;
    private readonly NativeTargetMonitor _monitor;
    private readonly bool _ownsMonitor;
    private readonly SnapshotCache _snapshots;
    private readonly Func<int, IntPtr, NativeTargetIdentity?> _nativeIdentityReader;
    private int _revision;
    private int _elementCounter;

    // Element ids stay stable (same "eN" label) for the same RuntimeId across consecutive
    // snapshots of one window, so an id read from one snapshot remains valid for a later
    // action even after another action on the same window forces a fresh snapshot in between.
    private readonly ConcurrentDictionary<IntPtr, ConcurrentDictionary<string, string>> _stableElementIds = new();

    public UiaService(UiaThread uia)
        : this(uia, new TargetRegistry(), monitor: null)
    {
    }

    public UiaService(
        UiaThread uia,
        TargetRegistry registry,
        NativeTargetMonitor? monitor = null,
        Func<int, IntPtr, NativeTargetIdentity?>? nativeIdentityReader = null)
    {
        _uia = uia ?? throw new ArgumentNullException(nameof(uia));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _monitor = monitor ?? new NativeTargetMonitor(_registry);
        _ownsMonitor = monitor == null;
        _nativeIdentityReader = nativeIdentityReader ?? ReadNativeTargetIdentity;
        _snapshots = new SnapshotCache(_registry);
    }

    public static string FormatTargetId(string targetId)
        => targetId.StartsWith(TargetIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? targetId
            : TargetIdPrefix + targetId;

    /// <summary>
    /// Reads the process creation time from the operating system's process handle through
    /// <see cref="Process.StartTime"/>. A missing or inaccessible value is unknown and is
    /// never converted into a PID-only lifetime identity.
    /// </summary>
    public static bool TryGetProcessCreationToken(int pid, out long token)
    {
        token = 0;
        if (pid <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid);
            var startTime = process.StartTime.ToUniversalTime();
            token = startTime.ToFileTimeUtc();
            return token > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IUIAutomation CreateUia()
    {
        try { return new CUIAutomation8(); }
        catch { return new CUIAutomation(); }
    }

    // ---------- App resolution ----------

    public Task<AppTarget[]> ListAppsAsync(CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var list = new List<AppTarget>();
            var processNames = new Dictionary<int, string>();
            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.DwmGetWindowAttribute(
                        hwnd,
                        NativeMethods.DWMWA_CLOAKED,
                        out int cloaked,
                        sizeof(int)) == 0
                    && cloaked != 0)
                    return true;
                var title = NativeMethods.GetWindowTextString(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;
                NativeMethods.GetWindowThreadProcessId(hwnd, out var rawPid);
                int pid = unchecked((int)rawPid);
                if (pid <= 0) return true;
                try
                {
                    if (!processNames.TryGetValue(pid, out var processName))
                    {
                        using var process = Process.GetProcessById(pid);
                        processName = process.ProcessName;
                        processNames[pid] = processName;
                    }

                    list.Add(ObserveTarget(new AppTarget
                    {
                        Pid = pid,
                        ProcessName = processName,
                        Hwnd = hwnd,
                        Title = title,
                    }));
                }
                catch { /* process exited or is inaccessible */ }
                return true;
            }, IntPtr.Zero);
            // Visibility filtering is not lifetime evidence. This only drops cached rows
            // whose HWND is actually invalid; the WinEvent monitor owns retirement.
            _snapshots.RemoveInvalidWindows(NativeMethods.IsWindow);
            foreach (var hwnd in _stableElementIds.Keys)
                if (!NativeMethods.IsWindow(hwnd))
                    _stableElementIds.TryRemove(hwnd, out _);
            return list.ToArray();
        }, ct);

    public async Task<AppTarget> ResolveAppAsync(string app, CancellationToken ct = default)
    {
        var apps = await ListAppsAsync(ct).ConfigureAwait(false);
        var target = SelectApp(apps, app);
        target.SelectionKind = GetSelectionKind(app, target);
        return target;
    }

    private AppTarget ObserveTarget(AppTarget target)
    {
        if (!TryGetProcessCreationToken(target.Pid, out var creationToken))
        {
            target.NativeIdentityError =
                $"Process creation identity for PID {target.Pid} is unavailable; the target cannot be trusted until it is re-observed.";
            return target;
        }

        var identity = new NativeTargetIdentity(target.Pid, creationToken, target.Hwnd);
        if (!_registry.TryRegister(identity, out var reference))
        {
            target.NativeIdentityError =
                $"Native target identity for {target.WindowId} could not be registered.";
            return target;
        }

        target.TargetReference = reference;
        if (!_monitor.TryTrack(reference, out var trackingError))
        {
            // A registry entry without an active lifecycle monitor must not become a
            // trusted cache binding. Leave the target visible for diagnostics, but fail
            // closed until a later observation can register and track it successfully.
            _registry.Invalidate(reference);
            target.TargetReference = null;
            target.NativeIdentityError = trackingError;
            return target;
        }

        // TryTrack performs native registration and availability checks. Re-read the
        // identity after it returns so a replacement during tracking is never published
        // as a trusted target reference.
        var postTrackIdentity = _nativeIdentityReader(target.Pid, target.Hwnd);
        if (postTrackIdentity is not { } freshIdentity
            || !freshIdentity.IsValid
            || !_registry.IsCurrent(reference, freshIdentity))
        {
            _monitor.Untrack(reference);
            _registry.Invalidate(reference);
            target.TargetReference = null;
            target.NativeIdentityError =
                $"Native target identity for {target.WindowId} changed during lifetime tracking; re-observe the target.";
        }
        return target;
    }

    private static AppSelectionKind GetSelectionKind(string selector, AppTarget target)
    {
        if (TryParseWindowId(selector, out _, out _))
            return AppSelectionKind.ExplicitWindowId;
        if (LooksLikeTargetId(selector))
            return AppSelectionKind.ExplicitTargetId;
        if (int.TryParse(selector, out _))
            return AppSelectionKind.ProcessId;

        var withoutExe = selector.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? selector[..^4]
            : selector;
        if (target.ProcessName.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || target.ProcessName.Equals(withoutExe, StringComparison.OrdinalIgnoreCase))
            return AppSelectionKind.LegacyProcessName;
        return AppSelectionKind.Title;
    }

    private static AppTarget SelectApp(AppTarget[] apps, string app)
    {
        AppTarget? match;
        if (TryParseWindowId(app, out var windowPid, out var windowHandle))
        {
            // An explicitly addressed window must never fall through to title/process
            // matching when the HWND disappeared or its PID changed.
            match = apps.SingleOrDefault(a => a.Pid == windowPid && a.Hwnd == windowHandle);
            if (match == null)
                throw new InvalidOperationException($"Window selector '{app}' was not found. Re-run list_apps.");
            return match;
        }

        if (HasTargetIdPrefix(app))
        {
            // The prefix reserves target_id selectors without stealing ordinary titles
            // that happen to be 32 hexadecimal characters. Any prefixed value that is
            // malformed or retired is explicit input and must fail closed.
            if (!LooksLikeTargetId(app))
                throw new InvalidOperationException(
                    $"Target selector '{app}' is malformed or stale. Use a current target_id from list_apps.");

            match = apps.SingleOrDefault(a => string.Equals(a.TargetId, app, StringComparison.Ordinal));
            if (match == null)
                throw new InvalidOperationException(
                    $"Target selector '{app}' is stale or was not found. Re-run list_apps.");
            return match;
        }

        match = int.TryParse(app, out var pid)
            ? SelectSingle(apps.Where(a => a.Pid == pid))
            : null;
        var appNoExeSuffix = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        if (match == null)
        {
            match = SelectSingle(apps.Where(a =>
                        a.ProcessName.Equals(app, StringComparison.OrdinalIgnoreCase)
                     || a.ProcessName.Equals(appNoExeSuffix, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.Title.Equals(app, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.Title.Contains(app, StringComparison.OrdinalIgnoreCase)))
                ?? SelectSingle(apps.Where(a => a.ProcessName.Contains(app, StringComparison.OrdinalIgnoreCase)));
        }
        if (match == null)
            throw new InvalidOperationException($"App not found: '{app}'. Running apps: {string.Join(", ", apps.Take(20).Select(a => $"{a.ProcessName} ({a.Pid})"))}");
        return match;

        AppTarget? SelectSingle(IEnumerable<AppTarget> candidates)
        {
            var matches = candidates.ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"App selector '{app}' is ambiguous. Use window_id or target_id: {string.Join(", ", matches.Select(a => $"{a.Title} (PID {a.Pid}, window_id {a.WindowId}, target_id {a.TargetId ?? "unavailable"})"))}");
            return matches.SingleOrDefault();
        }
    }

    private static bool HasTargetIdPrefix(string selector)
        => selector.StartsWith(TargetIdPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeTargetId(string selector)
        => HasTargetIdPrefix(selector)
            && selector.Length == TargetIdPrefix.Length + 32
            && selector[TargetIdPrefix.Length..].All(Uri.IsHexDigit);

    private static bool TryParseWindowId(string selector, out int pid, out IntPtr hwnd)
    {
        pid = 0;
        hwnd = IntPtr.Zero;
        int separator = selector.IndexOf(":0x", StringComparison.OrdinalIgnoreCase);
        if (separator <= 0
            || !int.TryParse(selector[..separator], out pid)
            || !long.TryParse(
                selector[(separator + 3)..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var rawHwnd))
            return false;
        hwnd = (IntPtr)rawHwnd;
        return true;
    }

    // ---------- Snapshot ----------

    // Ids must match ControlTypeName below exactly (they previously drifted, silently
    // swapping in the wrong control types — e.g. Image instead of Hyperlink).
    private static readonly HashSet<int> InteractiveTypes =
    [
        50000, // Button
        50001, // Calendar
        50002, // CheckBox
        50003, // ComboBox
        50004, // Edit
        50005, // Hyperlink
        50007, // ListItem
        50009, // Menu
        50010, // MenuBar
        50011, // MenuItem
        50012, // ProgressBar
        50013, // RadioButton
        50015, // Slider
        50016, // Spinner
        50018, // Tab
        50019, // TabItem
        50023, // Tree
        50024, // TreeItem
        50025, // Custom
        50026, // Group
        50028, // DataGrid
        50029, // DataItem
        50030, // Document
        50031, // SplitButton
        50033, // Pane
    ];

    public Task<Snapshot> SnapshotAsync(AppTarget app, int maxNodes = 1200, int maxDepth = 48, int textLimit = 500, CancellationToken ct = default)
    {
        EnsureTargetCurrent(app);
        return _uia.InvokeAsync(() =>
        {
            EnsureTargetCurrent(app);
            var uia = CreateUia();
            IUIAutomationElement root = uia.ElementFromHandle(app.Hwnd);
            var revision = Interlocked.Increment(ref _revision);
            NativeMethods.GetWindowRect(app.Hwnd, out var bounds);

            var snap = new Snapshot
            {
                Revision = revision,
                App = app.ProcessName,
                Pid = app.Pid,
                Hwnd = app.Hwnd,
                Title = app.Title,
                Bounds = bounds,
                TargetReference = app.TargetReference,
                ObservationId = $"obs-{Guid.NewGuid():N}",
            };

            var seen = new HashSet<string>();
            var sb = new System.Text.StringBuilder();
            int nodes = 0;
            bool nodeTruncated = false;
            bool depthTruncated = false;
            bool propertyErrors = false;

            // Control-view walk with per-field fault isolation (one broken element never kills the snapshot).
            void Walk(IUIAutomationElement el, int depth)
            {
                if (nodes >= maxNodes)
                {
                    nodeTruncated = true;
                    return;
                }
                if (depth > maxDepth)
                {
                    depthTruncated = true;
                    return;
                }

                nodes++;
                int[] runtimeId;
                try { runtimeId = (int[])el.GetRuntimeId(); }
                catch
                {
                    propertyErrors = true;
                    return;
                }
                if (runtimeId.Length == 0)
                {
                    propertyErrors = true;
                    return;
                }
                string key = string.Join(",", runtimeId);
                if (!seen.Add(key)) return;

                int controlType = 0;
                if (!TryRead(() => el.CurrentControlType, out controlType))
                    propertyErrors = true;
                string name = "";
                if (!TryRead(() => el.CurrentName, out string? rawName))
                    propertyErrors = true;
                else
                    name = rawName ?? "";
                string automationId = "";
                if (!TryRead(() => el.CurrentAutomationId, out string? rawAutomationId))
                    propertyErrors = true;
                else
                    automationId = rawAutomationId ?? "";
                string className = "";
                if (!TryRead(() => el.CurrentClassName, out string? rawClassName))
                    propertyErrors = true;
                else
                    className = rawClassName ?? "";

                int offscreenValue = 0;
                if (!TryRead(() => el.CurrentIsOffscreen, out offscreenValue))
                {
                    propertyErrors = true;
                    return;
                }
                bool offscreen = offscreenValue == 1;
                if (!TryRead(() => el.CurrentBoundingRectangle, out tagRECT r))
                {
                    propertyErrors = true;
                    return;
                }

                var patternResult = ReadPatternNames(el);
                if (patternResult.Error)
                    propertyErrors = true;
                var actions = patternResult.Names;
                bool interactive = InteractiveTypes.Contains(controlType)
                    || actions.Any(action => action is "Invoke" or "Toggle" or "Select" or "ExpandCollapse");
                // informative text (article paragraphs, labels) — readable content, not clickable
                bool informative = controlType == 50020 && name.Length >= 30;

                int nativeWindowHandle = 0;
                if (!TryRead(() => (int)el.CurrentNativeWindowHandle, out nativeWindowHandle))
                    propertyErrors = true;
                var valueResult = ReadValue(el);
                if (valueResult.Error)
                    propertyErrors = true;
                var readOnlyResult = ReadReadOnly(el);
                if (readOnlyResult.Error)
                    propertyErrors = true;
                _ = TryRead(() => el.CurrentAcceleratorKey, out string acceleratorKey);
                _ = TryRead(() => el.CurrentAccessKey, out string accessKey);
                bool? enabled = null;
                if (TryRead(() => el.CurrentIsEnabled, out int enabledValue))
                    enabled = enabledValue != 0;
                else
                    propertyErrors = true;

                if ((interactive || informative) && !offscreen && (r.right - r.left) > 0 && (r.bottom - r.top) > 0)
                {
                    var supportedOperations = new List<string>();
                    if (interactive)
                        supportedOperations.Add("click");
                    if (actions.Contains("Value", StringComparer.Ordinal)
                        || actions.Contains("Toggle", StringComparer.Ordinal))
                        supportedOperations.Add("set_value");
                    if (actions.Contains("Scroll", StringComparer.Ordinal)
                        || actions.Contains("ScrollIntoView", StringComparer.Ordinal))
                        supportedOperations.Add("scroll");

                    var idsForWindow = _stableElementIds.GetOrAdd(app.Hwnd, static _ => new ConcurrentDictionary<string, string>());
                    var id = idsForWindow.GetOrAdd(key, _ => "e" + Interlocked.Increment(ref _elementCounter));
                    var info = new ElementInfo
                    {
                        Id = id,
                        RuntimeId = runtimeId,
                        AutomationId = automationId,
                        Name = name,
                        ControlType = ControlTypeName(controlType),
                        ClassName = className,
                        Value = valueResult.Value,
                        AcceleratorKey = acceleratorKey ?? "",
                        AccessKey = accessKey ?? "",
                        NativeWindowHandle = nativeWindowHandle,
                        X = r.left - bounds.Left,
                        Y = r.top - bounds.Top,
                        Width = r.right - r.left,
                        Height = r.bottom - r.top,
                        ScreenX = r.left,
                        ScreenY = r.top,
                        Actions = actions,
                        Enabled = enabled,
                        ReadOnly = readOnlyResult.Value,
                        SupportedOperations = supportedOperations.ToArray(),
                        ActionableNow = enabled,
                    };
                    snap.Elements.Add(info);
                    sb.AppendLine($"[{id}] {info.ControlType} \"{Truncate(name, 60)}\" @({info.X},{info.Y} {info.Width}x{info.Height})" +
                        (info.Actions.Length > 0 ? $" actions:{string.Join("|", info.Actions)}" : "") +
                        (!string.IsNullOrEmpty(info.Value) ? $" value:\"{Truncate(info.Value, 80)}\"" : ""));
                }

                IUIAutomationElementArray? children;
                try { children = el.FindAll(TreeScope.TreeScope_Children, uia.CreateTrueCondition()); }
                catch
                {
                    propertyErrors = true;
                    return;
                }
                if (children == null)
                {
                    propertyErrors = true;
                    return;
                }

                int count;
                try { count = children.Length; }
                catch
                {
                    propertyErrors = true;
                    return;
                }
                for (int i = 0; i < count; i++)
                {
                    if (nodes >= maxNodes)
                    {
                        nodeTruncated = true;
                        break;
                    }
                    try { Walk(children.GetElement(i), depth + 1); }
                    catch { propertyErrors = true; }
                }
            }

            Walk(root, 0);
            snap.TreeText = sb.ToString();
            bool treeTextTruncated = false;
            if (snap.TreeText.Length > textLimit * 40)
            {
                snap.TreeText = snap.TreeText[..(textLimit * 40)] + "\n... (truncated)";
                treeTextTruncated = true;
            }
            snap.NativeCompleteness = new NativeSnapshotCompleteness
            {
                NodeTruncated = nodeTruncated,
                DepthTruncated = depthTruncated,
                TreeTextTruncated = treeTextTruncated,
                PropertyErrors = propertyErrors,
                CollectedElements = snap.Elements.Count,
                MaxNodes = maxNodes,
                MaxDepth = maxDepth,
                TextLimit = textLimit,
                ProjectionComplete = !nodeTruncated && !depthTruncated && !propertyErrors,
            };
            // Do not cache a tree if the process/window lifetime changed while UIA was
            // walking it. The registry comparison is exact and fails closed.
            EnsureTargetCurrent(app);
            // Store by HWND so multiple independent top-level windows from one process
            // retain separate element-id namespaces and snapshots.
            _snapshots.Store(snap);
            return snap;
        }, ct);
    }

    /// <summary>Checks the current HWND and process birth against the authoritative reference.</summary>
    public bool TryValidateTarget(AppTarget target, out string error)
    {
        error = "";
        if (target.TargetReference is not { } reference || !reference.IsValid)
        {
            error = target.NativeIdentityError
                ?? "Native target identity is unavailable. Re-run list_apps and use a current target.";
            return false;
        }

        if (!NativeMethods.IsWindow(target.Hwnd))
        {
            error = $"Target window {target.WindowId} no longer exists. Re-run list_apps.";
            return false;
        }

        var currentIdentity = _nativeIdentityReader(target.Pid, target.Hwnd);
        if (currentIdentity is not { } observedIdentity || !observedIdentity.IsValid)
        {
            error = $"Process creation identity for PID {target.Pid} is unavailable; re-observe the target.";
            return false;
        }

        if (!_registry.IsCurrent(reference, observedIdentity))
        {
            error = $"Target {target.WindowId} is stale because its process or window lifetime changed. Re-run get_app_state.";
            return false;
        }

        if (!_monitor.TryTrack(reference, out var trackingError))
        {
            error = trackingError
                ?? "Native target lifetime monitoring is unavailable; re-observe the target.";
            return false;
        }

        // Tracking performs native registration and has its own availability checks. A
        // process/HWND replacement can occur during that work, so never reuse the sample
        // taken before TryTrack for the final dispatch decision.
        var postTrackIdentity = _nativeIdentityReader(target.Pid, target.Hwnd);
        if (postTrackIdentity is not { } freshIdentity || !freshIdentity.IsValid)
        {
            error = $"Process creation identity for PID {target.Pid} became unavailable after native lifetime tracking; re-observe the target.";
            return false;
        }

        if (!_registry.IsCurrent(reference, freshIdentity))
        {
            error = $"Target {target.WindowId} became stale during native lifetime tracking. Re-run get_app_state.";
            return false;
        }

        // The monitor can quarantine after TryTrack while the post-track identity is read.
        // Confirm health again immediately before allowing a caller to dispatch input.
        if (!_monitor.TryConfirmTracking(reference, out var monitorError))
        {
            error = monitorError
                ?? "Native target lifetime monitoring failed; re-observe the target.";
            return false;
        }

        return true;
    }

    /// <summary>Validates the original owner of a cached element snapshot.</summary>
    public bool TryValidateSnapshot(Snapshot snapshot, out string error)
    {
        if (snapshot.TargetReference is not { } reference)
        {
            error = "The cached snapshot has no native lifetime identity. Re-run get_app_state.";
            return false;
        }

        var target = new AppTarget
        {
            Pid = snapshot.Pid,
            ProcessName = snapshot.App,
            Hwnd = snapshot.Hwnd,
            Title = snapshot.Title,
            TargetReference = reference,
        };
        return TryValidateTarget(target, out error);
    }

    /// <summary>Cached snapshot for the exact current resolved top-level window.</summary>
    public Snapshot? GetSnapshot(AppTarget target)
    {
        return _snapshots.GetForTarget(target);
    }

    /// <summary>Snapshot containing an element id for this target. Legacy process-name
    /// selection may retain a proven owner-chain replacement; explicit IDs never alias.</summary>
    public Snapshot? GetSnapshotForElement(AppTarget target, string elementId)
        => _snapshots.GetForElement(target, elementId);

    /// <summary>Creates a response-page token for the exact cached observation.</summary>
    public string CreateContinuationToken(Snapshot snapshot, int nextIndex)
        => _snapshots.CreateContinuation(snapshot, nextIndex);

    /// <summary>Resolves a response-page token without taking a new UIA snapshot.</summary>
    public bool TryGetContinuation(
        string token,
        out Snapshot snapshot,
        out int nextIndex)
        => _snapshots.TryGetContinuation(token, out snapshot, out nextIndex);

    private void EnsureTargetCurrent(AppTarget target)
    {
        if (!TryValidateTarget(target, out var error))
            throw new InvalidOperationException(error);
    }

    private static NativeTargetIdentity? ReadNativeTargetIdentity(int pid, IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd)
            || !TryGetProcessCreationToken(pid, out var creationToken))
            return null;

        var identity = new NativeTargetIdentity(pid, creationToken, hwnd);
        return identity.IsValid ? identity : null;
    }

    // ---------- Element resolution ----------

    /// <summary>
    /// Selects one candidate without touching COM. Matching is deliberately ordered from
    /// strongest to weakest evidence: an exact RuntimeId, a unique AutomationId/type pair,
    /// then a unique name/type pair. Ambiguous stronger evidence never falls through to a
    /// weaker selector, and an incomplete traversal never implies uniqueness.
    /// </summary>
    public static ElementResolutionSelection SelectElement(
        ElementInfo target,
        IReadOnlyList<ElementInfo> candidates,
        bool traversalComplete = true)
    {
        if (!traversalComplete)
            return new(ElementResolutionStatus.Incomplete);

        if (target.RuntimeId is { Length: > 0 })
        {
            var exact = SelectExactRuntimeId(target, candidates);
            if (exact.Status == ElementResolutionStatus.Ambiguous)
                return exact;
            if (exact.Status == ElementResolutionStatus.Found)
            {
                var candidate = candidates[exact.CandidateIndex];
                // A reused RuntimeId with a changed semantic type is not the same
                // trusted binding. Never repair it by selecting a similarly named control.
                if (!string.IsNullOrEmpty(target.ControlType)
                    && !string.Equals(candidate.ControlType, target.ControlType, StringComparison.Ordinal))
                    return new(ElementResolutionStatus.NotFound);
                return exact;
            }
        }

        // Fallback selectors require a control type. An AutomationId without its type
        // is not strong enough to identify a native control safely.
        if (string.IsNullOrEmpty(target.ControlType))
            return new(ElementResolutionStatus.NotFound);

        if (!string.IsNullOrEmpty(target.AutomationId))
        {
            var automationMatches = new List<int>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (string.Equals(candidate.AutomationId, target.AutomationId, StringComparison.Ordinal)
                    && string.Equals(candidate.ControlType, target.ControlType, StringComparison.Ordinal))
                    automationMatches.Add(i);
            }

            if (automationMatches.Count > 1)
                return new(ElementResolutionStatus.Ambiguous);
            if (automationMatches.Count == 1)
                return new(ElementResolutionStatus.Found, automationMatches[0]);
        }

        if (string.IsNullOrEmpty(target.Name))
            return new(ElementResolutionStatus.NotFound);

        var nameMatches = new List<int>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (string.Equals(candidate.Name, target.Name, StringComparison.Ordinal)
                && string.Equals(candidate.ControlType, target.ControlType, StringComparison.Ordinal))
                nameMatches.Add(i);
        }

        return nameMatches.Count switch
        {
            0 => new(ElementResolutionStatus.NotFound),
            1 => new(ElementResolutionStatus.Found, nameMatches[0]),
            _ => new(ElementResolutionStatus.Ambiguous),
        };
    }

    /// <summary>Resolves an element and preserves the reason when repair cannot be trusted.</summary>
    public async Task<IUIAutomationElement?> ResolveElementAsync(
        IntPtr hwnd,
        ElementInfo info,
        CancellationToken ct = default)
    {
        var resolution = await ResolveElementWithStatusAsync(hwnd, info, ct).ConfigureAwait(false);
        return resolution.Element;
    }

    /// <summary>F10: a LIVE (not cached-snapshot) read of an element's current UIA enabled state,
    /// run on the UIA thread like every other COM read here -- the snapshot's own Enabled value
    /// can be stale by the time a caller actually dispatches. Returns null (never a false refusal)
    /// when the property genuinely cannot be read; callers must only refuse dispatch on an
    /// explicit false, treating null the same as true (unknown is not a reason to block an
    /// action, matching this codebase's existing nullable-property honesty convention).</summary>
    public async Task<bool?> IsElementCurrentlyEnabledAsync(IUIAutomationElement element, CancellationToken ct = default)
    {
        try
        {
            return await _uia.InvokeAsync(
                () => TryRead(() => element.CurrentIsEnabled, out int enabledValue) ? (bool?)(enabledValue != 0) : null,
                ct).ConfigureAwait(false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    /// <summary>SetValue() parity with F10's enabled check: a LIVE read of the element's current
    /// ValuePattern.CurrentIsReadOnly, not the cached snapshot's ReadOnly field, which can be
    /// stale by dispatch time. No ValuePattern (null) or an unreadable property (null) is treated
    /// as not-read-only, matching the enabled check's honest nullable convention.</summary>
    public async Task<bool?> IsElementCurrentlyReadOnlyAsync(IUIAutomationElement element, CancellationToken ct = default)
    {
        try
        {
            return await _uia.InvokeAsync(() => ReadReadOnly(element).Value, ct).ConfigureAwait(false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public async Task<ElementResolutionResult> ResolveElementWithStatusAsync(
        IntPtr hwnd,
        ElementInfo info,
        CancellationToken ct = default)
    {
        try
        {
            return await _uia.InvokeAsync(() =>
            {
                var uia = CreateUia();
                IUIAutomationElement root;
                IUIAutomationElementArray? all;
                try
                {
                    root = uia.ElementFromHandle(hwnd);
                    if (root == null)
                        return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                    all = root.FindAll(TreeScope.TreeScope_Descendants, uia.CreateTrueCondition());
                    if (all == null)
                        return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                }
                catch
                {
                    return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                }

                int count;
                try { count = all.Length; }
                catch { return new ElementResolutionResult(ElementResolutionStatus.Incomplete); }

                // FindAll is a single provider call and cannot be terminated by this
                // cap. It bounds the candidate materialisation/property-read phase only;
                // UiaThread's timeout below returns Incomplete but cannot kill a blocked
                // in-process COM provider or restart the STA worker.
                if (count > MaxResolutionCandidates)
                    return new ElementResolutionResult(ElementResolutionStatus.Incomplete);

                var elements = new List<IUIAutomationElement>(count);
                var runtimeCandidates = new List<ElementInfo>(count);
                bool hasRuntimeId = info.RuntimeId is { Length: > 0 };
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    IUIAutomationElement element;
                    try { element = all.GetElement(i); }
                    catch { return new ElementResolutionResult(ElementResolutionStatus.Incomplete); }
                    if (element == null)
                        return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                    elements.Add(element);

                    // RuntimeId is the strongest binding. Do not read unrelated fallback
                    // properties until this tier has failed to find a candidate.
                    if (hasRuntimeId)
                    {
                        if (!TryReadRuntimeId(element, out var runtimeId))
                            return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                        runtimeCandidates.Add(new ElementInfo { Id = "", RuntimeId = runtimeId });
                    }
                }

                if (hasRuntimeId)
                {
                    var exact = SelectExactRuntimeId(info, runtimeCandidates);
                    if (exact.Status == ElementResolutionStatus.Ambiguous)
                        return new ElementResolutionResult(ElementResolutionStatus.Ambiguous);
                    if (exact.Status == ElementResolutionStatus.Found)
                    {
                        if (!string.IsNullOrEmpty(info.ControlType))
                        {
                            if (!TryReadControlType(elements[exact.CandidateIndex], out var currentType))
                                return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                            if (!string.Equals(currentType, info.ControlType, StringComparison.Ordinal))
                                return new ElementResolutionResult(ElementResolutionStatus.NotFound);
                        }
                        return new ElementResolutionResult(
                            ElementResolutionStatus.Found,
                            elements[exact.CandidateIndex]);
                    }
                }

                // No exact RuntimeId was found. Fallback matching needs every candidate's
                // AutomationId, name and control type; any failed read invalidates the tier.
                var candidates = new List<ElementInfo>(count);
                for (int i = 0; i < elements.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!TryReadFallbackInfo(elements[i], out var candidate))
                        return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
                    if (hasRuntimeId)
                        candidate.RuntimeId = runtimeCandidates[i].RuntimeId;
                    candidates.Add(candidate);
                }

                var selection = SelectElement(info, candidates, traversalComplete: true);
                if (selection.Status != ElementResolutionStatus.Found
                    || selection.CandidateIndex < 0
                    || selection.CandidateIndex >= elements.Count)
                    return new ElementResolutionResult(selection.Status);
                return new ElementResolutionResult(
                    ElementResolutionStatus.Found,
                    elements[selection.CandidateIndex]);
            }, ct, ElementResolutionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Timeout is a read-only resolution failure. Preserve caller cancellation
            // when it won the race; otherwise make the required reobserve explicit.
            ct.ThrowIfCancellationRequested();
            return new ElementResolutionResult(ElementResolutionStatus.Incomplete);
        }
    }

    private static ElementResolutionSelection SelectExactRuntimeId(
        ElementInfo target,
        IReadOnlyList<ElementInfo> candidates)
    {
        if (target.RuntimeId is not { Length: > 0 })
            return new(ElementResolutionStatus.NotFound);

        int match = -1;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].RuntimeId is not { Length: > 0 }
                || !candidates[i].RuntimeId.SequenceEqual(target.RuntimeId))
                continue;
            if (match >= 0)
                return new(ElementResolutionStatus.Ambiguous);
            match = i;
        }
        return match >= 0
            ? new(ElementResolutionStatus.Found, match)
            : new(ElementResolutionStatus.NotFound);
    }

    private static bool TryReadRuntimeId(
        IUIAutomationElement element,
        out int[] runtimeId)
    {
        runtimeId = [];
        try
        {
            runtimeId = (int[])element.GetRuntimeId();
            return runtimeId.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadControlType(
        IUIAutomationElement element,
        out string controlType)
    {
        controlType = "";
        try
        {
            controlType = ControlTypeName(element.CurrentControlType);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadFallbackInfo(
        IUIAutomationElement element,
        out ElementInfo info)
    {
        info = null!;
        try
        {
            info = new ElementInfo
            {
                Id = "",
                AutomationId = element.CurrentAutomationId ?? "",
                Name = element.CurrentName ?? "",
                ControlType = ControlTypeName(element.CurrentControlType),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<ElementFrame?> GetElementFrameAsync(
        IUIAutomationElement element,
        CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            try
            {
                var rectangle = element.CurrentBoundingRectangle;
                int width = rectangle.right - rectangle.left;
                int height = rectangle.bottom - rectangle.top;
                if (width <= 0 || height <= 0) return (ElementFrame?)null;
                return (ElementFrame?)new ElementFrame(
                    rectangle.left,
                    rectangle.top,
                    width,
                    height,
                    element.CurrentNativeWindowHandle.ToInt32());
            }
            catch
            {
                return (ElementFrame?)null;
            }
        }, ct);

    public Task<IUIAutomationElement?> FindByTextAsync(IntPtr hwnd, string text, CancellationToken ct = default)
        => _uia.InvokeAsync(() =>
        {
            var uia = CreateUia();
            var root = uia.ElementFromHandle(hwnd);
            var all = root.FindAll(TreeScope.TreeScope_Descendants, uia.CreateTrueCondition());
            IUIAutomationElement? partial = null;
            for (int i = 0; i < all.Length; i++)
            {
                var el = all.GetElement(i);
                string? name;
                try { name = el.CurrentName; } catch { continue; }
                // CurrentName is a COM property that can legitimately return null for some
                // elements (not just throw) -- treat that the same as "no name", never let it
                // reach .Equals/.Contains as a NullReferenceException.
                if (name is null) continue;
                if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) return el;
                if (partial == null && name.Contains(text, StringComparison.OrdinalIgnoreCase)) partial = el;
            }
            return partial;
        }, ct);

    // ---------- Pattern helpers ----------

    public static string[] GetPatternNames(IUIAutomationElement el)
        => ReadPatternNames(el).Names;

    private static (string[] Names, bool Error) ReadPatternNames(IUIAutomationElement el)
    {
        var names = new List<string>();
        bool error = false;
        void Has(int pid, string name)
        {
            try
            {
                if (el.GetCurrentPattern(pid) != null)
                    names.Add(name);
            }
            catch
            {
                error = true;
            }
        }
        Has(UiaIds.InvokePattern, "Invoke");
        Has(UiaIds.TogglePattern, "Toggle");
        Has(UiaIds.SelectionItemPattern, "Select");
        Has(UiaIds.ExpandCollapsePattern, "ExpandCollapse");
        Has(UiaIds.ScrollPattern, "Scroll");
        Has(UiaIds.ScrollItemPattern, "ScrollIntoView");
        Has(UiaIds.ValuePattern, "Value");
        return (names.ToArray(), error);
    }

    private static bool HasActionablePattern(IUIAutomationElement el)
        => ReadPatternNames(el).Names.Any(action => action is "Invoke" or "Toggle" or "Select" or "ExpandCollapse");

    public static string GetValue(IUIAutomation uia, IUIAutomationElement el)
        => ReadValue(el).Value;

    private static (string Value, bool Error) ReadValue(IUIAutomationElement el)
    {
        try
        {
            if (el.GetCurrentPattern(UiaIds.ValuePattern) is not IUIAutomationValuePattern valuePattern)
                return ("", false);
            try
            {
                return (valuePattern.CurrentValue ?? "", false);
            }
            catch
            {
                return ("", true);
            }
        }
        catch
        {
            return ("", true);
        }
    }

    private static (bool? Value, bool Error) ReadReadOnly(IUIAutomationElement el)
    {
        try
        {
            if (el.GetCurrentPattern(UiaIds.ValuePattern) is not IUIAutomationValuePattern valuePattern)
                return (null, false);
            if (!TryRead(() => valuePattern.CurrentIsReadOnly, out int readOnly))
                return (null, true);
            return (readOnly != 0, false);
        }
        catch
        {
            return (null, true);
        }
    }

    private static bool TryRead<T>(Func<T> reader, out T value)
    {
        try
        {
            value = reader();
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }

    public static string ControlTypeName(int id) => id switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        _ => $"Type{id}"
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose()
    {
        if (_ownsMonitor)
            _monitor.Dispose();
        GC.SuppressFinalize(this);
    }
}
