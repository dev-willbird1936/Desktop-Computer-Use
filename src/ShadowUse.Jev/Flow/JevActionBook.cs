// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;

namespace ShadowUse.Jev.Flow;

/// <summary>
/// Persistent per-app knowledge for the fast Jev loop: which screens exist, how to recognise each one
/// without a model call (fingerprints), which inputs each offers, where learned click targets are, and
/// which screen each input led to. This is what lets later runs skip images and verification screenshots.
/// </summary>
public sealed class JevActionBook
{
    public const string GlobalScreenId = "*global*";
    public const int MaxFingerprintsPerScreen = 6;
    public const int MaxScreens = 200;
    public const int MaxActionsPerScreen = 60;

    private readonly object _gate = new();
    private readonly string? _path;
    private Dictionary<string, FlowScreen> _screens = new(StringComparer.Ordinal);
    private Dictionary<string, string> _goalScreens = new(StringComparer.Ordinal);

    public JevActionBook(string? path = null)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<BookFile>(File.ReadAllText(path));
                if (loaded?.Screens is { } screens)
                    foreach (var screen in screens.Take(MaxScreens))
                        _screens[screen.Id] = Sanitize(screen);
                if (loaded?.GoalScreens is { } goals)
                    foreach (var (goal, screenId) in goals.Take(MaxScreens))
                        if (_screens.ContainsKey(screenId)) _goalScreens[goal] = screenId;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadError = ex.Message;
            }
        }
    }

    public string? LoadError { get; }
    public string? SaveError { get; private set; }

    public IReadOnlyList<FlowScreen> Screens
    {
        get { lock (_gate) return _screens.Values.Select(Clone).ToArray(); }
    }

    public FlowScreen? Get(string id)
    {
        lock (_gate) return _screens.TryGetValue(id, out var screen) ? Clone(screen) : null;
    }

    public FlowScreen EnsureScreen(string id, string description)
    {
        lock (_gate)
        {
            if (!_screens.TryGetValue(id, out var screen))
            {
                if (_screens.Count >= MaxScreens) throw new InvalidOperationException("Action book is full.");
                screen = new FlowScreen { Id = id, Description = description, LastSeenUtc = DateTimeOffset.UtcNow };
                _screens[id] = screen;
            }
            else if (string.IsNullOrWhiteSpace(screen.Description) && !string.IsNullOrWhiteSpace(description))
            {
                screen.Description = description;
            }
            return Clone(screen);
        }
    }

    /// <summary>
    /// Auto-named screens get a description Jev can match against a screenshot: where the screen was reached from, plus the control
    /// labels it offers. A caller- or user-written description is never overwritten.
    /// </summary>
    public void DescribeFromActions(string screenId)
    {
        lock (_gate)
        {
            if (!_screens.TryGetValue(screenId, out var screen) || screen.Actions.Count == 0) return;
            if (screen.Description.Length > 0 && !screen.Description.StartsWith(AutoDescriptionPrefix, StringComparison.Ordinal)) return;
            string origin = screen.Description.Contains(" | ") ? screen.Description[..screen.Description.IndexOf(" | ", StringComparison.Ordinal)] : screen.Description;
            string labels = string.Join("; ", screen.Actions.Take(10).Select(a => a.Description));
            string text = $"{origin} | shows controls: {labels}";
            screen.Description = text.Length <= 400 ? text : text[..400];
        }
    }

    public const string AutoDescriptionPrefix = "auto: ";

    /// <summary>User/caller-authored description; never auto-rewritten afterwards.</summary>
    public void SetDescription(string screenId, string description)
    {
        lock (_gate)
            if (_screens.TryGetValue(screenId, out var screen)) screen.Description = description.Length <= 400 ? description : description[..400];
    }

    public bool RemoveScreen(string screenId)
    {
        lock (_gate)
        {
            bool removed = _screens.Remove(screenId);
            foreach (var key in _goalScreens.Where(kv => kv.Value == screenId).Select(kv => kv.Key).ToArray()) _goalScreens.Remove(key);
            foreach (var screen in _screens.Values)
                foreach (var action in screen.Actions)
                    action.Transitions.Remove(screenId);
            return removed;
        }
    }

    public bool RemoveAction(string screenId, string actionId)
    {
        lock (_gate) return _screens.TryGetValue(screenId, out var screen) && screen.Actions.RemoveAll(a => a.Id == actionId) > 0;
    }

    public string NewScreenId()
    {
        lock (_gate)
        {
            for (int n = _screens.Count + 1; ; n++)
            {
                string id = $"screen_{n:D3}";
                if (!_screens.ContainsKey(id)) return id;
            }
        }
    }

    /// <summary>Actions offered on a screen, followed by global (any-screen) actions that it does not shadow.</summary>
    public IReadOnlyList<FlowAction> MenuFor(string screenId)
    {
        lock (_gate)
        {
            var result = new List<FlowAction>();
            if (_screens.TryGetValue(screenId, out var screen)) result.AddRange(screen.Actions.Select(Clone));
            if (screenId != GlobalScreenId && _screens.TryGetValue(GlobalScreenId, out var global))
                result.AddRange(global.Actions.Where(a => result.All(r => r.Id != a.Id)).Select(Clone));
            return result;
        }
    }

    /// <summary>Adds or refreshes an action. Existing learned data (box, transitions, usage) is kept unless the caller supplies new geometry.</summary>
    public bool Upsert(string screenId, FlowAction action, out string? refusal)
    {
        refusal = JevFlowGuard.Refusal(action);
        if (refusal is not null) return false;
        lock (_gate)
        {
            if (!_screens.TryGetValue(screenId, out var screen)) throw new InvalidOperationException($"Unknown screen '{screenId}'.");
            var existing = screen.Actions.FirstOrDefault(a => a.Id == action.Id);
            if (existing is null)
            {
                if (screen.Actions.Count >= MaxActionsPerScreen) { refusal = "screen already has the maximum number of actions"; return false; }
                screen.Actions.Add(Clone(action));
            }
            else
            {
                existing.Description = action.Description;
                existing.TargetDescription = action.TargetDescription ?? existing.TargetDescription;
                existing.Key = action.Key ?? existing.Key;
                if (action.Box is { } box && (existing.Box is null || action.Source == "caller"))
                {
                    existing.Box = box;
                    existing.BoxConfidence = action.BoxConfidence;
                    existing.NoEffectStreak = 0;
                }
            }
            return true;
        }
    }

    public void RecordBox(string screenId, string actionId, NormBox box, double confidence)
    {
        lock (_gate)
        {
            var action = Find(screenId, actionId);
            if (action is null || !box.IsValid || box.Area > JevFlowGuard.MaxBoxArea) return;
            action.Box = box;
            action.BoxConfidence = confidence;
            action.Source = action.Source == "caller" ? "caller" : "learned";
            action.NoEffectStreak = 0;
        }
    }

    public void RecordUse(string screenId, string actionId)
    {
        lock (_gate)
        {
            var action = Find(screenId, actionId);
            if (action is null) return;
            action.Uses++;
            action.LastUsedUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Records the confirmed destination of an action. Resets no-effect tracking because the click did something.</summary>
    public void RecordTransition(string fromScreen, string actionId, string toScreen)
    {
        lock (_gate)
        {
            var action = Find(fromScreen, actionId);
            if (action is null) return;
            action.Transitions[toScreen] = action.Transitions.GetValueOrDefault(toScreen) + 1;
            action.NoEffectStreak = 0;
        }
    }

    /// <summary>A click left the confirmed screen unchanged. After two in a row the learned box is dropped so Jev re-localizes it.</summary>
    public bool RecordNoEffect(string screenId, string actionId)
    {
        lock (_gate)
        {
            var action = Find(screenId, actionId);
            if (action is null || action.Kind != FlowActionKind.Click) return false;
            action.NoEffectStreak++;
            if (action.NoEffectStreak >= 2 && action.Box is not null && action.Source != "caller")
            {
                action.Box = null;
                action.BoxConfidence = 0;
                action.NoEffectStreak = 0;
                return true;
            }
            return false;
        }
    }

    public void AddFingerprint(string screenId, byte[] fingerprint)
    {
        lock (_gate)
        {
            if (!_screens.TryGetValue(screenId, out var screen) || fingerprint.Length == 0) return;
            screen.Visits++;
            screen.LastSeenUtc = DateTimeOffset.UtcNow;
            // Skip near-duplicates; otherwise keep the freshest samples so slow drift (animation, time of day) is covered.
            if (screen.Fingerprints.Any(f => Distance(f, fingerprint) < 0.01)) return;
            screen.Fingerprints.Add(fingerprint.ToArray());
            if (screen.Fingerprints.Count > MaxFingerprintsPerScreen) screen.Fingerprints.RemoveAt(0);
        }
    }

    public readonly record struct ScreenMatch(string ScreenId, double Distance, double Margin);

    /// <summary>
    /// Nearest saved screen by fingerprint. Returns null when nothing is within <paramref name="tolerance"/>, or when the
    /// best two screens are too close to tell apart (margin below <paramref name="minMargin"/>).
    /// </summary>
    public ScreenMatch? Match(byte[] fingerprint, double tolerance, double minMargin = 0.02)
    {
        lock (_gate)
        {
            var scored = _screens.Values
                .Where(s => s.Id != GlobalScreenId && s.Fingerprints.Count > 0)
                .Select(s => (s.Id, Dist: s.Fingerprints.Min(f => Distance(f, fingerprint))))
                .OrderBy(x => x.Dist)
                .Take(2)
                .ToArray();
            if (scored.Length == 0 || scored[0].Dist > tolerance) return null;
            double margin = scored.Length == 2 ? scored[1].Dist - scored[0].Dist : 1;
            if (margin < minMargin) return null;
            return new ScreenMatch(scored[0].Id, scored[0].Dist, margin);
        }
    }

    /// <summary>Fraction of grid cells that must differ by more than this (of 255) to count as a "changed cell".</summary>
    private const double ChangedCellLevel = 0.12;

    /// <summary>
    /// Screen-fingerprint distance, 0-1; different lengths are maximally different. Blends mean luminance difference with the share of
    /// cells that changed noticeably. Mean difference alone is dominated by a shared background, so a popup or a different panel over
    /// the same backdrop looked "identical" (measured 0.03-0.05 against 0.00-0.02 for harmless noise); the changed-cell term separates
    /// them (0.18-0.28 vs under 0.01).
    /// </summary>
    public static double Distance(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 1;
        long sum = 0;
        int changed = 0;
        int limit = (int)Math.Round(ChangedCellLevel * 255);
        for (int i = 0; i < a.Length; i++)
        {
            int d = Math.Abs(a[i] - b[i]);
            sum += d;
            if (d > limit) changed++;
        }
        double mean = sum / (255.0 * a.Length);
        double changedShare = Math.Min(1.0, 3.0 * changed / a.Length);
        return 0.5 * mean + 0.5 * changedShare;
    }

    /// <summary>Normalizes a free-text goal into a stable key so repeated phrasings of the same goal share learned end screens.</summary>
    public static string GoalKey(string goal) => string.Join(' ', goal.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Remembers which screen a verified goal ended on, so later runs can finish with a free fingerprint check instead of a model call.</summary>
    public void RecordGoalScreen(string goal, string screenId)
    {
        lock (_gate)
        {
            if (!_screens.ContainsKey(screenId) || screenId == GlobalScreenId) return;
            string key = GoalKey(goal);
            if (key.Length is > 0 and <= 300 && (_goalScreens.Count < MaxScreens || _goalScreens.ContainsKey(key))) _goalScreens[key] = screenId;
        }
    }

    public string? GoalScreenFor(string goal)
    {
        lock (_gate) return _goalScreens.GetValueOrDefault(GoalKey(goal));
    }

    public bool Save()
    {
        if (_path is null) return true;
        BookFile file;
        lock (_gate) file = new BookFile { Version = 1, Screens = _screens.Values.Select(Clone).ToList(), GoalScreens = new Dictionary<string, string>(_goalScreens) };
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
            SaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = ex.Message;
            return false;
        }
    }

    private FlowAction? Find(string screenId, string actionId)
    {
        if (_screens.TryGetValue(screenId, out var screen) && screen.Actions.FirstOrDefault(a => a.Id == actionId) is { } found) return found;
        return _screens.TryGetValue(GlobalScreenId, out var global) ? global.Actions.FirstOrDefault(a => a.Id == actionId) : null;
    }

    private static FlowScreen Sanitize(FlowScreen screen)
    {
        // Persisted books are untrusted input: drop anything the guard would refuse today.
        screen.Actions = screen.Actions.Where(a => JevFlowGuard.Refusal(a) is null).Take(MaxActionsPerScreen).ToList();
        screen.Fingerprints = screen.Fingerprints.Take(MaxFingerprintsPerScreen).ToList();
        return screen;
    }

    private static FlowAction Clone(FlowAction a) => new()
    {
        Id = a.Id, Kind = a.Kind, Description = a.Description, TargetDescription = a.TargetDescription, Key = a.Key,
        Box = a.Box, BoxConfidence = a.BoxConfidence, Source = a.Source, Uses = a.Uses, NoEffectStreak = a.NoEffectStreak,
        Transitions = new Dictionary<string, int>(a.Transitions, StringComparer.Ordinal), LastUsedUtc = a.LastUsedUtc,
    };

    private static FlowScreen Clone(FlowScreen s) => new()
    {
        Id = s.Id, Description = s.Description, Visits = s.Visits, LastSeenUtc = s.LastSeenUtc,
        Actions = s.Actions.Select(Clone).ToList(), Fingerprints = s.Fingerprints.Select(f => f.ToArray()).ToList(),
    };

    private sealed class BookFile
    {
        public int Version { get; set; }
        public List<FlowScreen> Screens { get; set; } = [];
        public Dictionary<string, string> GoalScreens { get; set; } = [];
    }
}
