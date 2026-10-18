using ShadowUse.Jev.Flow;

namespace ShadowUse.Jev.FlowTests;

public sealed class FlowBookTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dcu-flow-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static FlowAction Click(string id, string label = "Play button", NormBox? box = null) =>
        new() { Id = id, Kind = FlowActionKind.Click, Description = label, TargetDescription = label, Box = box, Source = "discovered" };

    [Theory]
    [InlineData("Buy Club Membership £9.99")]
    [InlineData("Purchase gems")]
    [InlineData("Subscribe now")]
    [InlineData("Delete save data")]
    [InlineData("Quit")]
    [InlineData("Watch ad for reward")]
    [InlineData("Unlock for $4.99")]
    public void GuardRefusesPurchaseAccountAndDestructiveControls(string label)
    {
        var book = new JevActionBook();
        book.EnsureScreen("main", "Main");
        Assert.False(book.Upsert("main", Click("x", label), out var reason));
        Assert.NotNull(reason);
        Assert.Empty(book.MenuFor("main"));
    }

    [Fact]
    public void GuardAcceptsOrdinaryMenuControls()
    {
        var book = new JevActionBook();
        book.EnsureScreen("main", "Main");
        foreach (var label in new[] { "Play button", "Single Player", "Back arrow", "Easy difficulty card", "Start round" })
            Assert.True(book.Upsert("main", Click(label.Replace(' ', '_'), label), out _), label);
    }

    [Theory]
    [InlineData("space", true)] [InlineData("escape", true)] [InlineData("5", true)]
    [InlineData("ctrl+c", false)] [InlineData("f4", false)] [InlineData("alt", false)]
    public void KeyAllowlistMatchesGuardedPath(string key, bool allowed)
    {
        var action = new FlowAction { Id = "k", Kind = FlowActionKind.KeyTap, Key = key, Description = "tap" };
        Assert.Equal(allowed, JevFlowGuard.Refusal(action) is null);
    }

    [Fact]
    public void OversizedBoxesAreNotLearned()
    {
        var book = new JevActionBook();
        book.EnsureScreen("main", "Main");
        book.Upsert("main", Click("big"), out _);
        book.RecordBox("main", "big", new NormBox(0.1, 0.1, 0.9, 0.9), 0.99);
        Assert.Null(book.MenuFor("main").Single().Box);
        book.RecordBox("main", "big", new NormBox(0.4, 0.4, 0.5, 0.5), 0.99);
        Assert.NotNull(book.MenuFor("main").Single().Box);
    }

    [Fact]
    public void SaveAndReloadKeepsScreensBoxesTransitionsAndFingerprints()
    {
        string path = Path.Combine(_dir, "book.json");
        var book = new JevActionBook(path);
        book.EnsureScreen("a", "Screen A"); book.EnsureScreen("b", "Screen B");
        book.Upsert("a", Click("go", "Go button", new NormBox(0.4, 0.4, 0.5, 0.5)), out _);
        book.RecordTransition("a", "go", "b");
        book.RecordTransition("a", "go", "b");
        book.AddFingerprint("a", Enumerable.Repeat((byte)10, 720).ToArray());
        Assert.True(book.Save());

        var loaded = new JevActionBook(path);
        var action = Assert.Single(loaded.MenuFor("a"));
        Assert.Equal("b", action.ExpectedNextScreen);
        Assert.Equal(2, action.Transitions["b"]);
        Assert.Equal(0.45, action.Box!.Value.CenterX, 3);
        Assert.NotNull(loaded.Match(Enumerable.Repeat((byte)12, 720).ToArray(), 0.05));
    }

    [Fact]
    public void LoadDropsActionsTheGuardWouldRefuseEvenIfFileWasEdited()
    {
        string path = Path.Combine(_dir, "book.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, """
        {"Version":1,"Screens":[{"Id":"a","Description":"A","Actions":[
          {"Id":"ok","Kind":"click","Description":"Play","TargetDescription":"Play"},
          {"Id":"bad","Kind":"click","Description":"Buy gems £4.99","TargetDescription":"Buy gems"},
          {"Id":"keys","Kind":"key_tap","Description":"copy","Key":"ctrl+c"}],"Fingerprints":[]}]}
        """);
        var book = new JevActionBook(path);
        Assert.Equal(["ok"], book.MenuFor("a").Select(a => a.Id).ToArray());
    }

    [Fact]
    public void CorruptBookFileReportsErrorAndStartsEmpty()
    {
        string path = Path.Combine(_dir, "book.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "{not json");
        var book = new JevActionBook(path);
        Assert.NotNull(book.LoadError);
        Assert.Empty(book.Screens);
    }

    [Fact]
    public void FingerprintMatchRequiresToleranceAndMargin()
    {
        var book = new JevActionBook();
        book.EnsureScreen("a", "A"); book.EnsureScreen("b", "B");
        var fa = Enumerable.Range(0, 720).Select(i => (byte)(i % 200)).ToArray();
        var fb = Enumerable.Range(0, 720).Select(i => (byte)((i * 7) % 200)).ToArray();
        book.AddFingerprint("a", fa); book.AddFingerprint("b", fb);
        Assert.Equal("a", book.Match(fa, 0.08)!.Value.ScreenId);
        Assert.Null(book.Match(Enumerable.Repeat((byte)255, 720).ToArray(), 0.08));
        // A frame exactly between two saved screens is ambiguous and must not be trusted.
        var mid = fa.Zip(fb, (x, y) => (byte)((x + y) / 2)).ToArray();
        Assert.Null(book.Match(mid, 0.5, minMargin: 0.2));
    }

    [Fact]
    public void LearnedBoxIsDroppedAfterRepeatedNoEffectButCallerBoxIsKept()
    {
        var book = new JevActionBook();
        book.EnsureScreen("a", "A");
        var learned = Click("learned", "Play button", new NormBox(0.4, 0.4, 0.5, 0.5));
        var seeded = Click("seeded", "Other button", new NormBox(0.1, 0.1, 0.2, 0.2)); seeded.Source = "caller";
        book.Upsert("a", learned, out _); book.Upsert("a", seeded, out _);
        Assert.False(book.RecordNoEffect("a", "learned"));
        Assert.True(book.RecordNoEffect("a", "learned"));
        Assert.Null(book.MenuFor("a").Single(a => a.Id == "learned").Box);
        book.RecordNoEffect("a", "seeded"); book.RecordNoEffect("a", "seeded");
        Assert.NotNull(book.MenuFor("a").Single(a => a.Id == "seeded").Box);
    }

    [Fact]
    public void GlobalActionsAppearOnEveryScreenWithoutShadowing()
    {
        var book = new JevActionBook();
        book.EnsureScreen(JevActionBook.GlobalScreenId, "any");
        book.EnsureScreen("a", "A");
        book.Upsert(JevActionBook.GlobalScreenId, new FlowAction { Id = "esc", Kind = FlowActionKind.KeyTap, Key = "escape", Description = "Back out" }, out _);
        book.Upsert("a", new FlowAction { Id = "esc", Kind = FlowActionKind.KeyTap, Key = "escape", Description = "Local escape" }, out _);
        var menu = book.MenuFor("a");
        Assert.Single(menu);
        Assert.Equal("Local escape", menu[0].Description);
        book.EnsureScreen("b", "B");
        Assert.Single(book.MenuFor("b"));
    }
}
