namespace ShadowUse.Vision;

public sealed record GameFrameMetadata(string Id, string WindowId, int Width, int Height, double Scale);

/// <summary>Singleton observation metadata shared by request-scoped game tools.</summary>
public sealed class GameFrameStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, GameFrameMetadata> frames = new();
    private readonly Queue<string> order = new();

    public GameFrameMetadata Record(string windowId, int width, int height, double scale)
    {
        var frame = new GameFrameMetadata(Guid.NewGuid().ToString("N"), windowId, width, height, scale);
        lock (gate)
        {
            frames[frame.Id] = frame;
            order.Enqueue(frame.Id);
            while (order.Count > 128) frames.Remove(order.Dequeue());
        }
        return frame;
    }

    public GameFrameMetadata Resolve(string id, string windowId, int width, int height)
    {
        lock (gate)
        {
            if (!frames.TryGetValue(id, out var frame) || frame.WindowId != windowId)
                throw new ArgumentException("source_frame_id must come from this server and the selected window; take a fresh game_screenshot.");
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException("Window resized since this frame; take a fresh game_screenshot.");
            return frame;
        }
    }
}
