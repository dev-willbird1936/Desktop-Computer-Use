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
