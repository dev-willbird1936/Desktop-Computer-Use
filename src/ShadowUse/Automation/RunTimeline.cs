// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;

namespace ShadowUse.Automation;

/// <summary>One recorded action on the run timeline (F48). DurationMs is the action's total
/// wall time; DecisionMs/DispatchMs/SettleMs (F51-R01) are an optional phase breakdown of that
/// same total -- decision is the upstream Jev call (null when the action was not Jev-driven),
/// dispatch is the actual click/fill/set_value call, settle is the post-action re-observation.
/// All three are best-effort and may not sum exactly to DurationMs (surrounding bookkeeping is
/// not attributed to any phase).</summary>
public sealed record RunTimelineEvent(
    string Surface,
    string Target,
    string Action,
    string? ElementId,
    bool Ok,
    string? Detail,
    long? DurationMs,
    DateTimeOffset At,
    long? DecisionMs = null,
    long? DispatchMs = null,
    long? SettleMs = null);

/// <summary>
/// Optional local JSON-lines event log so a failed fixture can be inspected after the
/// run without reproducing it (F48-T01). Off by default (F48-R02): when disabled, Record
/// does zero file I/O and costs nothing on the hot path. A logging failure never fails
/// the action it is recording.
/// </summary>
public sealed class RunTimeline
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _writeLock = new();
    private readonly string? _path;

    public RunTimeline(bool enabled, string? path)
    {
        Enabled = enabled;
        _path = path;
        if (!enabled || string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Enabled = false;
        }
    }

    public bool Enabled { get; private set; }

    public void Record(RunTimelineEvent entry)
    {
        if (!Enabled || _path is null)
            return;
        try
        {
            var line = JsonSerializer.Serialize(entry, WireJson);
            lock (_writeLock)
                File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: never let timeline logging fail the action it is recording.
        }
    }

    public IReadOnlyList<RunTimelineEvent> ReadRecent(int count)
    {
        if (_path is null || !File.Exists(_path))
            return [];
        var lines = File.ReadAllLines(_path);
        var entries = new List<RunTimelineEvent>();
        foreach (var line in lines.TakeLast(Math.Max(0, count)))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<RunTimelineEvent>(line, WireJson);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // Skip a malformed/truncated line rather than failing the whole read.
            }
        }
        return entries;
    }
}
