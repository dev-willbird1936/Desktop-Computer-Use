using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ShadowUse.Tools;
using ShadowUse.Vision;
using Xunit.Abstractions;

namespace ShadowUse.Tests;

public sealed class FrameRegionStabilityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptionalLiveBloonsRegionDiagnosticUsesTheProductionComparisonWithoutInput()
    {
        if (Environment.GetEnvironmentVariable("DCU_BTDB2_STATIC_REGION_DIAGNOSTIC") != "1") return;
        int pid = int.Parse(Environment.GetEnvironmentVariable("DCU_BTDB2_TARGET_PID") ?? "32492");
        using var process = Process.GetProcessById(pid);
        process.Refresh();
        Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
        string lifetime = $"static-region-diagnostic:{pid}:{process.StartTime.ToUniversalTime().Ticks}";
        var source = GameVision.Capture(process.MainWindowHandle, lifetime);
        Assert.NotNull(source);
        using var sourceBitmap = source!.Bitmap;
        double scale = Math.Min(1.0, 1280.0 / sourceBitmap.Width);
        int imageWidth = Math.Max(1, (int)Math.Round(sourceBitmap.Width * scale));
        int imageHeight = Math.Max(1, (int)Math.Round(sourceBitmap.Height * scale));
        byte[] sourceJpeg = GameVision.EncodeJpeg(sourceBitmap,
            new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height), imageWidth, imageHeight, 75L);

        await Task.Delay(650);
        var current = GameVision.Capture(process.MainWindowHandle, lifetime);
        Assert.NotNull(current);
        using var currentBitmap = current!.Bitmap;
        string? diagnosticImageDirectory = null;
        if (Environment.GetEnvironmentVariable("DCU_BTDB2_STATIC_REGION_DIAGNOSTIC_SAVE") == "1")
        {
            diagnosticImageDirectory = Path.Combine(Path.GetTempPath(), $"dcu-btd2-region-{Guid.NewGuid():N}");
            Directory.CreateDirectory(diagnosticImageDirectory);
            File.WriteAllBytes(Path.Combine(diagnosticImageDirectory, "source.jpg"), sourceJpeg);
            byte[] currentJpeg = GameVision.EncodeJpeg(currentBitmap,
                new Rectangle(0, 0, currentBitmap.Width, currentBitmap.Height), imageWidth, imageHeight, 75L);
            File.WriteAllBytes(Path.Combine(diagnosticImageDirectory, "current.jpg"), currentJpeg);
        }
        Assert.Equal(sourceBitmap.Width, currentBitmap.Width);
        Assert.Equal(sourceBitmap.Height, currentBitmap.Height);
        Rectangle[] regions = Environment.GetEnvironmentVariable("DCU_BTDB2_STATIC_REGION_DIAGNOSTIC_PRESET") switch
        {
            "back" => [new(230, 104, 65, 53), new(222, 96, 84, 70), new(545, 380, 140, 100)],
            "back_full" => [new(230, 104, 65, 53), new(227, 101, 71, 59), new(545, 380, 140, 100)],
            "back_box" => [new(230, 111, 65, 46), new(227, 108, 71, 52), new(545, 380, 140, 100)],
            "back_compact" => [new(230, 111, 52, 46), new(227, 108, 71, 52), new(545, 380, 140, 100)],
            "hero_challenge" => [new(473, 564, 206, 75), new(455, 555, 230, 95), new(760, 555, 225, 95)],
            "hero_tight" => [new(473, 564, 206, 75), new(460, 560, 225, 85), new(760, 555, 225, 95)],
            null or "battle" => [new(396, 549, 206, 76), new(370, 520, 260, 140), new(545, 380, 140, 100)],
            var preset => throw new InvalidOperationException($"Unknown static-region diagnostic preset '{preset}'."),
        };
        bool stable = FrameRegionStability.TryCompare(sourceJpeg, currentBitmap, imageWidth, imageHeight, 75,
            regions, out var differences);
        var motion = GameVision.MotionSince(source.FrameId, current.FrameId);
        var diagnostics = FrameRegionStability.Describe(regions, differences, comparisonAttempted: true);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            source_frame_id = source.FrameId,
            validation_frame_id = current.FrameId,
            diagnostic_image_directory = diagnosticImageDirectory,
            motion_available = motion.Available,
            whole_frame_moved = motion.Moved,
            whole_frame_unclassified = motion.Unclassified,
            stable,
            changed_pixel_fraction_exclusive_max = FrameRegionStability.MaxChangedPixelFraction,
            mean_absolute_channel_difference_inclusive_max = FrameRegionStability.MaxMeanAbsoluteChannelDifference,
            changed_channel_threshold = FrameRegionStability.ChangedChannelThreshold,
            regions = diagnostics.Select(diagnostic => new
            {
                role = diagnostic.Role,
                x = diagnostic.Region.X,
                y = diagnostic.Region.Y,
                width = diagnostic.Region.Width,
                height = diagnostic.Region.Height,
                status = diagnostic.Status,
                changed_pixel_fraction = diagnostic.ChangedPixelFraction,
                mean_absolute_channel_difference = diagnostic.MeanAbsoluteChannelDifference,
                within_limits = diagnostic.WithinLimits,
            }),
        }));
    }

    [Fact]
    public void BackgroundAnimationDoesNotDiluteTargetOrContextComparisons()
    {
        using var source = new Bitmap(320, 240);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Navy);
            graphics.FillRectangle(Brushes.DodgerBlue, 40, 40, 100, 60);
            graphics.FillRectangle(Brushes.Orange, 200, 25, 80, 50);
        }
        byte[] sourceJpeg = GameVision.EncodeJpeg(source, new Rectangle(0, 0, 320, 240), 320, 240, 75);
        using var current = (Bitmap)source.Clone();
        using (var graphics = Graphics.FromImage(current))
            graphics.FillRectangle(Brushes.Lime, 150, 150, 80, 60);

        bool stable = FrameRegionStability.TryCompare(sourceJpeg, current, 320, 240, 75,
            [new Rectangle(40, 40, 100, 60), new Rectangle(200, 25, 80, 50)], out var differences);

        Assert.True(stable);
        Assert.Equal(2, differences.Count);
        Assert.All(differences, difference =>
        {
            Assert.True(difference.ChangedPixelFraction < FrameRegionStability.MaxChangedPixelFraction);
            Assert.True(difference.MeanAbsoluteChannelDifference <= FrameRegionStability.MaxMeanAbsoluteChannelDifference);
        });
    }

    [Fact]
    public void ChangedTargetOrMenuContextRefusesTheFrame()
    {
        using var source = new Bitmap(320, 240);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.Navy);
            graphics.FillRectangle(Brushes.DodgerBlue, 40, 40, 100, 60);
            graphics.FillRectangle(Brushes.Orange, 200, 25, 80, 50);
        }
        byte[] sourceJpeg = GameVision.EncodeJpeg(source, new Rectangle(0, 0, 320, 240), 320, 240, 75);
        using var changedTarget = (Bitmap)source.Clone();
        using (var graphics = Graphics.FromImage(changedTarget))
            graphics.FillRectangle(Brushes.Red, 40, 40, 100, 60);

        Assert.False(FrameRegionStability.TryCompare(sourceJpeg, changedTarget, 320, 240, 75,
            [new Rectangle(40, 40, 100, 60), new Rectangle(200, 25, 80, 50)], out var targetDifferences));
        Assert.NotEmpty(targetDifferences);

        using var changedContext = (Bitmap)source.Clone();
        using (var graphics = Graphics.FromImage(changedContext))
            graphics.FillRectangle(Brushes.Purple, 200, 25, 80, 50);
        Assert.False(FrameRegionStability.TryCompare(sourceJpeg, changedContext, 320, 240, 75,
            [new Rectangle(40, 40, 100, 60), new Rectangle(200, 25, 80, 50)], out var contextDifferences));
        Assert.Equal(2, contextDifferences.Count);
    }

    [Fact]
    public void DiagnosticsReportTheFailingRegionAndDoNotMarkLaterRegionsStable()
    {
        Rectangle[] regions = [new(10, 10, 20, 20), new(40, 10, 30, 30), new(90, 10, 30, 30)];
        FrameRegionDifference[] differences = [new(regions[0], 0.01, 3.0)];

        var diagnostics = FrameRegionStability.Describe(regions, differences, comparisonAttempted: true);

        Assert.Equal("target_box", diagnostics[0].Role);
        Assert.Equal("failed", diagnostics[0].Status);
        Assert.False(diagnostics[0].WithinLimits);
        Assert.Equal("not_evaluated", diagnostics[1].Status);
        Assert.Equal("not_evaluated", diagnostics[2].Status);
        Assert.Null(diagnostics[1].ChangedPixelFraction);
    }

    [Fact]
    public void DiagnosticsMarkEveryRegionUnavailableWhenPixelsCouldNotBeCompared()
    {
        Rectangle[] regions = [new(10, 10, 20, 20), new(40, 10, 30, 30), new(90, 10, 30, 30)];

        var diagnostics = FrameRegionStability.Describe(regions, [], comparisonAttempted: false);

        Assert.Equal(["unavailable", "unavailable", "unavailable"], diagnostics.Select(item => item.Status));
        Assert.Equal(["target_box", "clicked_action_approved_region", "separate_menu_landmark"], diagnostics.Select(item => item.Role));
    }

    [Fact]
    public void PreDispatchRefusalPreservesMotionAndRegionDiagnosticsAndExplicitlySendsNoInput()
    {
        Rectangle[] regions = [new(10, 10, 20, 20), new(40, 10, 30, 30), new(90, 10, 30, 30)];
        FrameRegionDifference[] differences = [new(regions[0], 0.01, 3.0)];

        CallToolResult result = Assert.IsType<CallToolResult>(GameTools.ValidateSourceFrameStability(
            "source-frame", "validation-frame", 180,
            new CameraMotion(Moved: false, Unclassified: true, Available: true),
            regions, comparisonAttempted: true, regionsStable: false, differences)!);

        Assert.True(result.IsError);
        using var response = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        var root = response.RootElement;
        Assert.False(root.GetProperty("input_sent").GetBoolean());
        Assert.True(root.GetProperty("no_input_sent").GetBoolean());
        Assert.False(root.GetProperty("dispatch_attempted").GetBoolean());
        Assert.Equal("source-frame", root.GetProperty("source_frame_id").GetString());
        Assert.Equal("validation-frame", root.GetProperty("validation_frame_id").GetString());
        Assert.Equal(180, root.GetProperty("source_frame_age_ms").GetInt64());
        var motion = root.GetProperty("motion");
        Assert.True(motion.GetProperty("available").GetBoolean());
        Assert.False(motion.GetProperty("moved").GetBoolean());
        Assert.True(motion.GetProperty("unclassified").GetBoolean());
        var stability = root.GetProperty("source_frame_stability");
        Assert.Equal(0.002, stability.GetProperty("changed_pixel_fraction_exclusive_max").GetDouble());
        Assert.Equal(2.0, stability.GetProperty("mean_absolute_channel_difference_inclusive_max").GetDouble());
        var reportedRegions = stability.GetProperty("regions");
        Assert.Equal("target_box", reportedRegions[0].GetProperty("role").GetString());
        Assert.Equal("failed", reportedRegions[0].GetProperty("status").GetString());
        Assert.Equal(0.01, reportedRegions[0].GetProperty("changed_pixel_fraction").GetDouble());
        Assert.Equal("not_evaluated", reportedRegions[1].GetProperty("status").GetString());
        Assert.Equal("not_evaluated", reportedRegions[2].GetProperty("status").GetString());
    }

    [Fact]
    public void UnavailableMotionAndPixelsRemainFailClosed()
    {
        Rectangle[] regions = [new(10, 10, 20, 20), new(40, 10, 30, 30), new(90, 10, 30, 30)];

        CallToolResult result = Assert.IsType<CallToolResult>(GameTools.ValidateSourceFrameStability(
            "source-frame", null, 50, new CameraMotion(Moved: false, Unclassified: false, Available: false),
            regions, comparisonAttempted: false, regionsStable: false, differences: [])!);

        using var response = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        var root = response.RootElement;
        Assert.False(root.GetProperty("input_sent").GetBoolean());
        Assert.False(root.GetProperty("motion").GetProperty("available").GetBoolean());
        Assert.All(root.GetProperty("source_frame_stability").GetProperty("regions").EnumerateArray(),
            region => Assert.Equal("unavailable", region.GetProperty("status").GetString()));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, false, true, true, true, true, false)]
    [InlineData(false, true, true, false, false, false, false)]
    [InlineData(false, true, true, true, true, true, true)]
    [InlineData(false, false, true, true, true, true, true)]
    [InlineData(false, false, true, true, true, false, false)]
    [InlineData(false, false, true, false, false, false, true)]
    public void StaticMenuRegionsOverrideOnlyAvailableUnclassifiedMotion(bool moved, bool unclassified, bool available,
        bool regionsProvided, bool regionsCompared, bool regionsStable, bool expected)
        => Assert.Equal(expected, GameFrameStabilityPolicy.AllowsDispatch(
            new CameraMotion(moved, unclassified, available), regionsProvided, regionsCompared, regionsStable));

    [Theory]
    [InlineData(-1, 10, 30, 30)]
    [InlineData(10, 10, 0, 30)]
    [InlineData(290, 10, 40, 30)]
    [InlineData(0, 0, 320, 240)]
    public void InvalidOrOverlargeRegionsAreRejected(int x, int y, int width, int height)
    {
        using var frame = new Bitmap(320, 240);
        byte[] jpeg = GameVision.EncodeJpeg(frame, new Rectangle(0, 0, 320, 240), 320, 240, 75);

        Assert.False(FrameRegionStability.TryCompare(jpeg, frame, 320, 240, 75,
            [new Rectangle(x, y, width, height)], out _));
    }
}
