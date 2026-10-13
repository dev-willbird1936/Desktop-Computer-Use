// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Imaging;
using ShadowUse.Native;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class BloonsTdb2GroundingTests
{
    private const int Width = 1722;
    private const int Height = 999;
    private static readonly int[] CentersX = [291, 408, 526, 644, 762, 880, 998, 1116];

    [Fact]
    public void CalibratedMenu_ReturnsFrameBoundBoxesForSixteenCardsAndFiveControls()
    {
        using var frame = MakeFrame();
        const double scale = 0.975609756097561;
        var observation = BloonsTdb2SelectionGrounding.Detect(
            frame, "frame-test", DateTimeOffset.UtcNow, "target-test", scale, 96,
            new NativeMethods.RECT { Left = 10, Top = 20, Right = Width + 10, Bottom = Height + 20 },
            ["Cancel", "Ready!"]);

        Assert.True(observation.CandidateSetComplete, observation.CandidateSetIssue);
        Assert.Equal(23, observation.Candidates.Count);
        Assert.Equal(3, observation.ContextRegions.Count);
        var first = Assert.Single(observation.Candidates, candidate => candidate.Key == "hero-r1-c1");
        Assert.Equal("frame-test:hero-r1-c1", first.Id);
        Assert.True(first.PixelAnchorDetected);
        Assert.Equal((int)Math.Round((CentersX[0] - 52) * scale), first.X);
        Assert.Equal((int)Math.Round(104 * scale), first.Width);
        Assert.Equal("unverified", first.Actionability);
        Assert.Equal("unknown", first.IdentityStatus);
        Assert.Equal(1680, observation.ImageWidth);
        Assert.True(observation.Candidates.Single(candidate => candidate.Key == "player-profile-banner").PixelAnchorDetected);
        Assert.True(observation.Candidates.Single(candidate => candidate.Key == "opponent-profile-banner").PixelAnchorDetected);
    }

    [Fact]
    public void CalibratedMenu_AcceptsResponsiveFrameAspectWhenEveryAnchorMatches()
    {
        using var frame = MakeFrame(1116, 658);
        var observation = BloonsTdb2SelectionGrounding.Detect(
            frame, "frame-resized", DateTimeOffset.UtcNow, "target-test", 1, 96,
            new NativeMethods.RECT { Left = 0, Top = 0, Right = 1116, Bottom = 658 }, ["Cancel", "Ready!"]);

        Assert.True(observation.CandidateSetComplete, observation.CandidateSetIssue);
        Assert.Equal(23, observation.Candidates.Count);
        var first = observation.Candidates.Single(candidate => candidate.Key == "hero-r1-c1");
        Assert.Equal(155, first.X);
        Assert.Equal(375, first.Y);
        Assert.Equal(67, first.Width);
        Assert.Equal(92, first.Height);
    }

    [Fact]
    public void MissingCardPixelEvidenceOrTextAnchor_KeepsInventoryButRejectsCompleteness()
    {
        using var frame = MakeFrame(missingCardKey: "hero-r2-c8");
        var observation = BloonsTdb2SelectionGrounding.Detect(
            frame, "frame-incomplete", DateTimeOffset.UtcNow, "target-test", 1, 96,
            new NativeMethods.RECT { Right = Width, Bottom = Height }, ["Cancel"]);

        Assert.False(observation.CandidateSetComplete);
        Assert.Contains("hero-r2-c8", observation.CandidateSetIssue);
        Assert.Contains("OCR anchor 'Ready'", observation.CandidateSetIssue);
        Assert.Equal(23, observation.Candidates.Count);
        Assert.False(observation.Candidates.Single(c => c.Key == "hero-r2-c8").PixelAnchorDetected);
    }

    private static Bitmap MakeFrame(int width = Width, int height = Height, string? missingCardKey = null)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(90, 60, 40));
        double sx = width / (double)Width, sy = height / (double)Height;
        Rectangle Scaled(int x, int y, int w, int h) => new(
            (int)Math.Round(x * sx), (int)Math.Round(y * sy),
            Math.Max(1, (int)Math.Round(w * sx)), Math.Max(1, (int)Math.Round(h * sy)));
        int penWidth = Math.Max(2, (int)Math.Round(5 * Math.Min(sx, sy)));
        using var yellow = new Pen(Color.FromArgb(255, 220, 20), penWidth);
        using var green = new Pen(Color.FromArgb(123, 188, 132), penWidth);
        for (int row = 1; row <= 2; row++)
        {
            int y = row == 1 ? 570 : 727;
            int bottom = row == 1 ? 710 : 862;
            for (int column = 1; column <= 8; column++)
            {
                if ($"hero-r{row}-c{column}" == missingCardKey) continue;
                var card = Scaled(CentersX[column - 1] - 52, y, 103, bottom - y - 1);
                g.DrawRectangle(yellow, card.X, card.Y, card.Width - 1, card.Height - 1);
            }
        }
        g.FillRectangle(Brushes.Yellow, Scaled(1196, 582, 290, 121));
        g.FillRectangle(Brushes.Yellow, Scaled(1196, 757, 290, 122));
        g.FillRectangle(Brushes.White, Scaled(620, 128, 47, 70));
        g.FillRectangle(Brushes.White, Scaled(1057, 128, 44, 70));
        g.FillRectangle(Brushes.White, Scaled(865, 862, 26, 26));
        foreach (var banner in new[] { Scaled(229, 49, 459, 64), Scaled(1035, 49, 460, 64) })
            g.DrawRectangle(green, banner.X, banner.Y, banner.Width - 1, banner.Height - 1);
        return bitmap;
    }
}
