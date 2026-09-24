// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Automation;

public enum RecencyKind { OpenedAt, ModifiedAt, AddedAt }

/// <summary>F55-T02: every candidate carries all three recency dimensions as SEPARATE fields --
/// there is no single ambiguous "recency" field a caller could accidentally read as the wrong
/// kind, and DCU never substitutes one dimension's value into another's field.</summary>
public sealed record FileSearchCandidate(string Path, string FileName, DateTimeOffset? OpenedAt, DateTimeOffset? ModifiedAt, DateTimeOffset? AddedAt);

public sealed record FileSearchResult(IReadOnlyList<FileSearchCandidate> Candidates, RecencyKind RequestedSortKind, bool SortFallbackUsed, string? SortFallbackReason);

/// <summary>F55-R01/R02: scoped local retrieval only -- this walks a caller-bounded filesystem
/// directory and reports real file timestamps. It never reads browser history or email, and never
/// calls Jev/any model; F55-R02's "keep data local unless explicitly permitted" holds by
/// construction rather than by a runtime permission check, since there is no model call anywhere
/// in this path to permit or deny.</summary>
public static class LocalFileSearch
{
    /// <summary>Real filesystem I/O, kept separate from the pure sort/fallback logic below so the
    /// F55-T02 fallback-labeling guarantee is unit-testable against synthetic candidates without
    /// depending on this machine's actual NTFS last-access-tracking configuration.</summary>
    public static IReadOnlyList<FileSearchCandidate> EnumerateCandidates(string allowedRoot, string query)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot) || !Path.IsPathRooted(allowedRoot))
            throw new ArgumentException("search_local_files requires an absolute allowed_root.");
        if (!Directory.Exists(allowedRoot))
            throw new ArgumentException("allowed_root does not exist.");
        var rootFull = Path.GetFullPath(allowedRoot);
        var candidates = new List<FileSearchCandidate>();
        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!string.IsNullOrEmpty(query) && !name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            DateTimeOffset? opened = null, modified = null, added = null;
            try { modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero); } catch (IOException) { }
            try { opened = new DateTimeOffset(File.GetLastAccessTimeUtc(file), TimeSpan.Zero); } catch (IOException) { }
            try { added = new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero); } catch (IOException) { }
            candidates.Add(new FileSearchCandidate(file, name, opened, modified, added));
        }
        return candidates;
    }

    /// <summary>F55-T02: never silently substitutes a different recency dimension for the
    /// requested one. Sorting falls back to modified_at (the most reliable filesystem timestamp)
    /// ONLY when the requested dimension is unavailable for every single candidate, and the
    /// response always says so explicitly via SortFallbackUsed/Reason -- a caller can never
    /// mistake a modified_at-ordered list for an opened_at-ordered one.</summary>
    public static FileSearchResult ApplySortAndFallback(IReadOnlyList<FileSearchCandidate> candidates, RecencyKind sortBy, int maxResults)
    {
        if (maxResults <= 0 || maxResults > 200)
            throw new ArgumentException("max_results must be between 1 and 200.");
        var requested = Selector(sortBy);
        var fallbackUsed = false;
        string? fallbackReason = null;
        var effective = requested;
        if (candidates.Count > 0 && candidates.All(c => requested(c) is null) && sortBy != RecencyKind.ModifiedAt)
        {
            fallbackUsed = true;
            fallbackReason = $"{sortBy} was unavailable for every candidate (e.g. NTFS last-access tracking may be disabled); sorted by modified_at instead.";
            effective = Selector(RecencyKind.ModifiedAt);
        }
        var sorted = candidates.OrderByDescending(c => effective(c) ?? DateTimeOffset.MinValue).Take(maxResults).ToArray();
        return new FileSearchResult(sorted, sortBy, fallbackUsed, fallbackReason);
    }

    public static FileSearchResult Search(string allowedRoot, string query, RecencyKind sortBy, int maxResults)
        => ApplySortAndFallback(EnumerateCandidates(allowedRoot, query), sortBy, maxResults);

    private static Func<FileSearchCandidate, DateTimeOffset?> Selector(RecencyKind kind) => kind switch
    {
        RecencyKind.OpenedAt => c => c.OpenedAt,
        RecencyKind.ModifiedAt => c => c.ModifiedAt,
        RecencyKind.AddedAt => c => c.AddedAt,
        _ => throw new ArgumentException("Unknown recency kind."),
    };
}

/// <summary>F55-T01: "late search responses do not overwrite a newer query". A process-lifetime
/// monotonic sequence counter per caller-chosen scope. Begin() is called when a query starts;
/// IsFresh() is checked when it finishes. A response is stale exactly when a strictly newer query
/// for the same scope has already begun by the time this one completes -- the caller must never
/// apply a stale response over whatever a newer query (or the user's own manual selection) has
/// already produced.</summary>
public sealed class SearchSequencer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _issued = new();

    public long Begin(string scope) => _issued.AddOrUpdate(scope, 1, (_, v) => v + 1);

    public bool IsFresh(string scope, long sequence) => sequence >= _issued.GetValueOrDefault(scope, sequence);
}
