// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

/// <summary>F55-T01/T02 coverage: the sequencing guard and the sort/fallback logic, both pure and
/// independent of real filesystem timestamps or timing.</summary>
public sealed class LocalSearchServiceTests
{
    [Fact]
    public void SearchSequencer_FirstQuery_IsFresh_WhenNoNewerQueryBegan()
    {
        var seq = new SearchSequencer();
        var s1 = seq.Begin("panel");
        Assert.True(seq.IsFresh("panel", s1));
    }

    [Fact]
    public void SearchSequencer_OlderQuery_IsStale_OnceANewerQueryBegan()
    {
        // F55-T01: a late response must not overwrite a newer query's result.
        var seq = new SearchSequencer();
        var s1 = seq.Begin("panel");
        var s2 = seq.Begin("panel");
        Assert.False(seq.IsFresh("panel", s1));
        Assert.True(seq.IsFresh("panel", s2));
    }

    [Fact]
    public void SearchSequencer_TracksScopesIndependently()
    {
        var seq = new SearchSequencer();
        var a1 = seq.Begin("panel-a");
        seq.Begin("panel-b");
        seq.Begin("panel-b");
        Assert.True(seq.IsFresh("panel-a", a1)); // panel-b's newer queries must not affect panel-a
    }

    private static FileSearchCandidate Candidate(string name, DateTimeOffset? opened = null, DateTimeOffset? modified = null, DateTimeOffset? added = null)
        => new($"C:/root/{name}", name, opened, modified, added);

    [Fact]
    public void ApplySortAndFallback_SortsByRequestedDimension_WhenAvailable()
    {
        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow;
        var candidates = new[] { Candidate("a.txt", opened: older), Candidate("b.txt", opened: newer) };

        var result = LocalFileSearch.ApplySortAndFallback(candidates, RecencyKind.OpenedAt, 10);

        Assert.False(result.SortFallbackUsed);
        Assert.Null(result.SortFallbackReason);
        Assert.Equal("b.txt", result.Candidates[0].FileName);
        Assert.Equal("a.txt", result.Candidates[1].FileName);
    }

    [Fact]
    public void ApplySortAndFallback_NeverSubstitutesAnotherDimensionSilently_WhenSomeCandidatesHaveIt()
    {
        // Only some candidates lack opened_at -- this is NOT a fallback case (F55-T02): those
        // candidates without the requested dimension simply sort last, they are never re-ranked
        // by a different timestamp field pretending to be opened_at.
        var candidates = new[] { Candidate("has-opened.txt", opened: DateTimeOffset.UtcNow), Candidate("no-opened.txt", modified: DateTimeOffset.UtcNow) };

        var result = LocalFileSearch.ApplySortAndFallback(candidates, RecencyKind.OpenedAt, 10);

        Assert.False(result.SortFallbackUsed);
        Assert.Equal("has-opened.txt", result.Candidates[0].FileName);
    }

    [Fact]
    public void ApplySortAndFallback_FallsBackToModifiedAt_OnlyWhenRequestedDimensionIsUnavailableForEveryCandidate()
    {
        var candidates = new[] { Candidate("a.txt", modified: DateTimeOffset.UtcNow.AddDays(-1)), Candidate("b.txt", modified: DateTimeOffset.UtcNow) };

        var result = LocalFileSearch.ApplySortAndFallback(candidates, RecencyKind.OpenedAt, 10);

        Assert.True(result.SortFallbackUsed);
        Assert.Contains("OpenedAt", result.SortFallbackReason);
        Assert.Equal(RecencyKind.OpenedAt, result.RequestedSortKind); // the ORIGINAL request is still reported, not silently rewritten
        Assert.Equal("b.txt", result.Candidates[0].FileName); // actually sorted by modified_at
    }

    [Fact]
    public void ApplySortAndFallback_RequestingModifiedAt_NeverFallsBack()
    {
        var candidates = new[] { Candidate("a.txt") }; // nothing set at all
        var result = LocalFileSearch.ApplySortAndFallback(candidates, RecencyKind.ModifiedAt, 10);
        Assert.False(result.SortFallbackUsed);
    }

    [Fact]
    public void ApplySortAndFallback_RespectsMaxResults()
    {
        var candidates = new[] { Candidate("a.txt"), Candidate("b.txt"), Candidate("c.txt") };
        var result = LocalFileSearch.ApplySortAndFallback(candidates, RecencyKind.ModifiedAt, 2);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    [InlineData(-1)]
    public void ApplySortAndFallback_Rejects_OutOfRangeMaxResults(int maxResults)
    {
        Assert.Throws<ArgumentException>(() => LocalFileSearch.ApplySortAndFallback([], RecencyKind.ModifiedAt, maxResults));
    }

    [Fact]
    public void EnumerateCandidates_Rejects_RelativeAllowedRoot()
    {
        Assert.Throws<ArgumentException>(() => LocalFileSearch.EnumerateCandidates("relative/path", ""));
    }

    [Fact]
    public void EnumerateCandidates_Rejects_MissingDirectory()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dcu-search-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<ArgumentException>(() => LocalFileSearch.EnumerateCandidates(missing, ""));
    }

    [Fact]
    public void EnumerateCandidates_FiltersByFileNameSubstring_CaseInsensitive_AndReportsRealTimestamps()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcu-search-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var matchPath = Path.Combine(root, "Invoice-2026.txt");
            var otherPath = Path.Combine(root, "notes.txt");
            File.WriteAllText(matchPath, "x");
            File.WriteAllText(otherPath, "y");

            var results = LocalFileSearch.EnumerateCandidates(root, "invoice");

            var only = Assert.Single(results);
            Assert.Equal("Invoice-2026.txt", only.FileName);
            Assert.NotNull(only.ModifiedAt);
            Assert.NotNull(only.AddedAt);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
