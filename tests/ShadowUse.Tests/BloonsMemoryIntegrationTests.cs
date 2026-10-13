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

/// <summary>Opt-in static BTD2 regression for process-local screen memory and fresh Jev options.</summary>
public sealed class BloonsMemoryIntegrationTests
{
    [Fact]
    public async Task CleanAndWarmedFakeJevRunsReuseSemanticsButBindEveryChoiceToFreshFrame()
    {
        string? imagePath = Environment.GetEnvironmentVariable("DCU_BTDB2_STATIC_FIXTURE");
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            throw Xunit.Sdk.SkipException.ForSkip("Set DCU_BTDB2_STATIC_FIXTURE to a static BTD2 hero-selection screenshot; this test uses fake Jev and sends no input.");

        using var bitmap = new Bitmap(imagePath);
        string apiKeyVariable = "DCU_BTDB2_MEMORY_FAKE_JEV_KEY";
        string? previousApiKey = Environment.GetEnvironmentVariable(apiKeyVariable);
        Environment.SetEnvironmentVariable(apiKeyVariable, "test-only");
        try
        {
            using var handler = new MemoryFakeJevHandler("hero-r2-c1");
            using var http = new HttpClient(handler);
            var controller = new JevController(http, new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = apiKeyVariable,
                BaseUrl = "http://jev-btd2-memory.invalid",
                MaxRetries = 0,
            });
            var memory = new ScreenStateStore();
            var noCatalog = new AppActionCatalogLookup(false, null, false, "no_test_catalog");
            string? cleanScreenId = null;
            string? cleanContext = null;

            for (int run = 0; run < 4; run++)
            {
                string frameId = $"btd2-static-frame-{run + 1}";
                var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = bitmap.Width, Bottom = bitmap.Height };
                DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
                var observation = await BloonsTdb2SelectionGrounding.DetectAsync(bitmap, frameId, capturedAt,
                    "static-btd2-target", 1, 96, rect);
                Assert.Equal(BloonsTdb2SelectionGrounding.LayoutId, observation.LayoutId);
                Assert.Equal(23, observation.Candidates.Count);
                Assert.Contains("Cancel", string.Join(" ", observation.ObservedText), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Ready", string.Join(" ", observation.ObservedText), StringComparison.OrdinalIgnoreCase);

                var recipes = observation.Candidates.Select(candidate =>
                    new ScreenActionRecipe(candidate.Key, candidate.Role, candidate.Label)).ToArray();
                var screen = memory.Observe("btd2-static", "unknown", "default", "unknown", "hero-selection",
                    new ScreenStateCapture(frameId, observation.Scope, observation.CandidateSetComplete,
                        observation.CandidateSetIssue, recipes, observation.ObservedText, capturedAt));
                Assert.DoesNotContain("bbox", JsonSerializer.Serialize(screen.CachedActions));
                if (run == 0)
                {
                    Assert.False(screen.Repeated);
                    Assert.False(screen.Stable);
                    Assert.Equal(1, screen.ConsecutiveObservationCount);
                    Assert.Empty(screen.IncomingTransitions);
                    Assert.Empty(memory.RecentActions("btd2-static"));
                    cleanScreenId = screen.ScreenId;
                }
                else
                {
                    Assert.True(screen.Repeated);
                    Assert.True(screen.Stable);
                    Assert.Equal(run + 1, screen.ConsecutiveObservationCount);
                    Assert.Equal(cleanScreenId, screen.ScreenId);
                    Assert.NotEmpty(screen.IncomingTransitions);
                }

                var elements = GameJevCandidateMapper.Map(observation, source: GameJevCandidateMapper.BloonsLayoutSource);
                var options = observation.Candidates.Select(candidate =>
                {
                    var element = elements.Single(item => item.Id == candidate.Id);
                    return new ScreenActionOption(element, candidate.Key, [candidate.Key, candidate.Label],
                        new ScreenInputPlan(element.Id, candidate.Key, "click", [], frameId, null,
                            candidate.X, candidate.Y, candidate.Width, candidate.Height, "fresh BTD2 screenshot box"));
                }).ToArray();
                var binding = new GameScreenshotFrameBinding(frameId, capturedAt, "static-btd2-target",
                    bitmap.Width, bitmap.Height, bitmap.Width, bitmap.Height, 1, 96, rect, true);
                var plan = ScreenActionCoordinator.Build("btd2-static", "choose hero row 2 column 1",
                    "hero selection; no selection is confirmed", "unknown", "default", "unknown",
                    screen.ScreenId, "hero-selection", screen.Note, frameId, binding, observation.Scope,
                    observation.CandidateSetComplete, observation.CandidateSetIssue, observation.ObservedText,
                    options, noCatalog, storedRecentActions: memory.RecentActions("btd2-static"),
                    observedTransitions: screen.IncomingTransitions, screenObservationCount: screen.ObservationCount,
                    screenStateStable: screen.Stable);
                Assert.False(plan.Request.CandidateSetComplete);

                var decision = await controller.DecideAsync(plan.Request);
                Assert.Equal(frameId, decision.ObservationId);
                int heroIndex = Array.FindIndex(observation.Candidates.ToArray(), candidate => candidate.Key == "hero-r2-c1");
                Assert.True(heroIndex >= 0, "The calibrated BTD2 inventory did not contain hero-r2-c1.");
                var resolution = ScreenActionCoordinator.Resolve(decision, plan, binding, currentCatalogRevision: null);
                Assert.True(resolution.Prepared, resolution.Error);
                Assert.Equal("click", resolution.Input!.InputKind);
                Assert.Equal("hero-r2-c1", resolution.Input.ActionId);
                Assert.Equal(frameId, resolution.Input.ObservationId);
                Assert.Equal(observation.Candidates[heroIndex].X, resolution.Input.X);
                Assert.Equal(observation.Candidates[heroIndex].Y, resolution.Input.Y);

                var jevState = handler.ReadLastState();
                Assert.Equal(frameId, jevState.GetProperty("observation_id").GetString());
                Assert.Equal(23, jevState.GetProperty("candidate_count").GetInt32());
                Assert.False(jevState.GetProperty("candidate_set_complete").GetBoolean());
                Assert.Contains($"observation_count={run + 1}", jevState.GetProperty("context").GetString());
                if (run == 0)
                {
                    cleanContext = jevState.GetProperty("context").GetString();
                    Assert.Empty(jevState.GetProperty("recent_actions").EnumerateArray());
                    Assert.Contains("No prior screen transitions", cleanContext);
                }
                else
                {
                    Assert.Contains("Prior transitions are caller-reported and unverified", jevState.GetProperty("context").GetString());
                    Assert.NotEmpty(jevState.GetProperty("recent_actions").EnumerateArray());
                    Assert.NotEqual(cleanContext, jevState.GetProperty("context").GetString());
                }

                // Simulated report only: no dispatcher or game API is called by this test.
                Assert.True(memory.RecordInput("btd2-static", frameId, "hero-r2-c1", "click",
                    inputSent: true, effect: "fake input report; selection effect unverified"));
            }

            Assert.Equal(4, Assert.Single(memory.ScreensFor("btd2-static")).ObservationCount);
            Assert.Equal(4, memory.RecentActions("btd2-static").Count);
            Assert.Equal(3, memory.TransitionCount); // Three later frames can follow the four reported inputs.
            Assert.Equal(4, handler.RequestCount);
        }
        finally { Environment.SetEnvironmentVariable(apiKeyVariable, previousApiKey); }
    }

    private sealed class MemoryFakeJevHandler(string targetKey) : HttpMessageHandler
    {
        private readonly List<string> _requests = [];
        public int RequestCount => _requests.Count;

        public JsonElement ReadLastState()
        {
            using var document = JsonDocument.Parse(_requests[^1]);
            return document.RootElement.GetProperty("state").Clone();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string requestJson = await request.Content!.ReadAsStringAsync(ct);
            _requests.Add(requestJson);
            using var body = JsonDocument.Parse(requestJson);
            var questions = body.RootElement.GetProperty("questions");
            var operationCriteria = questions.GetProperty("operation").GetProperty("criteria");
            var operations = operationCriteria.EnumerateObject().Select(property => property.Name).ToArray();
            var candidateCriteria = questions.GetProperty("recommend_target_target").GetProperty("criteria");
            string targetChoice = candidateCriteria.EnumerateObject()
                .First(property => property.Value.GetProperty("metadata").GetProperty("candidate_key").GetString() == targetKey).Name;
            var targetProbabilities = candidateCriteria.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Name == targetChoice ? 1.0 : 0.0, StringComparer.Ordinal);
            var operationProbabilities = operations.ToDictionary(
                operation => operation,
                operation => operation == JevRecommendationRequestBuilder.RecommendTargetOperation ? 1.0 : 0.0,
                StringComparer.Ordinal);
            string response = JsonSerializer.Serialize(new
            {
                model = "offline-btd2-memory-fake-jev",
                answers = new Dictionary<string, object>
                {
                    ["operation"] = new { choice = JevRecommendationRequestBuilder.RecommendTargetOperation, confidence = 1.0, probabilities = operationProbabilities },
                    ["recommend_target_target"] = new { choice = targetChoice, confidence = 1.0, probabilities = targetProbabilities },
                },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
