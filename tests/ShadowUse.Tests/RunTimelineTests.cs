// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class RunTimelineTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"dcu-run-timeline-test-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Disabled_RecordDoesNoFileIo()
    {
        var path = TempPath();
        var timeline = new RunTimeline(false, path);

        timeline.Record(new RunTimelineEvent("native", "app", "click", "e1", true, null, 5, DateTimeOffset.UtcNow));

        Assert.False(timeline.Enabled);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Enabled_RecordThenReadRecent_RoundTrips()
    {
        var path = TempPath();
        try
        {
            var timeline = new RunTimeline(true, path);

            timeline.Record(new RunTimelineEvent("native", "notepad", "click", "e1", true, null, 12, DateTimeOffset.UtcNow));
            timeline.Record(new RunTimelineEvent("browser", "sess-1", "fill", "cand-2", false, "timed out", 340, DateTimeOffset.UtcNow));

            var entries = timeline.ReadRecent(10);

            Assert.Equal(2, entries.Count);
            Assert.Equal("native", entries[0].Surface);
            Assert.True(entries[0].Ok);
            Assert.Equal("browser", entries[1].Surface);
            Assert.False(entries[1].Ok);
            Assert.Equal("timed out", entries[1].Detail);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadRecent_LimitsToTheRequestedCount()
    {
        var path = TempPath();
        try
        {
            var timeline = new RunTimeline(true, path);
            for (var i = 0; i < 5; i++)
                timeline.Record(new RunTimelineEvent("native", "app", "click", $"e{i}", true, null, 1, DateTimeOffset.UtcNow));

            var entries = timeline.ReadRecent(2);

            Assert.Equal(2, entries.Count);
            Assert.Equal("e3", entries[0].ElementId);
            Assert.Equal("e4", entries[1].ElementId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReadRecent_MissingFile_ReturnsEmpty()
    {
        var timeline = new RunTimeline(true, TempPath());
        Assert.Empty(timeline.ReadRecent(10));
    }

    [Fact]
    public void ReadRecent_SkipsAMalformedLineRatherThanFailing()
    {
        var path = TempPath();
        try
        {
            var timeline = new RunTimeline(true, path);
            timeline.Record(new RunTimelineEvent("native", "app", "click", "e1", true, null, 1, DateTimeOffset.UtcNow));
            File.AppendAllText(path, "not json" + Environment.NewLine);
            timeline.Record(new RunTimelineEvent("native", "app", "click", "e2", true, null, 1, DateTimeOffset.UtcNow));

            var entries = timeline.ReadRecent(10);

            Assert.Equal(2, entries.Count);
            Assert.Equal("e1", entries[0].ElementId);
            Assert.Equal("e2", entries[1].ElementId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
