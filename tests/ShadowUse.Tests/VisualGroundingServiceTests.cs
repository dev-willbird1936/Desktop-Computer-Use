// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class VisualGroundingServiceTests
{
    [Fact]
    public async Task RecognizeAsync_ReturnsUnsupported_WhenOcrEngineUnavailable()
    {
        var serviceType = typeof(UiaService).Assembly.GetType("ShadowUse.Vision.VisualGroundingService");
        Assert.NotNull(serviceType);
        var engineField = serviceType!.GetField("_engine", BindingFlags.NonPublic | BindingFlags.Static);
        var initField = serviceType.GetField("_engineInitialized", BindingFlags.NonPublic | BindingFlags.Static);
        var recognizeMethod = serviceType.GetMethod("RecognizeAsync", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(engineField);
        Assert.NotNull(initField);
        Assert.NotNull(recognizeMethod);

        var previousEngine = engineField!.GetValue(null);
        var previousInit = initField!.GetValue(null);
        try
        {
            // Simulate OcrEngine.TryCreateFromUserProfileLanguages() having already run and
            // found no installed OCR language, without needing a real absent-language machine.
            engineField.SetValue(null, null);
            initField.SetValue(null, true);

            var task = (Task<VisualGroundingOutcome>)recognizeMethod!.Invoke(
                null, [IntPtr.Zero, CancellationToken.None, false])!;
            var outcome = await task;

            Assert.Null(outcome.Observation);
            Assert.NotNull(outcome.UnsupportedReason);
            Assert.Contains("OCR language", outcome.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            engineField.SetValue(null, previousEngine);
            initField.SetValue(null, previousInit);
        }
    }

    [Fact]
    public void RegionHash_IsDeterministic_ForTheSameBitmapAndRegion()
    {
        using var bitmap = DrawTestBitmap();
        var region = new Rectangle(10, 10, 40, 20);

        var hashType = typeof(UiaService).Assembly.GetType("ShadowUse.Vision.RegionHash");
        Assert.NotNull(hashType);
        var computeMethod = hashType!.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static);
        var distanceMethod = hashType.GetMethod("HammingDistance", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(computeMethod);
        Assert.NotNull(distanceMethod);

        var first = (ulong)computeMethod!.Invoke(null, [bitmap, region])!;
        var second = (ulong)computeMethod.Invoke(null, [bitmap, region])!;
        Assert.Equal(first, second);
        Assert.Equal(0, (int)distanceMethod!.Invoke(null, [first, second])!);
    }

    [Fact]
    public void RegionHash_DiffersForVisuallyDifferentRegions()
    {
        using var bitmap = DrawTestBitmap();
        var uniformRegion = new Rectangle(0, 0, 30, 30);  // solid white in DrawTestBitmap: dHash is always 0
        var edgeRegion = new Rectangle(35, 35, 30, 30);   // straddles the black/white boundary at (50,50)

        var hashType = typeof(UiaService).Assembly.GetType("ShadowUse.Vision.RegionHash");
        var computeMethod = hashType!.GetMethod("Compute", BindingFlags.Public | BindingFlags.Static);

        var uniformHash = (ulong)computeMethod!.Invoke(null, [bitmap, uniformRegion])!;
        var edgeHash = (ulong)computeMethod.Invoke(null, [bitmap, edgeRegion])!;

        Assert.NotEqual(uniformHash, edgeHash);
    }

    private static Bitmap DrawTestBitmap()
    {
        var bmp = new Bitmap(100, 100);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.Black, 50, 50, 50, 50);
        return bmp;
    }
}
