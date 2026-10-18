using ShadowUse.Jev.Flow;
using ShadowUse.Jev.FlowTests.Sim;

namespace ShadowUse.Jev.FlowTests;

public sealed class FlowEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcu-flow-" + Guid.NewGuid().ToString("N"));
    private string BookPath => Path.Combine(_dir, "btd2.json");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly JevFlowOptions Fast = new() { SettleMs = 5, MaxSettleMs = 250, PollMs = 5, MaxSteps = 20 };
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
        var result = await warm.Engine().RunAsync("Start a single-player match", ToMatch);

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
}
