using ShadowUse.Jev.Flow;

namespace ShadowUse.Jev.FlowTests;

/// <summary>
/// Gameplay-shaped fixture for the select-tower -> move -> verify-preview -> click-to-place sequence. Mirrors the locally observed behaviour:
/// a hotkey selects the tower, the ghost follows the cursor, the opponent's half shows "Wrong Side!", and only a click on a valid preview places it.
/// The decider is scripted; this tests the engine's guards, not Jev's judgement.
/// </summary>
public sealed class PlacementFlowTests
{
    private sealed class PlayfieldGame
    {
        public bool TowerSelected, RoundStarted;
        public int Placed, Moves, Clicks, Keys;
        public double HoverX = -1, HoverY = -1;
        public NormBox Obstacle = new(0.60, 0.60, 0.70, 0.70);            // unbuildable patch (kept off our half unless a test moves it)
        public bool OnOurHalf => HoverX is >= 0 and < 0.5;
        public bool GhostValid => TowerSelected && OnOurHalf && !(HoverX >= Obstacle.Left && HoverX <= Obstacle.Right && HoverY >= Obstacle.Top && HoverY <= Obstacle.Bottom);
        public int Frame;
    }

    private sealed class PlayfieldEnv(PlayfieldGame g) : IJevFlowEnvironment
    {
        public Task<FlowFrame> CaptureAsync(CancellationToken ct)
        {
            g.Frame++;
            var noise = new Random(g.Frame);                              // animated gameplay: every frame differs
            var fp = new byte[48 * 27]; noise.NextBytes(fp);
            return Task.FromResult(new FlowFrame("f" + g.Frame, new byte[40_000], 1280, 720, fp, DateTimeOffset.UtcNow));
        }
        public Task<FlowDispatchResult> MoveAsync(FlowFrame f, NormBox t, CancellationToken ct) { g.Moves++; g.HoverX = t.CenterX; g.HoverY = t.CenterY; return Task.FromResult(new FlowDispatchResult(true)); }
        public Task<FlowDispatchResult> ClickAsync(FlowFrame f, NormBox t, CancellationToken ct)
        {
            g.Clicks++;
            if (g.GhostValid) { g.Placed++; g.TowerSelected = false; }
            return Task.FromResult(new FlowDispatchResult(true));
        }
        public Task<FlowDispatchResult> TapKeyAsync(FlowFrame f, string key, CancellationToken ct)
        {
            g.Keys++;
            if (key == "w") g.TowerSelected = true;
            if (key == "space") g.RoundStarted = true;
            return Task.FromResult(new FlowDispatchResult(true));
        }
    }

    private sealed class ScriptedDecider(PlayfieldGame g, NormBox spot, double boxConfidence = 0.96) : IJevFlowDecider
    {
        public List<FlowDecisionRequest> Requests { get; } = [];
        public Task<FlowDecision> DecideAsync(FlowDecisionRequest r, CancellationToken ct)
        {
            Requests.Add(r);
            bool image = r.ImageJpeg is not null;
            if (r.RecognizeScreen && r.Menu.Count == 0)
                return Task.FromResult(new FlowDecision("", 0, new Dictionary<string, double>(), "in_game", 0.97, 0.02, new Dictionary<string, FlowBox>(), 900, true, "sim"));
            if (r.PlacementCheck is not null)
                return Task.FromResult(new FlowDecision("", 0, new Dictionary<string, double>(), null, null, null, new Dictionary<string, FlowBox>(), 800, true, "sim", g.GhostValid ? 0.97 : 0.04));
            string want = !g.TowerSelected && g.Placed == 0 ? "select_dart" : g.Placed == 0 ? "place_dart" : !g.RoundStarted ? "start_round" : FlowControl.Stop;
            string choice = r.Menu.Any(a => a.Id == want) ? want : image ? FlowControl.Stop : FlowControl.NeedVision;
            var ids = r.Menu.Select(a => a.Id).Concat([FlowControl.Wait, FlowControl.Stop, FlowControl.AskUser]).Concat(r.AllowNeedVision && !image ? [FlowControl.NeedVision, FlowControl.Done] : []).ToList();
            var probs = ids.ToDictionary(i => i, i => i == choice ? 0.94 : 0.06 / (ids.Count - 1));
            var boxes = image && r.NeedBox.Any(a => a.Id == "place_dart")
                ? new Dictionary<string, FlowBox> { ["place_dart"] = new FlowBox(spot, 0.98, boxConfidence) }
                : new Dictionary<string, FlowBox>();
            return Task.FromResult(new FlowDecision(choice, 0.94, probs, null, null, image ? 0.02 : null, boxes, image ? 1300 : 400, image, "sim"));
        }
    }

    private static readonly JevFlowOptions Opts = new() { SettleMs = 2, MaxSettleMs = 20, FirstSettleMs = 20, PollMs = 2, MaxActions = 4 };
    private static readonly NormBox OurHalf = new(0.02, 0.15, 0.48, 0.85);

    private static JevActionBook SeededBook()
    {
        var book = new JevActionBook();
        book.EnsureScreen("in_game", "Single-player match, map and tower shop");
        book.SetDynamic("in_game", true);
        var actions = new[]
        {
            new FlowAction { Id = "select_dart", Kind = FlowActionKind.KeyTap, Key = "w", Description = "Select tower 1 (Dart Monkey)", Source = "caller" },
            new FlowAction { Id = "place_dart", Kind = FlowActionKind.Place, Description = "Place the selected tower", TargetDescription = "an empty buildable spot on our half of the map",
                PlacementCheck = "Is the tower ghost under the cursor shown as valid, with no 'Wrong Side!' message?", ApprovedRegion = OurHalf, Source = "caller" },
            new FlowAction { Id = "start_round", Kind = FlowActionKind.KeyTap, Key = "space", Description = "Start the round", Source = "caller" },
        };
        foreach (var a in actions) Assert.True(book.Upsert("in_game", a, out var why), why);
        return book;
    }

    private static FlowSeed Seed => new(ScreenHint: "in_game", DynamicScreens: ["in_game"]);

    [Fact]
    public async Task SelectByKeyThenPlaceOnOurHalfThenStartRound()
    {
        var g = new PlayfieldGame();
        var decider = new ScriptedDecider(g, new NormBox(0.28, 0.45, 0.32, 0.50));
        var result = await new JevFlowEngine(SeededBook(), new PlayfieldEnv(g), decider, Opts).RunAsync("Place a Dart Monkey and start the round", Seed);

        Assert.Equal(1, g.Placed);
        Assert.True(g.RoundStarted);
        Assert.Equal(3, result.Stats.Dispatches);                    // select, place, start: three unique actions, no verification screenshot between them
        Assert.Equal(1, g.Moves);
        Assert.Equal(1, g.Clicks);
        Assert.Equal("action_menu_exhausted", result.StopReason);
        // The known-keybind selection and the round start needed no image; only the placement paid for vision (box + preview check).
        Assert.InRange(result.Stats.ImageCalls, 2, 3);                // box + preview check, plus at most one periodic screen sync
        Assert.Equal(decider.Requests.Count(r => r.ImageJpeg is null), result.Stats.TextCalls);
        Assert.Contains(decider.Requests, r => r.PlacementCheck is not null && r.ImageJpeg is not null);
    }

    [Fact]
    public async Task TargetOnTheOpponentsHalfIsRefusedBeforeTheCursorMoves()
    {
        var g = new PlayfieldGame();
        var wrongSide = new ScriptedDecider(g, new NormBox(0.68, 0.45, 0.72, 0.50), boxConfidence: 0.99);   // confident, but on their half
        var result = await new JevFlowEngine(SeededBook(), new PlayfieldEnv(g), wrongSide, Opts).RunAsync("Place a Dart Monkey", Seed);
        Assert.Equal("box_rejected", result.StopReason);
        Assert.True(result.Reobserve);
        Assert.Equal(0, g.Moves);
        Assert.Equal(0, g.Clicks);
        Assert.Equal(0, g.Placed);
    }

    [Fact]
    public async Task MislocalizedLowConfidenceBoxIsNeverUsed()
    {
        var g = new PlayfieldGame();
        var weak = new ScriptedDecider(g, new NormBox(0.28, 0.45, 0.32, 0.50), boxConfidence: 0.74);   // the observed live negative case
        var result = await new JevFlowEngine(SeededBook(), new PlayfieldEnv(g), weak, Opts).RunAsync("Place a Dart Monkey", Seed);
        Assert.Equal("box_rejected", result.StopReason);
        Assert.Equal(0, g.Moves + g.Clicks);
    }

    [Fact]
    public async Task InvalidPreviewOnOurHalfMovesTheCursorButNeverClicks()
    {
        var g = new PlayfieldGame { Obstacle = new NormBox(0.20, 0.40, 0.40, 0.60) };            // an unbuildable patch under the chosen spot
        var decider = new ScriptedDecider(g, new NormBox(0.28, 0.45, 0.32, 0.50));
        var result = await new JevFlowEngine(SeededBook(), new PlayfieldEnv(g), decider, Opts).RunAsync("Place a Dart Monkey", Seed);
        Assert.Equal("invalid_placement", result.StopReason);
        Assert.True(result.Reobserve);
        Assert.Equal(1, g.Moves);
        Assert.Equal(0, g.Clicks);
        Assert.Equal(0, g.Placed);
    }

    [Fact]
    public async Task PlacementBoxIsNeverStoredInTheBook()
    {
        var g = new PlayfieldGame();
        var book = SeededBook();
        await new JevFlowEngine(book, new PlayfieldEnv(g), new ScriptedDecider(g, new NormBox(0.28, 0.45, 0.32, 0.50)), Opts).RunAsync("Place a Dart Monkey and start the round", Seed);
        Assert.All(book.Get("in_game")!.Actions.Where(a => a.Kind == FlowActionKind.Place), a => Assert.Null(a.Box));
    }

    [Fact]
    public async Task AnimatedGameplayScreenDoesNotSpawnJunkScreens()
    {
        var g = new PlayfieldGame();
        var book = SeededBook();
        await new JevFlowEngine(book, new PlayfieldEnv(g), new ScriptedDecider(g, new NormBox(0.28, 0.45, 0.32, 0.50)), Opts).RunAsync("Place a Dart Monkey and start the round", Seed);
        Assert.Single(book.Screens);      // every frame differs, but the dynamic screen is identified by sync, not by pixels
    }
}

public sealed class ShippedSeedFileTests
{
    [Fact]
    public void TheShippedBtd2PracticePlacementSeedPassesTheGuardAndTheBoundedRun()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "bench", "seeds", "btd2-practice-placement.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "bench", "seeds", "btd2-practice-placement.json")));
        var book = new JevActionBook();
        string screen = doc.RootElement.GetProperty("seed_screen").GetString()!;
        book.EnsureScreen(screen, screen);
        int added = 0;
        foreach (var item in doc.RootElement.GetProperty("actions").EnumerateArray())
        {
            string? Text(string n) => item.TryGetProperty(n, out var p) ? p.GetString() : null;
            var action = new FlowAction { Id = Text("id")!, Kind = Text("kind")!, Description = Text("description")!, TargetDescription = Text("target_description"), Key = Text("key"), PlacementCheck = Text("placement_check"), Source = "caller" };
            if (item.TryGetProperty("approved_region", out var r))
                action.ApprovedRegion = new NormBox(r.GetProperty("left").GetDouble(), r.GetProperty("top").GetDouble(), r.GetProperty("right").GetDouble(), r.GetProperty("bottom").GetDouble());
            Assert.True(book.Upsert(screen, action, out var why), $"{action.Id}: {why}");
            added++;
        }
        Assert.Equal(3, added);
        Assert.True(doc.RootElement.GetProperty("actions").GetArrayLength() <= 4);   // fits one bounded serial run
    }
}
