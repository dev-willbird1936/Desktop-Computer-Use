using ShadowUse.Vision;

namespace ShadowUse.Tests;

public sealed class GameFrameStoreTests
{
    [Fact]
    public void LaterScreenshotDoesNotReplaceEarlierImageScale()
    {
        var store = new GameFrameStore();
        var first = store.Record("window-a", 1000, 600, 0.5);
        store.Record("window-a", 1000, 600, 0.2);
        Assert.Equal(0.5, store.Resolve(first.Id, "window-a", 1000, 600).Scale);
    }

    [Fact]
    public void FrameFromAnotherWindowOrSizeCannotBeUsed()
    {
        var store = new GameFrameStore();
        var frame = store.Record("window-a", 1000, 600, 0.5);
        Assert.Throws<ArgumentException>(() => store.Resolve(frame.Id, "window-b", 1000, 600));
        Assert.Throws<ArgumentException>(() => store.Resolve(frame.Id, "window-a", 500, 600));
    }
}
