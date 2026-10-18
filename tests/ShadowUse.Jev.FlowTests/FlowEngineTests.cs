using ShadowUse.Jev.Flow;
using ShadowUse.Jev.FlowTests.Sim;

namespace ShadowUse.Jev.FlowTests;

public sealed class FlowEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcu-flow-" + Guid.NewGuid().ToString("N"));
    private string BookPath => Path.Combine(_dir, "btd2.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly JevFlowOptions Fast = new() { SettleMs = 5, MaxSettleMs = 250, FirstSettleMs = 60, PollMs = 5, MaxSteps = 30, MaxActions = 8 };
    private static readonly FlowSeed ToMatch = new(ScreenHint: "main_menu");

    private sealed class Rig
    {
        public required SimGame Game { get; init; }
        public required SimEnvironment Env { get; init; }
        public required OracleDecider Decider { get; init; }
        public required SimDiscoverer Discoverer { get; init; }
        public required JevActionBook Book { get; init; }
        public JevFlowEngine Engine(JevFlowOptions? o = null, bool discover = true)
            => new(Book, Env, Decider, o ?? Fast, discover ? Discoverer : null);
    }

    private Rig NewRig(JevActionBook? book = null)
    {
        var game = new SimGame();
        return new Rig
        {
            Game = game, Env = new SimEnvironment(game), Decider = new OracleDecider(game),
            Discoverer = new SimDiscoverer(game), Book = book ?? new JevActionBook(BookPath),
        };
    }

    [Fact]
    public async Task ColdRunMapsUnknownGameAndReachesGoal()
    {
        var rig = NewRig();
        var result = await rig.Engine().RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.Equal("in_game", rig.Game.Screen);
        Assert.Equal(5, result.Stats.Dispatches);
        Assert.NotNull(rig.Book.GoalScreenFor("Start a single-player match"));
        Assert.Equal(rig.Book.GoalScreenFor("  start a SINGLE-player   match "), rig.Book.GoalScreenFor("Start a single-player match"));
        Assert.True(result.Stats.ImageCalls >= 1);                      // first sight of each screen needs vision
        Assert.True(result.Stats.LearnedTransitions >= 5);              // main_menu->mode_select->...->in_game
        Assert.True(rig.Book.Screens.Count(s => s.Fingerprints.Count > 0) >= 6);
        Assert.Empty(rig.Game.Purchases);
    }

    [Fact]
    public async Task WarmRunSendsNoImagesAndNoVerificationScreenshotsToJev()
    {
        var cold = NewRig();
        await cold.Engine().RunAsync("Start a single-player match", ToMatch);

        var warm = NewRig(new JevActionBook(BookPath));   // reloaded from disk
        var result = await warm.Engine().RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.Equal("in_game", warm.Game.Screen);
        Assert.Equal(0, result.Stats.ImageCalls);
        Assert.Equal(0, result.Stats.ImageBytesSent);
        Assert.Equal(0, result.Stats.Escalations);
        Assert.Equal(result.Stats.Dispatches, result.Stats.JevCalls);     // exactly one text call per input
        Assert.All(warm.Decider.Requests, r => Assert.Null(r.ImageJpeg));
        Assert.Equal(5, result.Stats.Dispatches);
        Assert.Equal(5 * OracleDecider.TextLatencyMs, result.Stats.JevLatencyMs);
    }

    [Fact]
    public async Task WarmRunWithoutBookIsStrictlyMoreExpensiveThanWithIt()
    {
        var cold = NewRig();
        var coldResult = await cold.Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        var warmResult = await warm.Engine().RunAsync("Start a single-player match", ToMatch);
        Assert.True(warmResult.Stats.JevLatencyMs < coldResult.Stats.JevLatencyMs);
        Assert.True(warmResult.Stats.ImageBytesSent < coldResult.Stats.ImageBytesSent);
    }

    [Fact]
    public async Task SlowTransitionsAreWaitedOutLocallyWithoutDoubleInput()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        warm.Game.TransitionLagCaptures = 3;
        var result = await warm.Engine(Fast).RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.Equal(5, warm.Game.Clicks + warm.Game.Keys);   // no duplicate click while the UI was catching up
        Assert.Equal(0, result.Stats.ImageCalls);
        Assert.True(result.Stats.SettleCaptures > 0);
    }

    [Fact]
    public async Task UnexpectedScreenIsDetectedLocallyAndRunRecovers()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        // "play" now lands on the map screen instead of the mode select screen this book has learned.
        warm.Game.Overrides[("main_menu", "play")] = "map_select";
        var result = await warm.Engine().RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.Stats.Mispredictions >= 1);
        Assert.Equal(0, result.Stats.ImageCalls);   // the saved fingerprint of the map screen was enough to notice and recover
    }

    [Fact]
    public async Task DriftedLayoutRelocalizesWithJevBoxInsteadOfBlindlyRepeatingClicks()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        warm.Game.LayoutShift = -0.20;   // every learned box is now stale
        var result = await warm.Engine(new JevFlowOptions { SettleMs = 5, MaxSettleMs = 250, FirstSettleMs = 60, PollMs = 5, MaxSteps = 40, MaxActions = 12 }).RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.True(result.Stats.LearnedBoxes >= 1);
        Assert.True(result.Stats.ImageCalls >= 1);
        Assert.True(warm.Game.Clicks < 20);
    }

    [Fact]
    public async Task PurchaseControlsAreNeverOfferedToJevOrClicked()
    {
        var rig = NewRig();
        await rig.Engine().RunAsync("Start a single-player match", ToMatch);
        Assert.All(rig.Decider.Requests, r => Assert.DoesNotContain(r.Menu, a => a.Id == "club_offer"));
        Assert.DoesNotContain(rig.Book.Screens.SelectMany(s => s.Actions), a => a.Id == "club_offer");
        Assert.Empty(rig.Game.Purchases);
    }

    [Fact]
    public async Task UnmappedScreenWithoutDiscovererStopsAndAsksForMapping()
    {
        var rig = NewRig();
        var result = await rig.Engine(discover: false).RunAsync("Start a single-player match", new FlowSeed(ScreenHint: "main_menu"));
        Assert.Equal("unmapped_screen", result.StopReason);
        Assert.Equal(0, result.Stats.Dispatches);
        Assert.Equal(0, rig.Game.Clicks + rig.Game.Keys);
    }

    [Fact]
    public async Task CallerSeedMapsTheStartScreenAndLaterScreensAreLearnedByDiscovery()
    {
        var rig = NewRig();
        var seed = new FlowSeed(ScreenHint: "main_menu", GoalScreenId: "in_game", Actions:
        [
            new FlowAction { Id = "play", Kind = FlowActionKind.Click, Description = "Play button", TargetDescription = "Play button", Source = "caller" },
        ]);
        var result = await rig.Engine(discover: true).RunAsync("Start a single-player match", seed);
        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.Contains(rig.Book.Get("main_menu")!.Actions, a => a.Id == "play" && a.Box is not null);   // box learned by Jev, not typed by the caller
    }

    [Fact]
    public async Task FailedDispatchStopsImmediatelyWithoutRetry()
    {
        var rig = NewRig();
        rig.Env.FailInput = true;
        var result = await rig.Engine().RunAsync("Start a single-player match", ToMatch);
        Assert.Equal("dispatch_failed", result.StopReason);
        Assert.Equal(0, rig.Game.Clicks + rig.Game.Keys);
        Assert.Equal(0, result.Stats.Dispatches);
    }

    [Fact]
    public async Task WithoutFingerprintsPredictionRunsAndOneImageConfirmsTheGoal()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        warm.Env.FingerprintsEnabled = false;
        var result = await warm.Engine().RunAsync("Start a single-player match", ToMatch);

        Assert.True(result.GoalReached, $"{result.StopReason}: {result.Error}");
        Assert.True(result.Stats.ImageCalls <= 2, $"image calls {result.Stats.ImageCalls}");
        Assert.Equal(5, result.Stats.Dispatches);
    }

    [Fact]
    public async Task BookIsPersistedAfterEveryRun()
    {
        var rig = NewRig();
        await rig.Engine().RunAsync("Start a single-player match", ToMatch);
        Assert.True(File.Exists(BookPath));
        Assert.True(new JevActionBook(BookPath).Screens.Count >= 6);
    }

    /// <summary>Flips the game to another screen right after the first decision, as if the UI changed while Jev was thinking.</summary>
    private sealed class FlippingDecider(IJevFlowDecider inner, SimGame game, string flipTo) : IJevFlowDecider
    {
        private bool _done;
        public async Task<FlowDecision> DecideAsync(FlowDecisionRequest request, CancellationToken ct)
        {
            var decision = await inner.DecideAsync(request, ct);
            if (!_done && request.ImageJpeg is null) { _done = true; game.Reset(flipTo); }
            return decision;
        }
    }

    [Fact]
    public async Task ScreenChangeDuringJevLatencyBlocksTheStaleInputAndStopsToReobserve()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        var engine = new JevFlowEngine(warm.Book, warm.Env, new FlippingDecider(warm.Decider, warm.Game, "difficulty"), Fast, null);
        var result = await engine.RunAsync("Start a single-player match", ToMatch);

        var stale = Assert.Single(result.Steps, s => s.Outcome == "stale_screen");
        Assert.Equal("play", stale.ActionId);                       // the decided click was held back
        Assert.Equal("stale_frame", result.StopReason);             // and the run stopped to reobserve instead of guessing
        Assert.True(result.Reobserve);
        Assert.Equal(0, warm.Game.Clicks + warm.Game.Keys);

        // The caller re-invokes; the book recognises the screen it was actually on and carries on.
        var again = await new JevFlowEngine(warm.Book, warm.Env, warm.Decider, Fast, null).RunAsync("Start a single-player match", new FlowSeed());
        Assert.True(again.GoalReached, again.StopReason);
    }

    [Fact]
    public async Task OptInStaleRetriesRecoverWithoutStopping()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var warm = NewRig(new JevActionBook(BookPath));
        var options = new JevFlowOptions { SettleMs = 5, MaxSettleMs = 250, FirstSettleMs = 60, PollMs = 5, MaxSteps = 30, MaxActions = 8, StaleRetries = 2 };
        var result = await new JevFlowEngine(warm.Book, warm.Env, new FlippingDecider(warm.Decider, warm.Game, "difficulty"), options, null)
            .RunAsync("Start a single-player match", ToMatch);
        Assert.Contains(result.Steps, s => s.Outcome == "stale_screen");
        Assert.True(result.GoalReached, result.StopReason);
    }

    [Fact]
    public async Task SerialRunIsBoundedToFourUniqueActionsAndCanBeChained()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);
        var four = new JevFlowOptions { SettleMs = 5, MaxSettleMs = 250, FirstSettleMs = 60, PollMs = 5 };   // defaults: MaxActions = 4
        var first = NewRig(new JevActionBook(BookPath));
        var r1 = await new JevFlowEngine(first.Book, first.Env, first.Decider, four, null).RunAsync("Start a single-player match", ToMatch);

        Assert.Equal("action_limit", r1.StopReason);
        Assert.False(r1.GoalReached);
        Assert.Equal(4, r1.Stats.Dispatches);
        Assert.Equal(0, r1.Stats.ImageCalls);                        // no verification screenshots between the four inputs
        Assert.Equal(4, first.Game.Clicks + first.Game.Keys);

        var second = new JevFlowEngine(first.Book, first.Env, first.Decider, four, null);   // continue from wherever the game is
        var r2 = await second.RunAsync("Start a single-player match", new FlowSeed());
        Assert.True(r2.GoalReached, r2.StopReason);
        Assert.Equal("in_game", first.Game.Screen);
        Assert.Equal(0, r2.Stats.ImageCalls);
    }

    [Fact]
    public async Task EachActionIsUsedAtMostOncePerRun()
    {
        var rig = NewRig();
        // From in_game the only known input is start_round (space). After pressing it once, the run must not press it again.
        rig.Game.Reset("in_game");
        var book = new JevActionBook();
        book.EnsureScreen("in_game", "in game");
        book.Upsert("in_game", new FlowAction { Id = "start_round", Kind = FlowActionKind.KeyTap, Key = "space", Description = "Start round", Source = "caller" }, out _);
        var result = await new JevFlowEngine(book, rig.Env, new OracleDecider(rig.Game, goalScreen: "nowhere"), Fast, null)
            .RunAsync("Keep playing", new FlowSeed(ScreenHint: "in_game"));
        Assert.Equal(1, rig.Game.Keys);
        Assert.Equal("action_menu_exhausted", result.StopReason);
    }

    [Fact]
    public async Task UnknownGoalScreenStillStopsBecauseAnImageGoalCheckIsForcedPeriodically()
    {
        await NewRig().Engine().RunAsync("Start a single-player match", ToMatch);   // learns the screens, and the end screen for THAT phrasing
        var warm = NewRig(new JevActionBook(BookPath));
        var result = await warm.Engine().RunAsync("Begin a match", ToMatch);          // different phrasing: no learned end screen yet

        Assert.True(result.GoalReached, $"{result.StopReason}");
        Assert.Equal("goal_reached", result.StopReason);
        Assert.InRange(result.Stats.Dispatches, 5, 5 + Fast.GoalCheckEvery);        // did not keep pressing buttons until MaxSteps
        Assert.True(result.Stats.ImageCalls >= 1);
        Assert.NotNull(warm.Book.GoalScreenFor("begin a match"));

        var again = NewRig(new JevActionBook(BookPath));
        var third = await again.Engine().RunAsync("Begin a match", ToMatch);
        Assert.Equal(0, third.Stats.ImageCalls);                                      // now known: finishes on a free fingerprint check
        Assert.Equal("goal_screen_reached", third.StopReason);
    }

    /// <summary>Rewrites every Jev Box the oracle returns, to model the live negative case (low-confidence / shifted boxes).</summary>
    private sealed class BoxTamperingDecider(IJevFlowDecider inner, Func<FlowBox, FlowBox> tamper) : IJevFlowDecider
    {
        public async Task<FlowDecision> DecideAsync(FlowDecisionRequest request, CancellationToken ct)
        {
            var d = await inner.DecideAsync(request, ct);
            return d with { Boxes = d.Boxes.ToDictionary(kv => kv.Key, kv => tamper(kv.Value)) };
        }
    }

    [Fact]
    public async Task LowConfidenceBoxIsNeverClicked()
    {
        var rig = NewRig();
        var engine = new JevFlowEngine(rig.Book, rig.Env, new BoxTamperingDecider(rig.Decider, b => b with { Confidence = 0.74 }), Fast, rig.Discoverer);
        var result = await engine.RunAsync("Start a single-player match", ToMatch);
        Assert.Equal("box_rejected", result.StopReason);
        Assert.Equal(0, rig.Game.Clicks);
    }

    [Fact]
    public async Task ConfidentBoxThatDisagreesWithTheLocalProposalIsNeverClicked()
    {
        var rig = NewRig();
        // Same confidence the gates accept, but the box sits on the neighbouring control.
        var engine = new JevFlowEngine(rig.Book, rig.Env,
            new BoxTamperingDecider(rig.Decider, b => b with { Box = new NormBox(b.Box.Left + 0.3, b.Box.Top, b.Box.Right + 0.3, b.Box.Bottom) }), Fast, rig.Discoverer);
        var result = await engine.RunAsync("Start a single-player match", ToMatch);
        Assert.Equal("box_rejected", result.StopReason);
        Assert.Equal(0, rig.Game.Clicks);
    }

    [Fact]
    public async Task ABoxIsOnlySavedAfterItsClickChangedTheScreen()
    {
        var rig = NewRig();
        rig.Game.Overrides[("main_menu", "play")] = "main_menu";   // the click does nothing
        var result = await rig.Engine().RunAsync("Start a single-player match", new FlowSeed(ScreenHint: "main_menu") , CancellationToken.None);
        Assert.Equal(0, result.Stats.LearnedBoxes);
        Assert.DoesNotContain(rig.Book.Get("main_menu")!.Actions, a => a.BoxVerified);
    }

    private sealed class UnavailableDecider : IJevFlowDecider
    {
        public Task<FlowDecision> DecideAsync(FlowDecisionRequest request, CancellationToken ct)
            => throw new JevUnavailableException("TYPESAFE_API_KEY is not set; no Jev call was made.");
    }

    [Fact]
    public async Task MissingJevKeyStopsWithJevUnavailableAndNoInput()
    {
        var rig = NewRig();
        var result = await new JevFlowEngine(rig.Book, rig.Env, new UnavailableDecider(), Fast, rig.Discoverer).RunAsync("Start a single-player match", ToMatch);
        Assert.Equal("jev_unavailable", result.StopReason);
        Assert.Contains("TYPESAFE_API_KEY", result.Error);
        Assert.Equal(0, rig.Game.Clicks + rig.Game.Keys);
    }
}
