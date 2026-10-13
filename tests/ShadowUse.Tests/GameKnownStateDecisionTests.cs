using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class GameKnownStateDecisionTests
{
    private const string ApiKeyName = "DCU_KNOWN_STATE_FAKE_JEV_KEY";

    [Fact]
    public async Task UsesPersistedKeybindingsForTextOnlyDecisionWithoutCapturingScreenshot()
    {
        string? oldKey = Environment.GetEnvironmentVariable(ApiKeyName);
        Environment.SetEnvironmentVariable(ApiKeyName, "fake-key");
        try
        {
            using var uiaThread = new UiaThread();
            var settings = new ShadowSettings { GameMode = true, DecisionBackend = "jev" };
            var catalog = new AppActionCatalog();
            Assert.True(catalog.Import("""
                {"app_id":"test-game","app_version":"1.2","profile":"default","keyboard_layout":"us","actions":[
                  {"action_id":"open_battle","label":"Open battle","key_sequence":["enter"],"screen_id":"main-menu","target_aliases":[],"source_kind":"official_documentation","source_reference":"https://example.org/controls","evidence_status":"source_attested"}
                ]}
                """).Imported);
            var screens = new ScreenStateStore();
            var mapped = screens.Observe("test-game", "1.2", "default", "us", "main-menu",
                new ScreenStateCapture("frame-1", "main menu", false, "partial",
                    [new ScreenActionRecipe("battle", "button", "Battle", ["enter"])],
                    LayoutFingerprint: "v1|test"));
            using var handler = new FakeJevHandler("RECOMMEND_TARGET", "1");
            var jev = new JevController(new HttpClient(handler), new JevOptions
            {
                Mode = JevMode.Assist,
                ApiKeyEnvironmentVariable = ApiKeyName,
                BaseUrl = "http://jev-known-state.invalid",
                MaxRetries = 0,
            });
            var tools = new GameJevTools(new UiaService(uiaThread), settings, jev, catalog, screens);

            var result = await tools.GameJevDecideKnownState("test-game", "open the battle menu", mapped.ScreenId,
                "main menu", "1.2", "default", "us");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var root = json.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.False(root.GetProperty("live_screenshot_captured").GetBoolean());
            Assert.False(root.GetProperty("image_sent").GetBoolean());
            Assert.True(root.GetProperty("no_input_sent").GetBoolean());
            Assert.Equal(1, handler.Calls);
            var action = root.GetProperty("recommended_action");
            Assert.Equal("open_battle", action.GetProperty("action_id").GetString());
            Assert.Equal("enter", action.GetProperty("key_sequence")[0].GetString());
            Assert.False(action.GetProperty("dispatch_enabled").GetBoolean());
        }
        finally { Environment.SetEnvironmentVariable(ApiKeyName, oldKey); }
    }

    [Fact]
    public async Task RoutesKnownStateToOpenAiAsTextOnlyWithoutImage()
    {
        const string openAiKeyName = "DCU_KNOWN_STATE_FAKE_OPENAI_KEY";
        string? oldKey = Environment.GetEnvironmentVariable(openAiKeyName);
        Environment.SetEnvironmentVariable(openAiKeyName, "fake-key");
        try
        {
            using var uiaThread = new UiaThread();
            var settings = new ShadowSettings { GameMode = true, JevMode = "assist", DecisionBackend = "openai" };
            var catalog = new AppActionCatalog();
            Assert.True(catalog.Import("""
                {"app_id":"test-game","app_version":"1.2","profile":"default","keyboard_layout":"us","actions":[
                  {"action_id":"open_battle","label":"Open battle","key_sequence":["enter"],"screen_id":"main-menu","target_aliases":[],"source_kind":"official_documentation","source_reference":"https://example.org/controls","evidence_status":"source_attested"}
                ]}
                """).Imported);
            var screens = new ScreenStateStore();
            var mapped = screens.Observe("test-game", "1.2", "default", "us", "main-menu",
                new ScreenStateCapture("frame-1", "main menu", false, "partial", [], LayoutFingerprint: "v1|test"));
            using var handler = new FakeResponsesHandler();
            var openAi = new OpenAiResponsesDecisionBackend(new HttpClient(handler), new OpenAiDecisionOptions
            {
                ApiKeyEnvironmentVariable = openAiKeyName,
                BaseUrl = "https://openai-known-state.invalid/v1/responses",
            });
            var jev = new JevController(new HttpClient(new FakeJevHandler("STOP", null)), new JevOptions { Mode = JevMode.Assist });
            var router = new DecisionBackendRouter(settings, jev, openAi);
            var tools = new GameJevTools(new UiaService(uiaThread), settings, jev, catalog, screens, openAi, router);

            var result = await tools.GameJevDecideKnownState("test-game", "open the battle menu", mapped.ScreenId,
                "main menu", "1.2", "default", "us");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean(), JsonSerializer.Serialize(result));
            Assert.Equal("openai-responses", json.RootElement.GetProperty("decision_backend").GetString());
            Assert.False(json.RootElement.GetProperty("image_sent").GetBoolean());
            Assert.Equal("open_battle", json.RootElement.GetProperty("recommended_action").GetProperty("action_id").GetString());
            Assert.NotNull(handler.RequestJson);
            using var sent = JsonDocument.Parse(handler.RequestJson!);
            var content = sent.RootElement.GetProperty("input")[1].GetProperty("content");
            Assert.Single(content.EnumerateArray());
            Assert.Equal("input_text", content[0].GetProperty("type").GetString());
        }
        finally { Environment.SetEnvironmentVariable(openAiKeyName, oldKey); }
    }

    [Fact]
    public async Task RefusesDynamicOrSpatialTaskWithoutCallingDecisionBackend()
    {
        using var uiaThread = new UiaThread();
        var settings = new ShadowSettings { GameMode = true };
        var screens = new ScreenStateStore();
        var mapped = screens.Observe("test-game", "1", "default", "us", "gameplay",
            new ScreenStateCapture("frame-1", "gameplay", false, "partial", []));
        using var handler = new FakeJevHandler("STOP", null);
        var jev = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.Assist,
            ApiKeyEnvironmentVariable = ApiKeyName,
            BaseUrl = "http://jev-known-state.invalid",
            MaxRetries = 0,
        });
        var tools = new GameJevTools(new UiaService(uiaThread), settings, jev,
            screenStateStore: screens);

        var result = await tools.GameJevDecideKnownState("test-game", "place a tower at a free point", mapped.ScreenId,
            "gameplay", "1", "default", "us");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("dynamic or spatial state", json.RootElement.GetProperty("error").GetString());
        var dynamicResult = await tools.GameJevDecideKnownState("test-game", "activate the ability that is ready now", mapped.ScreenId,
            "gameplay", "1", "default", "us");
        using var dynamicJson = JsonDocument.Parse(JsonSerializer.Serialize(dynamicResult));
        Assert.False(dynamicJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("dynamic or spatial state", dynamicJson.RootElement.GetProperty("error").GetString());
        var changingState = await tools.GameJevDecideKnownState("test-game", "choose an option for this dynamic round", mapped.ScreenId,
            "gameplay", "1", "default", "us");
        using var changingJson = JsonDocument.Parse(JsonSerializer.Serialize(changingState));
        Assert.False(changingJson.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("dynamic or spatial state", changingJson.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, handler.Calls);
    }

    private sealed class FakeResponsesHandler : HttpMessageHandler
    {
        public string? RequestJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("Bearer fake-key", request.Headers.Authorization?.ToString());
            RequestJson = await request.Content!.ReadAsStringAsync(ct);
            using var body = JsonDocument.Parse(RequestJson);
            string text = body.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
            using var context = JsonDocument.Parse(text);
            string targetId = context.RootElement.GetProperty("candidate_actions")[0].GetProperty("id").GetString()!;
            string outputText = JsonSerializer.Serialize(new
            {
                operation = JevRecommendationRequestBuilder.RecommendTargetOperation,
                target_element_id = targetId,
                confidence = .9,
                target_confidence = .9,
                goal_reached = (bool?)null,
            });
            string response = JsonSerializer.Serialize(new
            {
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = outputText } } } },
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FakeJevHandler(string operation, string? targetIndex) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            var operationOptions = root.GetProperty("questions").GetProperty("operation").GetProperty("criteria");
            var operationProbabilities = operationOptions.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Name == operation ? 1.0 : 0.0);
            var answers = new Dictionary<string, object>
            {
                ["operation"] = new { choice = operation, confidence = .9, probabilities = operationProbabilities },
            };
            if (operation == JevRecommendationRequestBuilder.RecommendTargetOperation)
            {
                var candidates = root.GetProperty("questions").GetProperty("recommend_target_target").GetProperty("criteria");
                var probabilities = candidates.EnumerateObject().ToDictionary(
                    property => property.Name, property => property.Name == targetIndex ? 1.0 : 0.0);
                answers["recommend_target_target"] = new { choice = targetIndex, confidence = .9, probabilities };
            }
            string response = JsonSerializer.Serialize(new { model = "fake-jev", answers });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
