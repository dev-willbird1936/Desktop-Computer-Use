using System.Drawing;
using ShadowUse.Native;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class GameVisionTests
{
    [Fact]
    public void ImageCoordinatesTranslateToCurrentWindowOrigin()
    {
        var rect = new NativeMethods.RECT { Left = 300, Top = 200, Right = 1300, Bottom = 800 };
        Assert.Equal((500, 300), GameVision.ToScreen(rect, 0.5, 100, 50));
    }

    [Fact]
    public void FrameClassificationNeverClaimsDispatchChangedTheWorld()
    {
        Assert.Equal("no_change", GameVision.ClassifyChange(0));
        Assert.Equal("minor_change", GameVision.ClassifyChange(0.005));
        Assert.Equal("frame_changed", GameVision.ClassifyChange(0.02));
    }

    [Fact]
    public void CalibratedBarReportsFillAndMissingColorIsNotZero()
    {
        using var image = new Bitmap(100, 10);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.Red, 0, 0, 50, 10);
        }
        var reading = GameVision.MeasureBar(image, new(0, 0, 100, 10), new("health", 0, 0, 100, 10, "#FF0000", 10));
        Assert.Equal(50, reading.Percent);
        Assert.True(GameVision.IsReliableBar(reading));
        var unknown = GameVision.MeasureBar(image, new(0, 0, 100, 10), new("health", 0, 0, 100, 10, "#00FF00", 10));
        Assert.Null(unknown.Percent);
    }

    [Fact]
    public void MissingWindowCaptureNeverFallsBackToUnrelatedDesktop()
        => Assert.Null(GameVision.Capture((IntPtr)(-1)));
}
