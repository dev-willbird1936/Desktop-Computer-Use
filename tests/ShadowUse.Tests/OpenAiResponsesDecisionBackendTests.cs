using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class OpenAiResponsesDecisionBackendTests
{
    private const string KeyName = "DCU_TEST_OPENAI_KEY";
    private static readonly JevDecideRequest Request = new(
        "open the battle menu",
        "frame-42",
        [new JevElement("battle", "button", "Battle", null, true,
            [JevRecommendationRequestBuilder.RecommendTargetOperation],
            new Dictionary<string, object?> { ["candidate_key"] = "battle" })],
        new Dictionary<string, string> { [JevRecommendationRequestBuilder.RecommendTargetOperation] = "Recommend one listed target" },
        [
            new JevControlOption("REOBSERVE", "Look again"),
            new JevControlOption("ASK_USER", "Ask the user"),
            new JevControlOption("STOP", "Stop"),
        ],
        Context: "app=test; current state=main menu; previous action=open launcher",
        AskGoalReached: false,
        CandidateSetComplete: false,
        CandidateSetIssue: "test candidate subset",
        AllowIncompleteCandidateSetForRecommendationOnly: true);

    [Fact]
    public async Task SendsStructuredListedActionsAndOptionalImageToResponsesApi()
    {
        string? oldKey = Environment.GetEnvironmentVariable(KeyName);
        Environment.SetEnvironmentVariable(KeyName, "test-key-not-real");
        try
        {
            var handler = new FakeHandler(DecisionResponse("RECOMMEND_TARGET", "battle", .82, .91));
            var backend = new OpenAiResponsesDecisionBackend(new HttpClient(handler), new OpenAiDecisionOptions
            {
                Model = "gpt-4o-mini",
                BaseUrl = "https://openai-fake.invalid/v1/responses",
                ApiKeyEnvironmentVariable = KeyName,
            });
            byte[] image = [0xFF, 0xD8, 0xFF, 0xD9];
            var result = await backend.DecideAsync(Request, image);

            Assert.Equal("openai-responses", backend.Name);
            Assert.Equal("RECOMMEND_TARGET", result.Operation);
            Assert.Equal("battle", result.TargetElementId);
            Assert.Equal(.82, result.Confidence);
            Assert.Equal(.91, result.TargetConfidence);
            Assert.Empty(result.Probabilities); // The Responses API did not return a calibrated distribution.
            Assert.DoesNotContain("data:image/jpeg;base64", result.RequestJson);

            using var sent = JsonDocument.Parse(handler.RequestJson!);
            var root = sent.RootElement;
            Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            var userContent = root.GetProperty("input")[1].GetProperty("content");
            Assert.Equal("input_text", userContent[0].GetProperty("type").GetString());
            Assert.Contains("main menu", userContent[0].GetProperty("text").GetString());
            Assert.Equal("input_image", userContent[1].GetProperty("type").GetString());
            Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(image), userContent[1].GetProperty("image_url").GetString());
            var format = root.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.Contains("RECOMMEND_TARGET", format.GetProperty("schema").GetProperty("properties").GetProperty("operation").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.Contains("battle", format.GetProperty("schema").GetProperty("properties").GetProperty("target_element_id").GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()));
            Assert.Equal("Bearer test-key-not-real", handler.Authorization);
        }
        finally { Environment.SetEnvironmentVariable(KeyName, oldKey); }
    }

    [Fact]
    public async Task RejectsTargetIdsOutsideTheCurrentCandidateSet()
    {
        string? oldKey = Environment.GetEnvironmentVariable(KeyName);
        Environment.SetEnvironmentVariable(KeyName, "test-key-not-real");
        try
        {
            var backend = new OpenAiResponsesDecisionBackend(new HttpClient(new FakeHandler(
                DecisionResponse("RECOMMEND_TARGET", "not-listed", .8, .8))), new OpenAiDecisionOptions
            {
                ApiKeyEnvironmentVariable = KeyName,
                BaseUrl = "https://openai-fake.invalid/v1/responses",
            });
            await Assert.ThrowsAsync<JevValidationException>(() => backend.DecideAsync(Request));
        }
        finally { Environment.SetEnvironmentVariable(KeyName, oldKey); }
    }

    [Fact]
    public async Task MissingApiKeyFailsBeforeNetworkRequest()
    {
        string? oldKey = Environment.GetEnvironmentVariable(KeyName);
        Environment.SetEnvironmentVariable(KeyName, null);
        try
        {
            var handler = new FakeHandler(DecisionResponse("RECOMMEND_TARGET", "battle", .8, .8));
            var backend = new OpenAiResponsesDecisionBackend(new HttpClient(handler), new OpenAiDecisionOptions
            {
                ApiKeyEnvironmentVariable = KeyName,
                BaseUrl = "https://openai-fake.invalid/v1/responses",
            });
            var error = await Assert.ThrowsAsync<JevUnavailableException>(() => backend.DecideAsync(Request));
            Assert.Contains(KeyName, error.Message);
            Assert.Equal(0, handler.Calls);
        }
        finally { Environment.SetEnvironmentVariable(KeyName, oldKey); }
    }

    [Fact]
    public async Task RouterSelectsOpenAiOrJevFromSettingsWithoutFallbackMixing()
    {
        const string openAiKey = "DCU_TEST_OPENAI_ROUTER_KEY";
        const string jevKey = "DCU_TEST_JEV_ROUTER_KEY";
        string? oldOpenAi = Environment.GetEnvironmentVariable(openAiKey);
        string? oldJev = Environment.GetEnvironmentVariable(jevKey);
        Environment.SetEnvironmentVariable(openAiKey, "openai-test-key");
        Environment.SetEnvironmentVariable(jevKey, "jev-test-key");
        try
        {
            var openAiHandler = new FakeHandler(DecisionResponse("RECOMMEND_TARGET", "battle", .8, .8));
            var jevHandlerWhenOpenAi = new FakeHandler("{}");
            var openAi = new OpenAiResponsesDecisionBackend(new HttpClient(openAiHandler), new OpenAiDecisionOptions
            {
                ApiKeyEnvironmentVariable = openAiKey,
                BaseUrl = "https://openai-fake.invalid/v1/responses",
            });
            var jevWhenOpenAi = new JevController(new HttpClient(jevHandlerWhenOpenAi), new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = jevKey,
                BaseUrl = "http://jev-fake.invalid",
                MaxRetries = 0,
            });
            var openAiRouter = new DecisionBackendRouter(new ShadowUse.Config.ShadowSettings { DecisionBackend = "openai" },
                jevWhenOpenAi, openAi);
            var openAiDecision = await openAiRouter.DecideAsync(Request);
            Assert.Equal("openai-responses", openAiRouter.Name);
            Assert.Equal("battle", openAiDecision.TargetElementId);
            Assert.Equal(1, openAiHandler.Calls);
            Assert.Equal(0, jevHandlerWhenOpenAi.Calls);

            var jevHandler = new FakeHandler(JevResponse("RECOMMEND_TARGET", "1"));
            var openAiHandlerWhenJev = new FakeHandler("{}");
            var jev = new JevController(new HttpClient(jevHandler), new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = jevKey,
                BaseUrl = "http://jev-fake.invalid",
                MaxRetries = 0,
            });
            var openAiUnused = new OpenAiResponsesDecisionBackend(new HttpClient(openAiHandlerWhenJev), new OpenAiDecisionOptions
            {
                ApiKeyEnvironmentVariable = openAiKey,
                BaseUrl = "https://openai-fake.invalid/v1/responses",
            });
            var jevRouter = new DecisionBackendRouter(new ShadowUse.Config.ShadowSettings(), jev, openAiUnused);
            var jevDecision = await jevRouter.DecideAsync(Request);
            Assert.Equal("jev", jevRouter.Name);
            Assert.Equal("battle", jevDecision.TargetElementId);
            Assert.Equal(1, jevHandler.Calls);
            Assert.Equal(0, openAiHandlerWhenJev.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable(openAiKey, oldOpenAi);
            Environment.SetEnvironmentVariable(jevKey, oldJev);
        }
    }

    private static string JevResponse(string operation, string target)
    {
        var operationProbabilities = new Dictionary<string, double>
        {
            ["RECOMMEND_TARGET"] = operation == "RECOMMEND_TARGET" ? 1 : 0,
            ["REOBSERVE"] = operation == "REOBSERVE" ? 1 : 0,
            ["ASK_USER"] = operation == "ASK_USER" ? 1 : 0,
            ["STOP"] = operation == "STOP" ? 1 : 0,
        };
        string response = JsonSerializer.Serialize(new
        {
            model = "fake-jev",
            answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = operation, confidence = .9, probabilities = operationProbabilities },
                ["recommend_target_target"] = new { choice = target, confidence = .9, probabilities = new Dictionary<string, double> { [target] = 1 } },
            },
        });
        return response;
    }

    private static string DecisionResponse(string operation, string? target, double confidence, double? targetConfidence)
    {
        string structured = JsonSerializer.Serialize(new
        {
            operation,
            target_element_id = target,
            confidence,
            target_confidence = targetConfidence,
            goal_reached = (bool?)null,
        });
        return JsonSerializer.Serialize(new
        {
            id = "resp-test",
            output = new[]
            {
                new { type = "message", content = new[] { new { type = "output_text", text = structured } } },
            },
        });
    }

    private sealed class FakeHandler(string response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? RequestJson { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            RequestJson = await request.Content!.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
