// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Security.Cryptography;
using System.Text;

namespace ShadowUse.Automation;

public sealed record ScreenUiAnchor(double X, double Y, double Width, double Height, string RegionHash,
    bool IsStatic = false, bool IsDynamic = true, int ConsecutiveMatches = 1,
    DateTimeOffset? LastVerifiedUtc = null, string? LayoutFingerprint = null);

public sealed record ScreenActionRecipe(string Key, string Role, string Label,
    IReadOnlyList<string>? KeyBindings = null, bool? Enabled = null, ScreenUiAnchor? Anchor = null);

public sealed record ScreenContextRegion(string Key, string Label, double X, double Y, double Width, double Height,
    string? Note = null, bool IsDynamic = true);

public sealed record ScreenStateCapture(
    string ObservationId,
    string Scope,
    bool CandidateSetComplete,
    string? CandidateSetIssue,
    IReadOnlyList<ScreenActionRecipe> Actions,
    IReadOnlyList<string>? StateClues = null,
    DateTimeOffset? CapturedAtUtc = null,
    string? LayoutFingerprint = null,
    IReadOnlyList<ScreenContextRegion>? ContextRegions = null);

/// <summary>A semantic screen record with optional hash-verified, normalized anchors for stable UI elements.</summary>
public sealed record ScreenStateRecord(
    string AppId,
    string ScreenId,
    string Signature,
    string? ScreenHint,
    string AppVersion,
    string Profile,
    string KeyboardLayout,
    string Scope,
    string LayoutFingerprint,
    bool CandidateSetComplete,
    string? CandidateSetIssue,
    int CandidateCount,
    IReadOnlyList<ScreenActionRecipe> Actions,
    IReadOnlyList<ScreenContextRegion> ContextRegions,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int ObservationCount,
    int ConsecutiveObservationCount);

/// <summary>Observed edge between semantic screens after an input was reported as sent.</summary>
public sealed record ScreenTransitionRecord(
    string AppId,
    string FromScreenId,
    string ToScreenId,
    string ActionId,
    string InputKind,
    string? Effect,
    string FromFrameId,
    string ToFrameId,
    DateTimeOffset ObservedAtUtc,
    string EvidenceStatus,
    string? Outcome = null,
    double? Confidence = null,
    int SuccessCount = 0,
    int FailureCount = 0);

public sealed record ScreenStateObservation(
    string ScreenId,
    string Signature,
    string? ScreenHint,
    bool Repeated,
    int ObservationCount,
    int ConsecutiveObservationCount,
    bool Stable,
    IReadOnlyList<ScreenTransitionRecord> IncomingTransitions,
    IReadOnlyList<ScreenActionRecipe> CachedActions,
    string Note);

/// <summary>
/// App-agnostic screen map with bounded local persistence. Cached anchors are normalized and
/// usable only after repeated stable observations and fresh-frame region-hash verification.
/// </summary>
public sealed class ScreenStateStore
{
    public const int MaxScreens = 256;
    public const int MaxTransitions = 1024;
    public const int MaxRecentActions = 8;
    private const int MaxPersistedScreens = 32;
    private const int MaxPersistedTransitions = 512;
    private const int MaxFrameBindings = 1024;
    private static readonly TimeSpan PendingInputLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StabilityObservationWindow = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly string? _persistencePath;
    private string? _persistenceError;
    private readonly Dictionary<string, ScreenStateRecord> _screens = new(StringComparer.Ordinal);
    private readonly Queue<string> _screenOrder = new();
    private readonly Dictionary<string, ScreenTransitionRecord> _transitions = new(StringComparer.Ordinal);
    private readonly Queue<string> _transitionOrder = new();
    private readonly Dictionary<string, string> _frameToScreen = new(StringComparer.Ordinal);
    private readonly Queue<string> _frameOrder = new();
    private readonly Dictionary<string, string> _latestFrameByApp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _latestScreenByApp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _latestScreenObservedAtByApp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<PendingInput>> _pendingInputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<string>> _recentActions = new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingInput(string AppId, string FromScreenId, string ActionId, string InputKind,
        string? Effect, string FromFrameId, DateTimeOffset SentAtUtc);
    private sealed record PersistedState(int SchemaVersion, IReadOnlyList<ScreenStateRecord> Screens,
        IReadOnlyList<ScreenTransitionRecord> Transitions, IReadOnlyDictionary<string, IReadOnlyList<string>> RecentActions);

    public ScreenStateStore(TimeProvider? clock = null, string? persistencePath = null)
    {
        _clock = clock ?? TimeProvider.System;
        _persistencePath = persistencePath;
        Load();
    }

    public string? PersistenceError { get { lock (_sync) return _persistenceError; } }

    /// <summary>Stores semantic labels and evidence from this frame, then links any reported prior input to the new state.</summary>
    public ScreenStateObservation Observe(string appId, string appVersion, string profile, string keyboardLayout,
        string? screenHint, ScreenStateCapture observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("app_id is required.", nameof(appId));
        if (string.IsNullOrWhiteSpace(observation.ObservationId)) throw new ArgumentException("observation_id is required.", nameof(observation));
        string signature = ComputeSignature(appId, appVersion, profile, keyboardLayout, screenHint,
            observation.Scope, observation.Actions, observation.StateClues, observation.LayoutFingerprint);
        string screenId = "screen-" + signature[..16];
        string appKey = appId.Trim();
        var actions = observation.Actions.Take(255).ToArray();
        var now = _clock.GetUtcNow();
        var observedAt = observation.CapturedAtUtc ?? now;

        lock (_sync)
        {
            bool repeated = _screens.TryGetValue(screenId, out var existing);
            bool consecutive = repeated && _latestScreenByApp.TryGetValue(appKey, out var lastScreen)
                && lastScreen == screenId && _latestScreenObservedAtByApp.TryGetValue(appKey, out var lastAt)
                && observedAt >= lastAt && observedAt - lastAt <= StabilityObservationWindow;
            int consecutiveCount = consecutive ? existing!.ConsecutiveObservationCount + 1 : 1;
            if (_frameToScreen.TryGetValue(observation.ObservationId, out var seenScreen)
                && seenScreen == screenId && existing is not null)
            {
                _latestFrameByApp[appKey] = observation.ObservationId;
                return CreateResult(existing, repeated: true, appKey);
            }

            var record = repeated
                ? existing! with
                {
                    LastSeenUtc = now,
                    ObservationCount = existing!.ObservationCount + 1,
                    CandidateSetComplete = existing.CandidateSetComplete && observation.CandidateSetComplete,
                    CandidateSetIssue = CombineIssues(existing.CandidateSetIssue, observation.CandidateSetIssue),
                    LayoutFingerprint = Clean(observation.LayoutFingerprint, 240) ?? "unknown",
                    CandidateCount = actions.Length,
                    Actions = MergeAnchors(existing.Actions, actions, observedAt, observation.LayoutFingerprint),
                    ContextRegions = CleanContextRegions(observation.ContextRegions),
                    ConsecutiveObservationCount = consecutiveCount,
                }
                : new ScreenStateRecord(appKey, screenId, signature, Clean(screenHint, 200), Clean(appVersion, 120) ?? "unknown",
                    Clean(profile, 120) ?? "default", Clean(keyboardLayout, 120) ?? "unknown", Clean(observation.Scope, 500) ?? "visible UI-like regions",
                    Clean(observation.LayoutFingerprint, 240) ?? "unknown", observation.CandidateSetComplete,
                    Clean(observation.CandidateSetIssue, 500), actions.Length,
                    MergeAnchors([], actions, observedAt, observation.LayoutFingerprint),
                    CleanContextRegions(observation.ContextRegions), now, now, 1, 1);
            _screens[screenId] = record;
            if (!repeated) _screenOrder.Enqueue(screenId);
            _frameToScreen[observation.ObservationId] = screenId;
            _frameOrder.Enqueue(observation.ObservationId);
            while (_frameToScreen.Count > MaxFrameBindings && _frameOrder.TryDequeue(out var oldestFrame))
                _frameToScreen.Remove(oldestFrame);
            _latestFrameByApp[appKey] = observation.ObservationId;
            _latestScreenByApp[appKey] = screenId;
            _latestScreenObservedAtByApp[appKey] = observedAt;
            while (_screens.Count > MaxScreens && _screenOrder.TryDequeue(out var oldest))
            {
                if (_screens.Remove(oldest))
                {
                    foreach (var frame in _frameToScreen.Where(entry => entry.Value == oldest).Select(entry => entry.Key).ToArray())
                        _frameToScreen.Remove(frame);
                }
            }

            LinkPendingInputs(appKey, screenId, observation.ObservationId, now);
            Save();
            return CreateResult(record, repeated, appKey);
        }
    }

    /// <summary>Records an input report only when its source frame was observed for this app.</summary>
    public bool RecordInput(string appId, string? sourceFrameId, string actionId, string inputKind,
        bool inputSent, string? effect = null)
    {
        if (!inputSent || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(actionId)
            || string.IsNullOrWhiteSpace(inputKind)) return false;
        lock (_sync)
        {
            string appKey = appId.Trim();
            sourceFrameId = string.IsNullOrWhiteSpace(sourceFrameId)
                ? _latestFrameByApp.GetValueOrDefault(appKey)
                : sourceFrameId;
            if (sourceFrameId is null || !_frameToScreen.TryGetValue(sourceFrameId, out var screenId)
                || !_screens.TryGetValue(screenId, out var state)
                || !string.Equals(state.AppId, appKey, StringComparison.OrdinalIgnoreCase)) return false;
            if (!_pendingInputs.TryGetValue(appKey, out var pending))
                _pendingInputs[appKey] = pending = new Queue<PendingInput>();
            pending.Enqueue(new PendingInput(appKey, screenId, Clean(actionId, 200)!, Clean(inputKind, 40)!,
                Clean(effect, 120), sourceFrameId, _clock.GetUtcNow()));
            while (pending.Count > MaxRecentActions) pending.Dequeue();
            if (!_recentActions.TryGetValue(appKey, out var history))
                _recentActions[appKey] = history = new Queue<string>();
            history.Enqueue($"{actionId} via {inputKind}; input_sent=true; effect={effect ?? "unknown"}; success unverified");
            while (history.Count > MaxRecentActions) history.Dequeue();
            Save();
            return true;
        }
    }

    public bool ReportTransitionOutcome(string appId, string fromScreenId, string actionId, string toScreenId,
        bool success, double confidence, string? evidence)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(fromScreenId)
            || string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(toScreenId)
            || !double.IsFinite(confidence) || confidence is < 0 or > 1) return false;
        lock (_sync)
        {
            var entry = _transitions.Where(pair => string.Equals(pair.Value.AppId, appId, StringComparison.OrdinalIgnoreCase)
                    && pair.Value.FromScreenId == fromScreenId && pair.Value.ActionId == actionId && pair.Value.ToScreenId == toScreenId)
                .MaxBy(pair => pair.Value.ObservedAtUtc);
            if (entry.Value is null) return false;
            var current = entry.Value;
            _transitions[entry.Key] = current with
            {
                Outcome = success ? "success" : "failure",
                Confidence = confidence,
                EvidenceStatus = "caller_reported_outcome",
                Effect = Clean(evidence, 500) ?? current.Effect,
                SuccessCount = current.SuccessCount + (success ? 1 : 0),
                FailureCount = current.FailureCount + (success ? 0 : 1),
            };
            Save();
            return true;
        }
    }

    public IReadOnlyList<string> RecentActions(string appId)
    {
        lock (_sync)
            return _recentActions.TryGetValue(appId.Trim(), out var history) ? history.ToArray() : [];
    }

    public IReadOnlyList<ScreenTransitionRecord> TransitionsFor(string appId, string screenId)
    {
        lock (_sync)
            return _transitions.Values.Where(item =>
                    string.Equals(item.AppId, appId, StringComparison.OrdinalIgnoreCase)
                    && (item.FromScreenId == screenId || item.ToScreenId == screenId))
                .OrderByDescending(item => item.ObservedAtUtc).Take(16).ToArray();
    }

    public bool TryGet(string screenId, out ScreenStateRecord? state)
    {
        lock (_sync) return _screens.TryGetValue(screenId, out state);
    }

    public bool TryGetForFrame(string appId, string observationId, out ScreenStateRecord? state)
    {
        lock (_sync)
        {
            if (_frameToScreen.TryGetValue(observationId, out var screenId)
                && _screens.TryGetValue(screenId, out state)
                && string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase)) return true;
            state = null;
            return false;
        }
    }

    public IReadOnlyList<ScreenStateRecord> ScreensFor(string appId)
    {
        lock (_sync)
            return _screens.Values.Where(state => string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(state => state.LastSeenUtc).Take(32).ToArray();
    }

    public int ScreenCount { get { lock (_sync) return _screens.Count; } }
    public int TransitionCount { get { lock (_sync) return _transitions.Count; } }

    /// <summary>Binds a hash-recognized fresh frame to its existing screen record for later input/transition reporting.</summary>
    public bool BindFrame(string appId, string screenId, string observationId)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(screenId)
            || string.IsNullOrWhiteSpace(observationId)) return false;
        lock (_sync)
        {
            string appKey = appId.Trim();
            if (!_screens.TryGetValue(screenId, out var state)
                || !string.Equals(state.AppId, appKey, StringComparison.OrdinalIgnoreCase)) return false;
            if (_frameToScreen.TryGetValue(observationId, out var previousScreen) && previousScreen != screenId) return false;
            var now = _clock.GetUtcNow();
            bool consecutive = _latestScreenByApp.TryGetValue(appKey, out var lastScreen) && lastScreen == screenId
                && _latestScreenObservedAtByApp.TryGetValue(appKey, out var lastAt)
                && now >= lastAt && now - lastAt <= StabilityObservationWindow;
            _screens[screenId] = state with
            {
                LastSeenUtc = now,
                ObservationCount = state.ObservationCount + 1,
                ConsecutiveObservationCount = consecutive ? state.ConsecutiveObservationCount + 1 : 1,
            };
            if (!_frameToScreen.ContainsKey(observationId)) _frameOrder.Enqueue(observationId);
            _frameToScreen[observationId] = screenId;
            while (_frameToScreen.Count > MaxFrameBindings && _frameOrder.TryDequeue(out var oldestFrame))
                _frameToScreen.Remove(oldestFrame);
            _latestFrameByApp[appKey] = observationId;
            _latestScreenByApp[appKey] = screenId;
            _latestScreenObservedAtByApp[appKey] = now;
            LinkPendingInputs(appKey, screenId, observationId, now);
            Save();
            return true;
        }
    }

    /// <summary>Matches exactly one prior screen only when every promoted static anchor passes a caller's fresh-frame verifier.</summary>
    public bool TryMatchCachedScreen(string appId, string appVersion, string profile, string keyboardLayout,
        string layoutFingerprint, Func<ScreenUiAnchor, bool> verify, out ScreenStateRecord? matchedState,
        out IReadOnlyList<ScreenActionRecipe> matchedActions, string? screenHint = null)
    {
        ArgumentNullException.ThrowIfNull(verify);
        lock (_sync)
        {
            var matches = new List<(ScreenStateRecord State, IReadOnlyList<ScreenActionRecipe> Actions)>();
            bool changed = false;
            foreach (var state in _screens.Values.Where(state =>
                         string.Equals(state.AppId, appId, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(state.AppVersion, appVersion, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(state.Profile, profile, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(state.KeyboardLayout, keyboardLayout, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(state.LayoutFingerprint, layoutFingerprint, StringComparison.Ordinal)
                         && (string.IsNullOrWhiteSpace(screenHint)
                             || string.Equals(state.ScreenHint, screenHint, StringComparison.OrdinalIgnoreCase))).ToArray())
            {
                var candidates = state.Actions.Where(action => action.Anchor is { } anchor
                    && string.Equals(anchor.LayoutFingerprint, layoutFingerprint, StringComparison.Ordinal)).ToArray();
                if (candidates.Length < 2) continue;
                var verified = new Dictionary<string, bool>(StringComparer.Ordinal);
                var updatedActions = state.Actions.Select(action =>
                {
                    if (action.Anchor is not { } anchor
                        || !string.Equals(anchor.LayoutFingerprint, layoutFingerprint, StringComparison.Ordinal)) return action;
                    bool valid;
                    try { valid = verify(anchor); }
                    catch { valid = false; }
                    verified[action.Key] = valid;
                    int count = valid ? Math.Min(anchor.ConsecutiveMatches + 1, 1000) : 1;
                    return action with
                    {
                        Anchor = anchor with
                        {
                            IsStatic = valid && count >= 3,
                            IsDynamic = !(valid && count >= 3),
                            ConsecutiveMatches = count,
                            LastVerifiedUtc = valid ? _clock.GetUtcNow() : anchor.LastVerifiedUtc,
                        },
                    };
                }).ToArray();
                var updatedState = state with { Actions = updatedActions };
                _screens[state.ScreenId] = updatedState;
                changed = true;
                var stable = updatedActions.Where(action => action.Anchor is { IsStatic: true }
                    && verified.GetValueOrDefault(action.Key)).ToArray();
                if (stable.Length >= 2)
                    matches.Add((updatedState, stable));
            }
            if (changed) Save();
            if (matches.Count == 1)
            {
                matchedState = matches[0].State;
                matchedActions = matches[0].Actions;
                return true;
            }
        }
        matchedState = null;
        matchedActions = [];
        return false;
    }

    public bool Invalidate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        lock (_sync)
        {
            string key = appId.Trim();
            bool removed = false;
            var screenIds = _screens.Where(pair => string.Equals(pair.Value.AppId, key, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key).ToArray();
            foreach (string screenId in screenIds)
            {
                removed |= _screens.Remove(screenId);
                foreach (string frame in _frameToScreen.Where(pair => pair.Value == screenId).Select(pair => pair.Key).ToArray())
                    _frameToScreen.Remove(frame);
            }
            foreach (string transitionKey in _transitions.Where(pair => string.Equals(pair.Value.AppId, key, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key).ToArray())
                removed |= _transitions.Remove(transitionKey);
            _latestFrameByApp.Remove(key);
            _latestScreenByApp.Remove(key);
            _latestScreenObservedAtByApp.Remove(key);
            _pendingInputs.Remove(key);
            _recentActions.Remove(key);
            if (removed) Save();
            return removed;
        }
    }

    private void LinkPendingInputs(string appId, string screenId, string observationId, DateTimeOffset now)
    {
        if (!_pendingInputs.Remove(appId, out var pendingInputs)) return;
        while (pendingInputs.TryDequeue(out var pending))
        {
            if (pending.FromFrameId == observationId || now - pending.SentAtUtc > PendingInputLifetime) continue;
            var transition = new ScreenTransitionRecord(appId, pending.FromScreenId, screenId,
                pending.ActionId, pending.InputKind, pending.Effect, pending.FromFrameId, observationId, now,
                "caller_reported_input_and_following_frame; causality_and_success_unverified");
            string key = TransitionKey(transition);
            _transitions[key] = transition;
            _transitionOrder.Enqueue(key);
        }
        while (_transitions.Count > MaxTransitions && _transitionOrder.TryDequeue(out var oldest))
            _transitions.Remove(oldest);
    }

    private static IReadOnlyList<ScreenContextRegion> CleanContextRegions(IReadOnlyList<ScreenContextRegion>? regions)
        => (regions ?? []).Take(64).Where(region => region is not null
                && !string.IsNullOrWhiteSpace(region.Key) && !string.IsNullOrWhiteSpace(region.Label)
                && double.IsFinite(region.X) && double.IsFinite(region.Y) && double.IsFinite(region.Width)
                && double.IsFinite(region.Height) && region.X >= 0 && region.Y >= 0 && region.Width > 0
                && region.Height > 0 && region.X + region.Width <= 1.001 && region.Y + region.Height <= 1.001)
            .Select(region => region with
            {
                Key = Clean(region.Key, 120)!,
                Label = Clean(region.Label, 300)!,
                Note = region.IsDynamic ? null : Clean(region.Note, 300),
            }).ToArray();

    private static IReadOnlyList<ScreenActionRecipe> MergeAnchors(IReadOnlyList<ScreenActionRecipe> previous,
        IReadOnlyList<ScreenActionRecipe> current, DateTimeOffset observedAt, string? layoutFingerprint)
    {
        var priorByKey = previous.GroupBy(action => action.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        string layout = Clean(layoutFingerprint, 240) ?? "unknown";
        return current.Take(255).Select(action =>
        {
            if (action.Anchor is not { } anchor || !ValidAnchor(anchor)) return action with { Anchor = null };
            int matches = 1;
            bool isStatic = false;
            if (priorByKey.TryGetValue(action.Key, out var prior) && prior.Anchor is { } old
                && string.Equals(old.LayoutFingerprint, layout, StringComparison.Ordinal)
                && Close(old.X, anchor.X) && Close(old.Y, anchor.Y) && Close(old.Width, anchor.Width) && Close(old.Height, anchor.Height)
                && HashDistance(old.RegionHash, anchor.RegionHash) <= 8)
            {
                matches = Math.Min(old.ConsecutiveMatches + 1, 1000);
                isStatic = matches >= 3;
            }
            return action with
            {
                Anchor = anchor with
                {
                    RegionHash = anchor.RegionHash.ToLowerInvariant(),
                    IsStatic = isStatic,
                    IsDynamic = !isStatic,
                    ConsecutiveMatches = matches,
                    LastVerifiedUtc = observedAt,
                    LayoutFingerprint = layout,
                },
            };
        }).ToArray();
    }

    private static bool ValidAnchor(ScreenUiAnchor anchor)
        => double.IsFinite(anchor.X) && double.IsFinite(anchor.Y) && double.IsFinite(anchor.Width)
            && double.IsFinite(anchor.Height) && anchor.X >= 0 && anchor.Y >= 0
            && anchor.Width > 0 && anchor.Height > 0 && anchor.X + anchor.Width <= 1.001
            && anchor.Y + anchor.Height <= 1.001 && anchor.RegionHash is { Length: 16 }
            && anchor.RegionHash.All(Uri.IsHexDigit);

    private static bool Close(double a, double b) => Math.Abs(a - b) <= 0.01;

    private static int HashDistance(string left, string right)
        => ulong.TryParse(left, System.Globalization.NumberStyles.HexNumber, null, out ulong a)
            && ulong.TryParse(right, System.Globalization.NumberStyles.HexNumber, null, out ulong b)
            ? System.Numerics.BitOperations.PopCount(a ^ b) : int.MaxValue;

    private void Load()
    {
        var saved = LocalStateFile.Read<PersistedState>(_persistencePath);
        if (saved is null || saved.SchemaVersion != 2 || saved.Screens is null || saved.Transitions is null) return;
        lock (_sync)
        {
            foreach (var state in saved.Screens.Where(state => state is not null && IsValidState(state))
                         .OrderBy(state => state.LastSeenUtc).TakeLast(MaxPersistedScreens))
            {
                _screens[state.ScreenId] = state;
                _screenOrder.Enqueue(state.ScreenId);
            }
            foreach (var transition in saved.Transitions.Where(item => item is not null
                         && !string.IsNullOrWhiteSpace(item.AppId) && !string.IsNullOrWhiteSpace(item.FromScreenId)
                         && !string.IsNullOrWhiteSpace(item.ToScreenId) && !string.IsNullOrWhiteSpace(item.ActionId))
                         .OrderBy(item => item.ObservedAtUtc).TakeLast(MaxPersistedTransitions))
            {
                string key = TransitionKey(transition);
                _transitions[key] = transition;
                _transitionOrder.Enqueue(key);
            }
            if (saved.RecentActions is not null)
                foreach (var (appId, actions) in saved.RecentActions.Take(128))
                    if (!string.IsNullOrWhiteSpace(appId) && actions is not null)
                        _recentActions[appId] = new Queue<string>(actions.Where(action => !string.IsNullOrWhiteSpace(action))
                            .Select(action => action[..Math.Min(action.Length, 500)]).TakeLast(MaxRecentActions));
        }
    }

    private static bool IsValidState(ScreenStateRecord state)
        => state is not null && !string.IsNullOrWhiteSpace(state.AppId) && state.AppId.Length <= 200
            && !string.IsNullOrWhiteSpace(state.ScreenId) && state.ScreenId.Length <= 200
            && state.Signature is { Length: 64 } && state.Signature.All(Uri.IsHexDigit)
            && state.Actions is not null && state.Actions.Count <= 255
            && state.ContextRegions is not null && state.ContextRegions.Count <= 64
            && state.Actions.All(action => action is not null && action.Key is { Length: <= 200 }
                && action.Role is { Length: <= 120 } && action.Label is { Length: <= 500 }
                && (action.Anchor is null || ValidAnchor(action.Anchor)))
            && state.ContextRegions.All(region => region is not null && region.Key is { Length: <= 120 }
                && region.Label is { Length: <= 300 } && double.IsFinite(region.X) && double.IsFinite(region.Y)
                && double.IsFinite(region.Width) && double.IsFinite(region.Height));

    private void Save()
    {
        if (_persistencePath is null) return;
        var snapshot = new PersistedState(2,
            _screens.Values.OrderBy(state => state.LastSeenUtc).TakeLast(MaxPersistedScreens).ToArray(),
            _transitions.Values.OrderBy(item => item.ObservedAtUtc).TakeLast(MaxPersistedTransitions).ToArray(),
            _recentActions.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
        LocalStateFile.Write(_persistencePath, snapshot, out _persistenceError);
    }

    private static string TransitionKey(ScreenTransitionRecord transition)
        => $"{transition.AppId}\0{transition.FromScreenId}\0{transition.ActionId}\0{transition.ToScreenId}\0{transition.ToFrameId}\0{transition.ObservedAtUtc:O}";

    private ScreenStateObservation CreateResult(ScreenStateRecord record, bool repeated, string appId)
    {
        var incoming = _transitions.Values.Where(item =>
                string.Equals(item.AppId, appId, StringComparison.OrdinalIgnoreCase) && item.ToScreenId == record.ScreenId)
            .OrderByDescending(item => item.ObservedAtUtc).Take(8).ToArray();
        bool stable = record.ConsecutiveObservationCount >= 2;
        int reusableAnchors = record.Actions.Count(action => action.Anchor is { IsStatic: true });
        string note = reusableAnchors >= 2
            ? $"Semantic screen matched with {reusableAnchors} promoted static anchors. Reuse only after every anchor hash passes on a fresh frame and the layout fingerprint matches."
            : stable
                ? $"Semantic screen signature matched {record.ConsecutiveObservationCount} consecutive observations within 5 seconds; cached transitions are context only. Use current-frame boxes unless static anchors are verified."
                : repeated
                    ? "A prior semantic signature matched, but this observation is not a verified consecutive repeat; do not reuse transition guidance."
                    : "New semantic screen signature; no reusable coordinates are available.";
        return new(record.ScreenId, record.Signature, record.ScreenHint, repeated, record.ObservationCount,
            record.ConsecutiveObservationCount, stable, stable ? incoming : [], record.Actions, note);
    }

    private static string ComputeSignature(string appId, string appVersion, string profile, string keyboardLayout,
        string? screenHint, string scope, IReadOnlyList<ScreenActionRecipe> candidates, IReadOnlyList<string>? stateClues,
        string? layoutFingerprint)
    {
        string controls = string.Join("\n", candidates
            .Select(candidate => $"{Normalize(candidate.Role)}|{Normalize(candidate.Label)}|enabled={candidate.Enabled?.ToString() ?? "unknown"}|{string.Join(',', (candidate.KeyBindings ?? []).Select(Normalize).OrderBy(value => value, StringComparer.Ordinal))}")
            .OrderBy(value => value, StringComparer.Ordinal));
        string clues = string.Join("\n", (stateClues ?? []).Take(80).Select(clue => Normalize(clue[..Math.Min(clue.Length, 200)]))
            .Where(clue => clue.Length > 0).OrderBy(value => value, StringComparer.Ordinal));
        string canonical = string.Join("\n", new[] { Normalize(appId), Normalize(appVersion), Normalize(profile),
            Normalize(keyboardLayout), Normalize(screenHint ?? ""), Normalize(scope), Normalize(layoutFingerprint ?? ""), controls, clues });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Clean(string? value, int maxLength)
        => string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, maxLength)];

    private static string? CombineIssues(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first)) return Clean(second, 500);
        if (string.IsNullOrWhiteSpace(second) || first.Contains(second, StringComparison.Ordinal)) return Clean(first, 500);
        return Clean(first + "; " + second, 500);
    }
}
