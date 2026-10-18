using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Jev.Flow;

namespace ShadowUse.Jev.FlowTests;

/// <summary>Protocol-level checks of the flow request/response contract against a local fake endpoint (not the real Jev service).</summary>
public sealed class JevControllerFlowTests : IDisposable
{
    private const string KeyVar = "DCU_FLOW_TEST_KEY";
    public JevControllerFlowTests() => Environment.SetEnvironmentVariable(KeyVar, "test-key");
    public void Dispose() => Environment.SetEnvironmentVariable(KeyVar, null);

    private sealed class Handler(Func<JsonElement, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<bool> ImageHeader { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Bodies.Add(body);
            ImageHeader.Add(request.Headers.Contains("TypeSafe-Danger-Zone-Images"));
            var (status, json) = respond(JsonDocument.Parse(body).RootElement);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static JevController Controller(Handler handler, string model = "jev-1.14.0") => new(new HttpClient(handler),
        new JevOptions { Mode = JevMode.FastLoop, Model = model, ApiKeyEnvironmentVariable = KeyVar, MaxRetries = 0 });

    private static string Choice(IEnumerable<string> ids, string pick, double confidence = 0.95)
    {
        var list = ids.ToList();
        var probs = list.ToDictionary(i => i, i => i == pick ? 0.9 : 0.1 / (list.Count - 1));
        return JsonSerializer.Serialize(new { type = "choice", choice = pick, confidence, probabilities = probs });
    }

    private static IEnumerable<string> Criteria(JsonElement request, string question)
        => request.GetProperty("questions").GetProperty(question).GetProperty("criteria").EnumerateObject().Select(p => p.Name);

    private static FlowAction Key(string id) => new() { Id = id, Kind = FlowActionKind.KeyTap, Key = "space", Description = "Start round" };
    private static FlowAction Click(string id, NormBox? box = null) => new() { Id = id, Kind = FlowActionKind.Click, Description = "Play", TargetDescription = "Play button", Box = box };

    private static FlowDecisionRequest Req(IReadOnlyList<FlowAction> menu, byte[]? image = null, IReadOnlyList<FlowAction>? needBox = null,
        bool recognize = false, bool goal = false, bool needVision = true)
        => new("Start a match", "frame-1", image, "main_menu", null, null, [], recognize ? [new("main_menu", "Main menu"), new("lobby", "Lobby")] : [],
            recognize, menu, needBox ?? [], goal, needVision, null);

    [Fact]
    public async Task TextOnlyRequestHasNoImageNoImageHeaderAndOffersNeedVision()
    {
        var handler = new Handler(r => (HttpStatusCode.OK, "{\"answers\":{\"action\":" + Choice(Criteria(r, "action"), "play_key") + "}}"));
        var decision = await Controller(handler).DecideFlowAsync(Req([Key("play_key")]));

        Assert.Equal("play_key", decision.Choice);
        Assert.False(decision.ImageSent);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.False(body.RootElement.TryGetProperty("assets", out _));
        Assert.False(handler.ImageHeader.Single());
        Assert.Contains(FlowControl.NeedVision, Criteria(body.RootElement, "action"));
        Assert.DoesNotContain("screen", body.RootElement.GetProperty("questions").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task ImageRequestAttachesAssetAsksScreenGoalAndBoxOnlyForUnlocalizedClicks()
    {
        var boxed = Click("boxed", new NormBox(0.4, 0.4, 0.5, 0.5));
        var loose = Click("loose");
        var handler = new Handler(r =>
        {
            var answers = new Dictionary<string, object>
            {
                ["action"] = JsonDocument.Parse(Choice(Criteria(r, "action"), "loose")).RootElement,
                ["screen"] = JsonDocument.Parse(Choice(Criteria(r, "screen"), "main_menu")).RootElement,
                ["goal_reached"] = new { type = "noul", noul = 0.05 },
                ["vis_01"] = new { type = "noul", noul = 0.98 },
                ["box_01"] = new { type = "box", confidence = 0.96, box = new { top = 0.2, bottom = 0.3, left = 0.4, right = 0.6 } },
            };
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new { model = "jev-1.14.0", answers }));
        });
        var decision = await Controller(handler).DecideFlowAsync(Req([boxed, loose], new byte[] { 1, 2, 3 }, [loose], recognize: true, goal: true, needVision: false));

        Assert.True(decision.ImageSent);
        Assert.Equal("loose", decision.Choice);
        Assert.Equal("main_menu", decision.RecognizedScreenId);
        Assert.Equal(0.05, decision.GoalReachedProbability);
        var box = Assert.Single(decision.Boxes);
        Assert.Equal("loose", box.Key);
        Assert.Equal(0.5, box.Value.Box.CenterX, 3);
        Assert.True(handler.ImageHeader.Single());
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.True(body.RootElement.TryGetProperty("assets", out _));
        var questions = body.RootElement.GetProperty("questions").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Contains("vis_01", questions);
        Assert.DoesNotContain("vis_02", questions);   // the already-located click needs no Box
        Assert.DoesNotContain(FlowControl.NeedVision, Criteria(body.RootElement, "action"));
        Assert.Contains(FlowControl.UnknownScreen, Criteria(body.RootElement, "screen"));
    }

    [Fact]
    public async Task ImageResponseMustReportTheConfiguredModel()
    {
        var handler = new Handler(r => (HttpStatusCode.OK, "{\"model\":\"some-other-model\",\"answers\":{\"action\":" + Choice(Criteria(r, "action"), "play_key") + "}}"));
        await Assert.ThrowsAsync<JevValidationException>(() => Controller(handler).DecideFlowAsync(Req([Key("play_key")], new byte[] { 1 })));
    }

    [Fact]
    public async Task InvalidProbabilityDistributionIsRejected()
    {
        var handler = new Handler(_ => (HttpStatusCode.OK, """{"answers":{"action":{"type":"choice","choice":"play_key","confidence":0.9,"probabilities":{"play_key":0.9,"WAIT":0.9,"STOP":0.1,"ASK_USER":0.1,"NEED_VISION":0.1}}}}"""));
        await Assert.ThrowsAsync<JevValidationException>(() => Controller(handler).DecideFlowAsync(Req([Key("play_key")])));
    }

    [Fact]
    public async Task ChoiceOutsideTheOfferedMenuIsRejected()
    {
        var handler = new Handler(r => (HttpStatusCode.OK, """{"answers":{"action":{"type":"choice","choice":"buy_gems","confidence":0.9,"probabilities":{"buy_gems":1}}}}"""));
        await Assert.ThrowsAsync<JevValidationException>(() => Controller(handler).DecideFlowAsync(Req([Key("play_key")])));
    }

    [Fact]
    public async Task InvalidBoxGeometryIsDroppedNotTrusted()
    {
        var loose = Click("loose");
        var handler = new Handler(r =>
        {
            var answers = new Dictionary<string, object>
            {
                ["action"] = JsonDocument.Parse(Choice(Criteria(r, "action"), "loose")).RootElement,
                ["vis_01"] = new { type = "noul", noul = 0.98 },
                ["box_01"] = new { type = "box", confidence = 0.96, box = new { top = 0.6, bottom = 0.2, left = 0.4, right = 0.6 } },
            };
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new { model = "jev-1.14.0", answers }));
        });
        var decision = await Controller(handler).DecideFlowAsync(Req([loose], new byte[] { 1 }, [loose], needVision: false));
        Assert.Empty(decision.Boxes);
    }

    [Theory]
    [InlineData("jev-latest")]
    public async Task BoxNeedsABoxCapableModelAndMakesNoRequestOtherwise(string model)
    {
        var handler = new Handler(_ => (HttpStatusCode.OK, "{}"));
        var loose = Click("loose");
        await Assert.ThrowsAsync<JevValidationException>(() => Controller(handler, model).DecideFlowAsync(Req([loose], new byte[] { 1 }, [loose], needVision: false)));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task OffModeMakesNoRequest()
    {
        var handler = new Handler(_ => (HttpStatusCode.OK, "{}"));
        var off = new JevController(new HttpClient(handler), new JevOptions { Mode = JevMode.Off, ApiKeyEnvironmentVariable = KeyVar });
        await Assert.ThrowsAsync<JevDisabledException>(() => off.DecideFlowAsync(Req([Key("k")])));
        Assert.Empty(handler.Bodies);
    }
}
