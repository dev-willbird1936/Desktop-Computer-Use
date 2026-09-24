using System.Reflection;
using ShadowUse.Automation;
using ShadowUse.Native;

namespace ShadowUse.Tests;

public sealed class SnapshotCacheTests
{
    [Fact]
    public void SnapshotCache_KeepsIndependentWindowsFromSameProcess()
    {
        var registry = new TargetRegistry("cache-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var firstReference = Register(registry, (IntPtr)1);
        var secondReference = Register(registry, (IntPtr)2);
        var first = CreateSnapshot((IntPtr)1, "e1", firstReference);
        var second = CreateSnapshot((IntPtr)2, "e2", secondReference);

        store.Invoke(cache, [first]);
        store.Invoke(cache, [second]);

        var selectedFirst = Assert.IsType<Snapshot>(
            forElement.Invoke(cache, [CreateTarget((IntPtr)1, firstReference), "e1"]));
        var selectedSecond = Assert.IsType<Snapshot>(
            forElement.Invoke(cache, [CreateTarget((IntPtr)2, secondReference), "e2"]));
        Assert.Equal((IntPtr)1, selectedFirst.Hwnd);
        Assert.Equal((IntPtr)2, selectedSecond.Hwnd);
    }

    [Fact]
    public void SnapshotCache_DoesNotUseElementFromAnotherLiveTargetWindow()
    {
        var registry = new TargetRegistry("cache-sibling-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod(
            "GetForElement",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(AppTarget), typeof(string)]);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var firstReference = Register(registry, (IntPtr)1);
        var secondReference = Register(registry, (IntPtr)2);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", firstReference)]);
        store.Invoke(cache, [CreateSnapshot((IntPtr)2, "e2", secondReference)]);
        var secondTarget = CreateTarget((IntPtr)2, secondReference);

        var selected = forElement.Invoke(cache, [secondTarget, "e1"]);

        Assert.Null(selected);
    }

    [Fact]
    public void SnapshotCache_RemovesSnapshotsForDestroyedWindows()
    {
        var registry = new TargetRegistry("cache-removal-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
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
        var firstReference = Register(registry, (IntPtr)1);
        var secondReference = Register(registry, (IntPtr)2);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", firstReference)]);
        store.Invoke(cache, [CreateSnapshot((IntPtr)2, "e2", secondReference)]);
        Func<IntPtr, bool> isWindow = hwnd => hwnd == (IntPtr)2;

        removeInvalid.Invoke(cache, [isWindow]);

        Assert.Null(forElement.Invoke(cache, [CreateTarget((IntPtr)1, firstReference), "e1"]));
        Assert.NotNull(forElement.Invoke(cache, [CreateTarget((IntPtr)2, secondReference), "e2"]));
    }

    [Fact]
    public void SnapshotCache_RejectsRetiredOriginalOwner()
    {
        var registry = new TargetRegistry("cache-retired-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var original = Register(registry, (IntPtr)1);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", original)]);
        registry.Invalidate(original);
        var replacement = Register(registry, (IntPtr)2);

        var selected = forElement.Invoke(cache, [CreateTarget((IntPtr)2, replacement), "e1"]);

        Assert.Null(selected);
    }

    [Fact]
    public void SnapshotCache_ExplicitWindowSelectionNeverAliases()
    {
        var registry = new TargetRegistry("cache-explicit-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var original = Register(registry, (IntPtr)1);
        var replacement = Register(registry, (IntPtr)2);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", original)]);
        var target = CreateTarget((IntPtr)2, replacement);
        target.SelectionKind = AppSelectionKind.ExplicitWindowId;

        var selected = forElement.Invoke(cache, [target, "e1"]);

        Assert.Null(selected);
    }

    [Fact]
    public void SnapshotCache_LegacyOwnerChainAliasContractRequiresCurrentRefsAndRelation()
    {
        var registry = new TargetRegistry("cache-owner-chain-contract-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        // This injected relation is a cache contract seam, not a simulation of a Win32 owner chain.
        var cache = Activator.CreateInstance(
            cacheType,
            registry,
            (Func<IntPtr, bool>)(_ => true),
            (Func<IntPtr, IntPtr, bool>)((targetHwnd, snapshotHwnd)
                => targetHwnd == (IntPtr)2 && snapshotHwnd == (IntPtr)1));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);

        var original = Register(registry, (IntPtr)1);
        var replacement = Register(registry, (IntPtr)2);
        Assert.True(registry.IsCurrent(original, original.Identity));
        Assert.True(registry.IsCurrent(replacement, replacement.Identity));
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", original)]);

        var legacyReplacement = CreateTarget((IntPtr)2, replacement);
        var aliased = Assert.IsType<Snapshot>(forElement.Invoke(cache, [legacyReplacement, "e1"]));
        Assert.Equal((IntPtr)1, aliased.Hwnd);

        legacyReplacement.SelectionKind = AppSelectionKind.ExplicitWindowId;
        Assert.Null(forElement.Invoke(cache, [legacyReplacement, "e1"]));
        legacyReplacement.SelectionKind = AppSelectionKind.ExplicitTargetId;
        Assert.Null(forElement.Invoke(cache, [legacyReplacement, "e1"]));

        var unrelatedSibling = Register(registry, (IntPtr)3);
        var siblingTarget = CreateTarget((IntPtr)3, unrelatedSibling);
        Assert.Null(forElement.Invoke(cache, [siblingTarget, "e1"]));
    }

    [Fact]
    public void SnapshotCache_FindsStaleIdFromSupersededSnapshotOfSameLiveWindow()
    {
        // Rebuild scenario: a window's content is replaced (e.g. duplicate controls
        // recreated), producing a new snapshot for the SAME hwnd/lifetime. An id from the
        // superseded snapshot must still resolve to that historical snapshot instead of
        // reporting "no cached snapshot" -- live re-resolution is what decides whether the
        // element is actually gone, ambiguous, or found, not this cache.
        var registry = new TargetRegistry("cache-history-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var reference = Register(registry, (IntPtr)1);
        var before = CreateSnapshot((IntPtr)1, "e-stale", reference);
        var after = CreateSnapshot((IntPtr)1, "e-fresh", reference);

        store.Invoke(cache, [before]);
        store.Invoke(cache, [after]);

        var target = CreateTarget((IntPtr)1, reference);
        var staleLookup = Assert.IsType<Snapshot>(forElement.Invoke(cache, [target, "e-stale"]));
        Assert.Same(before, staleLookup);
        var freshLookup = Assert.IsType<Snapshot>(forElement.Invoke(cache, [target, "e-fresh"]));
        Assert.Same(after, freshLookup);
        Assert.Null(forElement.Invoke(cache, [target, "e-never-existed"]));
    }

    [Fact]
    public void SnapshotCache_DoesNotFindStaleIdAcrossLifetimeReplacement()
    {
        // The window at hwnd 1 was destroyed and a new one created at the same hwnd: the
        // old lifetime's history must never leak into the new lifetime, even same-hwnd.
        var registry = new TargetRegistry("cache-history-epoch-test");
        var cacheType = typeof(UiaService).Assembly.GetType("ShadowUse.Automation.SnapshotCache");
        Assert.NotNull(cacheType);
        var cache = Activator.CreateInstance(cacheType, registry, (Func<IntPtr, bool>)(_ => true));
        Assert.NotNull(cache);
        var store = cacheType.GetMethod("Store", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var forElement = cacheType.GetMethod("GetForElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(store);
        Assert.NotNull(forElement);
        var original = Register(registry, (IntPtr)1);
        store.Invoke(cache, [CreateSnapshot((IntPtr)1, "e1", original)]);
        registry.Invalidate(original);
        var replacement = Register(registry, (IntPtr)1);

        var selected = forElement.Invoke(cache, [CreateTarget((IntPtr)1, replacement), "e1"]);

        Assert.Null(selected);
    }

    private static NativeTargetReference Register(TargetRegistry registry, IntPtr hwnd)
        => registry.Register(new NativeTargetIdentity(100, 1234, hwnd));

    private static AppTarget CreateTarget(IntPtr hwnd, NativeTargetReference reference)
        => new()
        {
            Pid = 100,
            ProcessName = "fixture",
            Hwnd = hwnd,
            Title = $"fixture-{hwnd}",
            TargetReference = reference,
        };

    private static Snapshot CreateSnapshot(
        IntPtr hwnd,
        string elementId,
        NativeTargetReference reference)
        => new()
        {
            Revision = 1,
            App = "fixture",
            Pid = 100,
            Hwnd = hwnd,
            Title = $"fixture-{hwnd}",
            Bounds = new NativeMethods.RECT { Right = 400, Bottom = 300 },
            TargetReference = reference,
            Elements = [new ElementInfo { Id = elementId }],
        };
}
