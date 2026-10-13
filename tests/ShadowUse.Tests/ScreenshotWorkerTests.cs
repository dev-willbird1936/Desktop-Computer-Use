using System.Drawing;
using System.Reflection;
using ShadowUse.Automation;

namespace ShadowUse.Tests;

public sealed class ScreenshotWorkerTests
{
    [Fact]
    public void GetAnnotationPoint_UsesWindowRelativeSnapshotCoordinates()
    {
        var screenshotType = typeof(UiaService).Assembly.GetType("ShadowUse.Capture.ScreenshotService");
        Assert.NotNull(screenshotType);
        var method = screenshotType.GetMethod(
            "GetAnnotationPoint",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var element = new ElementInfo
        {
            Id = "e1",
            X = 28,
            Y = 111,
            ScreenX = 128,
            ScreenY = 311,
        };

        var point = Assert.IsType<Point>(method.Invoke(null, [element]));

        Assert.Equal(new Point(28, 111), point);
    }

    [Fact]
    public void SelectWindowCapture_DoesNotReadScreenWithoutExplicitPermission()
    {
        var screenshotType = typeof(UiaService).Assembly.GetType("ShadowUse.Capture.ScreenshotService");
        Assert.NotNull(screenshotType);
        var method = screenshotType.GetMethod(
            "SelectWindowCapture",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        bool screenCaptureCalled = false;
        Func<Bitmap?> screenCapture = () =>
        {
            screenCaptureCalled = true;
            return new Bitmap(1, 1);
        };

        var selected = (Bitmap?)method.Invoke(null, [null, screenCapture, false]);

        Assert.Null(selected);
        Assert.False(screenCaptureCalled);
    }

    [Fact]
    public async Task RunPrintWindowWorker_AllowsOnlyOneOutstandingCapture()
    {
        var screenshotType = typeof(UiaService).Assembly.GetType("ShadowUse.Capture.ScreenshotService");
        Assert.NotNull(screenshotType);
        var method = screenshotType.GetMethod(
            "RunPrintWindowWorker",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Func<Bitmap?> blockedCapture = () =>
        {
            entered.Set();
            release.Wait();
            completed.Set();
            return null;
        };

        var first = Task.Run(
            () => (Bitmap?)method.Invoke(null, [blockedCapture, 50]));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var second = (Bitmap?)method.Invoke(null, [(Func<Bitmap?>)(() => new Bitmap(1, 1)), 50]);
            Assert.Null(second);
            Assert.Null(await first);
        }
        finally
        {
            release.Set();
            Assert.True(completed.Wait(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public void DisposeLateCapture_DisposesSuccessfulResult()
    {
        var screenshotType = typeof(UiaService).Assembly.GetType("ShadowUse.Capture.ScreenshotService")!;
        var method = screenshotType.GetMethod("DisposeLateCapture", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var bitmap = new Bitmap(1, 1);
        method.Invoke(null, [Task.FromResult<Bitmap?>(bitmap)]);
        // The cleanup has finished; never race a native GDI+ read against Dispose.
        Assert.Throws<ArgumentException>(() => bitmap.GetPixel(0, 0));
    }

    [Fact]
    public void DisposeLateCapture_AcceptsMissingAndFaultedResults()
    {
        var screenshotType = typeof(UiaService).Assembly.GetType("ShadowUse.Capture.ScreenshotService")!;
        var method = screenshotType.GetMethod("DisposeLateCapture", BindingFlags.Static | BindingFlags.NonPublic)!;
        method.Invoke(null, [Task.FromResult<Bitmap?>(null)]);
        method.Invoke(null, [Task.FromException<Bitmap?>(new InvalidOperationException("capture failed"))]);
    }
}
