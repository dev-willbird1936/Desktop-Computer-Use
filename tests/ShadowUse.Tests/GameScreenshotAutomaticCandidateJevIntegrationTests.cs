using System.Drawing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using ModelContextProtocol.Protocol;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

/// <summary>Opt-in smoke test of automatic game_screenshot discovery through the fake-JeV handoff.</summary>
public sealed class GameScreenshotAutomaticCandidateJevIntegrationTests
{
    private sealed record FixtureTarget(string Id, double X, double Y, double Width, double Height, bool IsCard = false);

    private static readonly FixtureTarget[] SettingsTargets =
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

    private static void AssertTargets(JsonElement[] candidates, IReadOnlyList<FixtureTarget> referenceTargets,
        double referenceWidth, double referenceHeight, int imageWidth, int imageHeight, string scope)
    {
        double scaleX = imageWidth / referenceWidth, scaleY = imageHeight / referenceHeight;
        var targets = referenceTargets.Select(target => target with
        {
            X = target.X * scaleX,
            Y = target.Y * scaleY,
            Width = target.Width * scaleX,
            Height = target.Height * scaleY,
        }).ToArray();
        var pairs = new List<(double Score, int Target, int Candidate)>();
        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            var target = targets[targetIndex];
            double centerX = target.X + target.Width / 2, centerY = target.Y + target.Height / 2;
            double areaLimit = target.Width * target.Height * 8;
            for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                var box = candidate.TryGetProperty("bbox", out var directBox)
                    ? directBox
                    : candidate.GetProperty("metadata").GetProperty("bbox");
                double x = box.GetProperty("x").GetDouble(), y = box.GetProperty("y").GetDouble();
                double width = box.GetProperty("width").GetDouble(), height = box.GetProperty("height").GetDouble();
                double area = width * height;
                if (target.IsCard)
                {
                    double overlapWidth = Math.Max(0, Math.Min(x + width, target.X + target.Width) - Math.Max(x, target.X));
                    double overlapHeight = Math.Max(0, Math.Min(y + height, target.Y + target.Height) - Math.Max(y, target.Y));
                    double intersection = overlapWidth * overlapHeight;
                    double union = area + target.Width * target.Height - intersection;
                    double iou = union > 0 ? intersection / union : 0;
                    if (iou >= 0.5) pairs.Add((iou, targetIndex, candidateIndex));
                }
                else if (x <= centerX && centerX <= x + width && y <= centerY && centerY <= y + height && area <= areaLimit)
                    pairs.Add((1 - area / areaLimit, targetIndex, candidateIndex));
            }
        }

        pairs.Sort((left, right) => right.Score.CompareTo(left.Score));
        var matchedTargets = new HashSet<int>();
        var matchedCandidates = new HashSet<int>();
        foreach (var pair in pairs)
            if (!matchedTargets.Contains(pair.Target) && matchedCandidates.Add(pair.Candidate))
                matchedTargets.Add(pair.Target);
        var missed = targets.Where((_, index) => !matchedTargets.Contains(index)).Select(target => target.Id).ToArray();
        Assert.True(missed.Length == 0,
            $"GameTools screenshot candidates missed {scope} targets: {string.Join(", ", missed)}; candidate count={candidates.Length}.");
    }

    private static void AssertSettingsTargets(JsonElement[] candidates, int imageWidth, int imageHeight)
        => AssertTargets(candidates, SettingsTargets, 1280, 800, imageWidth, imageHeight, "settings-fixture");

    private static void AssertSyntheticTargets(JsonElement[] candidates, int imageWidth, int imageHeight)
        => AssertTargets(candidates, SyntheticUiTargets, 1280, 800, imageWidth, imageHeight, "synthetic generic UI");

    private static void AssertAnvilTargets(JsonElement[] candidates, int imageWidth, int imageHeight)
        => AssertTargets(candidates, AnvilStableTransferTargets, 2546, 1439, imageWidth, imageHeight, "stable cross-frame Anvil");

    private static void AssertSyntheticTextClues(JsonElement state, JsonElement[] candidates)
    {
        string observedText = state.GetProperty("observed_text").GetString() ?? string.Empty;
        Assert.Contains("Search", observedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Continue", observedText, StringComparison.OrdinalIgnoreCase);
        var names = candidates.Select(candidate => candidate.GetProperty("name").GetString() ?? string.Empty).ToArray();
        Assert.True(names.Any(name => name.Contains("Search", StringComparison.OrdinalIgnoreCase)),
            "No fake-JeV candidate carried a Search clue.");
        Assert.True(names.Any(name => name.Contains("Continue", StringComparison.OrdinalIgnoreCase)),
            "No fake-JeV candidate carried a Continue clue.");
    }

    private static void AssertNearbySettingsTextClues(JsonElement[] candidates)
    {
        var names = candidates.Select(candidate => candidate.GetProperty("name").GetString() ?? string.Empty).ToArray();
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Enable notifications", StringComparison.OrdinalIgnoreCase)),
            "The checkbox proposal had no nearby OCR clue in the outgoing Jev request.");
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Automatic updates", StringComparison.OrdinalIgnoreCase)),
            "The toggle proposal had no nearby OCR clue in the outgoing Jev request.");
        Assert.True(names.Any(name => name.Contains("nearby unverified OCR text", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Master", StringComparison.OrdinalIgnoreCase)
            && name.Contains("vol", StringComparison.OrdinalIgnoreCase)),
            "The slider proposal had no nearby OCR clue in the outgoing Jev request.");
    }

    [Fact]
    public async Task GameScreenshotBuildsLocalInventoryAndSendsItToFakeJev()
    {
        if (Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_FAKE_JEV_SMOKE") != "1")
            throw Xunit.Sdk.SkipException.ForSkip("Set DCU_GAME_SCREENSHOT_FAKE_JEV_SMOKE=1 to capture a local non-activating fixture window.");

        string? python = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_PYTHON");
        string? weights = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_WEIGHTS");
        string? fixturePath = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_IMAGE");
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python)
            || string.IsNullOrWhiteSpace(weights) || !File.Exists(weights)
            || string.IsNullOrWhiteSpace(fixturePath) || !File.Exists(fixturePath))
            throw Xunit.Sdk.SkipException.ForSkip("Set DCU_LOCAL_YOLO_TEST_PYTHON, DCU_LOCAL_YOLO_TEST_WEIGHTS, and DCU_LOCAL_YOLO_TEST_IMAGE to run this local fake-JeV smoke test.");

        int imageSize = int.TryParse(Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_IMAGE_SIZE"), out var configuredImageSize)
            ? configuredImageSize
            : 1536;
        string localMethod = Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_LOCAL_METHOD") ?? "edge";
        int repeatCount = int.TryParse(Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_REPEATS"), out var configuredRepeats)
            ? configuredRepeats : 1;
        bool expectSettingsTargets = Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_EXPECT_SETTINGS_UI") == "1";
        bool expectSyntheticTargets = Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_EXPECT_SYNTHETIC_UI") == "1";
        bool expectAnvilTargets = Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_EXPECT_ANVIL") == "1";
        Assert.Contains(imageSize, new[] { 640, 960, 1280, 1536 });
        Assert.Contains(localMethod, new[] { "edge", "ocr", "combined" });
        Assert.InRange(new[] { expectSettingsTargets, expectSyntheticTargets, expectAnvilTargets }.Count(value => value), 0, 1);
        Assert.InRange(repeatCount, 1, 5);
        string title = $"ShadowUse Game Screenshot Fixture {Guid.NewGuid():N}";
        using var fixtureBitmap = new Bitmap(fixturePath);
        int captureWidth = int.TryParse(Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_CAPTURE_WIDTH"), out var configuredCaptureWidth)
            ? configuredCaptureWidth
            : expectAnvilTargets || expectSyntheticTargets ? 1280 : 640;
        Assert.InRange(captureWidth, 640, 1920);
        int captureHeight = (int)Math.Round(fixtureBitmap.Height * (captureWidth / (double)fixtureBitmap.Width));
        var captureSize = new Size(captureWidth, captureHeight);
        var formReady = new TaskCompletionSource<Form>(TaskCreationOptions.RunContinuationsAsynchronously);
        Form? fixture = null;
        var thread = new Thread(() =>
        {
            try
            {
                fixture = new FixtureForm(title, fixtureBitmap, captureSize);
                fixture.Shown += (_, _) => formReady.TrySetResult(fixture);
                Application.Run(fixture);
            }
            catch (Exception ex)
            {
                formReady.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShadowUse.GameScreenshotFixture",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        string keyName = $"DCU_GAME_SCREENSHOT_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(keyName, "fake-key");
        try
        {
            _ = await formReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var uiaThread = new UiaThread();
            using var uia = new UiaService(uiaThread);
            var settings = new ShadowSettings
            {
                GameMode = true,
                LocalUiYoloPythonExecutable = python,
                LocalUiYoloWeightsPath = weights,
                LocalUiYoloDevice = Environment.GetEnvironmentVariable("DCU_LOCAL_YOLO_TEST_DEVICE") ?? "cuda",
                LocalUiYoloImageSize = imageSize,
            };
            await using var detector = new LocalYoloCandidateDetector(settings);
            var gameTools = new GameTools(uia, settings, new LoopGuard(), localYoloDetector: detector);
            using var handler = new CapturingJevHandler();
            using var http = new HttpClient(handler);
            var controller = new JevController(http, new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = keyName,
                TimeoutMs = 5_000,
                MaxRetries = 0,
            });
            var jevTools = new GameJevTools(uia, settings, controller);

            Task<CallToolResult> CaptureLocalInventory() => gameTools.GameScreenshot(
                server: null!, app: title, max_width: captureWidth, quality: 80,
                bring_to_front: false, include_local_yolo_candidates: true,
                local_candidate_method: localMethod);

            var perceptionSamples = new List<double>(repeatCount);
            for (int run = 0; run < repeatCount; run++)
            {
            // The worker starts before capture, so cold model loading does not age the frame.
            CallToolResult screenshot = await CaptureLocalInventory();
            Assert.False(screenshot.IsError, ReadText(screenshot));
            if (run == 0 && Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_DUMP_IMAGE") is { Length: > 0 } imageDumpPath)
            {
                Assert.False(File.Exists(imageDumpPath), $"Refusing to overwrite {imageDumpPath}.");
                var imageBlock = Assert.IsType<ModelContextProtocol.Protocol.ImageContentBlock>(
                    screenshot.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>().Single());
                await File.WriteAllBytesAsync(imageDumpPath, imageBlock.DecodedData.ToArray());
            }
            using var screenshotJson = JsonDocument.Parse(ReadText(screenshot));
            var screenshotRoot = screenshotJson.RootElement;
            Assert.True(screenshotRoot.GetProperty("ok").GetBoolean());
            string frameId = screenshotRoot.GetProperty("frame_id").GetString()!;
            var ui = screenshotRoot.GetProperty("ui_observation");
            Assert.DoesNotContain("bloons", ui.GetProperty("layout_id").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(ui.GetProperty("candidate_set_complete").GetBoolean());
            double perceptionMs = ui.GetProperty("perception_ms").GetDouble();
            perceptionSamples.Add(perceptionMs);
            Assert.True(perceptionMs < 10_000,
                $"Local candidate processing exceeded Jev's freshness window on run {run + 1}/{repeatCount}: {perceptionMs:0} ms; samples so far={string.Join(", ", perceptionSamples.Select(sample => sample.ToString("0")))}.");
            string candidateSetJson = ui.GetProperty("candidate_set_json").GetString()!;
            if (run == 0 && Environment.GetEnvironmentVariable("DCU_GAME_SCREENSHOT_DUMP_CANDIDATES") is { Length: > 0 } candidateDumpPath)
            {
                Assert.False(File.Exists(candidateDumpPath), $"Refusing to overwrite {candidateDumpPath}.");
                await File.WriteAllTextAsync(candidateDumpPath, candidateSetJson);
            }
            using var inventory = JsonDocument.Parse(candidateSetJson);
            var candidateArray = inventory.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.NotEmpty(candidateArray);
            Assert.InRange(candidateArray.Length, 1,
                LocalGameUiCandidateFusion.MaxYoloCandidates + LocalGameUiCandidateFusion.MaxLocalCandidates);
            int screenshotWidth = screenshotRoot.GetProperty("width").GetInt32();
            int screenshotHeight = screenshotRoot.GetProperty("height").GetInt32();
            if (expectSettingsTargets) AssertSettingsTargets(candidateArray, screenshotWidth, screenshotHeight);
            if (expectSyntheticTargets) AssertSyntheticTargets(candidateArray, screenshotWidth, screenshotHeight);
            if (expectAnvilTargets) AssertAnvilTargets(candidateArray, screenshotWidth, screenshotHeight);
            foreach (var candidate in candidateArray)
            {
                var bbox = candidate.GetProperty("bbox");
                int x = bbox.GetProperty("x").GetInt32(), y = bbox.GetProperty("y").GetInt32();
                int width = bbox.GetProperty("width").GetInt32(), height = bbox.GetProperty("height").GetInt32();
                Assert.True(x >= 0 && y >= 0 && width > 0 && height > 0);
                Assert.True(x + width <= screenshotRoot.GetProperty("width").GetInt32());
                Assert.True(y + height <= screenshotRoot.GetProperty("height").GetInt32());
            }

            var decision = await jevTools.GameJevDecideCandidates(
                app: title,
                goal: "Rank the locally detected UI regions; do not activate anything.",
                observation_id: frameId,
                candidate_set_json: null,
                allow_incomplete_coverage: true);
            using var decisionJson = JsonDocument.Parse(JsonSerializer.Serialize(decision));
            Assert.True(decisionJson.RootElement.GetProperty("ok").GetBoolean(),
                $"Jev handoff failed: {JsonSerializer.Serialize(decision)}; perception_ms={perceptionMs:0}, startup_ms={screenshotRoot.GetProperty("local_yolo_startup_ms").GetDouble():0}.");
            Assert.Equal("STOP", decisionJson.RootElement.GetProperty("operation").GetString());
            Assert.Equal(run + 1, handler.Calls);

            using var jevRequest = JsonDocument.Parse(handler.RequestJson!);
            var state = jevRequest.RootElement.GetProperty("state");
            Assert.Equal(frameId, state.GetProperty("observation_id").GetString());
            Assert.False(state.GetProperty("candidate_set_complete").GetBoolean());
            var stateCandidates = state.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(candidateArray.Length, stateCandidates.Length);
            if (expectSettingsTargets) AssertSettingsTargets(stateCandidates, screenshotWidth, screenshotHeight);
            if (expectSyntheticTargets) AssertSyntheticTargets(stateCandidates, screenshotWidth, screenshotHeight);
            if (expectAnvilTargets) AssertAnvilTargets(stateCandidates, screenshotWidth, screenshotHeight);
            if (expectSettingsTargets && localMethod == "combined") AssertNearbySettingsTextClues(stateCandidates);
            if (expectSyntheticTargets && localMethod == "combined") AssertSyntheticTextClues(state, stateCandidates);
            Assert.All(stateCandidates, candidate =>
            {
                var metadata = candidate.GetProperty("metadata");
                Assert.Equal(GameJevCandidateMapper.LocalYoloVisualSource, metadata.GetProperty("source").GetString());
                Assert.Equal("screenshot_image_pixels", metadata.GetProperty("coordinate_space").GetString());
                Assert.True(metadata.GetProperty("bbox").GetProperty("width").GetInt32() > 0);
            });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyName, null);
            if (fixture is { IsDisposed: false, IsHandleCreated: true })
                fixture.BeginInvoke((Action)fixture.Close);
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The non-activating game screenshot fixture did not close.");
        }
    }

    private static string ReadText(CallToolResult result)
        => string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class FixtureForm : Form
    {
        private const int WsExNoActivate = 0x08000000;

        public FixtureForm(string title, Image fixtureImage, Size clientSize)
        {
            Text = title;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(120, 120);
            ClientSize = clientSize;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackgroundImage = fixtureImage;
            BackgroundImageLayout = ImageLayout.Stretch;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExNoActivate;
                return parameters;
            }
        }
    }

    private sealed class CapturingJevHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var body = JsonDocument.Parse(RequestJson);
            var criteria = body.RootElement.GetProperty("questions").GetProperty("operation").GetProperty("criteria");
            var probabilities = criteria.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Name == "STOP" ? 1.0 : 0.0,
                StringComparer.Ordinal);
            var response = JsonSerializer.Serialize(new
            {
                model = "fake-game-screenshot-jev",
                answers = new
                {
                    operation = new { choice = "STOP", confidence = 1.0, probabilities },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
