using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class GameJevActTests
{
    private sealed class FakeHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> bodies = new(responses);
        public readonly List<string> Requests = [];
        public int ImageRequestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues("TypeSafe-Danger-Zone-Images", out var values))
            {
                Assert.Equal("true", Assert.Single(values!));
                ImageRequestCount++;
            }
            Requests.Add(request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Assert.NotEmpty(bodies);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(bodies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeFrameSource(GameJevActionFrameCapture capture) : IGameJevActionFrameSource
    {
        public int CaptureCalls;
        public int ValidationCalls;
        public int FailValidationCall;
        public string? ValidationError;

        public Task<GameJevActionFrameCapture> CaptureAsync(McpServer server, string app, CancellationToken ct)
        {
            CaptureCalls++;
            return Task.FromResult(capture);
        }

        public Task<string?> ValidateBindingAsync(string app, GameScreenshotFrameBinding binding, CancellationToken ct)
        {
            ValidationCalls++;
            return Task.FromResult(ValidationCalls == FailValidationCall ? ValidationError : null);
        }
    }

    private sealed class RecordingDispatcher : IGameJevActionDispatcher
    {
        public int Calls;
        public int X;
        public int Y;
        public string? FrameId;
        public string? Key;
        public IReadOnlyList<Rectangle>? StableRegions;
        public bool Verify;
        public bool ThenScreenshot;
        public CallToolResult? ResultOverride;
        public string? AppId;
        public string? ActionId;
        public readonly List<bool> VerifyCalls = [];
        public readonly List<bool> ScreenshotCalls = [];
        public readonly List<string?> AppIds = [];
        public readonly List<string?> ActionIds = [];

        public Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality,
            IReadOnlyList<Rectangle>? sourceFrameStableRegions, bool verify, bool thenScreenshot,
            string? appId, string? actionId, CancellationToken ct)
        {
            Calls++;
            X = x;
            Y = y;
            FrameId = sourceFrameId;
            StableRegions = sourceFrameStableRegions;
            Verify = verify;
            ThenScreenshot = thenScreenshot;
            AppId = appId;
            ActionId = actionId;
            VerifyCalls.Add(verify);
            ScreenshotCalls.Add(thenScreenshot);
            AppIds.Add(appId);
            ActionIds.Add(actionId);
            return Task.FromResult(ResultOverride ?? GameJevActTests.Result(inputSent: true));
        }

        public Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId,
            bool verify, bool thenScreenshot, string? appId, string? actionId, CancellationToken ct)
        {
            Calls++;
            Key = key;
            FrameId = sourceFrameId;
            Verify = verify;
            ThenScreenshot = thenScreenshot;
            AppId = appId;
            ActionId = actionId;
            VerifyCalls.Add(verify);
            ScreenshotCalls.Add(thenScreenshot);
            AppIds.Add(appId);
            ActionIds.Add(actionId);
            return Task.FromResult(ResultOverride ?? GameJevActTests.Result(inputSent: true));
        }
    }

    private const string ClickOptions = """
        [{"id":"start","kind":"click","description":"Start a solo match","target_description":"Start Solo button","approved_region":{"x":100,"y":100,"width":120,"height":120}}]
        """;

    private static string ChoiceResponse(double confidence = 0.97, double applicability = 0.98,
        string? model = "jev-1.14.0", double operationProbability = 0.94)
    {
        string modelField = model is null ? "" : $"\"model\":\"{model}\",";
        double controlProbability = (1 - operationProbability) / 4;
        return $$"""
            { {{modelField}} "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": {{confidence}}, "probabilities": { "CLICK": {{operationProbability}}, "WAIT": {{controlProbability}}, "REOBSERVE": {{controlProbability}}, "ASK_USER": {{controlProbability}}, "STOP": {{controlProbability}} } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.98, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": {{applicability}} },
              "target_visible_01": { "type": "noul", "noul": 0.97 },
              "target_box_01": { "type": "box", "confidence": 0.96, "box": { "top": 0.4, "bottom": 0.6, "left": 0.4, "right": 0.6 } }
            } }
            """;
    }

    private static string BoxResponse(double left = 0.4, double right = 0.6, double targetVisible = 0.97, double confidence = 0.96, string? model = "jev-1.14.0")
    {
        string modelField = model is null ? "" : $"\"model\":\"{model}\",";
        return $$"""
            { {{modelField}} "answers": {
              "target_visible_01": { "type": "noul", "noul": {{targetVisible}} },
              "target_box_01": { "type": "box", "confidence": {{confidence}}, "box": { "top": 0.4, "bottom": 0.6, "left": {{left}}, "right": {{right}} } }
            } }
            """;
    }

    private static string MergeResponses(string choiceResponse, string boxResponse)
    {
        using var choiceDocument = JsonDocument.Parse(choiceResponse);
        using var boxDocument = JsonDocument.Parse(boxResponse);
        var answers = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in choiceDocument.RootElement.GetProperty("answers").EnumerateObject())
            answers[property.Name] = property.Value.Clone();
        foreach (var property in boxDocument.RootElement.GetProperty("answers").EnumerateObject())
            answers[property.Name] = property.Value.Clone();
        string? model = choiceDocument.RootElement.TryGetProperty("model", out var modelElement)
            && modelElement.ValueKind == JsonValueKind.String ? modelElement.GetString() : null;
        return JsonSerializer.Serialize(new { model, answers });
    }

    private static string KeyChoiceResponse(int targetCount, string selected = "1")
    {
        var targetProbabilities = Enumerable.Range(1, targetCount).ToDictionary(index => index.ToString(),
            index => targetCount == 1 ? 1.0 : index.ToString() == selected ? 0.98 : 0.02 / (targetCount - 1));
        var answers = new Dictionary<string, object>
        {
            ["operation"] = new { type = "choice", choice = "PRESS_KEY", confidence = 0.97,
                probabilities = new Dictionary<string, double> { ["PRESS_KEY"] = 0.94, ["WAIT"] = 0.015, ["REOBSERVE"] = 0.015, ["ASK_USER"] = 0.015, ["STOP"] = 0.015 } },
            ["press_key_target"] = new { type = "choice", choice = selected, confidence = 0.98, probabilities = targetProbabilities },
        };
        for (int index = 1; index <= targetCount; index++)
            answers[$"action_applicable_{index:D2}"] = new { type = "noul", noul = 0.98 };
        return JsonSerializer.Serialize(new { model = "jev-1.14.0", answers });
    }

    private static (GameJevTools Tools, FakeHandler Handler, FakeFrameSource Frames, RecordingDispatcher Dispatcher, string ApiKeyVariable)
        Create(string choiceResponse, string? boxResponse = null, DateTimeOffset? capturedAt = null,
            ShadowSettings? settingsOverride = null, ScreenStateStore? screenStateStore = null)
    {
        string keyVariable = "JEV_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(keyVariable, "test-key");
        var handler = boxResponse is null
            ? new FakeHandler(choiceResponse)
            : new FakeHandler(MergeResponses(choiceResponse, boxResponse));
        var settings = settingsOverride ?? new ShadowSettings
        {
            GameMode = true,
            EnableExperimentalGameJevAct = true,
            DecisionBackend = "jev",
            JevMode = "fast_loop",
            JevModel = "jev-1.14.0",
        };
        var jev = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = keyVariable,
            MaxRetries = 0,
        });
        byte[] jpeg = MakeJpeg();
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 320, Bottom = 320 };
        var binding = new GameScreenshotFrameBinding("frame-1", capturedAt ?? DateTimeOffset.UtcNow,
            "test-target-lifetime", 320, 320, 320, 320, 1, 96, rect, IsFullFrame: true);
        var frames = new FakeFrameSource(new GameJevActionFrameCapture(new GameJevActionFrame(binding, jpeg), null));
        var dispatcher = new RecordingDispatcher();
        var tools = new GameJevTools(null!, settings, jev, actionFrames: frames, actionDispatcher: dispatcher,
            screenStateStore: screenStateStore);
        return (tools, handler, frames, dispatcher, keyVariable);
    }

    [Fact]
    public async Task DefaultOffGateReturnsBeforeScreenshotProviderOrDispatcher()
    {
        var handler = new FakeHandler();
        var jev = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
        });
        var tools = new GameJevTools(null!, new ShadowSettings { GameMode = true }, jev);

        CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", "[]", execute: true);

        Assert.Equal(true, result.IsError);
        Assert.Empty(handler.Requests);
        Assert.Contains("disabled", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-json", false)]
    [InlineData("[{\"id\":\"escape-chord\",\"kind\":\"key_tap\",\"description\":\"Escape chord\",\"key\":\"ctrl+shift+esc\"}]", false)]
    [InlineData("[{\"id\":\"start\",\"kind\":\"click\",\"description\":\"Start\",\"target_description\":\"Start Solo button\"}]", true)]
    public async Task InvalidOptionsAreRefusedBeforeCaptureAndDispatch(string options, bool execute)
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute);
            Assert.Equal(true, result.IsError);
            Assert.Equal(0, frames.CaptureCalls);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ExecuteFalseReturnsABoxPreviewWithoutDispatch()
    {
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: false);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
            using var preview = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.True(preview.RootElement.GetProperty("preview_only").GetBoolean());
            Assert.True(preview.RootElement.GetProperty("dispatch_eligible").GetBoolean());
            Assert.True(preview.RootElement.GetProperty("no_input_sent").GetBoolean());
            Assert.IsType<ImageContentBlock>(result.Content[1]);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ExpiredFrameIsRejectedBeforeProviderCallOrDispatch()
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(), capturedAt: DateTimeOffset.UtcNow.AddSeconds(-6));
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.Equal(true, result.IsError);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task FinalFrameValidationFailureRefusesDispatch()
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        frames.FailValidationCall = 2;
        frames.ValidationError = "The frame changed before dispatch.";
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(2, frames.ValidationCalls);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task MissingResponseModelRefusesBeforeDispatch()
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(model: null), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.Equal(true, result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task DuplicateChoiceSafetyFieldRefusesDispatch()
    {
        const string duplicate = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": 0.97, "confidence": 0.99, "probabilities": { "CLICK": 0.94, "WAIT": 0.02, "REOBSERVE": 0.02, "ASK_USER": 0.01, "STOP": 0.01 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.98, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        var (tools, handler, _, dispatcher, keyVariable) = Create(duplicate, BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task LowChoiceConfidenceRefusesDispatch()
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(confidence: 0.84), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.Equal(true, result.IsError);
            Assert.True(handler.Requests.Count == 1, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ConfiguredChoiceThresholdAllowsLowerScoreTopOption()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "PRESS_KEY", "confidence": 0.73, "probabilities": { "PRESS_KEY": 0.94, "WAIT": 0.015, "REOBSERVE": 0.015, "ASK_USER": 0.015, "STOP": 0.015 } },
              "press_key_target": { "type": "choice", "choice": "1", "confidence": 0.98, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.70 }
            } }
            """;
        string options = """[{"id":"space","kind":"key_tap","description":"Tap Space","key":"space"}]""";
        var settings = new ShadowSettings
        {
            GameMode = true,
            EnableExperimentalGameJevAct = true,
            DecisionBackend = "jev",
            JevMode = "fast_loop",
            JevModel = "jev-1.14.0",
            ExperimentalGameJevActChoiceThreshold = 0.70,
        };
        var (tools, handler, _, dispatcher, keyVariable) = Create(response, settingsOverride: settings);
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal("space", dispatcher.Key);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task LowOperationProbabilityDespiteHighConfidenceRefusesDispatch()
    {
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(confidence: 0.97, operationProbability: 0.84), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task LowBoxConfidenceRefusesDispatch()
    {
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse(confidence: 0.89));
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task CloseTargetChoiceMarginRefusesDispatch()
    {
        const string choice = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": 0.97, "probabilities": { "CLICK": 0.94, "WAIT": 0.015, "REOBSERVE": 0.015, "ASK_USER": 0.015, "STOP": 0.015 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.97, "probabilities": { "1": 0.53, "2": 0.47 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 },
              "action_applicable_02": { "type": "noul", "noul": 0.98 }
            } }
            """;
        string options = """
            [
              {"id":"start","kind":"click","description":"Start","target_description":"Start Solo button","approved_region":{"x":100,"y":100,"width":120,"height":120}},
              {"id":"continue","kind":"click","description":"Continue","target_description":"Continue button","approved_region":{"x":100,"y":100,"width":120,"height":120}}
            ]
            """;
        var (tools, handler, _, dispatcher, keyVariable) = Create(choice, BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ConfiguredClickThresholdAllowsLowerTargetAndBoxScores()
    {
        var settings = new ShadowSettings
        {
            GameMode = true,
            EnableExperimentalGameJevAct = true,
            DecisionBackend = "jev",
            JevMode = "fast_loop",
            JevModel = "jev-1.14.0",
            ExperimentalGameJevActClickThreshold = 0.89,
        };
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse(targetVisible: 0.89, confidence: 0.89), settingsOverride: settings);
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ConfiguredAmbiguityMarginAllowsTopRankedOperation()
    {
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "PRESS_KEY", "confidence": 0.97, "probabilities": { "PRESS_KEY": 0.53, "WAIT": 0.47, "REOBSERVE": 0.0, "ASK_USER": 0.0, "STOP": 0.0 } },
              "press_key_target": { "type": "choice", "choice": "1", "confidence": 0.97, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        string options = """[{"id":"space","kind":"key_tap","description":"Tap Space","key":"space"}]""";
        var settings = new ShadowSettings
        {
            GameMode = true,
            EnableExperimentalGameJevAct = true,
            DecisionBackend = "jev",
            JevMode = "fast_loop",
            JevModel = "jev-1.14.0",
            ExperimentalGameJevActChoiceThreshold = 0.50,
            ExperimentalGameJevActAmbiguityMargin = 0.05,
        };
        var (tools, handler, _, dispatcher, keyVariable) = Create(response, settingsOverride: settings);
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal("space", dispatcher.Key);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task BoxOutsideApprovedRegionRefusesDispatch()
    {
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse(left: 0.75, right: 0.85));
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.Equal(true, result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task LowTargetPresenceNoulRefusesDispatch()
    {
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse(targetVisible: 0.89));
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ExactThreePixelRegionMarginAllowsBoundedClick()
    {
        string options = """[{"id":"start","kind":"click","description":"Start","target_description":"Start Solo button","approved_region":{"x":125,"y":125,"width":70,"height":70}}]""";
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.False(result.IsError);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal(160, dispatcher.X);
            Assert.Equal(160, dispatcher.Y);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task LessThanThreePixelRegionMarginRefusesClick()
    {
        string options = """[{"id":"start","kind":"click","description":"Start","target_description":"Start Solo button","approved_region":{"x":126,"y":126,"width":70,"height":70}}]""";
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.True(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ValidKeyMenuDispatchesOneAllowlistedTapWithoutBox()
    {
        const string keyResponse = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "PRESS_KEY", "confidence": 0.97, "probabilities": { "PRESS_KEY": 0.94, "WAIT": 0.015, "REOBSERVE": 0.015, "ASK_USER": 0.015, "STOP": 0.015 } },
              "press_key_target": { "type": "choice", "choice": "1", "confidence": 0.98, "probabilities": { "1": 1.0 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 }
            } }
            """;
        string options = """[{"id":"space","kind":"key_tap","description":"Tap Space","key":"space"}]""";
        var (tools, handler, _, dispatcher, keyVariable) = Create(keyResponse);
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", options, execute: true);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal("space", dispatcher.Key);
            Assert.Equal("frame-1", dispatcher.FrameId);
            Assert.Equal(0, handler.ImageRequestCount);
            Assert.Null(dispatcher.StableRegions);
            Assert.True(dispatcher.Verify);
            Assert.True(dispatcher.ThenScreenshot);
            Assert.Equal("space", dispatcher.ActionId);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task MatchingSavedScreenMapIsAdvisoryContextButDoesNotExpandTheCallerMenu()
    {
        var memory = new ScreenStateStore();
        var (firstTools, _, _, _, firstKey) = Create(ChoiceResponse(), BoxResponse(), screenStateStore: memory);
        try
        {
            CallToolResult first = await firstTools.GameJevAct(null!, "BTD2", "Choose a solo mode.", ClickOptions,
                execute: false, app_id: "btb2", screen_hint: "main-menu");
            Assert.False(first.IsError);
        }
        finally { Environment.SetEnvironmentVariable(firstKey, null); }

        const string nextMenu = """[{"id":"bloon_lab","kind":"click","description":"Open Bloon Lab","target_description":"Bloon Lab button","approved_region":{"x":100,"y":100,"width":120,"height":120}}]""";
        var (nextTools, handler, _, _, nextKey) = Create(ChoiceResponse(), BoxResponse(), screenStateStore: memory);
        try
        {
            CallToolResult next = await nextTools.GameJevAct(null!, "BTD2", "Open Bloon Lab.", nextMenu,
                execute: false, app_id: "btb2", screen_hint: "main-menu");
            Assert.False(next.IsError);

            using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
            var state = request.RootElement.GetProperty("state");
            Assert.Contains("Historical screen map", state.GetProperty("context").GetString());
            Assert.Contains("start", state.GetProperty("context").GetString());
            JsonElement candidates = state.GetProperty("candidates");
            Assert.Equal(1, candidates.GetArrayLength());
            Assert.Equal("bloon_lab", candidates[0].GetProperty("id").GetString());
        }
        finally { Environment.SetEnvironmentVariable(nextKey, null); }
    }

    [Fact]
    public async Task ScreenshotFreeMultiKeyChoiceRequiresKnownState()
    {
        string options = """[{"id":"space","kind":"key_tap","description":"Start round","key":"space"},{"id":"enter","kind":"key_tap","description":"Confirm selection","key":"enter"}]""";
        var (tools, handler, frames, dispatcher, keyVariable) = Create(KeyChoiceResponse(2));
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "Continue solo play.", options, execute: true);

            Assert.True(result.IsError);
            Assert.Contains("require current_state", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, frames.CaptureCalls);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task BoundedKeyLoopIsTextOnlyCarriesHistoryAndPersistsTheActionMenu()
    {
        string apiKeyVariable = "JEV_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(apiKeyVariable, "test-key");
        string appId = "btd2-test-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "dcu-screen-map-" + Guid.NewGuid().ToString("N"));
        string persistencePath = Path.Combine(directory, "screen-map.json");
        var memory = new ScreenStateStore(persistencePath: persistencePath);
        var handler = new FakeHandler(KeyChoiceResponse(2), KeyChoiceResponse(1));
        var jev = new JevController(new HttpClient(handler), new JevOptions
        {
            Mode = JevMode.FastLoop,
            Model = "jev-1.14.0",
            ApiKeyEnvironmentVariable = apiKeyVariable,
            MaxRetries = 0,
        });
        var settings = new ShadowSettings
        {
            GameMode = true,
            EnableExperimentalGameJevAct = true,
            DecisionBackend = "jev",
            JevMode = "fast_loop",
            JevModel = "jev-1.14.0",
        };
        byte[] jpeg = MakeJpeg();
        var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = 320, Bottom = 320 };
        var binding = new GameScreenshotFrameBinding("frame-1", DateTimeOffset.UtcNow,
            "test-target-lifetime", 320, 320, 320, 320, 1, 96, rect, IsFullFrame: true);
        var frames = new FakeFrameSource(new GameJevActionFrameCapture(new GameJevActionFrame(binding, jpeg), null));
        var dispatcher = new RecordingDispatcher();
        var tools = new GameJevTools(null!, settings, jev, screenStateStore: memory,
            actionFrames: frames, actionDispatcher: dispatcher);
        const string actions = """[{"id":"space","kind":"key_tap","description":"Start or pause the solo round","key":"space"},{"id":"enter","kind":"key_tap","description":"Confirm the selected solo option","key":"enter"}]""";

        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "32492", "Play a short solo test round.", actions,
                execute: true, max_actions: 2, max_runtime_ms: 10_000, app_id: appId,
                app_version: "4.13", profile: "default", keyboard_layout: "unknown",
                current_state: "Solo mode chooser", screen_hint: "solo-mode-chooser");

            Assert.False(result.IsError);
            using var output = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Equal(2, output.RootElement.GetProperty("actions_sent").GetInt32());
            Assert.Equal("action_limit", output.RootElement.GetProperty("stop_reason").GetString());
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal(0, handler.ImageRequestCount);
            Assert.Equal(2, dispatcher.Calls);
            Assert.All(dispatcher.VerifyCalls, verify => Assert.False(verify));
            Assert.All(dispatcher.ScreenshotCalls, screenshot => Assert.False(screenshot));
            Assert.Equal(new string?[] { "space", "enter" }, dispatcher.ActionIds);
            Assert.Equal(new string?[] { appId, appId }, dispatcher.AppIds);

            using var firstRequest = JsonDocument.Parse(handler.Requests[0]);
            using var secondRequest = JsonDocument.Parse(handler.Requests[1]);
            var firstState = firstRequest.RootElement.GetProperty("state");
            var secondState = secondRequest.RootElement.GetProperty("state");
            Assert.False(firstState.TryGetProperty("screenshot", out _));
            Assert.False(firstRequest.RootElement.TryGetProperty("assets", out _));
            Assert.Contains("Caller-believed current state (unverified): Solo mode chooser", firstState.GetProperty("context").GetString());
            Assert.Contains("Last dispatched action id: space", secondState.GetProperty("context").GetString());
            Assert.Contains("outcome may be unverified", secondState.GetProperty("context").GetString());

            Assert.Equal(2, memory.ScreensFor(appId).Count);
            Assert.Null(memory.PersistenceError);
            var reloaded = new ScreenStateStore(persistencePath: persistencePath);
            Assert.Equal(2, reloaded.ScreensFor(appId).Count);
            Assert.Contains(reloaded.ScreensFor(appId).SelectMany(screen => screen.Actions)
                .SelectMany(action => action.KeyBindings ?? []), key => key is "space" or "enter");
        }
        finally
        {
            Environment.SetEnvironmentVariable(apiKeyVariable, null);
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StaticMenuOverrideRequiresSeparateLandmarkAndPassesAllThreeRegions()
    {
        const string oneClick = """[{"id":"start","kind":"click","description":"Start","target_description":"Start button","approved_region":{"x":100,"y":100,"width":120,"height":120},"stability":"static_menu"}]""";
        var (blockedTools, _, _, blockedDispatcher, blockedKey) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult blocked = await blockedTools.GameJevAct(null!, "BTD2", "Start solo.", oneClick, execute: true);
            Assert.True(blocked.IsError);
            Assert.Equal(0, blockedDispatcher.Calls);
            Assert.Contains("separate reviewed menu landmark", Assert.IsType<TextContentBlock>(blocked.Content[0]).Text);
        }
        finally { Environment.SetEnvironmentVariable(blockedKey, null); }

        const string options = """
            [{"id":"start","kind":"click","description":"Start solo","target_description":"Start Solo button","approved_region":{"x":100,"y":100,"width":120,"height":120},"stability":"static_menu"},
             {"id":"other","kind":"click","description":"Open another menu","target_description":"Other button","approved_region":{"x":220,"y":100,"width":90,"height":120}}]
            """;
        const string response = """
            { "model": "jev-1.14.0", "answers": {
              "operation": { "type": "choice", "choice": "CLICK", "confidence": 0.97, "probabilities": { "CLICK": 0.94, "WAIT": 0.02, "REOBSERVE": 0.02, "ASK_USER": 0.01, "STOP": 0.01 } },
              "click_target": { "type": "choice", "choice": "1", "confidence": 0.98, "probabilities": { "1": 0.98, "2": 0.02 } },
              "action_applicable_01": { "type": "noul", "noul": 0.98 },
              "action_applicable_02": { "type": "noul", "noul": 0.98 },
              "target_visible_01": { "type": "noul", "noul": 0.98 },
              "target_visible_02": { "type": "noul", "noul": 0.98 },
              "target_box_01": { "type": "box", "confidence": 0.96, "box": { "top": 0.4, "bottom": 0.6, "left": 0.4, "right": 0.6 } },
              "target_box_02": { "type": "box", "confidence": 0.96, "box": { "top": 0.4, "bottom": 0.6, "left": 0.75, "right": 0.85 } }
            } }
            """;
        var (tools, handler, _, dispatcher, keyVariable) = Create(response);
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "Start solo.", options, execute: true);
            Assert.False(result.IsError, Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal(3, dispatcher.StableRegions!.Count);
            Assert.Contains(new Rectangle(128, 128, 64, 64), dispatcher.StableRegions);
            Assert.True(dispatcher.Verify);
            Assert.True(dispatcher.ThenScreenshot);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }

        string lowLandmarkResponse = response.Replace(
            "\"target_visible_02\": { \"type\": \"noul\", \"noul\": 0.98 }",
            "\"target_visible_02\": { \"type\": \"noul\", \"noul\": 0.50 }", StringComparison.Ordinal);
        var (lowLandmarkTools, _, _, lowLandmarkDispatcher, lowLandmarkKey) = Create(lowLandmarkResponse);
        try
        {
            CallToolResult blocked = await lowLandmarkTools.GameJevAct(null!, "BTD2", "Start solo.", options, execute: true);
            Assert.True(blocked.IsError);
            Assert.Equal(0, lowLandmarkDispatcher.Calls);
            Assert.Contains("landmark must also pass", Assert.IsType<TextContentBlock>(blocked.Content[0]).Text);
        }
        finally { Environment.SetEnvironmentVariable(lowLandmarkKey, null); }
    }

    [Fact]
    public async Task MultiActionRunRejectsStaticMenuOverrideBecauseItSkipsPostActionVerification()
    {
        const string options = """[{"id":"start","kind":"click","description":"Start","target_description":"Start button","approved_region":{"x":100,"y":100,"width":120,"height":120},"stability":"static_menu"},{"id":"other","kind":"click","description":"Other","target_description":"Other button","approved_region":{"x":220,"y":100,"width":90,"height":120}}]""";
        var (tools, handler, frames, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "Start solo.", options,
                execute: true, max_actions: 2);

            Assert.True(result.IsError);
            Assert.Contains("one-step action with post-action verification", Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.Empty(handler.Requests);
            Assert.Equal(0, frames.CaptureCalls);
            Assert.Equal(0, dispatcher.Calls);
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task ChoiceNoulAndBoxShareOneImageRequestAndDispatchOnceThroughTheGuardedSeam()
    {
        var (tools, handler, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);
            Assert.False(result.IsError);
            Assert.Single(handler.Requests);
            Assert.Equal(1, dispatcher.Calls);
            Assert.Equal("frame-1", dispatcher.FrameId);
            Assert.Equal(160, dispatcher.X);
            Assert.Equal(160, dispatcher.Y);
            Assert.True(dispatcher.Verify);
            Assert.True(dispatcher.ThenScreenshot);
            Assert.Equal("start", dispatcher.ActionId);
            using var request = JsonDocument.Parse(handler.Requests[0]);
            var root = request.RootElement;
            string imageReference = root.GetProperty("state").GetProperty("screenshot").GetString()!;
            byte[] attachedImage = Convert.FromBase64String(root.GetProperty("assets").GetProperty(imageReference).GetProperty("data_base64").GetString()!);
            Assert.NotEmpty(attachedImage);
            Assert.Equal(1, handler.ImageRequestCount);
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("operation").GetProperty("type").GetString());
            Assert.Equal("noul", root.GetProperty("questions").GetProperty("target_visible_01").GetProperty("type").GetString());
            Assert.Equal("box", root.GetProperty("questions").GetProperty("target_box_01").GetProperty("type").GetString());
            using var summary = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.True(summary.RootElement.GetProperty("input_sent").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("image_sent_to_jev").GetBoolean());
            Assert.Equal(1, summary.RootElement.GetProperty("box_questions_in_same_request").GetInt32());
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    [Fact]
    public async Task GuardedPreDispatchRefusalWithInputFalseIsReportedAsNoInputAndNeverRetried()
    {
        var (tools, _, _, dispatcher, keyVariable) = Create(ChoiceResponse(), BoxResponse());
        dispatcher.ResultOverride = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
            {
                ok = false,
                error = "The static-menu region changed before dispatch.",
                input_sent = false,
                no_input_sent = true,
                dispatch_attempted = false,
                do_not_retry = true,
            }) }],
        };
        try
        {
            CallToolResult result = await tools.GameJevAct(null!, "BTD2", "start a solo match", ClickOptions, execute: true);

            Assert.True(result.IsError);
            Assert.Equal(1, dispatcher.Calls);
            using var summary = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
            Assert.False(summary.RootElement.GetProperty("input_sent").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("no_input_sent").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("dispatch_attempted").GetBoolean());
            Assert.True(summary.RootElement.GetProperty("do_not_retry").GetBoolean());
        }
        finally { Environment.SetEnvironmentVariable(keyVariable, null); }
    }

    private static CallToolResult Result(bool inputSent) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { input_sent = inputSent, ok = inputSent }) }],
    };

    private static byte[] MakeJpeg()
    {
        using var bitmap = new Bitmap(320, 320);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Jpeg);
        return output.ToArray();
    }
}
