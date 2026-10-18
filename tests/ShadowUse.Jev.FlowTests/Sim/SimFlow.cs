using ShadowUse.Jev.Flow;

namespace ShadowUse.Jev.FlowTests.Sim;

public sealed class SimEnvironment(SimGame game) : IJevFlowEnvironment
{
    public bool FailInput { get; set; }
    public bool FingerprintsEnabled { get; set; } = true;

    public Task<FlowFrame> CaptureAsync(CancellationToken ct)
    {
        var frame = game.Capture("sim");
        return Task.FromResult(FingerprintsEnabled ? frame : frame with { Fingerprint = null });
    }

    public Task<FlowDispatchResult> ClickAsync(FlowFrame frame, NormBox target, CancellationToken ct)
        => Task.FromResult(FailInput ? new FlowDispatchResult(false, "blocked") : new FlowDispatchResult(game.Click(target)));

    public Task<FlowDispatchResult> MoveAsync(FlowFrame frame, NormBox target, CancellationToken ct)
        => Task.FromResult(FailInput ? new FlowDispatchResult(false, "blocked") : new FlowDispatchResult(true));

    public Task<FlowDispatchResult> TapKeyAsync(FlowFrame frame, string key, CancellationToken ct)
        => Task.FromResult(FailInput ? new FlowDispatchResult(false, "blocked") : new FlowDispatchResult(game.Tap(key)));
}

/// <summary>
/// Stand-in for Jev's judgement: follows a fixed route toward the goal screen. In image mode it sees the TRUE screen (as a screenshot
/// would show); in text mode it can only trust the caller's predicted screen. This tests the engine's tiering and learning, NOT Jev's accuracy.
/// </summary>
public sealed class OracleDecider(SimGame game, string goalScreen = "in_game") : IJevFlowDecider
{
    public const int TextLatencyMs = 400, ImageLatencyMs = 1_300;
    public List<FlowDecisionRequest> Requests { get; } = [];
    private readonly Dictionary<string, string> _idToTruth = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Route = new()
    {
        ["main_menu"] = "play", ["mode_select"] = "single_player", ["map_select"] = "beginner_map",
        ["difficulty"] = "easy", ["rules"] = "standard", ["in_game"] = "start_round",
        ["settings_screen"] = "back", ["purchase_offer"] = "dismiss",
    };

    /// <summary>Book ids that are real graph names mean themselves. Auto-named screens are mapped the first time they are seen,
    /// which is correct because the engine creates them from the frame being judged.</summary>
    private string TruthOf(string bookId)
    {
        if (SimGame.Graph.ContainsKey(bookId)) return bookId;
        if (!_idToTruth.TryGetValue(bookId, out var truth)) _idToTruth[bookId] = truth = game.Screen;
        return truth;
    }

    public Task<FlowDecision> DecideAsync(FlowDecisionRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        bool image = request.ImageJpeg is not null;
        string truth = game.Screen;

        string? recognized = null; double? screenConf = null;
        if (request.RecognizeScreen)
        {
            // Stand-in for visual matching of a screenshot against the screen descriptions the engine supplies.
            var labels = SimGame.Graph[truth].Select(b => b.Label).Where(l => !l.Contains('£')).ToArray();
            recognized = request.KnownScreens.FirstOrDefault(s => SimGame.Graph.ContainsKey(s.Id)
                    ? s.Id == truth
                    : labels.Length > 0 && labels.All(l => s.Description.Contains(l, StringComparison.OrdinalIgnoreCase)))?.Id
                ?? FlowControl.UnknownScreen;
            if (recognized != FlowControl.UnknownScreen && !SimGame.Graph.ContainsKey(recognized)) _idToTruth[recognized] = truth;
            screenConf = 0.97;
        }
        string effective = image ? truth : request.PredictedScreenId is { } p ? TruthOf(p) : truth;
        string choice = "";
        var probs = new Dictionary<string, double>();
        if (request.Menu.Count > 0)
        {
            string wanted = Route.GetValueOrDefault(effective, "");
            choice = request.Menu.Any(a => a.Id == wanted) ? wanted : image ? FlowControl.Stop : FlowControl.NeedVision;
            var ids = request.Menu.Select(a => a.Id).Concat([FlowControl.Wait, FlowControl.Stop, FlowControl.AskUser]).ToList();
            if (request.AllowNeedVision && !image) ids.Add(FlowControl.NeedVision);
            foreach (var id in ids) probs[id] = id == choice ? 0.94 : 0.06 / (ids.Count - 1);
        }

        var boxes = new Dictionary<string, FlowBox>();
        if (image)
            foreach (var a in request.NeedBox)
                if (SimGame.Graph[truth].FirstOrDefault(b => b.Id == a.Id) is { } button)
                    boxes[a.Id] = new FlowBox(game.RectOf(button), 0.97, 0.95);
        double? goal = image && truth == goalScreen ? 0.95 : image ? 0.03 : null;
        return Task.FromResult(new FlowDecision(choice, choice.Length == 0 ? 0 : 0.94, probs, recognized, screenConf, goal, boxes,
            image ? ImageLatencyMs : TextLatencyMs, image, "sim-oracle"));
    }
}

/// <summary>Plays the role of the local edge/OCR detector: proposes every visible button with its box and label.</summary>
public sealed class SimDiscoverer(SimGame game) : IJevFlowDiscoverer
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<FlowAction>> DiscoverAsync(FlowFrame frame, CancellationToken ct)
    {
        Calls++;
        IReadOnlyList<FlowAction> found = SimGame.Graph[game.Screen].Select(b => new FlowAction
        {
            Id = b.Id,
            Kind = b.Key is not null ? FlowActionKind.KeyTap : FlowActionKind.Click,
            Description = b.Label,
            TargetDescription = b.Label,
            Key = b.Key,
            Box = b.Key is null ? game.RectOf(b) : null,
            BoxConfidence = 0.9,
            Source = "discovered",
        }).ToArray();
        return Task.FromResult(found);
    }
}
