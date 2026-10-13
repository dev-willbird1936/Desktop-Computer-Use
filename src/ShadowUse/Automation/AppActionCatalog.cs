// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShadowUse.Automation;

/// <summary>A sourced, app-specific action/binding fact. Source metadata is caller-reported and never treated as proof.</summary>
public sealed record AppActionCatalogEntry(
    string ActionId,
    string Label,
    IReadOnlyList<string> KeySequence,
    string? ScreenId,
    IReadOnlyList<string> TargetAliases,
    string SourceKind,
    string SourceReference,
    string? SourceHash,
    string EvidenceStatus,
    string? Preconditions);

/// <summary>One imported action catalog for an app, keyboard layout, and user-selected profile.</summary>
public sealed record AppActionCatalogSnapshot(
    string AppId,
    string AppVersion,
    string Profile,
    string KeyboardLayout,
    string Revision,
    DateTimeOffset ImportedAtUtc,
    IReadOnlyList<AppActionCatalogEntry> Actions);

public readonly record struct AppActionCatalogLookup(
    bool Found,
    AppActionCatalogSnapshot? Snapshot,
    bool Advisory,
    string? Reason);

public readonly record struct AppActionCatalogImportResult(
    bool Imported,
    string? Revision,
    int ActionCount,
    int ConflictCount,
    string? Error,
    bool Persisted = false);

/// <summary>
/// Process-lifetime store of app action knowledge imported from bounded config/doc research.
/// It stores semantic actions and bindings only, never live coordinates or secrets.
/// </summary>
public sealed class AppActionCatalog
{
    public const int MaxJsonLength = 64 * 1024;
    public const int MaxActions = 255;
    private static readonly HashSet<string> SourceKinds = new(StringComparer.Ordinal)
    {
        "local_config", "official_documentation", "uia_access_key", "uia_accelerator_key", "background_research", "user_confirmed",
    };
    private static readonly HashSet<string> EvidenceStatuses = new(StringComparer.Ordinal)
    {
        "source_attested", "observed", "user_confirmed", "unverified",
    };

    private sealed record PersistedState(int SchemaVersion, IReadOnlyList<AppActionCatalogSnapshot> Catalogs);

    private readonly ConcurrentDictionary<string, AppActionCatalogSnapshot> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _persistencePath;
    private string? _persistenceError;

    public AppActionCatalog(string? persistencePath = null)
    {
        _persistencePath = persistencePath;
        Load();
    }

    public string? PersistenceError => _persistenceError;

    public AppActionCatalogImportResult Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
            return new(false, null, 0, 0, $"catalog_json must be non-empty and at most {MaxJsonLength} characters.");
        if (!TryParse(json, out var appId, out var appVersion, out var profile, out var keyboardLayout, out var actions, out var error))
            return new(false, null, 0, 0, error);

        string revision = ComputeRevision(appId, appVersion, profile, keyboardLayout, actions);
        var snapshot = new AppActionCatalogSnapshot(appId, appVersion, profile, keyboardLayout, revision,
            DateTimeOffset.UtcNow, actions);
        _catalogs[Key(appId, profile, keyboardLayout)] = snapshot;
        while (_catalogs.Count > 128)
        {
            var oldest = _catalogs.Values.MinBy(item => item.ImportedAtUtc);
            if (oldest is null) break;
            _catalogs.TryRemove(Key(oldest.AppId, oldest.Profile, oldest.KeyboardLayout), out _);
        }
        int conflicts = GetAllConflicts(actions).Count;
        bool persisted = Save();
        return new(true, revision, actions.Count, conflicts,
            persisted || _persistencePath is null ? null : _persistenceError, persisted);
    }

    public AppActionCatalogLookup Lookup(string appId, string appVersion, string profile, string keyboardLayout)
    {
        if (string.IsNullOrWhiteSpace(appId)) return new(false, null, false, "app_id_required");
        if (!_catalogs.TryGetValue(Key(appId, profile, keyboardLayout), out var snapshot))
            return new(false, null, false, "catalog_not_found_for_app_profile_and_keyboard_layout");
        bool advisory = !string.Equals(snapshot.AppVersion, appVersion, StringComparison.OrdinalIgnoreCase);
        return new(true, snapshot, advisory, advisory ? "app_version_mismatch" : null);
    }

    public bool Invalidate(string appId, string profile, string keyboardLayout)
    {
        if (!_catalogs.TryRemove(Key(appId, profile, keyboardLayout), out _)) return false;
        Save();
        return true;
    }

    public int Count => _catalogs.Count;

    /// <summary>Returns action IDs whose applicable entries disagree about the key sequence.</summary>
    public static IReadOnlySet<string> GetConflicts(IReadOnlyList<AppActionCatalogEntry> entries, string? screenId = null)
        => entries.Where(entry => AppliesToScreen(entry, screenId))
            .GroupBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(entry => SequenceKey(entry.KeySequence)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> GetAllConflicts(IReadOnlyList<AppActionCatalogEntry> entries)
    {
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in entries.GroupBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase))
        {
            var scoped = group.ToArray();
            for (int i = 0; i < scoped.Length && !conflicts.Contains(group.Key); i++)
                for (int j = i + 1; j < scoped.Length; j++)
                {
                    bool scopesOverlap = scoped[i].ScreenId is null || scoped[j].ScreenId is null
                        || string.Equals(scoped[i].ScreenId, scoped[j].ScreenId, StringComparison.OrdinalIgnoreCase);
                    if (scopesOverlap && !string.Equals(SequenceKey(scoped[i].KeySequence),
                            SequenceKey(scoped[j].KeySequence), StringComparison.OrdinalIgnoreCase))
                    {
                        conflicts.Add(group.Key);
                        break;
                    }
                }
        }
        return conflicts;
    }

    public static IReadOnlyList<AppActionCatalogEntry> ApplicableActions(
        AppActionCatalogSnapshot snapshot, string? screenId)
        => snapshot.Actions.Where(entry => AppliesToScreen(entry, screenId)).ToArray();

    private static bool AppliesToScreen(AppActionCatalogEntry entry, string? screenId)
        => string.IsNullOrWhiteSpace(entry.ScreenId)
            || string.Equals(entry.ScreenId, screenId, StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string json, out string appId, out string appVersion, out string profile,
        out string keyboardLayout, out IReadOnlyList<AppActionCatalogEntry> actions, out string? error)
    {
        appId = appVersion = profile = keyboardLayout = "";
        actions = [];
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadString(root, "app_id", 200, out appId)
                || !TryReadString(root, "app_version", 120, out appVersion, allowEmpty: true)
                || !TryReadString(root, "profile", 120, out profile)
                || !TryReadString(root, "keyboard_layout", 120, out keyboardLayout)
                || !root.TryGetProperty("actions", out var actionArray)
                || actionArray.ValueKind != JsonValueKind.Array
                || actionArray.GetArrayLength() > MaxActions)
            {
                error = $"catalog_json requires app_id, app_version, profile, keyboard_layout, and 0-{MaxActions} actions.";
                return false;
            }

            var parsed = new List<AppActionCatalogEntry>(actionArray.GetArrayLength());
            foreach (var item in actionArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryReadString(item, "action_id", 120, out var actionId)
                    || !TryReadString(item, "label", 200, out var label)
                    || !TryReadString(item, "source_kind", 40, out var sourceKind)
                    || !SourceKinds.Contains(sourceKind)
                    || !TryReadString(item, "source_reference", 500, out var sourceReference)
                    || !TryReadString(item, "evidence_status", 32, out var evidenceStatus)
                    || !EvidenceStatuses.Contains(evidenceStatus))
                {
                    error = "Each catalog action needs action_id, label, source_kind, source_reference, and supported evidence_status.";
                    return false;
                }
                if (!SafeSourceReference(sourceKind, sourceReference, out error)) return false;
                var keySequence = ReadStringArray(item, "key_sequence", maxItems: 8, maxLength: 64, out error);
                if (keySequence is null) return false;
                foreach (string chord in keySequence)
                {
                    if (chord.Split('+', StringSplitOptions.TrimEntries).Any(part => part.Length == 0))
                    {
                        error = $"Action '{actionId}' has an invalid key chord.";
                        return false;
                    }
                    try { _ = InteractiveInput.ParseKeySet(chord); }
                    catch (ArgumentException)
                    {
                        error = $"Action '{actionId}' has a key chord unsupported by the game-key input adapter.";
                        return false;
                    }
                }
                var aliases = ReadStringArray(item, "target_aliases", maxItems: 20, maxLength: 120, out error);
                if (aliases is null) return false;
                string? screenId = TryReadOptionalString(item, "screen_id", 160, out var screenValue) ? screenValue : null;
                string? sourceHash = TryReadOptionalString(item, "source_hash", 64, out var hash) ? hash : null;
                if (sourceHash is not null && (sourceHash.Length != 64 || sourceHash.Any(ch => !Uri.IsHexDigit(ch))))
                {
                    error = $"Action '{actionId}' source_hash must be a 64-character SHA-256 hex string.";
                    return false;
                }
                string? preconditions = TryReadOptionalString(item, "preconditions", 500, out var preconditionValue) ? preconditionValue : null;
                parsed.Add(new(actionId, label, keySequence, screenId, aliases, sourceKind, sourceReference,
                    sourceHash?.ToLowerInvariant(), evidenceStatus, preconditions));
            }
            actions = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = "catalog_json is invalid JSON: " + ex.Message;
            return false;
        }
    }

    private static bool SafeSourceReference(string kind, string reference, out string? error)
    {
        error = null;
        if (kind == "official_documentation")
        {
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                error = "Official documentation source_reference must be an HTTPS URL without embedded credentials.";
                return false;
            }
        }
        if (kind == "local_config")
        {
            var segments = reference.Replace('\\', '/').Split('/');
            if (Path.IsPathRooted(reference) || segments.Any(segment => segment is ".." or "")
                || segments.Any(segment => segment.Contains(".env", StringComparison.OrdinalIgnoreCase)
                    || segment.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    || segment.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    || segment.Contains("cookie", StringComparison.OrdinalIgnoreCase)))
            {
                error = "local_config source_reference must be a safe relative path; secret-like, parent, and absolute paths are refused.";
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement root, string name, int maxItems, int maxLength, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(name, out var array)) return [];
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maxItems)
        {
            error = $"{name} must be an array of at most {maxItems} strings.";
            return null;
        }
        var values = new List<string>();
        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                || text.Length == 0 || text.Length > maxLength)
            {
                error = $"{name} items must be non-empty strings of at most {maxLength} characters.";
                return null;
            }
            values.Add(text);
        }
        return values;
    }

    private static bool TryReadString(JsonElement root, string name, int maxLength, out string value, bool allowEmpty = false)
    {
        value = "";
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String) return false;
        value = prop.GetString() ?? "";
        return value.Length <= maxLength && (allowEmpty || !string.IsNullOrWhiteSpace(value));
    }

    private static bool TryReadOptionalString(JsonElement root, string name, int maxLength, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null) return false;
        return prop.ValueKind == JsonValueKind.String && (value = prop.GetString() ?? "").Length <= maxLength;
    }

    private void Load()
    {
        var saved = LocalStateFile.Read<PersistedState>(_persistencePath);
        if (saved is null || saved.SchemaVersion != 1 || saved.Catalogs is null) return;
        foreach (var snapshot in saved.Catalogs.Take(128))
        {
            if (snapshot is null || snapshot.Actions is null || snapshot.Actions.Count > MaxActions
                || snapshot.Actions.Any(action => action is null)) continue;
            string json = JsonSerializer.Serialize(new
            {
                app_id = snapshot.AppId,
                app_version = snapshot.AppVersion,
                profile = snapshot.Profile,
                keyboard_layout = snapshot.KeyboardLayout,
                actions = snapshot.Actions.Select(action => new
                {
                    action_id = action.ActionId, label = action.Label, key_sequence = action.KeySequence,
                    screen_id = action.ScreenId, target_aliases = action.TargetAliases, source_kind = action.SourceKind,
                    source_reference = action.SourceReference, source_hash = action.SourceHash,
                    evidence_status = action.EvidenceStatus, preconditions = action.Preconditions,
                }),
            });
            if (!TryParse(json, out var appId, out var version, out var profile, out var layout, out var actions, out _)) continue;
            string revision = ComputeRevision(appId, version, profile, layout, actions);
            if (!string.Equals(revision, snapshot.Revision, StringComparison.Ordinal)) continue;
            _catalogs[Key(appId, profile, layout)] = snapshot with { Actions = actions, Revision = revision };
        }
    }

    private bool Save()
    {
        if (_persistencePath is null) return false;
        var snapshot = new PersistedState(1, _catalogs.Values.OrderBy(item => item.AppId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Profile, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.KeyboardLayout, StringComparer.OrdinalIgnoreCase)
            .Take(128).ToArray());
        bool saved = LocalStateFile.Write(_persistencePath, snapshot, out var error);
        _persistenceError = error;
        return saved;
    }

    private static string Key(string appId, string profile, string layout)
        => $"{appId.Trim()}\0{(string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim())}\0{(string.IsNullOrWhiteSpace(layout) ? "unknown" : layout.Trim())}";

    private static string ComputeRevision(string appId, string appVersion, string profile, string layout,
        IReadOnlyList<AppActionCatalogEntry> entries)
    {
        string canonical = string.Join('\n', new[] { appId, appVersion, profile, layout }.Concat(entries
            .OrderBy(entry => entry.ActionId, StringComparer.Ordinal)
            .ThenBy(entry => entry.ScreenId, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourceKind, StringComparer.Ordinal)
            .ThenBy(entry => entry.SourceReference, StringComparer.Ordinal)
            .Select(entry => string.Join('|', entry.ActionId, entry.Label, string.Join(',', entry.KeySequence), entry.ScreenId,
                string.Join(',', entry.TargetAliases), entry.SourceKind, entry.SourceReference, entry.SourceHash,
                entry.EvidenceStatus, entry.Preconditions))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
    }

    private static string SequenceKey(IReadOnlyList<string> sequence)
        => string.Join('\u001f', sequence.Select(chord => chord.Trim().ToLowerInvariant()));

}
