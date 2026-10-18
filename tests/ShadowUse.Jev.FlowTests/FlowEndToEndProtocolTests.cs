using System.Net;
using System.Text;
using System.Text.Json;
using ShadowUse.Jev.Flow;
using ShadowUse.Jev.FlowTests.Sim;

namespace ShadowUse.Jev.FlowTests;

/// <summary>Engine + real JevController over HTTP against a fake endpoint that answers from the simulated game's true screen.</summary>
public sealed class FlowEndToEndProtocolTests : IDisposable
{
    private const string KeyVar = "DCU_FLOW_E2E_KEY";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcu-e2e-" + Guid.NewGuid().ToString("N"));
    public FlowEndToEndProtocolTests() => Environment.SetEnvironmentVariable(KeyVar, "k");
    public void Dispose() { Environment.SetEnvironmentVariable(KeyVar, null); try { Directory.Delete(_dir, true); } catch { } }

    private static readonly Dictionary<string, string> Route = new()
    {
        ["main_menu"] = "play", ["mode_select"] = "single_player", ["map_select"] = "beginner_map",
        ["difficulty"] = "easy", ["rules"] = "standard", ["in_game"] = "start_round",
    };

    private sealed class FakeJev(SimGame game) : HttpMessageHandler
    {
        public int Requests, ImageRequests;
        public List<string> RequestBodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string text = await request.Content!.ReadAsStringAsync(ct);
            Requests++; RequestBodies.Add(text);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            bool image = root.TryGetProperty("assets", out _);
            if (image) ImageRequests++;
            string truth = game.Screen;
            var answers = new Dictionary<string, object>();
            var questions = root.GetProperty("questions");

            static object Choice(IEnumerable<string> ids, string pick)
            {
                var list = ids.ToList();
                return new { type = "choice", choice = pick, confidence = 0.95, probabilities = list.ToDictionary(i => i, i => i == pick ? 0.9 : 0.1 / (list.Count - 1)) };
            }

            if (questions.TryGetProperty("screen", out var screenQ))
            {
                var labels = SimGame.Graph[truth].Select(b => b.Label).Where(l => !l.Contains('£')).ToArray();
                var ids = screenQ.GetProperty("criteria").EnumerateObject().ToList();
                string pick = ids.Where(p => SimGame.Graph.ContainsKey(p.Name) ? p.Name == truth
                    : p.Name != FlowControl.UnknownScreen && labels.All(l => p.Value.GetString()!.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Name).FirstOrDefault() ?? FlowControl.UnknownScreen;
                answers["screen"] = Choice(ids.Select(p => p.Name), pick);
            }
            if (questions.TryGetProperty("action", out var actionQ))
            {
                var ids = actionQ.GetProperty("criteria").EnumerateObject().Select(p => p.Name).ToList();
                string wanted = Route.GetValueOrDefault(truth, "");
                string pick = ids.Contains(wanted) ? wanted : ids.Contains(FlowControl.NeedVision) && !image ? FlowControl.NeedVision : FlowControl.Stop;
                answers["action"] = Choice(ids, pick);
            }
            if (questions.TryGetProperty("goal_reached", out _)) answers["goal_reached"] = new { type = "noul", noul = truth == "in_game" ? 0.95 : 0.02 };
            foreach (var q in questions.EnumerateObject().Where(p => p.Name.StartsWith("vis_")))
            {
                string target = q.Value.GetProperty("instructions").GetProperty("target_description").GetString()!;
                var button = SimGame.Graph[truth].FirstOrDefault(b => b.Label == target);
                answers[q.Name] = new { type = "noul", noul = button is null ? 0.02 : 0.98 };
                string boxName = "box_" + q.Name[4..];
                var rect = button is null ? new NormBox(0.1, 0.1, 0.2, 0.2) : game.RectOf(button);
                answers[boxName] = new { type = "box", confidence = 0.96, box = new { top = rect.Top, bottom = rect.Bottom, left = rect.Left, right = rect.Right } };
            }
            var body = JsonSerializer.Serialize(new { model = "jev-1.14.0", answers });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ColdThenWarmRunsThroughTheRealControllerAndHttp()
    {
        string bookPath = Path.Combine(_dir, "book.json");
        var options = new JevFlowOptions { SettleMs = 5, MaxSettleMs = 250, FirstSettleMs = 60, PollMs = 5, MaxActions = 8 };
        JevController Controller(FakeJev handler) => new(new HttpClient(handler),
            new JevOptions { Mode = JevMode.FastLoop, Model = "jev-1.14.0", ApiKeyEnvironmentVariable = KeyVar, MaxRetries = 0 });

        var coldGame = new SimGame();
        var coldHandler = new FakeJev(coldGame);
        var cold = await new JevFlowEngine(new JevActionBook(bookPath), new SimEnvironment(coldGame), Controller(coldHandler), options, new SimDiscoverer(coldGame))
            .RunAsync("Start a single-player match", new FlowSeed(ScreenHint: "main_menu"));
        Assert.True(cold.GoalReached, $"{cold.StopReason}: {cold.Error}");
        Assert.True(coldHandler.ImageRequests > 0);

        var warmGame = new SimGame();
        var warmHandler = new FakeJev(warmGame);
        var warm = await new JevFlowEngine(new JevActionBook(bookPath), new SimEnvironment(warmGame), Controller(warmHandler), options, new SimDiscoverer(warmGame))
            .RunAsync("Start a single-player match", new FlowSeed(ScreenHint: "main_menu"));

        Assert.True(warm.GoalReached, $"{warm.StopReason}: {warm.Error}");
        Assert.Equal("in_game", warmGame.Screen);
        Assert.Equal(0, warmHandler.ImageRequests);                 // no screenshot ever left the machine on the warm run
        Assert.Equal(5, warmHandler.Requests);                      // one tiny text request per input, nothing else
        Assert.All(warmHandler.RequestBodies, b => Assert.DoesNotContain("data_base64", b));
        Assert.True(warmHandler.Requests < coldHandler.Requests);
    }
}
