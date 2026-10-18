using System.Text.Json;
using ModelContextProtocol.Protocol;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class GameJevFlowToolsTests
{
    private static JevController Jev(JevMode mode = JevMode.FastLoop, string model = "jev-1.14.0")
        => new(new HttpClient(), new JevOptions { Mode = mode, Model = model });

    private static JsonElement Payload(CallToolResult result)
    {
        using var doc = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task DisabledByDefaultMakesNoCaptureAndNoInput()
    {
        var tools = new GameJevFlowTools(new ShadowSettings { GameMode = true }, Jev(), null!);
        var result = await tools.GameJevFlow(null!, "game", "start a match", "app", execute: true);
        Assert.True(result.IsError);
        var payload = Payload(result);
        Assert.True(payload.GetProperty("no_input_sent").GetBoolean());
        Assert.Contains("disabled", payload.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false, JevMode.FastLoop, "jev-1.14.0", "jev", "Game mode")]
    [InlineData(true, JevMode.Off, "jev-1.14.0", "jev", "Jev is off")]
    [InlineData(true, JevMode.FastLoop, "jev-latest", "jev", "Box-capable")]
    [InlineData(true, JevMode.Assist, "jev-1.14.0", "jev", "fast_loop")]
    public async Task EveryPrerequisiteIsEnforcedBeforeAnyCaptureOrInput(bool gameMode, JevMode mode, string model, string backend, string expected)
    {
        var settings = new ShadowSettings { GameMode = gameMode, EnableExperimentalGameJevFlow = true, DecisionBackend = backend };
        var tools = new GameJevFlowTools(settings, Jev(mode, model), null!);
        var result = await tools.GameJevFlow(null!, "game", "start a match", "app", execute: true);
        Assert.True(result.IsError);
        Assert.Contains(expected, Payload(result).GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingApiKeyInTheProcessRefusesBeforeAnyCaptureOrInput()
    {
        var settings = new ShadowSettings { GameMode = true, EnableExperimentalGameJevFlow = true };
        var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.FastLoop, Model = "jev-1.14.0", ApiKeyEnvironmentVariable = "DCU_TEST_KEY_THAT_IS_NOT_SET_" + Guid.NewGuid().ToString("N") });
        var tools = new GameJevFlowTools(settings, jev, null!);
        var result = await tools.GameJevFlow(null!, "game", "start a match", "app", execute: true);
        Assert.True(result.IsError);
        var payload = Payload(result);
        Assert.True(payload.GetProperty("no_input_sent").GetBoolean());
        Assert.Contains("not set", payload.GetProperty("error").GetString());
        Assert.DoesNotContain("sk-", payload.GetRawText());   // never echoes credential material
    }

    [Theory]
    [InlineData(0, 30_000)] [InlineData(5, 30_000)] [InlineData(4, 500)] [InlineData(4, 61_000)]
    public async Task SerialRunBoundsAreEnforced(int maxActions, int maxRuntimeMs)
    {
        Environment.SetEnvironmentVariable("DCU_FLOW_BOUND_KEY", "present");
        try
        {
            var jev = new JevController(new HttpClient(), new JevOptions { Mode = JevMode.FastLoop, Model = "jev-1.14.0", ApiKeyEnvironmentVariable = "DCU_FLOW_BOUND_KEY" });
            var settings = new ShadowSettings { GameMode = true, EnableExperimentalGameJevFlow = true };
            var tools = new GameJevFlowTools(settings, jev, null!, new NoFrames(), new NoDispatcher());
            var result = await tools.GameJevFlow(null!, "game", "start a match", "app", execute: true, max_actions: maxActions, max_runtime_ms: maxRuntimeMs);
            Assert.True(result.IsError);
            Assert.Contains("max_actions", Payload(result).GetProperty("error").GetString());
        }
        finally { Environment.SetEnvironmentVariable("DCU_FLOW_BOUND_KEY", null); }
    }

    private sealed class NoFrames : IGameJevActionFrameSource
    {
        public Task<GameJevActionFrameCapture> CaptureAsync(ModelContextProtocol.Server.McpServer server, string app, CancellationToken ct) => throw new InvalidOperationException("no capture expected");
        public Task<string?> ValidateBindingAsync(string app, ShadowUse.Vision.GameScreenshotFrameBinding binding, CancellationToken ct) => throw new InvalidOperationException("no validation expected");
    }

    private sealed class NoDispatcher : IGameJevFlowDispatcher
    {
        public Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality, string? appId, string? actionId, CancellationToken ct) => throw new InvalidOperationException("no input expected");
        public Task<CallToolResult> MoveAsync(string app, int x, int y, string sourceFrameId, string? appId, string? actionId, CancellationToken ct) => throw new InvalidOperationException("no input expected");
        public Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId, string? appId, string? actionId, CancellationToken ct) => throw new InvalidOperationException("no input expected");
    }

    [Fact]
    public void BookSeedAcceptsReviewedLetterHotkeysAndPlaceActionsButNotChordsOrUnboundedRegions()
    {
        var tools = new GameJevFlowTools(new ShadowSettings(), Jev(), null!);
        string appId = "flow-test-" + Guid.NewGuid().ToString("N");
        var ok = Payload(tools.GameJevFlowBook(appId, "seed", "in_game", """
            [{"id":"select_dart","kind":"key_tap","description":"Select tower 1 (Dart Monkey)","key":"w"},
             {"id":"place_dart","kind":"place","description":"Place the selected tower","target_description":"an empty buildable spot on our half",
              "placement_check":"Is the ghost valid with no Wrong Side message?","approved_region":{"left":0.02,"top":0.15,"right":0.48,"bottom":0.85}}]
            """));
        Assert.Equal(2, ok.GetProperty("added").GetInt32());
        Assert.True(tools.GameJevFlowBook(appId, "seed", "in_game", """[{"id":"c","kind":"key_tap","description":"chord","key":"ctrl+w"}]""").IsError == false);   // refused per-entry, reported in "refused"
        Assert.True(tools.GameJevFlowBook(appId, "seed", "in_game", """[{"id":"p2","kind":"place","description":"Place","target_description":"x","placement_check":"ok?","approved_region":{"left":0,"top":0,"right":1,"bottom":1}}]""").IsError == false);
        var shown = Payload(tools.GameJevFlowBook(appId, "show"));
        Assert.Equal(2, shown.GetProperty("screens")[0].GetProperty("actions").GetArrayLength());
    }

    [Fact]
    public void BookSeedFiltersPurchaseControlsAndRejectsUnknownProperties()
    {
        var tools = new GameJevFlowTools(new ShadowSettings(), Jev(), null!);
        string appId = "flow-test-" + Guid.NewGuid().ToString("N");
        var seeded = Payload(tools.GameJevFlowBook(appId, "seed", "menu", """
            [{"id":"play","kind":"click","description":"Play button","target_description":"Play button"},
             {"id":"club","kind":"click","description":"Buy Club Membership","target_description":"Buy Club Membership"}]
            """));
        Assert.Equal(1, seeded.GetProperty("added").GetInt32());
        Assert.Single(seeded.GetProperty("refused").EnumerateArray());

        var bad = tools.GameJevFlowBook(appId, "seed", "menu", """[{"id":"x","kind":"click","description":"d","target_description":"d","approved_region":{}}]""");
        Assert.True(bad.IsError);
        var badKey = tools.GameJevFlowBook(appId, "seed", "menu", """[{"id":"k","kind":"key_tap","description":"copy","key":"ctrl+c"}]""");
        Assert.True(badKey.IsError);
    }
}
