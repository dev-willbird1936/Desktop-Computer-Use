using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShadowUse.Vision;
using Xunit.Abstractions;

namespace ShadowUse.Tests;

public sealed class PatchTrackerBenchmarkTests(ITestOutputHelper output)
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 360;
    private const int FrameCount = 30;
    private const int SeedStart = 202605;
    private const int SeedCount = 10;
    private const string InitialLifetime = "bench-target-a";
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions TraceJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Families =
    [
        "easy-translation",
        "fast-motion-boundary",
        "hard-background",
        "low-contrast",
        "illumination-change",
        "shape-scale-change",
        "occlusion-loss-recovery",
        "distractor-loss-recovery",
        "color-metamer",
        "geometry-lifetime-change"
    ];

    [Fact]
    public void PatchTracker_TracksFastDiagonalMoveAtSearchCorner()
    {
        var patch = MakePatch(31, 24, 20);
        var first = MakeFrame(160, 100, patch, new Rectangle(60, 35, 24, 20), "fast-seed");
        var next = MakeFrame(160, 100, patch, new Rectangle(84, 59, 24, 20), "fast-next");
        Assert.True(PatchTracker.TryCreate(first, new Rectangle(60, 35, 24, 20), out var tracker, out var error), error);
        var result = tracker!.Update(next);
        Assert.Equal("tracking", result.State);
        Assert.Equal(new Rectangle(84, 59, 24, 20), result.Box);
    }

    [Fact]
    public void PatchTracker_TracksUnchangedPatchAcrossStructuredClutterAndAffineLighting()
    {
        var patch = MakePatch(47, 24, 20);
        var background = Background(640, 360, 47, "hard");
        var firstPixels = (byte[])background.Clone();
        DrawPatch(firstPixels, 640, new Rectangle(230, 140, 24, 20), patch, 24, 20);
        var changedPixels = TransformPixels(background, 0.82, 21);
        DrawPatch(changedPixels, 640, new Rectangle(242, 146, 24, 20), TransformPatch(patch, 0.82, 21), 24, 20);
        Assert.True(PatchTracker.TryCreate(Frame("clutter-seed", firstPixels, 640, 360, InitialLifetime),
            new Rectangle(230, 140, 24, 20), out var tracker, out var error), error);
        var result = tracker!.Update(Frame("clutter-lit", changedPixels, 640, 360, InitialLifetime));
        Assert.Equal("tracking", result.State);
        Assert.Equal(new Rectangle(242, 146, 24, 20), result.Box);
    }

    [PatchTrackerBenchmark]
    public void OptIn_PatchTrackerStressBenchmarkEmitsGroundTruthTrace()
    {
        string outputRoot = Environment.GetEnvironmentVariable("DCU_TRACKING_BENCHMARK_OUTPUT_DIR")
            ?? throw new InvalidOperationException("Set DCU_TRACKING_BENCHMARK_OUTPUT_DIR to a writable output directory.");
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        string runId = Guid.NewGuid().ToString("N");
        string runDirectory = Path.Combine(Path.GetFullPath(outputRoot), runId);
        string sampleDirectory = Path.Combine(runDirectory, "samples");
        Directory.CreateDirectory(sampleDirectory);

        WarmUp();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        int[] gcStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        var rows = new List<TraceRow>(Families.Length * SeedCount * FrameCount);
        var timings = new List<double>(Families.Length * SeedCount * (FrameCount - 1));
        long allocatedBytes = 0;
        var wall = Stopwatch.StartNew();
        int sequenceCount = 0;
        int initializationFailures = 0;

        foreach (string family in Families)
        {
            for (int seedIndex = 0; seedIndex < SeedCount; seedIndex++)
            {
                int seed = SeedStart + seedIndex;
                var frames = BuildSequence(family, seed);
                Assert.Equal(FrameCount, frames.Count);
                var seedFrame = frames[0];
                string? seedSample = SaveSample(seedFrame, family, seed, sampleDirectory);
                var seedTrackingFrame = ToTrackingFrame(family, seed, seedFrame);
                var initClock = Stopwatch.StartNew();
                bool initialized = PatchTracker.TryCreate(seedTrackingFrame, seedFrame.TargetBox!.Value, out var tracker, out var initError);
                initClock.Stop();
                if (!initialized) initializationFailures++;
                sequenceCount++;
                rows.Add(new TraceRow(runId, family, seed, 0, seedFrame.Condition, seedFrame.Width, seedFrame.Height,
                    BoxData.From(seedFrame.TargetBox), seedFrame.TargetPresent, seedFrame.VisibleFraction, seedFrame.IdentityUnique,
                    seedFrame.Distractors.Select(BoxData.From).ToArray(), initialized ? "seeded" : "init_failed",
                    null, initialized ? BoxData.From(seedFrame.TargetBox) : null, initialized ? BoxData.From(seedFrame.TargetBox) : null,
                    null, null, string.Empty, null, null, initialized ? "seed" : "init_failure",
                    initClock.Elapsed.TotalMilliseconds, null, null, seedSample));

                for (int frameIndex = 1; frameIndex < frames.Count; frameIndex++)
                {
                    var input = frames[frameIndex];
                    string? samplePath = SaveSample(input, family, seed, sampleDirectory);
                    string state, reason = string.Empty;
                    BoxData? returnedBox = null, currentAcceptedBox = null, lastKnownBox = null;
                    double? confidence = null, margin = null, updateMs = null, iou = null, centerError = null;
                    long? updateAllocated = null;
                    string outcome;
                    if (!initialized)
                    {
                        state = "init_failed";
                        reason = initError ?? "tracker_creation_failed";
                        outcome = "initialization_failure";
                    }
                    else
                    {
                        var trackingFrame = ToTrackingFrame(family, seed, input);
                        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        long started = Stopwatch.GetTimestamp();
                        PatchTrackingResult result = tracker!.Update(trackingFrame);
                        long stopped = Stopwatch.GetTimestamp();
                        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                        updateMs = Stopwatch.GetElapsedTime(started, stopped).TotalMilliseconds;
                        updateAllocated = allocatedAfter - allocatedBefore;
                        allocatedBytes += updateAllocated.Value;
                        timings.Add(updateMs.Value);
                        state = result.State;
                        returnedBox = BoxData.From(result.Box);
                        currentAcceptedBox = result.State == "tracking" ? BoxData.From(result.Box) : null;
                        lastKnownBox = BoxData.From(tracker!.LastBox);
                        confidence = result.Confidence;
                        margin = result.Margin;
                        reason = result.Reason ?? string.Empty;
                        (outcome, iou, centerError) = Evaluate(input, result);
                    }

                    rows.Add(new TraceRow(runId, family, seed, frameIndex, input.Condition, input.Width, input.Height,
                        BoxData.From(input.TargetBox), input.TargetPresent, input.VisibleFraction, input.IdentityUnique,
                        input.Distractors.Select(BoxData.From).ToArray(), state, returnedBox, currentAcceptedBox, lastKnownBox,
                        confidence, margin, reason, iou, centerError, outcome, null, updateMs, updateAllocated, samplePath));
                }
            }
        }
        wall.Stop();

        string[] sourceFiles = [
            "src/ShadowUse/Vision/GameTracking.cs",
            "src/ShadowUse/Vision/GameVision.cs",
            "tests/ShadowUse.Tests/PatchTrackerBenchmarkTests.cs"
        ];
        var repositoryRoot = RepositoryRoot();
        var sourceHashes = sourceFiles.ToDictionary(path => path, path => Sha256(Path.Combine(repositoryRoot, path)));
        var sourceRevision = Environment.GetEnvironmentVariable("DCU_TRACKING_BENCHMARK_BASE_COMMIT") ?? "not supplied";
        var workingTreeState = Environment.GetEnvironmentVariable("DCU_TRACKING_BENCHMARK_WORKTREE_STATE") ?? "not supplied";
        var configuration = BuildConfiguration();
        var metadata = new RunMetadata(runId, startedUtc, "patch-tracker-stress-v1", sourceRevision, workingTreeState,
            RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount, configuration, Stopwatch.Frequency,
            FrameWidth, FrameHeight, SeedStart, SeedCount, FrameCount, Families,
            ReadPrivateConstant("TemplateSide"), ReadPrivateConstant("SearchRadius"), ReadPrivateConstant("MinimumConfidence"),
            ReadPrivateConstant("MinimumMargin"), sourceHashes,
            [
                "Inputs are deterministic synthetic grayscale frames; capture, resize/preprocess, coordinate mapping, and cursor following are not timed.",
                "The color-metamer case uses distinct RGB triples (v,v,v) and (v+30,v-15,v-1), then the production integer luma weights; the resulting grayscale bytes are equal.",
                "Confidence is normalized correlation remapped to 0..1, not a calibrated probability.",
                "The template and output box size remain fixed after initialization; shape and scale changes are stress conditions, not supported guarantees.",
                "Trace replay/rendering is not real-time throughput evidence."
            ]);
        var scenarioSummaries = Families.Select(family => Summarize(family, rows.Where(row => row.Scenario == family && row.FrameIndex > 0).ToArray())).ToArray();
        var totals = Summarize("ALL", rows.Where(row => row.FrameIndex > 0).ToArray());
        var resultSummary = new BenchmarkSummary(metadata, rows.Count, rows.Count(row => row.FrameIndex > 0), sequenceCount,
            initializationFailures, wall.Elapsed.TotalSeconds, totals, scenarioSummaries,
            GC.CollectionCount(0) - gcStart[0], GC.CollectionCount(1) - gcStart[1], GC.CollectionCount(2) - gcStart[2], allocatedBytes);

        string manifestPath = Path.Combine(runDirectory, "manifest.json");
        string tracePath = Path.Combine(runDirectory, "trace.jsonl");
        string summaryPath = Path.Combine(runDirectory, "summary.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(metadata, JsonOptions));
        File.WriteAllLines(tracePath, rows.Select(row => JsonSerializer.Serialize(row, TraceJsonOptions)));
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(resultSummary, JsonOptions));

        output.WriteLine($"run_id={runId}");
        output.WriteLine($"manifest={manifestPath}");
        output.WriteLine($"trace={tracePath}");
        output.WriteLine($"summary={summaryPath}");
        output.WriteLine(JsonSerializer.Serialize(new
        {
            resultSummary.Updates,
            resultSummary.InitializationFailures,
            totals.CorrectRateOnVisibleUniqueFrames,
            totals.FalseAcceptancesWhenTargetAbsent,
            totals.WrongLocalizations,
            totals.AmbiguousAcceptances,
            totals.Abstentions,
            totals.P50UpdateMs,
            totals.P95UpdateMs,
            totals.P99UpdateMs,
            totals.MaxUpdateMs,
            totals.UpdatesPerSecond,
            totals.AllocatedBytesPerUpdate
        }, JsonOptions));

        Assert.Equal(Families.Length * SeedCount, sequenceCount);
        Assert.Equal(Families.Length * SeedCount * FrameCount, rows.Count);
        Assert.True(timings.Count >= 1000, $"Expected >=1000 timed updates; got {timings.Count}.");
        Assert.All(timings, value => Assert.True(double.IsFinite(value) && value >= 0));
    }

    private static void WarmUp()
    {
        var patch = MakePatch(8881, 24, 20);
        byte[] background = Background(FrameWidth, FrameHeight, 8881, "easy");
        var initial = new Rectangle(250, 150, 24, 20);
        var seedPixels = (byte[])background.Clone();
        DrawPatch(seedPixels, FrameWidth, initial, patch, 24, 20);
        Assert.True(PatchTracker.TryCreate(Frame("warmup-seed", seedPixels, FrameWidth, FrameHeight, InitialLifetime), initial, out var tracker, out var error), error);
        for (int i = 0; i < 100; i++)
        {
            var pixels = (byte[])background.Clone();
            DrawPatch(pixels, FrameWidth, initial, patch, 24, 20);
            tracker!.Update(Frame($"warmup-{i}", pixels, FrameWidth, FrameHeight, InitialLifetime));
        }
    }

    private static List<FrameInput> BuildSequence(string family, int seed)
    {
        int patchWidth = family is "distractor-loss-recovery" ? 16 : 24;
        int patchHeight = family is "distractor-loss-recovery" ? 16 : 20;
        var patch = MakePatch(seed, patchWidth, patchHeight);
        var lowContrastPatch = ReduceContrast(patch, 0.16);
        var metamerPatch = MakeLumaEquivalentMetamer(patch);
        Assert.Equal(patch, metamerPatch);

        string backgroundStyle = family switch
        {
            "hard-background" or "shape-scale-change" or "occlusion-loss-recovery" or "distractor-loss-recovery" => "hard",
            "low-contrast" => "low",
            _ => "easy"
        };
        var backgrounds = new Dictionary<(int Width, int Height), byte[]>();
        byte[] GetBackground(int width, int height)
        {
            if (!backgrounds.TryGetValue((width, height), out var value))
            {
                value = Background(width, height, seed, backgroundStyle);
                backgrounds[(width, height)] = value;
            }
            return (byte[])value.Clone();
        }

        int startX = 250, startY = 150;
        var initialBox = new Rectangle(startX, startY, patchWidth, patchHeight);
        var frames = new List<FrameInput>(FrameCount);
        int x = startX, y = startY;
        var movementRandom = new Random(seed * 31 + 7);
        for (int index = 0; index < FrameCount; index++)
        {
            int width = FrameWidth, height = FrameHeight;
            string lifetime = InitialLifetime;
            string condition = "unchanged";
            byte[] targetPattern = patch;
            Rectangle targetBox = initialBox;
            bool targetPresent = true, identityUnique = true;
            double visibleFraction = 1;
            var distractors = new List<(Rectangle Box, byte[] Pattern)>();
            double? ambientGain = null, ambientOffset = null;
            Rectangle? occluder = null;
            byte occluderValue = 0;

            switch (family)
            {
                case "easy-translation":
                    if (index > 0)
                    {
                        x = Math.Clamp(x + movementRandom.Next(-12, 13), 45, FrameWidth - patchWidth - 45);
                        y = Math.Clamp(y + movementRandom.Next(-12, 13), 35, FrameHeight - patchHeight - 35);
                    }
                    targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                    condition = index == 0 ? "seed" : "jitter_12px_max";
                    break;

                case "fast-motion-boundary":
                    if (index > 0)
                    {
                        (int Dx, int Dy)[] steps =
                        [
                            (24, 24), (-24, 24), (-24, -24), (24, -24),
                            (23, 0), (-23, 0), (0, 23), (0, -23),
                            (25, 0), (-25, 0), (0, 25), (0, -25),
                            (32, 0), (-32, 0), (0, 32), (0, -32),
                            (12, 8), (-12, 8), (-12, -8), (12, -8)
                        ];
                        var step = steps[(index - 1) % steps.Length];
                        x += step.Dx;
                        y += step.Dy;
                        condition = $"dx={step.Dx},dy={step.Dy}";
                    }
                    targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                    break;

                case "hard-background":
                    if (index > 0)
                    {
                        x = Math.Clamp(x + movementRandom.Next(-6, 7), 40, FrameWidth - patchWidth - 40);
                        y = Math.Clamp(y + movementRandom.Next(-6, 7), 30, FrameHeight - patchHeight - 30);
                    }
                    targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                    var nearBox = new Rectangle(Math.Min(FrameWidth - patchWidth, x + patchWidth), y, patchWidth, patchHeight);
                    distractors.Add((nearBox, MakeNearMatch(patch, seed + index)));
                    condition = "structured_clutter_plus_near_match";
                    break;

                case "low-contrast":
                    targetPattern = lowContrastPatch;
                    if (index > 0)
                    {
                        x += index % 2 == 0 ? 4 : -4;
                        y += index % 3 == 0 ? 3 : -3;
                    }
                    targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                    condition = "low_local_texture_contrast";
                    break;

                case "illumination-change":
                    double gain = index switch
                    {
                        <= 10 => 0.72 + index * 0.025,
                        <= 20 => 0.95 + (index - 10) * 0.025,
                        _ => 1.25 + (index - 20) * 0.09
                    };
                    double offset = index <= 10 ? 18 : 0;
                    ambientGain = gain;
                    ambientOffset = offset;
                    targetPattern = TransformPatch(patch, gain, offset);
                    if (index > 0) { x += index % 2 == 0 ? 4 : -4; y += index % 3 == 0 ? 3 : -3; }
                    targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                    condition = $"gain={gain:F2},offset={offset:F0}";
                    break;

                case "shape-scale-change":
                    double scale = index switch
                    {
                        <= 5 => 0.75,
                        <= 10 => 0.90,
                        <= 15 => 1.10,
                        <= 20 => 1.25,
                        <= 25 => 1.00,
                        _ => 1.00
                    };
                    int scaledWidth = Math.Max(8, (int)Math.Round(patchWidth * scale));
                    int scaledHeight = Math.Max(8, (int)Math.Round(patchHeight * scale));
                    x = startX + (patchWidth - scaledWidth) / 2;
                    y = startY + (patchHeight - scaledHeight) / 2;
                    targetBox = new Rectangle(x, y, scaledWidth, scaledHeight);
                    targetPattern = ResizeNearest(patch, patchWidth, patchHeight, scaledWidth, scaledHeight);
                    if (index is >= 21 and <= 25) targetPattern = ShearPattern(targetPattern, scaledWidth, scaledHeight);
                    condition = index is >= 21 and <= 25 ? "nonrigid_horizontal_shear" : $"scale={scale:F2}";
                    break;

                case "occlusion-loss-recovery":
                    targetBox = initialBox;
                    if (index is >= 1 and <= 5)
                    {
                        int[] covered = [0, 25, 50, 75, 100];
                        int percentage = covered[index - 1];
                        int coveredWidth = patchWidth * percentage / 100;
                        occluder = coveredWidth > 0 ? new Rectangle(x, y, coveredWidth, patchHeight) : null;
                        visibleFraction = 1 - percentage / 100.0;
                        condition = $"occlusion={percentage}%";
                    }
                    else if (index is >= 6 and <= 10)
                    {
                        targetPresent = false;
                        targetBox = Rectangle.Empty;
                        visibleFraction = 0;
                        condition = "target_absent";
                    }
                    else if (index >= 11)
                    {
                        x = startX + (index < 16 ? 12 : Math.Clamp(movementRandom.Next(-8, 9), -8, 8));
                        y = startY + (index < 16 ? 0 : Math.Clamp(movementRandom.Next(-8, 9), -8, 8));
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        condition = index < 16 ? "return_within_search_window" : "post_recovery_jitter";
                    }
                    occluderValue = (byte)(40 + seed % 30);
                    break;

                case "distractor-loss-recovery":
                    targetBox = initialBox;
                    if (index is >= 1 and <= 5)
                    {
                        var decoy = new Rectangle(startX + 20, startY, patchWidth, patchHeight);
                        distractors.Add((decoy, patch));
                        identityUnique = false;
                        condition = "exact_duplicate_enters_search_window";
                    }
                    else if (index is >= 6 and <= 10)
                    {
                        targetPresent = false;
                        targetBox = Rectangle.Empty;
                        visibleFraction = 0;
                        var decoy = new Rectangle(startX + 20, startY, patchWidth, patchHeight);
                        distractors.Add((decoy, patch));
                        identityUnique = false;
                        condition = "target_absent_exact_decoy_remains";
                    }
                    else if (index is >= 11 and <= 18)
                    {
                        x = startX;
                        y = startY;
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        condition = "target_returns_without_decoy";
                    }
                    else if (index == 19)
                    {
                        x = startX + 32;
                        y = startY;
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        condition = "32px_jump_after_recovery";
                    }
                    else
                    {
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        condition = "hold_after_large_jump";
                    }
                    break;

                case "color-metamer":
                    if (index is >= 1 and <= 5)
                    {
                        x = startX + 8 * index;
                        y = startY;
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        targetPattern = metamerPatch;
                        condition = "different_rgb_same_grayscale_target";
                    }
                    else if (index is >= 6 and <= 10)
                    {
                        targetPresent = false;
                        targetBox = Rectangle.Empty;
                        visibleFraction = 0;
                        var decoy = new Rectangle(x + 20, y, patchWidth, patchHeight);
                        distractors.Add((decoy, metamerPatch));
                        identityUnique = false;
                        condition = "target_absent_chroma_only_decoy";
                    }
                    else if (index >= 11)
                    {
                        x = startX;
                        y = startY;
                        targetBox = new Rectangle(x, y, patchWidth, patchHeight);
                        targetPattern = patch;
                        condition = "original_luma_target_returns";
                    }
                    break;

                case "geometry-lifetime-change":
                    targetBox = initialBox;
                    if (index is >= 6 and <= 7)
                    {
                        width = FrameWidth + 1;
                        targetBox = new Rectangle(startX, startY, patchWidth, patchHeight);
                        condition = "pixel_and_window_width_changed";
                    }
                    else if (index == 15)
                    {
                        lifetime = "bench-target-b";
                        condition = "target_lifetime_changed";
                    }
                    else if (index is 8 or 16)
                    {
                        condition = "original_geometry_and_lifetime_restored";
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown benchmark scenario: {family}");
            }

            byte[] pixels = GetBackground(width, height);
            if (ambientGain is { } lightGain && ambientOffset is { } lightOffset)
                pixels = TransformPixels(pixels, lightGain, lightOffset);
            if (targetPresent)
                DrawPatch(pixels, width, targetBox, targetPattern, targetBox.Width, targetBox.Height);
            foreach (var distractor in distractors)
                DrawPatch(pixels, width, distractor.Box, distractor.Pattern, distractor.Box.Width, distractor.Box.Height);
            if (occluder is { } coveredBox) FillRect(pixels, width, height, coveredBox, occluderValue);
            double visibility = targetPresent ? visibleFraction : 0;
            frames.Add(new FrameInput(index, width, height, lifetime, pixels,
                targetPresent ? targetBox : null, targetPresent, visibility, identityUnique,
                condition, distractors.Select(d => d.Box).ToArray()));
        }
        return frames;
    }

    private static (string Outcome, double? IoU, double? CenterError) Evaluate(FrameInput frame, PatchTrackingResult result)
    {
        if (result.State != "tracking") return ("abstain", null, null);
        if (!frame.TargetPresent || frame.TargetBox is null) return ("false_acceptance", null, null);
        double iou = IntersectionOverUnion(result.Box, frame.TargetBox.Value);
        double center = CenterError(result.Box, frame.TargetBox.Value);
        if (!frame.IdentityUnique) return ("accepted_ambiguous_identity", iou, center);
        if (frame.VisibleFraction < 0.5) return ("accepted_low_visibility", iou, center);
        return iou >= 0.5 && center <= 2 ? ("correct", iou, center) : ("wrong_localization", iou, center);
    }

    private static ScenarioMetrics Summarize(string name, TraceRow[] rows)
    {
        var updates = rows.Where(row => row.UpdateMs is not null).ToArray();
        var times = updates.Select(row => row.UpdateMs!.Value).ToArray();
        int visibleUnique = rows.Count(row => row.TargetPresent && row.VisibleFraction >= 0.5 && row.IdentityUnique);
        int correct = rows.Count(row => row.Outcome == "correct");
        int targetAbsentFrames = rows.Count(row => !row.TargetPresent);
        int falseAcceptances = rows.Count(row => row.Outcome == "false_acceptance");
        return new ScenarioMetrics(name, updates.Length, visibleUnique, targetAbsentFrames,
            rows.Count(row => row.TrackerState == "tracking"), correct,
            Ratio(correct, visibleUnique), falseAcceptances, Ratio(falseAcceptances, targetAbsentFrames),
            rows.Count(row => row.Outcome == "wrong_localization"),
            rows.Count(row => row.Outcome == "accepted_ambiguous_identity"),
            rows.Count(row => row.Outcome == "accepted_low_visibility"),
            rows.Count(row => row.TrackerState is "uncertain" or "lost"),
            rows.Count(row => row.TargetPresent && row.VisibleFraction < 0.5),
            Mean(rows.Where(row => row.Iou is not null).Select(row => row.Iou!.Value)),
            Mean(rows.Where(row => row.CenterErrorPx is not null).Select(row => row.CenterErrorPx!.Value)),
            Percentile(times, 0.50), Percentile(times, 0.95), Percentile(times, 0.99),
            times.Length == 0 ? null : times.Max(),
            times.Length == 0 || times.Sum() <= 0 ? null : times.Length / (times.Sum() / 1000.0),
            updates.Length == 0 ? null : updates.Average(row => row.UpdateAllocatedBytes ?? 0));
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;
    private static double? Mean(IEnumerable<double> values)
    {
        double[] array = values.ToArray();
        return array.Length == 0 ? null : array.Average();
    }
    private static double? Percentile(double[] values, double percentile)
    {
        if (values.Length == 0) return null;
        Array.Sort(values);
        int index = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
        return values[index];
    }

    private static string? SaveSample(FrameInput frame, string family, int seed, string sampleDirectory)
    {
        if (seed != SeedStart) return null;
        bool fullSequence = family is "easy-translation" or "fast-motion-boundary" or "hard-background" or "distractor-loss-recovery" or "color-metamer";
        if (!fullSequence && frame.Index is not (0 or 1 or 3 or 4 or 5 or 6 or 8 or 9 or 10 or 15 or 20 or 29)) return null;
        string path = Path.Combine(sampleDirectory, $"{family}-s{seed}-f{frame.Index:D2}.pgm");
        using var stream = File.Create(path);
        byte[] header = Encoding.ASCII.GetBytes($"P5\n{frame.Width} {frame.Height}\n255\n");
        stream.Write(header);
        stream.Write(frame.Pixels);
        return Path.GetRelativePath(Path.GetDirectoryName(sampleDirectory)!, path).Replace('\\', '/');
    }

    private static GameTrackingFrame ToTrackingFrame(string family, int seed, FrameInput input)
        => new($"bench-{family}-{seed}-{input.Index}", Epoch.AddMilliseconds(input.Index * 100L), input.TargetLifetime,
            input.Width, input.Height, 1, input.Width, input.Height, input.Pixels);

    private static GameTrackingFrame Frame(string id, byte[] pixels, int width, int height, string lifetime)
        => new(id, Epoch, lifetime, width, height, 1, width, height, pixels);

    private static GameTrackingFrame MakeFrame(int width, int height, byte[] patch, Rectangle box, string id)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)24);
        DrawPatch(pixels, width, box, patch, box.Width, box.Height);
        return Frame(id, pixels, width, height, InitialLifetime);
    }

    private static byte[] MakePatch(int seed, int width, int height)
    {
        var random = new Random(seed);
        var patch = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int checker = ((x / 3 + y / 4) & 1) == 0 ? 32 : -28;
                int asymmetric = x < width / 5 && y > height / 3 ? 18 : 0;
                patch[y * width + x] = ClampByte(128 + checker + asymmetric + random.Next(-42, 43));
            }
        return patch;
    }

    private static byte[] Background(int width, int height, int seed, string style)
    {
        var pixels = new byte[width * height];
        if (style == "easy")
        {
            Array.Fill(pixels, (byte)24);
            return pixels;
        }
        var random = new Random(seed * 97 + width * 7 + height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int value;
                if (style == "low")
                    value = 126 + random.Next(-4, 5);
                else
                {
                    int tile = ((x / 4 + y / 5 + seed) & 1) == 0 ? 24 : -18;
                    int stripe = x % 53 < 2 || y % 41 < 2 ? 15 : 0;
                    int motif = ((x * 13 + y * 7 + seed) % 17) - 8;
                    value = 92 + tile + stripe + motif + random.Next(-9, 10);
                }
                pixels[y * width + x] = ClampByte(value);
            }
        return pixels;
    }

    private static byte[] ReduceContrast(byte[] pixels, double gain)
    {
        double mean = pixels.Average(value => (double)value);
        var result = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++) result[i] = ClampByte(128 + (pixels[i] - mean) * gain);
        return result;
    }

    private static byte[] MakeLumaEquivalentMetamer(byte[] grayscale)
    {
        var result = new byte[grayscale.Length];
        for (int i = 0; i < grayscale.Length; i++)
        {
            int value = grayscale[i];
            if (value is < 15 or > 225) throw new InvalidOperationException("Metamer input must have room for the chroma shift.");
            int red = value + 30, green = value - 15, blue = value - 1;
            result[i] = (byte)((red * 299 + green * 587 + blue * 114) / 1000);
        }
        return result;
    }

    private static byte[] MakeNearMatch(byte[] pixels, int seed)
    {
        var result = (byte[])pixels.Clone();
        int stride = Math.Max(7, result.Length / 13);
        for (int i = seed % stride; i < result.Length; i += stride) result[i] = ClampByte(result[i] + 18);
        return result;
    }

    private static byte[] TransformPatch(byte[] pixels, double gain, double offset)
        => pixels.Select(value => ClampByte(value * gain + offset)).ToArray();

    private static byte[] TransformPixels(byte[] pixels, double gain, double offset)
        => TransformPatch(pixels, gain, offset);

    private static byte[] ResizeNearest(byte[] pixels, int width, int height, int newWidth, int newHeight)
    {
        var resized = new byte[newWidth * newHeight];
        for (int y = 0; y < newHeight; y++)
            for (int x = 0; x < newWidth; x++)
                resized[y * newWidth + x] = pixels[Math.Min(height - 1, y * height / newHeight) * width + Math.Min(width - 1, x * width / newWidth)];
        return resized;
    }

    private static byte[] ShearPattern(byte[] pixels, int width, int height)
    {
        var sheared = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            int offset = (y - height / 2) / 4;
            for (int x = 0; x < width; x++)
                sheared[y * width + x] = pixels[y * width + Math.Clamp(x - offset, 0, width - 1)];
        }
        return sheared;
    }

    private static void DrawPatch(byte[] pixels, int frameWidth, Rectangle box, byte[] patch, int patchWidth, int patchHeight)
    {
        if (box.Width != patchWidth || box.Height != patchHeight || box.X < 0 || box.Y < 0 || box.Right > frameWidth)
            throw new InvalidOperationException($"Invalid patch geometry: {box} in {frameWidth}-pixel-wide frame.");
        int frameHeight = pixels.Length / frameWidth;
        if (box.Bottom > frameHeight || patch.Length != patchWidth * patchHeight)
            throw new InvalidOperationException($"Patch data does not match geometry: {box}, {patch.Length} bytes.");
        for (int y = 0; y < patchHeight; y++)
            Array.Copy(patch, y * patchWidth, pixels, (box.Y + y) * frameWidth + box.X, patchWidth);
    }

    private static void FillRect(byte[] pixels, int frameWidth, int frameHeight, Rectangle rect, byte value)
    {
        var clipped = Rectangle.Intersect(rect, new Rectangle(0, 0, frameWidth, frameHeight));
        for (int y = clipped.Top; y < clipped.Bottom; y++) Array.Fill(pixels, value, y * frameWidth + clipped.Left, clipped.Width);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static double IntersectionOverUnion(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        double intersectionArea = intersection.Width * (double)intersection.Height;
        double union = a.Width * (double)a.Height + b.Width * (double)b.Height - intersectionArea;
        return union <= 0 ? 0 : intersectionArea / union;
    }

    private static double CenterError(Rectangle a, Rectangle b)
    {
        double dx = a.X + a.Width / 2.0 - (b.X + b.Width / 2.0);
        double dy = a.Y + a.Height / 2.0 - (b.Y + b.Height / 2.0);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static object ReadPrivateConstant(string name)
        => typeof(PatchTracker).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue()
           ?? throw new InvalidOperationException($"Could not read PatchTracker.{name} for benchmark metadata.");

    private static string RepositoryRoot()
    {
        string? root = typeof(PatchTrackerBenchmarkTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DcuRepositoryRoot")?.Value;
        return root is null ? throw new InvalidOperationException("DcuRepositoryRoot assembly metadata is missing.") : Path.GetFullPath(root);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string BuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private sealed record FrameInput(int Index, int Width, int Height, string TargetLifetime, byte[] Pixels,
        Rectangle? TargetBox, bool TargetPresent, double VisibleFraction, bool IdentityUnique, string Condition,
        Rectangle[] Distractors);

    private sealed record BoxData(int X, int Y, int Width, int Height)
    {
        public static BoxData? From(Rectangle? box) => box is { } value ? From(value) : null;
        public static BoxData From(Rectangle box) => new(box.X, box.Y, box.Width, box.Height);
    }

    private sealed record TraceRow(string RunId, string Scenario, int Seed, int FrameIndex, string Condition,
        int FrameWidth, int FrameHeight, BoxData? GroundTruthBox, bool TargetPresent, double VisibleFraction,
        bool IdentityUnique, BoxData[] DistractorBoxes, string TrackerState, BoxData? ReturnedBox,
        BoxData? CurrentAcceptedBox, BoxData? LastKnownBox, double? Confidence, double? Margin,
        string Reason, double? Iou, double? CenterErrorPx,
        string Outcome, double? InitializationMs, double? UpdateMs, long? UpdateAllocatedBytes, string? InputSamplePath);

    private sealed record RunMetadata(string RunId, DateTimeOffset StartedUtc, string BenchmarkVersion,
        string BaseCommit, string WorkingTreeState, string DotnetRuntime, string OperatingSystem,
        string ProcessArchitecture, int LogicalProcessors, string BuildConfiguration, long StopwatchFrequency,
        int FrameWidth, int FrameHeight, int SeedStart, int SeedCount, int FramesPerSequence, string[] ScenarioFamilies,
        object TemplateSide, object SearchRadius, object MinimumConfidence, object MinimumMargin,
        Dictionary<string, string> SourceSha256, string[] ScopeNotes);

    private sealed record ScenarioMetrics(string Scenario, int Updates, int VisibleUniqueFrames, int TargetAbsentFrames,
        int AcceptedTrackingFrames, int CorrectLocalizations, double? CorrectRateOnVisibleUniqueFrames,
        int FalseAcceptancesWhenTargetAbsent, double? FalseAcceptanceRateWhenTargetAbsent,
        int WrongLocalizations, int AmbiguousAcceptances, int LowVisibilityAcceptances, int Abstentions,
        int LowVisibilityFrames, double? MeanIou, double? MeanCenterErrorPx, double? P50UpdateMs,
        double? P95UpdateMs, double? P99UpdateMs, double? MaxUpdateMs, double? UpdatesPerSecond,
        double? AllocatedBytesPerUpdate);

    private sealed record BenchmarkSummary(RunMetadata Metadata, int TraceRows, int Updates, int Sequences,
        int InitializationFailures, double MeasurementWallSeconds, ScenarioMetrics Aggregate,
        ScenarioMetrics[] ByScenario, int Gen0Collections, int Gen1Collections, int Gen2Collections,
        long TotalAllocatedBytes);
}

public sealed class PatchTrackerBenchmarkAttribute : FactAttribute
{
    public PatchTrackerBenchmarkAttribute()
    {
        if (Environment.GetEnvironmentVariable("DCU_TRACKING_BENCHMARK") != "1")
            Skip = "Opt-in benchmark. Set DCU_TRACKING_BENCHMARK=1 to run the seeded stress suite.";
    }
}
