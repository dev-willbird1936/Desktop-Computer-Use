using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ShadowUse.Tools;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class FastUiCandidateDetectorTests
{
    [Fact]
    public async Task BlankImageHasNoEdgeRegionsAndNeverClaimsCompleteness()
    {
        using var image = new Bitmap(320, 180, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Black);
        var result = await FastUiCandidateDetector.DetectAsync(image, "edge", "blank");
        Assert.Empty(result.Candidates);
        Assert.False(result.CandidateSetComplete);
        Assert.NotNull(result.CandidateSetIssue);
    }

    [Fact]
    public async Task BorderedShapesProduceBoundedProposalsWithUnknownPurpose()
    {
        using var image = new Bitmap(641, 361, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image))
        using (var pen = new Pen(Color.White, 3))
        {
            graphics.Clear(Color.Black);
            graphics.DrawRectangle(pen, 10, 10, 48, 38);
            graphics.DrawRectangle(pen, 100, 10, 48, 38);
            graphics.DrawRectangle(pen, 190, 10, 48, 38);
        }
        var result = await FastUiCandidateDetector.DetectAsync(image, "edge", "shapes", maxCandidates: 2);
        Assert.NotEmpty(result.Candidates);
        Assert.InRange(result.Candidates.Count, 1, 2);
        Assert.False(result.CandidateSetComplete);
        foreach (var candidate in result.Candidates)
        {
            Assert.Equal("possible_visual_region", candidate.Role);
            Assert.Contains("purpose unknown", candidate.Label);
            Assert.InRange(candidate.X, 0, image.Width - 1);
            Assert.InRange(candidate.Y, 0, image.Height - 1);
            Assert.InRange(candidate.Width, 1, image.Width - candidate.X);
            Assert.InRange(candidate.Height, 1, image.Height - candidate.Y);
        }
    }

    [Fact]
    public async Task InvalidMethodAndLimitsAreRejected()
    {
        using var image = new Bitmap(20, 20);
        await Assert.ThrowsAsync<ArgumentException>(() => FastUiCandidateDetector.DetectAsync(image, "unknown", "f"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FastUiCandidateDetector.DetectAsync(image, "edge", "f", maxCandidates: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FastUiCandidateDetector.DetectAsync(image, "edge", "f", maxCandidates: 256));
    }

    [Fact]
    public async Task AlreadyCancelledDetectionDoesNotAnalyzePixels()
    {
        using var image = new Bitmap(20, 20);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FastUiCandidateDetector.DetectAsync(image, "edge", "f", source.Token));
    }

    [Fact]
    public async Task StaticImageToolReturnsParseableFrameBoundInventoryAndOverlay()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dcu-edge-fixture-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Bitmap(320, 180))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Black);
                image.Save(path, ImageFormat.Png);
            }
            var reply = await new ImageUiCandidateTools().DetectImage(path, "edge");
            Assert.False(reply.IsError);
            using var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(reply.Content[0]).Text);
            var root = json.RootElement;
            string frame = root.GetProperty("frame_id").GetString()!;
            Assert.StartsWith("image-", frame);
            Assert.False(root.GetProperty("candidate_set_complete").GetBoolean());
            Assert.True(ExternalGameVisualCandidateSetParser.TryParse(root.GetProperty("candidate_set_json").GetString()!,
                frame, 320, 180, out var inventory, out var error), error);
            Assert.Empty(inventory!.Candidates);
            Assert.IsType<ImageContentBlock>(reply.Content[1]);
        }
        finally { File.Delete(path); }
    }
}
