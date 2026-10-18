using ShadowUse.Jev.Flow;

namespace ShadowUse.Jev.FlowTests.Sim;

/// <summary>
/// A tiny deterministic menu graph shaped like the Bloons TD Battles 2 path to a single-player match.
/// It is a mechanics fixture for the flow engine, not a model of the real game's visuals or timing.
/// </summary>
public sealed class SimGame
{
    public sealed record Button(string Id, NormBox Rect, string? Key, string To, string Label);

    public static readonly IReadOnlyDictionary<string, Button[]> Graph = new Dictionary<string, Button[]>
    {
        ["main_menu"] =
        [
            new("play", new(0.40, 0.62, 0.60, 0.74), null, "mode_select", "Play button"),
            new("settings", new(0.88, 0.04, 0.96, 0.12), null, "settings_screen", "Settings gear"),
            new("club_offer", new(0.05, 0.80, 0.25, 0.92), null, "purchase_offer", "Buy Club Membership £9.99"),
        ],
        ["mode_select"] =
        [
            new("single_player", new(0.20, 0.35, 0.45, 0.60), null, "map_select", "Single Player button"),
            new("back", new(0.02, 0.04, 0.10, 0.12), "escape", "main_menu", "Back arrow"),
        ],
        ["map_select"] =
        [
            new("beginner_map", new(0.30, 0.40, 0.45, 0.58), null, "difficulty", "Beginner map card"),
            new("back", new(0.02, 0.04, 0.10, 0.12), "escape", "mode_select", "Back arrow"),
        ],
        ["difficulty"] =
        [
            new("easy", new(0.15, 0.40, 0.32, 0.58), null, "rules", "Easy difficulty card"),
            new("back", new(0.02, 0.04, 0.10, 0.12), "escape", "map_select", "Back arrow"),
        ],
        ["rules"] =
        [
            new("standard", new(0.55, 0.40, 0.72, 0.58), null, "in_game", "Standard mode card"),
            new("back", new(0.02, 0.04, 0.10, 0.12), "escape", "difficulty", "Back arrow"),
        ],
        ["in_game"] =
        [
            new("start_round", new(0.88, 0.88, 0.98, 0.98), "space", "in_game", "Play round button"),
        ],
        ["settings_screen"] = [new("back", new(0.02, 0.04, 0.10, 0.12), "escape", "main_menu", "Back arrow")],
        ["purchase_offer"] = [new("dismiss", new(0.90, 0.04, 0.97, 0.10), "escape", "main_menu", "Close offer")],
    };

    public string Screen { get; private set; } = "main_menu";
    public int Clicks { get; private set; }
    public int Keys { get; private set; }
    public int Captures { get; private set; }
    public List<string> Purchases { get; } = [];

    /// <summary>Frames between an input and the UI reacting, to exercise settle/latency handling.</summary>
    public int TransitionLagCaptures { get; set; }
    private string? _pendingScreen;
    private int _lag;

    public void Reset(string screen = "main_menu") { Screen = screen; _pendingScreen = null; }

    /// <summary>Layout drift: shift every rect, as after a resolution/theme change. Learned boxes become wrong.</summary>
    public double LayoutShift { get; set; }

    public NormBox RectOf(Button b) => new(Math.Clamp(b.Rect.Left + LayoutShift, 0, 0.99), b.Rect.Top, Math.Clamp(b.Rect.Right + LayoutShift, 0.01, 1), b.Rect.Bottom);

    public FlowFrame Capture(string frameId)
    {
        Captures++;
        if (_pendingScreen is not null && --_lag <= 0) { Screen = _pendingScreen; _pendingScreen = null; }
        return new FlowFrame(frameId + Captures, new byte[48_000], 1280, 720, Fingerprint(Screen, Captures), DateTimeOffset.UtcNow);
    }

    public bool Click(NormBox target)
    {
        Clicks++;
        double x = target.CenterX, y = target.CenterY;
        foreach (var b in Graph[Screen])
        {
            var r = RectOf(b);
            if (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom) { Go(b); return true; }
        }
        return true;
    }

    public bool Tap(string key)
    {
        Keys++;
        var b = Graph[Screen].FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (b is not null) Go(b);
        return true;
    }

    /// <summary>(screen, button) → destination that differs from the learned graph, to force mispredictions.</summary>
    public Dictionary<(string Screen, string Button), string> Overrides { get; } = [];

    private void Go(Button b)
    {
        string to = Overrides.GetValueOrDefault((Screen, b.Id), b.To);
        if (to == "purchase_offer") Purchases.Add("offer opened");
        if (TransitionLagCaptures > 0) { _pendingScreen = to; _lag = TransitionLagCaptures; }
        else Screen = to;
    }

    /// <summary>36x20 luminance grid: a per-screen pattern plus small frame noise (animated backgrounds).</summary>
    public static byte[] Fingerprint(string screen, int noiseSeed)
    {
        var rng = new Random(StringComparer.Ordinal.GetHashCode(screen) ^ 0x5bd1e995);
        var noise = new Random(noiseSeed);
        var fp = new byte[36 * 20];
        for (int i = 0; i < fp.Length; i++) fp[i] = (byte)Math.Clamp(rng.Next(256) + noise.Next(-6, 7), 0, 255);
        return fp;
    }
}
