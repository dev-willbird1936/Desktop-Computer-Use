using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ShadowUse.Vision;
using Xunit.Abstractions;

namespace ShadowUse.Tests;

public sealed class JevBoxSpeedBenchmarkTests(ITestOutputHelper output)
{
    private const string ExpectedImageSha256 = "218a6d734b0ca7ee4d3eb74eff9a872d5ff7ed7e17fcde269387fdf1d56f3063";
    private const string JevModel = "jev-1.14.0";
    private const string JevEndpoint = "https://api.typesafe.ai/v1/systemone";
    private const double HitThreshold = 0.50;
    private static readonly Rectangle CardStrip = new(160, 410, 710, 213);
    private static readonly (string Id, string Description)[] CardTargets = Enumerable.Range(1, 2)
        .SelectMany(row => Enumerable.Range(1, 8).Select(column =>
        {
            string id = $"r{row}c{column}";
            return (id, $"the complete selectable hero card frame in row {row}, column {column} of the two-row hero selection grid");
        })).ToArray();

    [Fact]
    public async Task OptionalFrozenImageJevBoxSpeedBenchmark()
    {
        if (Environment.GetEnvironmentVariable("DCU_JEV_BOX_SPEED_BENCHMARK") != "1") return;

        string root = FindRepositoryRoot();
        string imagePath = Path.Combine(root, "bench", "results", "jev-1.14-btdb2-hero-selection-20261015", "input-redacted.png");
        string referencePath = Path.Combine(root, "bench", "results", "jev-1.14-btdb2-hero-selection-20261015", "same-image-box-comparison.json");
        byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
        string imageSha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        Assert.Equal(ExpectedImageSha256, imageSha256);
        var imageReferences = ReadReferences(referencePath);
        Assert.Equal(16, imageReferences.Count);

        string? apiKey = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "TYPESAFE_API_KEY must be present; its value is never written to the result.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var runs = new List<BenchmarkRun>();

        BenchmarkRun localWarmup = await RunLocalAsync(imageBytes, imageSha256, imageReferences, "warmup", 0);
        runs.Add(localWarmup);
        BenchmarkRun jevWarmup = await RunJevAsync(http, apiKey!, imageBytes, imageSha256, imageReferences, "warmup", 0);
        runs.Add(jevWarmup);

        bool warmupsPassed = localWarmup.Error is null && jevWarmup.Error is null;
        if (warmupsPassed)
        {
            for (int pair = 1; pair <= 5; pair++)
            {
                if (pair % 2 == 1)
                {
                    runs.Add(await RunLocalAsync(imageBytes, imageSha256, imageReferences, "measured", pair));
                    runs.Add(await RunJevAsync(http, apiKey!, imageBytes, imageSha256, imageReferences, "measured", pair));
                }
                else
                {
                    runs.Add(await RunJevAsync(http, apiKey!, imageBytes, imageSha256, imageReferences, "measured", pair));
                    runs.Add(await RunLocalAsync(imageBytes, imageSha256, imageReferences, "measured", pair));
                }
            }
        }

        var localMeasured = runs.Where(run => run.Method == "bloons-template" && run.Phase == "measured" && run.Error is null).ToArray();
        var jevMeasured = runs.Where(run => run.Method == "jev-box" && run.Phase == "measured" && run.Error is null).ToArray();
        double? localMedian = localMeasured.Length == 5 ? Median(localMeasured.Select(run => run.ElapsedMs).ToArray()) : null;
        double? jevMedian = jevMeasured.Length == 5 ? Median(jevMeasured.Select(run => run.ElapsedMs).ToArray()) : null;
        var localQuality = AggregateScore(localMeasured);
        var jevQuality = AggregateScore(jevMeasured);
        bool? qualityNotReduced = localQuality is not null && jevQuality is not null
            ? jevQuality.MeanHits >= localQuality.MeanHits && jevQuality.MeanIou >= localQuality.MeanIou
            : null;
        var pairedTimings = Enumerable.Range(1, 5).Select(pair =>
        {
            double? local = localMeasured.FirstOrDefault(run => run.Pair == pair)?.ElapsedMs;
            double? jev = jevMeasured.FirstOrDefault(run => run.Pair == pair)?.ElapsedMs;
            return new { pair, local_ms = local, jev_ms = jev, jev_speedup = local is > 0 && jev is > 0 ? local / jev : (double?)null };
        }).ToArray();
        double? speedup = localMedian is > 0 && jevMedian is > 0 ? localMedian / jevMedian : null;
        bool? jevFasterWithoutQualityLoss = speedup is { } ratio && qualityNotReduced is { } quality ? ratio > 1 && quality : null;

        string runId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        string outputDirectory = Path.Combine(root, "bench", "results", "jev-box-speed-" + runId);
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, "benchmark.json");
        var report = new
        {
            schema_version = 1,
            title = "Frozen-image Jev Box vs DCU calibrated BTD2 detector speed benchmark",
            input = new { path = Path.GetRelativePath(root, imagePath), sha256 = imageSha256, bytes = imageBytes.Length, width = 1272, height = 713 },
            reference = new { path = Path.GetRelativePath(root, referencePath), method = "existing exact-image one-to-one maximum-IoU Hungarian matching", hit_threshold_iou = HitThreshold, target_count = imageReferences.Count },
            task = new { description = "Return 16 individual hero card frame boxes; no hero-name recognition or action selection.", targets = CardTargets.Select(target => new { id = target.Id, description = target.Description }), proposal_roi = new { x_min = CardStrip.Left, x_max = CardStrip.Right, y_min = CardStrip.Top, y_max = CardStrip.Bottom } },
            timing_contract = "Preloaded PNG bytes and fixed task specification to complete, parsed, normalized pixel-space box list. Local path includes PNG decode, production calibrated detector, card proposal filtering and normalization. Jev path includes Box-question construction, image base64 and JSON serialization, HTTP upload/network/full response, JSON parsing and box normalization. File I/O, process startup, overlays, logging and reference scoring are excluded. One unscored warm-up per path; five measured pairs in alternating order; HTTP client is reused; no answer cache or request retries.",
            methods = new
            {
                local = new { name = "DCU FastUiCandidateDetector method=bloons-template", scope = "Production local calibrated BTD2 detector; only the 16 hero-card boxes in the specified task are scored." },
                jev = new { model = JevModel, name = "TypeSafe image-backed Box batch", scope = "One Box question per card in one API request; no action is dispatched. Redacted screenshot only." },
            },
            warmups_passed = warmupsPassed,
            local_median_ms = localMedian,
            local_range_ms = Range(localMeasured.Select(run => run.ElapsedMs).ToArray()),
            jev_median_ms = jevMedian,
            jev_range_ms = Range(jevMeasured.Select(run => run.ElapsedMs).ToArray()),
            paired_timings = pairedTimings,
            local_quality = localQuality,
            jev_quality = jevQuality,
            jev_speedup_ratio_local_over_jev = speedup,
            quality_not_reduced = qualityNotReduced,
            jev_faster_without_quality_loss = jevFasterWithoutQualityLoss,
            run_count = runs.Count,
            runs,
            limitations = new[]
            {
                "This compares Jev Box with a DCU calibrated localization component on one frozen BTD2 hero-selection screen; it does not measure the full normal agent-driven DCU loop or the Hero Challenge action.",
                "A speed improvement is not claimed unless all five pairs succeeded and Jev is faster without lower hit count or mean IoU.",
                "No game capture, input, purchase, or matchmaking occurred.",
            },
        };
        await using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });

        output.WriteLine(JsonSerializer.Serialize(new
        {
            output = outputPath,
            warmups_passed = warmupsPassed,
            local_median_ms = localMedian,
            jev_median_ms = jevMedian,
            paired_timings = pairedTimings,
            local_quality = localQuality,
            jev_quality = jevQuality,
            jev_speedup_ratio_local_over_jev = speedup,
            jev_faster_without_quality_loss = jevFasterWithoutQualityLoss,
            failures = runs.Where(run => run.Error is not null).Select(run => new { run.Method, run.Phase, run.Pair, run.Error }),
        }));

        Assert.True(warmupsPassed, "A benchmark warm-up failed; see the preserved benchmark JSON.");
        Assert.Equal(5, localMeasured.Length);
        Assert.Equal(5, jevMeasured.Length);
    }

    private static async Task<BenchmarkRun> RunLocalAsync(byte[] imageBytes, string imageSha256,
        IReadOnlyDictionary<string, PixelBox> references, string phase, int pair)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var decoded = new Bitmap(stream);
            using var bitmap = new Bitmap(decoded);
            var detection = await FastUiCandidateDetector.DetectAsync(bitmap, "bloons-template", "image-" + imageSha256[..20],
                maxCandidates: 128).ConfigureAwait(false);
            var boxes = detection.Candidates
                .Where(candidate => candidate.Key.StartsWith("hero-r", StringComparison.Ordinal))
                .Select(candidate => new PixelBox(candidate.X, candidate.Y, candidate.X + candidate.Width, candidate.Y + candidate.Height))
                .Where(box => IsInProposalRoi(box))
                .ToArray();
            timer.Stop();
            var score = Score(boxes, references);
            return new BenchmarkRun("bloons-template", phase, pair, timer.Elapsed.TotalMilliseconds,
                detection.PerceptionMs, detection.CandidateSetComplete, detection.CandidateSetIssue, boxes, score, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            timer.Stop();
            return new BenchmarkRun("bloons-template", phase, pair, timer.Elapsed.TotalMilliseconds,
                null, false, null, [], null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task<BenchmarkRun> RunJevAsync(HttpClient http, string apiKey, byte[] imageBytes, string imageSha256,
        IReadOnlyDictionary<string, PixelBox> references, string phase, int pair)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            string imageReference = $"frozen-{imageSha256[..12]}-{phase}-{pair}-{Guid.NewGuid():N}";
            var questions = CardTargets.ToDictionary(target => "box_" + target.Id, target => (object)new
            {
                type = "box",
                instructions = $"A tight box around the complete selectable hero card frame at row {target.Id[1]}, column {target.Id[3]} in the two-row hero selection grid. Include the frame border; use the screenshot only and do not infer clickability or hero identity.",
            }, StringComparer.Ordinal);
            var state = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["goal"] = "Return one tight box for each of the 16 visible selectable hero-card frames. This is image localization only; do not choose an action.",
                ["observation_id"] = imageReference,
                ["context"] = "Frozen redacted BTD2 hero-selection screenshot; target set is exactly two rows by eight columns.",
                ["candidates"] = Array.Empty<object>(),
                ["candidate_count"] = 0,
                ["candidate_set_complete"] = true,
                ["candidate_set_issue"] = null,
                ["expires_at_utc"] = null,
                ["observed_text"] = null,
                ["observed_text_truncated"] = false,
                ["candidate_role_filter"] = null,
                ["key_bindings"] = Array.Empty<object>(),
                ["recent_actions"] = Array.Empty<string>(),
                ["screenshot"] = imageReference,
            };
            var body = new Dictionary<string, object?>
            {
                ["model"] = JevModel,
                ["state"] = state,
                ["questions"] = questions,
                ["assets"] = new Dictionary<string, object>
                {
                    [imageReference] = new { type = "image", format = "png", data_base64 = Convert.ToBase64String(imageBytes) },
                },
            };
            string bodyJson = JsonSerializer.Serialize(body);
            using var request = new HttpRequestMessage(HttpMethod.Post, JevEndpoint)
            {
                Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("TypeSafe-Danger-Zone-Images", "true");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Jev HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(responseJson.Length, 1000)]}");

            using var document = JsonDocument.Parse(responseJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("answers", out JsonElement answers) || answers.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Jev response omitted answers.");
            var boxes = new List<PixelBox>(CardTargets.Length);
            foreach (var target in CardTargets)
            {
                string questionId = "box_" + target.Id;
                if (!answers.TryGetProperty(questionId, out JsonElement answer)
                    || !answer.TryGetProperty("type", out JsonElement type) || type.GetString() != "box"
                    || !answer.TryGetProperty("box", out JsonElement coordinates))
                    throw new InvalidDataException($"Jev response omitted valid Box answer '{questionId}'.");
                double top = coordinates.GetProperty("top").GetDouble();
                double bottom = coordinates.GetProperty("bottom").GetDouble();
                double left = coordinates.GetProperty("left").GetDouble();
                double right = coordinates.GetProperty("right").GetDouble();
                if (!double.IsFinite(top) || !double.IsFinite(bottom) || !double.IsFinite(left) || !double.IsFinite(right)
                    || top < 0 || left < 0 || bottom > 1 || right > 1 || top >= bottom || left >= right)
                    throw new InvalidDataException($"Jev Box '{questionId}' was empty or outside normalized bounds.");
                boxes.Add(new PixelBox(left * 1272, top * 713, right * 1272, bottom * 713));
            }
            timer.Stop();
            var score = Score(boxes, references);
            string? reportedModel = root.TryGetProperty("model", out JsonElement modelElement) && modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString() : null;
            double? avgConfidence = null;
            var confidences = CardTargets.Select(target => answers.GetProperty("box_" + target.Id))
                .Where(answer => answer.TryGetProperty("confidence", out _))
                .Select(answer => answer.GetProperty("confidence").GetDouble()).ToArray();
            if (confidences.Length > 0) avgConfidence = confidences.Average();
            return new BenchmarkRun("jev-box", phase, pair, timer.Elapsed.TotalMilliseconds,
                null, null, null, boxes, score, null, reportedModel, avgConfidence);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            timer.Stop();
            return new BenchmarkRun("jev-box", phase, pair, timer.Elapsed.TotalMilliseconds,
                null, null, null, [], null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static IReadOnlyDictionary<string, PixelBox> ReadReferences(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var result = new Dictionary<string, PixelBox>(StringComparer.Ordinal);
        foreach (var target in root.GetProperty("target_reference").GetProperty("targets").EnumerateArray())
        {
            string id = target.GetProperty("id").GetString()!;
            var box = target.GetProperty("bbox");
            double x = box.GetProperty("x").GetDouble(), y = box.GetProperty("y").GetDouble();
            result.Add(id, new PixelBox(x, y, x + box.GetProperty("width").GetDouble(), y + box.GetProperty("height").GetDouble()));
        }
        return result;
    }

    private static bool IsInProposalRoi(PixelBox box)
    {
        double centerX = (box.Left + box.Right) / 2, centerY = (box.Top + box.Bottom) / 2;
        return centerX >= CardStrip.Left && centerX < CardStrip.Right && centerY >= CardStrip.Top && centerY < CardStrip.Bottom;
    }

    private static BoxScore Score(IReadOnlyList<PixelBox> predictions, IReadOnlyDictionary<string, PixelBox> references)
    {
        var targets = CardTargets.Select(target => references[target.Id]).ToArray();
        int rows = targets.Length, columns = Math.Max(rows, predictions.Count);
        var cost = new double[rows + 1, columns + 1];
        for (int row = 1; row <= rows; row++)
            for (int column = 1; column <= columns; column++)
                cost[row, column] = column <= predictions.Count ? 1 - IoU(targets[row - 1], predictions[column - 1]) : 1;
        int[] assignment = Hungarian(cost, rows, columns);
        var ious = new double[rows];
        for (int row = 1; row <= rows; row++)
        {
            int column = assignment[row];
            ious[row - 1] = column is > 0 && column <= predictions.Count
                ? IoU(targets[row - 1], predictions[column - 1]) : 0;
        }
        int hits = ious.Count(value => value >= HitThreshold);
        int falsePositives = Math.Max(0, predictions.Count - hits);
        double precision = hits + falsePositives == 0 ? 0 : hits / (double)(hits + falsePositives);
        double recall = hits / (double)rows;
        return new BoxScore(hits, rows, predictions.Count, falsePositives, rows - hits,
            precision, recall, ious.Average(), ious);
    }

    private static int[] Hungarian(double[,] cost, int rows, int columns)
    {
        var u = new double[rows + 1];
        var v = new double[columns + 1];
        var p = new int[columns + 1];
        var way = new int[columns + 1];
        for (int i = 1; i <= rows; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, columns + 1).ToArray();
            var used = new bool[columns + 1];
            do
            {
                used[j0] = true;
                int i0 = p[j0], j1 = 0;
                double delta = double.PositiveInfinity;
                for (int j = 1; j <= columns; j++)
                {
                    if (used[j]) continue;
                    double current = cost[i0, j] - u[i0] - v[j];
                    if (current < minv[j]) { minv[j] = current; way[j] = j0; }
                    if (minv[j] < delta) { delta = minv[j]; j1 = j; }
                }
                for (int j = 0; j <= columns; j++)
                {
                    if (used[j]) { u[p[j]] += delta; v[j] -= delta; }
                    else minv[j] -= delta;
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }
        var assignment = new int[rows + 1];
        for (int j = 1; j <= columns; j++)
            if (p[j] != 0) assignment[p[j]] = j;
        return assignment;
    }

    private static double IoU(PixelBox a, PixelBox b)
    {
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double intersection = width * height;
        double union = (a.Right - a.Left) * (a.Bottom - a.Top) + (b.Right - b.Left) * (b.Bottom - b.Top) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double Median(double[] values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? double.NaN : sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static object? Range(double[] values) => values.Length == 0 ? null : new { min = values.Min(), max = values.Max() };

    private static AggregateQuality? AggregateScore(BenchmarkRun[] runs)
    {
        var scores = runs.Where(run => run.Score is not null).Select(run => run.Score!).ToArray();
        if (scores.Length != 5) return null;
        return new AggregateQuality(
            scores.Average(score => score.Hits), scores.Average(score => score.MeanIou),
            scores.Average(score => score.Precision), scores.Average(score => score.Recall),
            scores.Average(score => score.PredictionCount), scores.Average(score => score.FalsePositives),
            scores.Average(score => score.FalseNegatives));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new DirectoryInfo(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "bench", "results", "jev-1.14-btdb2-hero-selection-20261015", "input-redacted.png")))
                return current.FullName;
        throw new DirectoryNotFoundException("Could not find repository root containing the saved BTD2 benchmark image.");
    }

    private sealed record PixelBox(double Left, double Top, double Right, double Bottom);
    private sealed record BoxScore(int Hits, int TargetCount, int PredictionCount, int FalsePositives, int FalseNegatives,
        double Precision, double Recall, double MeanIou, IReadOnlyList<double> PerTargetIou);
    private sealed record AggregateQuality(double MeanHits, double MeanIou, double MeanPrecision, double MeanRecall,
        double MeanPredictionCount, double MeanFalsePositives, double MeanFalseNegatives);
    private sealed record BenchmarkRun(string Method, string Phase, int Pair, double ElapsedMs,
        double? DetectorPerceptionMs, bool? CandidateSetComplete, string? CandidateSetIssue,
        IReadOnlyList<PixelBox> Boxes, BoxScore? Score, string? Error,
        string? ReportedModel = null, double? MeanBoxConfidence = null);
}
