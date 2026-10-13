// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using ShadowUse.Native;

namespace ShadowUse.Vision;

public sealed record GameScreenshotFrameBinding(
    string ObservationId,
    DateTimeOffset CapturedAtUtc,
    string? TargetLifetime,
    int FrameWidth,
    int FrameHeight,
    int ImageWidth,
    int ImageHeight,
    double ImageScale,
    uint DpiAtCapture,
    NativeMethods.RECT WindowRect,
    bool IsFullFrame,
    IReadOnlyList<Rectangle>? MaskedImageBoxes = null);

internal static class GameScreenshotFrameCache
{
    private const int Capacity = 32;
    private const int MaxCachedJpegBytes = 4 * 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, GameScreenshotFrameBinding> Frames = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte[]> Jpegs = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    public static void Store(GameScreenshotFrameBinding frame, byte[]? jpeg = null)
    {
        lock (Sync)
        {
            Frames[frame.ObservationId] = frame;
            if (jpeg is { Length: > 0 and <= MaxCachedJpegBytes }) Jpegs[frame.ObservationId] = (byte[])jpeg.Clone();
            else Jpegs.Remove(frame.ObservationId);
            Order.Enqueue(frame.ObservationId);
            while (Order.Count > Capacity)
            {
                string oldest = Order.Dequeue();
                Frames.Remove(oldest);
                Jpegs.Remove(oldest);
            }
        }
    }

    public static bool TryGet(string observationId, out GameScreenshotFrameBinding? frame)
    {
        lock (Sync) return Frames.TryGetValue(observationId, out frame);
    }

    public static bool TryGetJpeg(string observationId, out byte[]? jpeg)
    {
        lock (Sync)
        {
            if (Jpegs.TryGetValue(observationId, out var stored))
            {
                jpeg = (byte[])stored.Clone();
                return true;
            }
        }
        jpeg = null;
        return false;
    }
}
