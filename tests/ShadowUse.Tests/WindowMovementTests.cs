using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Native;
using ShadowUse.Tools;

namespace ShadowUse.Tests;

public sealed class WindowMovementTests
{
    [Fact]
    public void RebaseFromSnapshot_TranslatesCoordinatesWithMovedWindow()
    {
        var mapper = typeof(ComputerUseTools).Assembly.GetType("ShadowUse.Tools.WindowCoordinateMapper");
        Assert.NotNull(mapper);
        var method = mapper.GetMethod("RebaseFromSnapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var snapshotBounds = new NativeMethods.RECT { Left = 100, Top = 200, Right = 500, Bottom = 500 };
        var currentBounds = new NativeMethods.RECT { Left = 500, Top = 600, Right = 900, Bottom = 900 };

        var point = Assert.IsType<NativeMethods.POINT>(
            method.Invoke(null, [snapshotBounds, currentBounds, 128, 311]));

        Assert.Equal(528, point.X);
        Assert.Equal(711, point.Y);
    }

    [Fact]
    public void RebaseFromSnapshot_DoesNotTranslatePointOutsideOldWindow()
    {
        var mapper = typeof(ComputerUseTools).Assembly.GetType("ShadowUse.Tools.WindowCoordinateMapper");
        Assert.NotNull(mapper);
        var method = mapper.GetMethod("RebaseFromSnapshot", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var snapshotBounds = new NativeMethods.RECT { Left = 100, Top = 200, Right = 500, Bottom = 500 };
        var currentBounds = new NativeMethods.RECT { Left = 500, Top = 600, Right = 900, Bottom = 900 };

        var point = Assert.IsType<NativeMethods.POINT>(
            method.Invoke(null, [snapshotBounds, currentBounds, 900, 700]));

        Assert.Equal(900, point.X);
        Assert.Equal(700, point.Y);
    }

    [Fact]
    public void HasActionDelta_IgnoresPureWindowGeometryChanges()
    {
        var method = typeof(ComputerUseTools).GetMethod(
            "HasActionDelta",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var before = SnapshotAt(100, 200, 10, 20);
        var after = SnapshotAt(500, 600, 10, 20);

        var changed = Assert.IsType<bool>(method.Invoke(null, [before, after]));

        Assert.False(changed);
    }

    private static Snapshot SnapshotAt(int left, int top, int relativeX, int relativeY)
        => new()
        {
            Revision = 1,
            App = "fixture",
            Pid = 42,
            Hwnd = (IntPtr)1,
            Title = "fixture",
            Bounds = new NativeMethods.RECT { Left = left, Top = top, Right = left + 400, Bottom = top + 300 },
            Elements =
            [
                new ElementInfo
                {
                    Id = "e1",
                    RuntimeId = [1, 2, 3],
                    AutomationId = "surface",
                    Name = "Surface",
                    ControlType = "Pane",
                    Value = "unchanged",
                    X = relativeX,
                    Y = relativeY,
                    ScreenX = left + relativeX,
                    ScreenY = top + relativeY,
                    Width = 100,
                    Height = 50,
                },
            ],
        };
}
