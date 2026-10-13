using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Native;

namespace ShadowUse.Tests;

public sealed class SnapshotCacheTests
{
    [Fact]
    public void SnapshotCache_KeepsIndependentWindowsFromSameProcess()
    {
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType);
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var first = CreateSnapshot((IntPtr)1, "e1");
        var second = CreateSnapshot((IntPtr)2, "e2");

        store.Invoke(cache, [first]);
        store.Invoke(cache, [second]);

        var selectedFirst = Assert.IsType<Snapshot>(
            forElement.Invoke(cache, [CreateTarget((IntPtr)1), "e1"]));
        var selectedSecond = Assert.IsType<Snapshot>(
            forElement.Invoke(cache, [CreateTarget((IntPtr)2), "e2"]));
        Assert.Equal((IntPtr)1, selectedFirst.Hwnd);
        Assert.Equal((IntPtr)2, selectedSecond.Hwnd);
    }

    [Fact]
    public void SnapshotCache_DoesNotUseElementFromAnotherLiveTargetWindow()
    {
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType);
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod(
            "GetForElement",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(AppTarget), typeof(string)]);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1")]);
        store.Invoke(cache, [CreateSnapshot((IntPtr)2, "e2")]);
        var secondTarget = CreateTarget((IntPtr)2);

        var selected = forElement.Invoke(cache, [secondTarget, "e1"]);

        Assert.Null(selected);
    }

    [Fact]
    public void SnapshotCache_RemovesSnapshotsForDestroyedWindows()
    {
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType);
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var removeInvalid = cacheType.GetMethod(
            "RemoveInvalidWindows",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod(
            "GetForElement",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(AppTarget), typeof(string)]);
        Assert.NotNull(store);
        Assert.NotNull(removeInvalid);
        Assert.NotNull(forElement);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1")]);
        store.Invoke(cache, [CreateSnapshot((IntPtr)2, "e2")]);
        Func<IntPtr, bool> isWindow = hwnd => hwnd == (IntPtr)2;

        removeInvalid.Invoke(cache, [isWindow]);

        Assert.Null(forElement.Invoke(cache, [CreateTarget((IntPtr)1), "e1"]));
        Assert.NotNull(forElement.Invoke(cache, [CreateTarget((IntPtr)2), "e2"]));
    }

    [Fact]
    public void SnapshotCache_KeepsOlderIdsWhenAnotherAgentObservesSameWindow()
    {
        var type = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache")!;
        var cache = Activator.CreateInstance(type)!;
        type.GetMethod("Store")!.Invoke(cache, [CreateSnapshot((IntPtr)1, "agent-a")]);
        type.GetMethod("Store")!.Invoke(cache, [CreateSnapshot((IntPtr)1, "agent-b")]);
        var method = type.GetMethod("GetForElement")!;
        Assert.NotNull(method.Invoke(cache, [CreateTarget((IntPtr)1), "agent-a"]));
        Assert.NotNull(method.Invoke(cache, [CreateTarget((IntPtr)1), "agent-b"]));
    }

    [Fact]
    public void SnapshotCache_BoundsHistoryWithoutInvalidatingTheNewestSnapshot()
    {
        var type = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache")!;
        var cache = Activator.CreateInstance(type)!;
        for (int i = 0; i < 65; i++)
            type.GetMethod("Store")!.Invoke(cache, [CreateSnapshot((IntPtr)1, $"e{i}")]);
        var method = type.GetMethod("GetForElement")!;
        Assert.Null(method.Invoke(cache, [CreateTarget((IntPtr)1), "e0"]));
        Assert.NotNull(method.Invoke(cache, [CreateTarget((IntPtr)1), "e1"]));
        Assert.NotNull(method.Invoke(cache, [CreateTarget((IntPtr)1), "e64"]));
    }

    private static AppTarget CreateTarget(IntPtr hwnd)
        => new()
        {
            Pid = 100,
            ProcessName = "fixture",
            Hwnd = hwnd,
            Title = $"fixture-{hwnd}",
        };

    private static Snapshot CreateSnapshot(IntPtr hwnd, string elementId)
        => new()
        {
            Revision = 1,
            App = "fixture",
            Pid = 100,
            Hwnd = hwnd,
            Title = $"fixture-{hwnd}",
            Bounds = new NativeMethods.RECT { Right = 400, Bottom = 300 },
            Elements = [new ElementInfo { Id = elementId }],
        };
}
