using System.Drawing;
using ShadowUse.Automation;
using ShadowUse.Native;
using ShadowUse.Tools;
using ShadowUse.Vision;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;

namespace ShadowUse.Tests;

public sealed class GameModeTests
{
    [Theory]
    [InlineData("shift", new[] { 0x10 })]
    [InlineData("ctrl", new[] { 0x11 })]
    [InlineData("w", new[] { 0x57 })]
    [InlineData("shift+w", new[] { 0x10, 0x57 })]
    [InlineData("w+d", new[] { 0x57, 0x44 })]
    [InlineData("ctrl+e", new[] { 0x11, 0x45 })]
    [InlineData("numpad1", new[] { 0x61 })]
    [InlineData("f5", new[] { 0x74 })]
    public void ParseKeySet_MapsKeysAndModifiers(string keys, int[] expected)
        => Assert.Equal(expected, InteractiveInput.ParseKeySet(keys));

    [Fact]
    public void ForegroundRetry_OnlyRepeatsBeforeAnyInputWasSent()
    {
        var unsent = new InputResult(false, "sendinput", "target window lost the foreground", InputSent: false);
        var sent = unsent with { InputSent = true };
        Assert.True(GameTools.CanRetryForegroundLoss(unsent, 1));
        Assert.True(GameTools.CanRetryForegroundLoss(unsent, 2));
        Assert.False(GameTools.CanRetryForegroundLoss(unsent, 3));
        Assert.False(GameTools.CanRetryForegroundLoss(sent, 1));
        Assert.False(GameTools.CanRetryForegroundLoss(unsent with { Detail = "sendinput rejected the key press" }, 1));
    }

    private static GameTrackingFrame TrackFrame(string id, byte[] pixels, int width, int height)
        => new(id, DateTimeOffset.UtcNow, "test-target-lifetime", width, height, 1, width, height, pixels);

    private static void DrawPatch(byte[] pixels, int frameWidth, int x, int y, int width, int height, byte[] patch)
    {
        for (int row = 0; row < height; row++)
            Array.Copy(patch, row * width, pixels, (y + row) * frameWidth + x, width);
    }

    [Fact]
    public void PatchTracker_FollowsSelectedTextureInSyntheticFrames()
    {
        const int frameWidth = 160, frameHeight = 100, patchWidth = 24, patchHeight = 20;
        var patch = new byte[patchWidth * patchHeight];
        new Random(19).NextBytes(patch);
        var first = new byte[frameWidth * frameHeight];
        var second = new byte[first.Length];
        DrawPatch(first, frameWidth, 22, 30, patchWidth, patchHeight, patch);
        DrawPatch(second, frameWidth, 34, 37, patchWidth, patchHeight, patch);
        Assert.True(PatchTracker.TryCreate(TrackFrame("seed", first, frameWidth, frameHeight), new Rectangle(22, 30, patchWidth, patchHeight), out var tracker, out var error), error);
        var result = tracker!.Update(TrackFrame("next", second, frameWidth, frameHeight));
        Assert.True(result.State == "tracking", $"{result.Reason}: confidence={result.Confidence}, margin={result.Margin}, box={result.Box}");
        Assert.Equal(new Rectangle(34, 37, patchWidth, patchHeight), result.Box);
        Assert.True(result.Confidence >= 0.72);
    }

    [Fact]
    public void PatchTracker_RejectsFlatAndAmbiguousSelections()
    {
        const int frameWidth = 120, frameHeight = 80, patchWidth = 20, patchHeight = 20;
        var flat = new byte[frameWidth * frameHeight];
        Array.Fill(flat, (byte)80);
        Assert.False(PatchTracker.TryCreate(TrackFrame("flat", flat, frameWidth, frameHeight), new Rectangle(20, 20, patchWidth, patchHeight), out _, out var flatError));
        Assert.Contains("texture", flatError);

        var patch = new byte[patchWidth * patchHeight];
        new Random(27).NextBytes(patch);
        var first = new byte[flat.Length];
        var next = new byte[flat.Length];
        DrawPatch(first, frameWidth, 40, 20, patchWidth, patchHeight, patch);
        DrawPatch(next, frameWidth, 30, 20, patchWidth, patchHeight, patch);
        DrawPatch(next, frameWidth, 50, 20, patchWidth, patchHeight, patch);
        Assert.True(PatchTracker.TryCreate(TrackFrame("seed2", first, frameWidth, frameHeight), new Rectangle(40, 20, patchWidth, patchHeight), out var tracker, out var error), error);
        var result = tracker!.Update(TrackFrame("ambiguous", next, frameWidth, frameHeight));
        Assert.True(result.State == "uncertain", $"{result.Reason}: confidence={result.Confidence}, margin={result.Margin}, box={result.Box}");
        Assert.Equal("ambiguous_patch_match", result.Reason);
    }

    [Fact]
    public void PatchTracker_StopsWhenFrameGeometryChanges()
    {
        const int frameWidth = 80, frameHeight = 60;
        var pixels = new byte[frameWidth * frameHeight];
        new Random(31).NextBytes(pixels);
        Assert.True(PatchTracker.TryCreate(TrackFrame("seed3", pixels, frameWidth, frameHeight), new Rectangle(10, 10, 16, 16), out var tracker, out var error), error);
        var changedGeometry = TrackFrame("resized", new byte[90 * frameHeight], 90, frameHeight);
        var result = tracker!.Update(changedGeometry);
        Assert.Equal("lost", result.State);
        Assert.Equal("target_or_frame_geometry_changed", result.Reason);
    }

    [Fact]
    public void ParseKeySet_DedupesAndRejectsUnknown()
    {
        Assert.Equal([0x10, 0x57], InteractiveInput.ParseKeySet("shift+shift+w"));
        Assert.Throws<ArgumentException>(() => InteractiveInput.ParseKeySet("w+banana"));
    }

    [Fact]
    public void ToScreen_ScalesImagePixelsBackToWindowPixels()
    {
        var rect = new NativeMethods.RECT { Left = 100, Top = 50, Right = 2660, Bottom = 1490 };
        Assert.Equal((100 + 1280, 50 + 720), GameVision.ToScreen(rect, 0.5, 640, 360));
        Assert.Equal((100, 50), GameVision.ToScreen(rect, 0.5, 0, 0));
    }

    [Fact]
    public void PhysicalRegion_ConvertsAndClamps()
    {
        using var bmp = new Bitmap(2000, 1000);
        Assert.Equal(new Rectangle(200, 100, 400, 200), GameVision.PhysicalRegion(bmp, 0.5, 100, 50, 200, 100));
        Assert.Equal(new Rectangle(1900, 900, 100, 100), GameVision.PhysicalRegion(bmp, 0.5, 950, 450, 500, 500));
        Assert.Null(GameVision.PhysicalRegion(bmp, 0.5, 5000, 0, 10, 10));
    }

    private static Bitmap Solid(Color c, int w = 320, int h = 180)
    {
        var b = new Bitmap(w, h);
        using var g = Graphics.FromImage(b);
        g.Clear(c);
        return b;
    }

    [Fact]
    public void FrameDiff_ClassifiesIdenticalSmallAndLargeChanges()
    {
        using var a = Solid(Color.Black);
        using var same = Solid(Color.Black);
        using var big = Solid(Color.White);
        using var small = Solid(Color.Black);
        using (var g = Graphics.FromImage(small)) g.FillRectangle(Brushes.White, 0, 0, 100, 60);
        var sig = GameVision.Signature(a);
        Assert.Equal("no_change", GameVision.ClassifyChange(GameVision.ChangedFraction(sig, GameVision.Signature(same))));
        Assert.Equal("frame_changed", GameVision.ClassifyChange(GameVision.ChangedFraction(sig, GameVision.Signature(big))));
        Assert.Equal("frame_changed", GameVision.ClassifyChange(GameVision.ChangedFraction(sig, GameVision.Signature(small))));
        Assert.False(GameVision.IsVisualChange("no_change"));
        Assert.True(GameVision.IsVisualChange("minor_change"));
    }

    [Fact]
    public void EncodeJpeg_BlacksOnlyConfiguredObservationMasks()
    {
        using var frame = Solid(Color.White, 100, 50);
        var bytes = GameVision.EncodeJpeg(frame, new Rectangle(0, 0, 100, 50), 100, 50, 95, [new Rectangle(20, 10, 30, 20)]);
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        Assert.True(image.GetPixel(30, 20).R < 20);
        Assert.True(image.GetPixel(5, 5).R > 230);
    }

    [Fact]
    public void Signature_UsesOnlyTheRequestedCropAndMasksExcludedRegions()
    {
        using var baseline = Solid(Color.Black);
        using var outsideChanged = Solid(Color.Black);
        using (var g = Graphics.FromImage(outsideChanged)) g.FillRectangle(Brushes.White, 220, 0, 100, 180);
        Assert.Equal(GameVision.Signature(baseline, new Rectangle(0, 0, 160, 180)),
            GameVision.Signature(outsideChanged, new Rectangle(0, 0, 160, 180)));
        Assert.Equal(GameVision.Signature(baseline, exclusions: [new Rectangle(220, 0, 100, 180)]),
            GameVision.Signature(outsideChanged, exclusions: [new Rectangle(220, 0, 100, 180)]));
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 50.0)]
    [InlineData(1.0, 100.0)]
    public void MeasureBar_ReadsFillOfVividBar(double fill, double expectedPercent)
    {
        using var bmp = Solid(Color.FromArgb(20, 20, 20), 300, 40);
        using (var g = Graphics.FromImage(bmp))
            if (fill > 0) g.FillRectangle(new SolidBrush(Color.FromArgb(200, 40, 40)), 50, 10, (int)(200 * fill), 20);
        var reading = GameVision.MeasureBar(bmp, new Rectangle(50, 10, 200, 20), new BarSpec("health", 50, 10, 200, 20));
        if (fill == 0)
        {
            Assert.Null(reading.Percent);
            Assert.Contains("empty", reading.Note);
            Assert.Equal("uncalibrated_no_match", reading.CalibrationStatus);
        }
        else
        {
            Assert.Equal(expectedPercent, reading.Percent!.Value, 1.5);
            Assert.Equal("#C82828", reading.Color);
            Assert.Equal("auto_detected_unverified", reading.CalibrationStatus);
            Assert.InRange(reading.Confidence, 0, 1);
        }
    }

    [Fact]
    public void MeasureBar_CalibratedNoMatchIsUnknownAndUnreliable()
    {
        using var bmp = Solid(Color.FromArgb(20, 20, 20), 240, 30);
        var reading = GameVision.MeasureBar(bmp, new Rectangle(0, 0, 200, 20), new BarSpec("health", 0, 0, 200, 20, "#C83232"));
        Assert.Null(reading.Percent);
        Assert.Equal("calibrated_no_match", reading.CalibrationStatus);
        Assert.False(GameVision.IsReliableBar(reading));
        Assert.Equal(0.1, reading.Confidence);
        Assert.Contains("empty, miscalibrated, or hidden", reading.Note);
    }

    [Fact]
    public void MeasureBar_CoveredOrScatteredCalibratedMatchesStayUnknown()
    {
        using var bmp = Solid(Color.FromArgb(20, 20, 20), 240, 30);
        using (var g = Graphics.FromImage(bmp))
        {
            using var red = new SolidBrush(Color.FromArgb(200, 50, 50));
            // a window covers the middle of a full bar, leaving two disjoint red slivers
            g.FillRectangle(red, 0, 0, 40, 20);
            g.FillRectangle(red, 150, 0, 40, 20);
        }
        var reading = GameVision.MeasureBar(bmp, new Rectangle(0, 0, 200, 20), new BarSpec("health", 0, 0, 200, 20, "#C83232"));
        Assert.Null(reading.Percent);
        Assert.Equal("calibrated_fragmented", reading.CalibrationStatus);
        Assert.False(GameVision.IsReliableBar(reading));
        Assert.True(reading.Confidence < 0.5);
    }

    [Fact]
    public void MeasureBar_WithColorReadsGreyBarAndVertical()
    {
        using var bmp = Solid(Color.FromArgb(10, 10, 10), 100, 100);
        using (var g = Graphics.FromImage(bmp)) g.FillRectangle(new SolidBrush(Color.FromArgb(180, 180, 180)), 10, 40, 20, 40);
        var v = GameVision.MeasureBar(bmp, new Rectangle(10, 10, 20, 70), new BarSpec("stamina", 10, 10, 20, 70, "#B4B4B4", 30, "vertical"));
        Assert.Equal(57.1, v.Percent!.Value, 2);
        Assert.True(GameVision.IsReliableBar(v));
        var vivid = GameVision.MeasureBar(bmp, new Rectangle(10, 10, 20, 70), new BarSpec("stamina", 10, 10, 20, 70, null, 60, "vertical"));
        Assert.Null(vivid.Percent);
        Assert.Contains("empty", vivid.Note);
        Assert.Equal("uncalibrated_no_match", vivid.CalibrationStatus);
    }

    [Fact]
    public void GameTools_AreHiddenUntilGameModeAndCatalogued()
    {
        foreach (var name in new[] { "game_screenshot", "game_click", "game_move", "game_drag", "game_scroll", "game_key", "game_release_keys", "game_ocr", "game_bars", "game_wait", "game_track", "game_follow" })
        {
            var entry = ToolCatalog.Get(name);
            Assert.NotNull(entry);
            Assert.True(entry!.Game);
            Assert.False(entry.Core);
        }
    }

    [Fact]
    public void ParseKeySet_AcceptsMouseButtons()
    {
        Assert.Equal([0x02, 0x01], InteractiveInput.ParseKeySet("rmb+lmb"));
        Assert.Equal([0x04], InteractiveInput.ParseKeySet("mmb"));
        Assert.Contains(0x01, InteractiveInput.ParseKeySet("shift+lmb"));
    }

    [Fact]
    public void FastPace_PlansAreShorterAndStillRandom()
    {
        var rng = new Random(7);
        double Total(bool fast) => Enumerable.Range(0, 60).Average(_ => HumanPlanner.PlanPath((0, 0), (900, 400), 40, 1, rng, fast).DurationMs);
        Assert.True(Total(true) < Total(false) * 0.6);
        var holds = Enumerable.Range(0, 200).Select(_ => HumanPlanner.KeyHoldMs(rng, true)).ToList();
        Assert.All(holds, h => Assert.InRange(h, 28, 72));
        Assert.True(holds.Distinct().Count() > 10);
        var fastTyping = HumanPlanner.PlanTyping("hello world", 1, 0.5, rng, true);
        Assert.Equal("hello world".Length, fastTyping.Count);
        Assert.All(fastTyping, s => Assert.True(s.DelayAfterMs <= 60 + 400));
    }

    [Fact]
    public void InputPace_ScopeOverridesAndRestores()
    {
        Assert.False(InputPace.Fast);
        using (InputPace.Scope("fast", 150))
        {
            Assert.True(InputPace.Fast);
            Assert.Equal(150, InputPace.MaxLatencyMs);
            Assert.True(InputPace.Ms(120) < 60);
            Assert.True(InputPace.Ms(10) >= 10);
        }
        Assert.False(InputPace.Fast);
        Assert.Equal(0, InputPace.MaxLatencyMs);
        Assert.Equal(120, InputPace.Ms(120));
    }

    [Fact]
    public void EstimateMotion_FindsShiftAndStill()
    {
        var rng = new Random(3);
        int w = GameVision.MotionWidth, h = GameVision.MotionHeight;
        var a = new byte[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = (byte)rng.Next(256);
        Assert.Equal("none", GameVision.EstimateMotion(a, (byte[])a.Clone()).Kind);

        var b = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b[y * w + x] = a[Math.Clamp(y - 3, 0, h - 1) * w + Math.Clamp(x - 8, 0, w - 1)];
        var m = GameVision.EstimateMotion(a, b);
        Assert.Equal("shift", m.Kind);
        Assert.Equal((8, 3), (m.Dx, m.Dy));

        var other = new byte[w * h];
        for (int i = 0; i < other.Length; i++) other[i] = (byte)rng.Next(256);
        Assert.Equal("changed", GameVision.EstimateMotion(a, other).Kind);
    }
}
