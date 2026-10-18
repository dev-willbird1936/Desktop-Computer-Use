using System.Diagnostics;
using System.Text.Json;
using ShadowUse.Jev.Flow;
using ShadowUse.Jev.FlowTests.Sim;

namespace ShadowUse.Jev.FlowTests;

/// <summary>
/// Cost accounting of the flow loop on the simulated menu graph. Counts (model calls, image uploads, verification screenshots) come from the
/// engine's own bookkeeping; latencies are a PARAMETERISED MODEL, not measurements of Jev or an agent. Set DCU_FLOW_BENCH_OUT to write the JSON.
/// </summary>
public sealed class FlowSimulationBenchmark
{
    private const string Goal = "Start a single-player match";

    // Per-call latency assumptions (seconds). Jev image: ~1.3 s is the live BTD2 decision latency recorded in docs/experimental-game-jev.md
    // (1,273 ms with Box). Jev text-only has no live measurement; 0.4 s is an assumption. Normal-agent step times are scenarios, not data.
    private static readonly (string Name, double TextS, double ImageS)[] JevLatencies = [("assumed_text_0.4s_image_1.3s", 0.4, 1.3), ("slower_text_1.0s_image_2.5s", 1.0, 2.5)];
    private static readonly double[] AgentStepSeconds = [5, 15, 49];

    [Fact]
    public async Task CountsAndModelledLatencyForNormalLoopVersusColdAndWarmFlow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dcu-bench-" + Guid.NewGuid().ToString("N"));
        string bookPath = Path.Combine(dir, "book.json");
        try
        {
            var options = new JevFlowOptions { SettleMs = 1, MaxSettleMs = 50, FirstSettleMs = 20, PollMs = 1, MaxActions = 8 };

            async Task<(FlowRunResult Result, long RealEngineMs, int ImageRequests)> Run()
            {
                var game = new SimGame();
                var decider = new OracleDecider(game);
                var sw = Stopwatch.StartNew();
                var result = await new JevFlowEngine(new JevActionBook(bookPath), new SimEnvironment(game), decider, options, new SimDiscoverer(game))
                    .RunAsync(Goal, new FlowSeed(ScreenHint: "main_menu"));
                return (result, sw.ElapsedMilliseconds, decider.Requests.Count(r => r.ImageJpeg is not null));
            }

            var cold = await Run();
            var warm1 = await Run();
            var warm2 = await Run();
            Assert.All(new[] { cold, warm1, warm2 }, r => Assert.True(r.Result.GoalReached));

            int inputs = warm1.Result.Stats.Dispatches;
            // Normal loop per input: screenshot, model views image and decides, input, verification screenshot (viewed by the next call).
            var normal = new { inputs, model_calls = inputs, image_uploads = inputs, verification_screenshots = inputs };

            object Describe((FlowRunResult Result, long RealEngineMs, int ImageRequests) r) => new
            {
                inputs = r.Result.Stats.Dispatches, jev_calls = r.Result.Stats.JevCalls, text_calls = r.Result.Stats.TextCalls,
                image_calls = r.Result.Stats.ImageCalls, image_bytes_sent = r.Result.Stats.ImageBytesSent,
                verification_screenshots_to_a_model = 0, local_captures = r.Result.Stats.Captures,
                local_screen_confirmations = r.Result.Stats.LocalScreenConfirmations,
                learned_screens = r.Result.Stats.LearnedScreens, learned_transitions = r.Result.Stats.LearnedTransitions, learned_boxes = r.Result.Stats.LearnedBoxes,
                real_engine_overhead_ms_including_sim_waits = r.RealEngineMs,
            };

            var scenarios = new List<object>();
            foreach (var (name, textS, imageS) in JevLatencies)
                foreach (double agent in AgentStepSeconds)
                    scenarios.Add(new
                    {
                        jev_latency_model = name, normal_agent_seconds_per_step = agent,
                        normal_loop_s = Math.Round(inputs * agent, 1),
                        flow_cold_s = Math.Round(cold.Result.Stats.TextCalls * textS + cold.Result.Stats.ImageCalls * imageS, 1),
                        flow_warm_s = Math.Round(warm1.Result.Stats.TextCalls * textS + warm1.Result.Stats.ImageCalls * imageS, 1),
                    });

            // Real, deterministic expectations from the engine's bookkeeping.
            Assert.Equal(0, warm1.Result.Stats.ImageCalls);
            Assert.Equal(0, warm2.Result.Stats.ImageCalls);
            Assert.True(cold.Result.Stats.ImageCalls > 0);

            if (Environment.GetEnvironmentVariable("DCU_FLOW_BENCH_OUT") is { Length: > 0 } outPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllText(outPath, JsonSerializer.Serialize(new
                {
                    kind = "simulation",
                    warning = "Mechanics/cost accounting on a simulated BTD2-shaped menu graph with an oracle standing in for Jev. Counts are the engine's own bookkeeping. Latencies are a parameterised model; no Jev request, game, or agent was timed. Not a speed claim about real Jev or real BTD2.",
                    goal = Goal,
                    normal_loop_counts = normal,
                    flow_cold = Describe(cold),
                    flow_warm_run_1 = Describe(warm1),
                    flow_warm_run_2 = Describe(warm2),
                    modelled_latency_scenarios = scenarios,
                    latency_assumptions = new
                    {
                        jev_image_with_box_s_basis = "1.273 s live BTD2 Hero Challenge decision (docs/experimental-game-jev.md); scenario 2 is a pessimistic 2.5 s",
                        jev_text_only_s_basis = "no measurement exists; assumed",
                        normal_agent_step_s_basis = "scenario values; the documented host-agent surrogate took ~49 s for a different 16-card localization task and is not a per-step measurement",
                    },
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
