// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
namespace ShadowUse.Automation;

/// <summary>F53: aggregates already-recorded RunTimelineEvent entries (F48/F51) into a per-action
/// speed/capability report -- pure data transformation, no new measurement, no live tool calls.
/// Groups by (surface, action, jev_driven) so a caller can directly compare jev=off vs jev-assist/
/// fast-loop for the SAME action, which is the objective's own explicit benchmarking priority.
/// jev_driven is true only when an entry actually carries a DecisionMs (a real jev_decide/
/// jev_act/browser_jev_act call happened before it), never inferred from action name alone.</summary>
public sealed record BenchmarkGroup(
    string Surface,
    string Action,
    bool JevDriven,
    int Count,
    int OkCount,
    double AvgTotalMs,
    long MinTotalMs,
    long MaxTotalMs,
    double? AvgDecisionMs,
    double? AvgDispatchMs,
    double? AvgSettleMs,
    string DominantPhase);

public static class RunTimelineBenchmark
{
    public static IReadOnlyList<BenchmarkGroup> Summarize(IReadOnlyList<RunTimelineEvent> events)
    {
        return events
            .GroupBy(e => (e.Surface, e.Action, JevDriven: e.DecisionMs is not null))
            .Select(g =>
            {
                var totals = g.Where(e => e.DurationMs is not null).Select(e => e.DurationMs!.Value).ToArray();
                var decisions = g.Where(e => e.DecisionMs is not null).Select(e => (double)e.DecisionMs!.Value).ToArray();
                var dispatches = g.Where(e => e.DispatchMs is not null).Select(e => (double)e.DispatchMs!.Value).ToArray();
                var settles = g.Where(e => e.SettleMs is not null).Select(e => (double)e.SettleMs!.Value).ToArray();

                var decisionTotal = decisions.Sum();
                var dispatchTotal = dispatches.Sum();
                var settleTotal = settles.Sum();
                var attributedTotal = decisionTotal + dispatchTotal + settleTotal;

                string dominant;
                if (attributedTotal <= 0)
                {
                    dominant = "unattributed";
                }
                else
                {
                    var wallTotal = totals.Sum();
                    var unattributed = Math.Max(0, wallTotal - attributedTotal);
                    (string Name, double Ms)[] phases =
                    [
                        ("decision", decisionTotal),
                        ("dispatch", dispatchTotal),
                        ("settle", settleTotal),
                        ("unattributed", unattributed),
                    ];
                    dominant = phases.OrderByDescending(p => p.Ms).First().Name;
                }

                return new BenchmarkGroup(
                    g.Key.Surface,
                    g.Key.Action,
                    g.Key.JevDriven,
                    g.Count(),
                    g.Count(e => e.Ok),
                    totals.Length > 0 ? totals.Average() : 0,
                    totals.Length > 0 ? totals.Min() : 0,
                    totals.Length > 0 ? totals.Max() : 0,
                    decisions.Length > 0 ? decisions.Average() : null,
                    dispatches.Length > 0 ? dispatches.Average() : null,
                    settles.Length > 0 ? settles.Average() : null,
                    dominant);
            })
            .OrderBy(g => g.Surface).ThenBy(g => g.Action).ThenBy(g => g.JevDriven)
            .ToArray();
    }
}
