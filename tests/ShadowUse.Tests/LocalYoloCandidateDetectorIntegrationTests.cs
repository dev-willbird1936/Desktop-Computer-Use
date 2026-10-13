using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

/// <summary>Opt-in smoke test for the resident Python worker; requires local model files and never contacts Jev.</summary>
public sealed class LocalYoloCandidateDetectorIntegrationTests
{
    private sealed class FakeJevHandler(string responseJson) : HttpMessageHandler
    {
        public int Calls;
        public string? RequestJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record FixtureTarget(string Id, double X, double Y, double Width, double Height, bool IsCard = false);

    private static readonly FixtureTarget[] SyntheticUiTargets =
    [
        new("close", 1192, 30, 56, 56),
        new("search", 80, 132, 360, 60),
        new("dropdown", 466, 132, 244, 60),
        new("avatar", 1120, 128, 70, 70),
        new("card-1", 80, 228, 240, 350, IsCard: true),
        new("card-2", 350, 228, 240, 350, IsCard: true),
        new("card-3", 620, 228, 240, 350, IsCard: true),
        new("star-1", 260, 252, 36, 36),
        new("star-2", 530, 252, 36, 36),
        new("star-3", 800, 252, 36, 36),
        new("page-previous", 900, 540, 70, 70),
        new("page-next", 1120, 540, 70, 70),
        new("continue", 990, 674, 200, 70),
    ];

    private static readonly FixtureTarget[] SettingsUiTargets =
    [
        new("close", 1200, 24, 56, 56),
        new("nav-general", 18, 112, 252, 54),
        new("nav-audio", 18, 180, 252, 54),
        new("nav-controls", 18, 248, 252, 54),
        new("nav-account", 18, 316, 252, 54),
        new("notification-checkbox", 352, 222, 34, 34),
        new("auto-updates-toggle", 1110, 286, 76, 40),
        new("language-dropdown", 848, 358, 338, 56),
        new("volume-slider", 852, 464, 326, 36),
        new("radio-small", 470, 598, 89, 31),
        new("radio-medium", 640, 598, 121, 31),
        new("radio-large", 805, 598, 90, 31),
        new("cancel", 904, 708, 134, 60),
        new("apply", 1056, 708, 164, 60),
    ];

    private static double? FixtureMatchScore(JsonElement candidate, FixtureTarget target)
    {
        var bbox = candidate.GetProperty("metadata").GetProperty("bbox");
        double x = bbox.GetProperty("x").GetDouble();
        double y = bbox.GetProperty("y").GetDouble();
        double width = bbox.GetProperty("width").GetDouble();
        double height = bbox.GetProperty("height").GetDouble();
        double right = x + width, bottom = y + height;
        if (target.IsCard)
        {
            double intersectionWidth = Math.Max(0, Math.Min(right, target.X + target.Width) - Math.Max(x, target.X));
            double intersectionHeight = Math.Max(0, Math.Min(bottom, target.Y + target.Height) - Math.Max(y, target.Y));
            double intersection = intersectionWidth * intersectionHeight;
            double union = width * height + target.Width * target.Height - intersection;
            double iou = union > 0 ? intersection / union : 0;
            return iou >= 0.5 ? iou : null;
        }

        double centerX = target.X + target.Width / 2d;
        double centerY = target.Y + target.Height / 2d;
        double areaLimit = 8d * target.Width * target.Height;
        double area = width * height;
        return x <= centerX && centerX <= right && y <= centerY && centerY <= bottom && area <= areaLimit
            ? 1 - area / areaLimit
            : null;
    }

    private static string NearestCandidateSummary(JsonElement[] candidates, FixtureTarget target)
    {
        double centerX = target.X + target.Width / 2d;
        double centerY = target.Y + target.Height / 2d;
        return string.Join("; ", candidates.Select(candidate =>
        {
            var bbox = candidate.GetProperty("metadata").GetProperty("bbox");
            double x = bbox.GetProperty("x").GetDouble(), y = bbox.GetProperty("y").GetDouble();
            double width = bbox.GetProperty("width").GetDouble(), height = bbox.GetProperty("height").GetDouble();
            double dx = x + width / 2d - centerX, dy = y + height / 2d - centerY;
            string name = candidate.GetProperty("name").GetString() ?? "";
            return (Distance: dx * dx + dy * dy, Text: $"{name} @({x:0},{y:0} {width:0}x{height:0})");
        }).OrderBy(candidate => candidate.Distance).Take(3).Select(candidate => candidate.Text));
    }

    private static void AssertFixtureTargetsReachJev(JsonElement candidates, IReadOnlyList<FixtureTarget> targets, string scope)
    {
        var candidateArray = candidates.EnumerateArray().ToArray();
        var pairs = new List<(double Score, int TargetIndex, int CandidateIndex)>();
        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            for (int candidateIndex = 0; candidateIndex < candidateArray.Length; candidateIndex++)
                if (FixtureMatchScore(candidateArray[candidateIndex], targets[targetIndex]) is { } score)
                    pairs.Add((score, targetIndex, candidateIndex));

        pairs.Sort((left, right) =>
        {
            int order = right.Score.CompareTo(left.Score);
            if (order != 0) return order;
            order = right.TargetIndex.CompareTo(left.TargetIndex);
            return order != 0 ? order : right.CandidateIndex.CompareTo(left.CandidateIndex);
        });
        var matchedTargets = new HashSet<int>();
        var matchedCandidates = new HashSet<int>();
        foreach (var pair in pairs)
            if (matchedTargets.Add(pair.TargetIndex))
                if (!matchedCandidates.Add(pair.CandidateIndex)) matchedTargets.Remove(pair.TargetIndex);

        var missedTargets = targets.Where((_, index) => !matchedTargets.Contains(index)).ToArray();
        var details = missedTargets.Select(target => $"{target.Id} nearest: {NearestCandidateSummary(candidateArray, target)}");
        Assert.True(missedTargets.Length == 0,
            $"{scope} targets missing from fake Jev candidate payload: {string.Join("; ", details)}.");
    }

    // The alert action is transient and is absent from the earlier clean inference frame.
    // Check only the seven controls whose layout persists across the Anvil frame transfer.
    private static readonly FixtureTarget[] AnvilStableTransferTargets =
    [
        new("chat-toolbar-1", 1855, 1067, 43, 45),
        new("chat-toolbar-2", 1899, 1067, 44, 45),
        new("chat-toolbar-3", 1944, 1067, 44, 45),
        new("chat-scroll-control", 2505, 1305, 40, 48),
        new("chat-log-control", 1852, 1362, 112, 44),
        new("chat-input-region", 1964, 1362, 555, 44),
        new("lower-left-town-control", 39, 1194, 70, 68),
    ];

    private static void AssertAnvilStableTransferTargetsReachJev(JsonElement candidates, int imageWidth, int imageHeight)
    {
        const double referenceWidth = 2546, referenceHeight = 1439;
        double scaleX = imageWidth / referenceWidth, scaleY = imageHeight / referenceHeight;
        var scaledTargets = AnvilStableTransferTargets.Select(target => target with
        {
            X = target.X * scaleX,
            Y = target.Y * scaleY,
            Width = target.Width * scaleX,
            Height = target.Height * scaleY,
        }).ToArray();
        AssertFixtureTargetsReachJev(candidates, scaledTargets, "Stable approximate cross-frame Anvil");
    }

    private static void AssertSettingsUiTargetsReachJev(JsonElement candidates)
        => AssertFixtureTargetsReachJev(candidates, SettingsUiTargets, "Synthetic settings UI");

    private static void AssertNearbySettingsTextClues(JsonElement candidates)
    {
        var names = candidates.EnumerateArray()
            .Select(candidate => candidate.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Enable notifications", StringComparison.OrdinalIgnoreCase)),
            "The checkbox proposal had no nearby OCR clue.");
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Automatic updates", StringComparison.OrdinalIgnoreCase)),
            "The toggle proposal had no nearby OCR clue.");
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Master", StringComparison.OrdinalIgnoreCase)
            && name.Contains("vol", StringComparison.OrdinalIgnoreCase)),
            "The slider proposal had no nearby OCR clue.");
    }

    private static void AssertSyntheticUiTargetsReachJev(JsonElement state, JsonElement candidates)
    {
        AssertFixtureTargetsReachJev(candidates, SyntheticUiTargets, "Synthetic UI");

        string observedText = state.GetProperty("observed_text").GetString() ?? string.Empty;
        Assert.Contains("Search", observedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Continue", observedText, StringComparison.OrdinalIgnoreCase);
        var names = candidates.EnumerateArray()
            .Select(candidate => candidate.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Assert.True(names.Any(name => name.Contains("Search", StringComparison.OrdinalIgnoreCase)),
            "No Jev candidate carried a search-text clue.");
        Assert.True(names.Any(name => name.Contains("Continue", StringComparison.OrdinalIgnoreCase)),
            "No Jev candidate carried a Continue-text clue.");
    }

    [Fact]
    public async Task ResidentWorkerReturnsFrameBoundCandidatesAcrossOneProcessLifetime()
    {
        string? python = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_PYTHON");
        string? weights = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_WEIGHTS");
        string? imagePath = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python)
            || string.IsNullOrWhiteSpace(weights) || !File.Exists(weights)
            || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw Xunit.Sdk.SkipException.ForSkip("Set DCU_LOCAL_YOLO_TEST_PYTHON, DCU_LOCAL_YOLO_TEST_WEIGHTS, and DCU_LOCAL_YOLO_TEST_IMAGE to run the offline model smoke test.");

        using var bitmap = new Bitmap(imagePath);
        using var jpegStream = new MemoryStream();
        bitmap.Save(jpegStream, ImageFormat.Jpeg);
        byte[] jpeg = jpegStream.ToArray();
        if (Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_DUMP_JPEG") is { Length: > 0 } jpegDumpPath)
            await File.WriteAllBytesAsync(jpegDumpPath, jpeg);
        int width = bitmap.Width, height = bitmap.Height;
        bool expectCalibratedTemplate = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_EXPECT_TEMPLATE") == "1";
        bool expectSyntheticUiTargets = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_EXPECT_SYNTHETIC_UI") == "1";
        bool expectSettingsUiTargets = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_EXPECT_SETTINGS_UI") == "1";
        bool expectAnvilStableTransferTargets = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_EXPECT_ANVIL_STABLE_TRANSFER") == "1";
        string localMethod = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_LOCAL_METHOD") ?? "combined";
        Assert.Contains(localMethod, new[] { "edge", "ocr", "combined" });
        int yoloImageSize = int.TryParse(Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_IMAGE_SIZE"), out var configuredImageSize)
            ? configuredImageSize
            : 1536;
        Assert.Contains(yoloImageSize, new[] { 640, 960, 1280, 1536 });
        var settings = new ShadowSettings
        {
            LocalUiYoloPythonExecutable = python,
            LocalUiYoloWeightsPath = weights,
            LocalUiYoloDevice = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_DEVICE") ?? "cuda",
            LocalUiYoloImageSize = yoloImageSize,
        };

        await using var detector = new LocalYoloCandidateDetector(settings);
        await detector.EnsureReadyAsync();
        LocalYoloDetectionResult? latest = null;
        var detectionRuns = new List<LocalYoloDetectionResult>(3);
        var detectionSignatures = new HashSet<string>(StringComparer.Ordinal);
        for (int run = 0; run < 3; run++)
        {
            latest = await detector.DetectAsync(jpeg, $"integration-frame-{run + 1}", width, height);
            detectionRuns.Add(latest);
            Assert.Equal(width, latest.ImageWidth);
            Assert.Equal(height, latest.ImageHeight);
            Assert.NotEmpty(latest.Detections);
            Assert.All(latest.Detections, candidate =>
            {
                Assert.True(candidate.X >= 0 && candidate.Y >= 0);
                Assert.True(candidate.Width > 0 && candidate.Height > 0);
                Assert.True((long)candidate.X + candidate.Width <= width);
                Assert.True((long)candidate.Y + candidate.Height <= height);
            });
            detectionSignatures.Add(JsonSerializer.Serialize(latest.Detections.Select(candidate => new
            {
                candidate.Key, candidate.Role, candidate.Label, candidate.Score,
                candidate.X, candidate.Y, candidate.Width, candidate.Height,
            })));
        }
        if (Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_REQUIRE_STABLE_DETECTIONS") == "1")
            Assert.Single(detectionSignatures);

        var local = await FastUiCandidateDetector.DetectAsync(bitmap, localMethod, "integration-frame-3");
        var template = await FastUiCandidateDetector.DetectAsync(
            bitmap, "bloons-template", "integration-frame-3", observedText: local.ObservedText);
        if (expectCalibratedTemplate) Assert.Equal(23, template.Candidates.Count);
        if (template.Candidates.Count > 0)
        {
            string? templateIssue = string.Join(" ", new[] { local.CandidateSetIssue, template.CandidateSetIssue }
                .Where(issue => !string.IsNullOrWhiteSpace(issue)));
            local = new FastUiDetectionResult(
                "combined+calibrated-bloons-layout", false, templateIssue, local.ObservedText,
                local.Candidates.Concat(template.Candidates).ToArray(), local.PerceptionMs + template.PerceptionMs,
                local.UnsupportedReason ?? template.UnsupportedReason);
        }
        GameUiObservation CreateObservation(LocalYoloDetectionResult detections)
            => LocalGameUiCandidateFusion.CreateObservation(
                "integration-frame-3", DateTimeOffset.UtcNow, "local-test-window", width, height, width, height,
                1, 96, new NativeMethods.RECT { Left = 0, Top = 0, Right = width, Bottom = height }, detections, local);

        if (expectAnvilStableTransferTargets)
            foreach (var detectionRun in detectionRuns)
            {
                var repeatedCandidates = GameJevCandidateMapper.Map(
                    CreateObservation(detectionRun), source: GameJevCandidateMapper.LocalYoloVisualSource);
                var repeatedCandidateJson = JsonSerializer.SerializeToElement(repeatedCandidates.Select(element => new
                {
                    name = element.Name,
                    metadata = element.Extra,
                }).ToArray());
                AssertAnvilStableTransferTargetsReachJev(repeatedCandidateJson, width, height);
            }

        var observation = CreateObservation(latest!);
        Assert.False(observation.CandidateSetComplete);
        var elements = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.LocalYoloVisualSource);
        Assert.NotEmpty(elements);
        Assert.InRange(elements.Count, 1,
            LocalGameUiCandidateFusion.MaxYoloCandidates + LocalGameUiCandidateFusion.MaxLocalCandidates);

        const string keyName = "DCU_LOCAL_YOLO_FAKE_JEV_KEY";
        Environment.SetEnvironmentVariable(keyName, "fake-key");
        var targetProbabilities = Enumerable.Range(1, elements.Count)
            .ToDictionary(index => index.ToString(), index => index == 1 ? 1.0 : 0.0);
        var responseJson = JsonSerializer.Serialize(new
        {
            model = "jev-test",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = "RECOMMEND_TARGET", confidence = 0.9, probabilities = new { RECOMMEND_TARGET = 0.9, REOBSERVE = 0.05, ASK_USER = 0.03, STOP = 0.02 } },
                ["recommend_target_target"] = new { choice = "1", confidence = 0.8, probabilities = targetProbabilities },
            },
        });
        var handler = new FakeJevHandler(responseJson);
        var controller = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = keyName,
            MaxRetries = 0,
        });
        var request = JevRecommendationRequestBuilder.Build(
            "Recommend one listed visible region; do not activate it.", observation.ObservationId, elements,
            observation.Scope + " Decision only.", string.Join("\n", observation.ObservedText),
            DateTimeOffset.UtcNow.AddSeconds(20), candidateSetComplete: false,
            candidateSetIssue: observation.CandidateSetIssue,
            allowIncompleteCandidateSetForRecommendationOnly: true,
            allowScoreDisambiguation: false, maxRetriesOverride: 0);

        try
        {
            var decision = await controller.DecideAsync(request);
            Assert.Equal(elements[0].Id, decision.TargetElementId);
            Assert.Equal(1, handler.Calls);
            using var json = JsonDocument.Parse(handler.RequestJson!);
            var state = json.RootElement.GetProperty("state");
            Assert.False(state.GetProperty("candidate_set_complete").GetBoolean());
            var stateCandidates = state.GetProperty("candidates");
            Assert.Equal(elements.Count, stateCandidates.GetArrayLength());
            if (Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_DUMP_REQUEST") is { Length: > 0 } dumpPath)
                await File.WriteAllTextAsync(dumpPath, handler.RequestJson!);
            if (expectSyntheticUiTargets) AssertSyntheticUiTargetsReachJev(state, stateCandidates);
            if (expectSettingsUiTargets)
            {
                AssertSettingsUiTargetsReachJev(stateCandidates);
                if (localMethod == "combined") AssertNearbySettingsTextClues(stateCandidates);
            }
            if (expectAnvilStableTransferTargets) AssertAnvilStableTransferTargetsReachJev(stateCandidates, width, height);
            if (expectCalibratedTemplate)
                Assert.Equal(23, stateCandidates.EnumerateArray().Count(candidate =>
                    candidate.GetProperty("metadata").GetProperty("identity_source").GetString() == "bloons_calibrated_pixel_anchor"));
            var expectedMetadata = JsonSerializer.SerializeToElement(elements[0].Extra);
            var actualMetadata = stateCandidates[0].GetProperty("metadata");
            Assert.Equal(expectedMetadata.GetProperty("bbox").GetProperty("x").GetInt32(),
                actualMetadata.GetProperty("bbox").GetProperty("x").GetInt32());
            Assert.Equal(expectedMetadata.GetProperty("screen_bbox").GetProperty("y").GetInt32(),
                actualMetadata.GetProperty("screen_bbox").GetProperty("y").GetInt32());
            var criteriaMetadata = json.RootElement.GetProperty("questions")
                .GetProperty("recommend_target_target").GetProperty("criteria").GetProperty("1").GetProperty("metadata");
            Assert.Equal(GameJevCandidateMapper.LocalYoloVisualSource, criteriaMetadata.GetProperty("source").GetString());
            Assert.Equal(expectedMetadata.GetProperty("bbox").GetProperty("width").GetInt32(),
                criteriaMetadata.GetProperty("bbox").GetProperty("width").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
        }
    }
}
