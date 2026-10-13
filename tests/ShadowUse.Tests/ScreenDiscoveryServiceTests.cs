using System.Drawing;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class ScreenDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoveryBackendCannotReturnCandidatesForAnotherFrame()
    {
        using var bitmap = new Bitmap(80, 48);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
        string frameId = "frame-expected";
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 80, Bottom = 48 };
        var frame = new ScreenDiscoveryFrame(bitmap, stream.ToArray(), frameId, DateTimeOffset.UtcNow,
            "target", 80, 48, 80, 48, 1, 96, rect);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ScreenDiscoveryService(new WrongFrameBackend()).DiscoverAsync(frame, "test"));
    }

    [Fact]
    public async Task UnfamiliarNonBloonsScreenshotDiscoversOwnCandidatesRoutesToFakeJevAndRejectsMutatedFrame()
    {
        string path = Path.Combine(RepositoryRoot(), "tests", "fixtures", "generic-ui-discovery-fixture.png");
        Assert.True(File.Exists(path), $"Fixture not found: {path}");
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
        byte[] jpeg = stream.ToArray();
        string frameId = "fixture-frame-" + Guid.NewGuid().ToString("N");
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = bitmap.Width, Bottom = bitmap.Height };
        var frame = new ScreenDiscoveryFrame(bitmap, jpeg, frameId, capturedAt, "fixture-target", bitmap.Width,
            bitmap.Height, bitmap.Width, bitmap.Height, 1, 96, rect);
        var discovery = new ScreenDiscoveryService(new LocalScreenDiscoveryBackend());
        var observation = await discovery.DiscoverAsync(frame, "edge");

        Assert.NotEqual("bloons-tdb2-hero-select-1722x999-v1", observation.LayoutId);
        Assert.False(observation.CandidateSetComplete);
        Assert.Contains("exhaustive inventory", observation.CandidateSetIssue);
        Assert.True(observation.Candidates.Count >= 2, "The non-Bloons fixture should produce multiple locally discovered proposals.");
        Assert.All(observation.Candidates, candidate =>
        {
            Assert.True(candidate.X >= 0 && candidate.Y >= 0 && candidate.Width > 0 && candidate.Height > 0);
            Assert.True(candidate.X + candidate.Width <= bitmap.Width);
            Assert.True(candidate.Y + candidate.Height <= bitmap.Height);
        });

        var catalog = new AppActionCatalog();
        var first = observation.Candidates[0];
        string catalogJson = JsonSerializer.Serialize(new
        {
            app_id = "fixture-app", app_version = "1", profile = "default", keyboard_layout = "us",
            actions = new[]
            {
                new { action_id = "activate_first_proposal", label = "Activate first proposal", key_sequence = new[] { "enter" },
                    target_aliases = new[] { first.Key }, source_kind = "user_confirmed", source_reference = "static fixture label",
                    evidence_status = "user_confirmed" },
            },
        });
        Assert.True(catalog.Import(catalogJson).Imported);
        var lookup = catalog.Lookup("fixture-app", "1", "default", "us");
        var stateStore = new ScreenStateStore();
        var state = stateStore.Observe("fixture-app", "1", "default", "us", "unknown screen",
            new ScreenStateCapture(frameId, observation.Scope, false, observation.CandidateSetIssue,
                observation.Candidates.Select(candidate => new ScreenActionRecipe(candidate.Key, candidate.Role, candidate.Label)).ToArray()));
        var mapped = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.LocalFastVisualSource);
        var options = observation.Candidates.Select(candidate =>
        {
            var element = mapped.Single(item => item.Id == candidate.Id);
            var input = new ScreenInputPlan(element.Id, candidate.Key, "click", [], frameId, lookup.Snapshot!.Revision,
                candidate.X, candidate.Y, candidate.Width, candidate.Height, "current-frame box");
            return new ScreenActionOption(element, candidate.Key, [candidate.Key, candidate.Label], input);
        }).ToArray();
        var binding = new GameScreenshotFrameBinding(frameId, capturedAt, "fixture-target", bitmap.Width, bitmap.Height,
            bitmap.Width, bitmap.Height, 1, 96, rect, true);
        var plan = ScreenActionCoordinator.Build("fixture-app", "open the primary screen", "unknown",
            "1", "default", "us", state.ScreenId, "unknown screen", state.Note, frameId, binding,
            observation.Scope, false, observation.CandidateSetIssue, observation.ObservedText, options, lookup);
        Assert.Contains(plan.Request.KeyBindings!, item => item.Code == "activate_first_proposal");

        var keyDecision = await FakeJevDecide(plan.Request, "1");
        var keyDispatcher = new RecordingDispatcher();
        Assert.True(keyDispatcher.Dispatch(keyDecision, plan, binding, lookup.Snapshot!.Revision));
        Assert.Equal("key", Assert.Single(keyDispatcher.Sent).InputKind);
        Assert.Equal(["enter"], keyDispatcher.Sent[0].KeySequence);

        var boxDecision = await FakeJevDecide(plan.Request, plan.Request.Elements.Count.ToString());
        var boxDispatcher = new RecordingDispatcher();
        Assert.True(boxDispatcher.Dispatch(boxDecision, plan, binding, lookup.Snapshot.Revision));
        var click = Assert.Single(boxDispatcher.Sent);
        Assert.Equal("click", click.InputKind);
        Assert.Equal(frameId, click.ObservationId);
        Assert.True(click.X.HasValue && click.Y.HasValue && click.Width.HasValue && click.Height.HasValue);

        var staleDispatcher = new RecordingDispatcher();
        var changedFrame = binding with { ObservationId = "mutated-frame", CapturedAtUtc = capturedAt.AddMilliseconds(1) };
        Assert.False(staleDispatcher.Dispatch(keyDecision, plan, changedFrame, lookup.Snapshot.Revision));
        Assert.Empty(staleDispatcher.Sent);
    }

    private static async Task<JevDecision> FakeJevDecide(JevDecideRequest request, string targetChoice)
    {
        string keyVariable = "DCU_SCREEN_DISCOVERY_FAKE_JEV_KEY";
        string? previous = Environment.GetEnvironmentVariable(keyVariable);
        Environment.SetEnvironmentVariable(keyVariable, "test-only");
        try
        {
            var operationProbabilities = request.OperationDescriptions.Keys
                .Concat(request.ControlOptions?.Select(option => option.Code) ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(operation => operation,
                    operation => operation == JevRecommendationRequestBuilder.RecommendTargetOperation ? 1.0 : 0.0,
                    StringComparer.Ordinal);
            var targetProbabilities = Enumerable.Range(1, request.Elements.Count).ToDictionary(
                index => index.ToString(), index => index.ToString() == targetChoice ? 1.0 : 0.0, StringComparer.Ordinal);
            string response = JsonSerializer.Serialize(new
            {
                model = "offline-fake-jev",
                answers = new Dictionary<string, object>
                {
                    ["operation"] = new { choice = JevRecommendationRequestBuilder.RecommendTargetOperation, confidence = 1.0, probabilities = operationProbabilities },
                    ["recommend_target_target"] = new { choice = targetChoice, confidence = 1.0, probabilities = targetProbabilities },
                },
            });
            using var client = new HttpClient(new FakeJevHandler(response));
            var controller = new JevController(client, new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = keyVariable,
                BaseUrl = "http://jev-offline.invalid",
                MaxRetries = 0,
            });
            return await controller.DecideAsync(request);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, previous); }
    }

    private static string RepositoryRoot()
        => typeof(ScreenDiscoveryServiceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DcuRepositoryRoot")?.Value
            is { Length: > 0 } root ? Path.GetFullPath(root) : throw new InvalidOperationException("DcuRepositoryRoot is missing.");

    private sealed class WrongFrameBackend : IScreenDiscoveryBackend
    {
        public Task<GameUiObservation> DiscoverAsync(ScreenDiscoveryFrame frame, string method,
            bool includeLocalYolo, string? detectorError, CancellationToken ct)
            => Task.FromResult(new GameUiObservation(frame.ObservationId + "-wrong", frame.CapturedAtUtc,
                frame.TargetLifetime, frame.FrameWidth, frame.FrameHeight, frame.ImageWidth, frame.ImageHeight,
                frame.ImageScale, frame.DpiAtCapture, frame.WindowRect, "fake", "fake backend", false,
                "incomplete", [], [], [], "fake"));
    }

    private sealed class RecordingDispatcher
    {
        public List<ScreenInputPlan> Sent { get; } = [];

        public bool Dispatch(JevDecision decision, ScreenActionPlan plan,
            GameScreenshotFrameBinding currentFrame, string? currentCatalogRevision)
        {
            var resolution = ScreenActionCoordinator.Resolve(decision, plan, currentFrame, currentCatalogRevision);
            if (!resolution.Prepared || resolution.Input is null) return false;
            Sent.Add(resolution.Input);
            return true;
        }
    }

    private sealed class FakeJevHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
    }
}
