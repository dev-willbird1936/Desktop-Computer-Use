// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;

namespace ShadowUse.Automation;

/// <summary>Identifies one remembered task/step experience (F28-R01): the adapter, the
/// app/page it applies to, and the caller-chosen task name. Deliberately excludes app
/// version — a version change is not a different task, it is a reason to treat the
/// same task's experience as advisory rather than a cache-miss.</summary>
public readonly record struct ExperienceKey(string Adapter, string TargetIdentity, string TaskIntent);

/// <summary>One recorded task/step experience: which app version it was observed under,
/// an optional free-text environment note, an optional selector recipe (reusing F31's
/// shape), and a caller-asserted outcome/failure category. DCU never infers "outcome" on
/// its own — a caller must have already verified the result before recording it, so this
/// store never promotes an unverified successful-looking run to reusable memory (F28-R02).</summary>
public sealed record ExperienceRecord(
    string AppVersion,
    string? Environment,
    ActionResolutionRecipe? Recipe,
    string Outcome,
    string? FailureCategory,
    DateTimeOffset RecordedAt);

/// <summary>Result of looking up an experience. <see cref="Advisory"/> is true whenever the
/// live app version differs from the recorded one (F28-T02): the record is returned for
/// context, but its recipe is deliberately not re-resolved and must not be trusted until
/// the caller revalidates targets and postconditions against the current app.</summary>
public readonly record struct ExperienceLookup(bool Found, ExperienceRecord? Record, bool Advisory, string? Reason);

/// <summary>
/// Process-lifetime store of task/step experience (F28-R01), shared by the native and
/// browser tool surfaces. Complements <see cref="ActionResolutionCache"/> (which only
/// remembers how to find an element again) with outcome/failure-category/version memory,
/// so later runs can skip rediscovery for unchanged apps and stay cautious across app
/// version changes rather than trusting a stale claim of success.
/// </summary>
public sealed class ExperienceStore
{
    private readonly ConcurrentDictionary<ExperienceKey, ExperienceRecord> _entries = new();

    public void Record(ExperienceKey key, ExperienceRecord record) => _entries[key] = record;

    public ExperienceLookup Lookup(ExperienceKey key, string currentAppVersion)
    {
        if (!_entries.TryGetValue(key, out var record))
            return new ExperienceLookup(false, null, false, "not_found");
        var advisory = !string.Equals(record.AppVersion, currentAppVersion, StringComparison.Ordinal);
        return new ExperienceLookup(true, record, advisory, advisory ? "app_version_changed" : null);
    }

    public int Count => _entries.Count;
}
