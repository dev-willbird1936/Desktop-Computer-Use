// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ShadowUse.Capture;
using ShadowUse.Native;

namespace ShadowUse.Vision;

/// <summary>One HUD bar to measure. X/Y/Width/Height are in screenshot ("image") coordinates.</summary>
public sealed record BarSpec(string Name, int X, int Y, int Width, int Height, string? Color = null, int Tolerance = 60, string Direction = "horizontal");

public sealed record BarReading(string Name, double? Percent, string? Color, string? Note, double Confidence = 0, string CalibrationStatus = "uncalibrated");

public sealed record CapturedGameFrame(Bitmap Bitmap, NativeMethods.RECT Rect, string FrameId, DateTimeOffset Timestamp, string? TargetLifetime, int Width, int Height, double ImageScale);
public sealed record CameraMotion(bool Moved, bool Unclassified, bool Available);

public sealed record OcrLine(string Text, int X, int Y, int Width, int Height, int ScreenX, int ScreenY);

/// <summary>
/// Game-mode perception with no UI Automation: window capture that works for GPU-rendered games, JPEG
/// screenshots in a fixed "image" coordinate space, cheap frame signatures for change detection, region OCR and
/// HUD bar measurement. Image coordinates are window pixels times <see cref="ImageScale"/>; the scale is set
/// by the last full screenshot of that window and defaults to fit <see cref="DefaultMaxWidth"/>.
/// </summary>
public static class GameVision
{
    public const int DefaultMaxWidth = 1280;
    private const int SigWidth = 64, SigHeight = 36;

    private sealed record FrameRecord(long Sequence, string Id, DateTimeOffset Timestamp, string? TargetLifetime, int Width, int Height, double Scale,
        byte[] Gray, int TrackingWidth, int TrackingHeight, byte[]? TrackingPixels);
    private const int FrameHistoryLimit = 512, TrackingFrameHistoryLimit = 16;

    private static readonly ConcurrentDictionary<(IntPtr Hwnd, string Lifetime), double> ImageScales = new();
    private static readonly object FrameLock = new();
    private static readonly Dictionary<string, FrameRecord> Frames = new(StringComparer.Ordinal);
    private static readonly Queue<string> FrameOrder = new();
    private static readonly Queue<string> TrackingFrameOrder = new();
    private static long _nextFrameId;

    public static ulong ComputeRegionHash(Bitmap frame, Rectangle region) => RegionHash.Compute(frame, region);

    public static bool RegionHashMatches(Bitmap frame, Rectangle region, string expectedHash, int maxHammingDistance = 8)
        => ulong.TryParse(expectedHash, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong expected)
            && System.Numerics.BitOperations.PopCount(RegionHash.Compute(frame, region) ^ expected) <= Math.Clamp(maxHammingDistance, 0, 64);

    public static string LayoutFingerprint(int frameWidth, int frameHeight, int imageWidth, int imageHeight,
        double imageScale, uint dpi)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"v1|frame={frameWidth}x{frameHeight}|image={imageWidth}x{imageHeight}|scale={imageScale:R}|dpi={dpi}");

    public static double ImageScale(IntPtr hwnd, int windowWidth, string? targetLifetime = null)
        => (targetLifetime != null && ImageScales.TryGetValue((hwnd, targetLifetime), out var s)) || ImageScales.TryGetValue((hwnd, ""), out s)
            ? s : Math.Min(1.0, DefaultMaxWidth / (double)Math.Max(1, windowWidth));

    public static void SetImageScale(IntPtr hwnd, double scale, string? targetLifetime = null)
        => ImageScales[(hwnd, targetLifetime ?? "")] = scale;

    public static void SetFrameScale(string frameId, double scale)
    {
        lock (FrameLock)
            if (Frames.TryGetValue(frameId, out var frame)) Frames[frameId] = frame with { Scale = scale };
    }

    public static bool TryGetTrackingFrame(string frameId, out GameTrackingFrame? frame)
    {
        lock (FrameLock)
        {
            if (Frames.TryGetValue(frameId, out var record) && record.TrackingPixels != null)
            {
                frame = new GameTrackingFrame(record.Id, record.Timestamp, record.TargetLifetime, record.Width, record.Height,
                    record.Scale, record.TrackingWidth, record.TrackingHeight, (byte[])record.TrackingPixels.Clone());
                return true;
            }
        }
        frame = null;
        return false;
    }

    public static bool TryGetFrame(string frameId, out (DateTimeOffset Timestamp, string? TargetLifetime, int Width, int Height, double Scale) metadata)
    {
        lock (FrameLock)
        {
            if (Frames.TryGetValue(frameId, out var frame))
            {
                metadata = (frame.Timestamp, frame.TargetLifetime, frame.Width, frame.Height, frame.Scale);
                return true;
            }
        }
        metadata = default;
        return false;
    }

    public static CameraMotion MotionSince(string sourceFrameId, string currentFrameId)
    {
        lock (FrameLock)
        {
            if (!Frames.TryGetValue(sourceFrameId, out var source) || !Frames.TryGetValue(currentFrameId, out var current)
                || source.Sequence >= current.Sequence || source.TargetLifetime != current.TargetLifetime
                || source.Width != current.Width || source.Height != current.Height)
                return new CameraMotion(false, false, false);
            var chain = Frames.Values.Where(f => f.Sequence >= source.Sequence && f.Sequence <= current.Sequence)
                .OrderBy(f => f.Sequence).ToArray();
            bool unclassified = false;
            for (int i = 1; i < chain.Length; i++)
            {
                var motion = EstimateMotion(chain[i - 1].Gray, chain[i].Gray);
                if (motion.Kind == "shift") return new CameraMotion(true, unclassified, true);
                if (motion.Kind == "changed") unclassified = true;
            }
            return new CameraMotion(false, unclassified, true);
        }
    }

    /// <summary>Window capture. A foreground window is read from the composited screen, which works for D3D/Vulkan/OpenGL games
    /// that PrintWindow returns black for; any other window uses PrintWindow. The caller disposes the bitmap.</summary>
    private static readonly object CaptureLock = new();
    private static readonly SemaphoreSlim OcrLock = new(1, 1);

    /// <summary>Serialised capture that retries once, so overlapping calls and a transient GDI failure do not surface as errors.</summary>
    public static CapturedGameFrame? Capture(IntPtr hwnd, string? targetLifetime = null, bool retainTrackingPixels = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            (Bitmap Bitmap, NativeMethods.RECT Rect)? result = null;
            lock (CaptureLock)
            {
                try { result = CaptureOnce(hwnd); }
                catch (Exception) when (attempt == 0) { }
            }
            if (result is { } captured)
            {
                if (targetLifetime != null)
                    foreach (var key in ImageScales.Keys.Where(k => k.Hwnd == hwnd && k.Lifetime != targetLifetime)) ImageScales.TryRemove(key, out _);
                double scale = ImageScale(hwnd, captured.Rect.Width, targetLifetime);
                var timestamp = DateTimeOffset.UtcNow;
                long sequence = Interlocked.Increment(ref _nextFrameId);
                string id = $"frame-{sequence:x}";
                var tracking = retainTrackingPixels ? TrackingGray(captured.Bitmap) : (Width: 0, Height: 0, Pixels: (byte[]?)null);
                var record = new FrameRecord(sequence, id, timestamp, targetLifetime, captured.Bitmap.Width, captured.Bitmap.Height, scale,
                    Gray(captured.Bitmap), tracking.Width, tracking.Height, tracking.Pixels);
                lock (FrameLock)
                {
                    Frames[id] = record;
                    FrameOrder.Enqueue(id);
                    if (retainTrackingPixels) TrackingFrameOrder.Enqueue(id);
                    while (FrameOrder.Count > FrameHistoryLimit) Frames.Remove(FrameOrder.Dequeue());
                    while (TrackingFrameOrder.Count > TrackingFrameHistoryLimit)
                    {
                        var oldId = TrackingFrameOrder.Dequeue();
                        if (Frames.TryGetValue(oldId, out var old)) Frames[oldId] = old with { TrackingPixels = null };
                    }
                }
                return new CapturedGameFrame(captured.Bitmap, captured.Rect, id, timestamp, targetLifetime, captured.Bitmap.Width, captured.Bitmap.Height, scale);
            }
            if (attempt >= 1) return null;
            Thread.Sleep(60);
        }
    }

    private static (int Width, int Height, byte[] Pixels) TrackingGray(Bitmap source)
    {
        double scale = Math.Min(1, 640.0 / Math.Max(source.Width, source.Height));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        }
        var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var gray = new byte[width * height];
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                    gray[y * width + x] = (byte)((row[x * 3 + 2] * 299 + row[x * 3 + 1] * 587 + row[x * 3] * 114) / 1000);
            }
            return (width, height, gray);
        }
        finally { small.UnlockBits(data); }
    }

    private static (Bitmap Bitmap, NativeMethods.RECT Rect)? CaptureOnce(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0) return null;
        if (IsForeground(hwnd))
        {
            try
            {
                var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                return (bmp, rect);
            }
            catch { /* fall through to PrintWindow */ }
        }
        return ScreenshotService.CaptureWindowForVision(hwnd);
    }

    private static bool IsForeground(IntPtr hwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == hwnd) return true;
        NativeMethods.GetWindowThreadProcessId(fg, out var fgPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return fgPid != 0 && fgPid == pid;
    }

    /// <summary>Maps image-space coordinates to screen coordinates.</summary>
    public static (int X, int Y) ToScreen(NativeMethods.RECT window, double imageScale, double x, double y)
        => (window.Left + (int)Math.Round(x / imageScale), window.Top + (int)Math.Round(y / imageScale));

    /// <summary>Image-space region to a window-pixel rectangle clamped to the bitmap, or null when empty.</summary>
    public static Rectangle? PhysicalRegion(Bitmap bmp, double imageScale, int x, int y, int width, int height)
        => PhysicalRegion(bmp.Width, bmp.Height, imageScale, x, y, width, height);

    public static Rectangle? PhysicalRegion(int windowWidth, int windowHeight, double imageScale, int x, int y, int width, int height)
    {
        var r = Rectangle.Intersect(
            new Rectangle((int)Math.Round(x / imageScale), (int)Math.Round(y / imageScale),
                Math.Max(1, (int)Math.Round(width / imageScale)), Math.Max(1, (int)Math.Round(height / imageScale))),
            new Rectangle(0, 0, windowWidth, windowHeight));
        return r.Width > 0 && r.Height > 0 ? r : null;
    }

    /// <summary>JPEG of the whole bitmap or a crop of it, resized to outWidth x outHeight.</summary>
    public static byte[] EncodeJpeg(Bitmap src, Rectangle crop, int outWidth, int outHeight, long quality, IReadOnlyList<Rectangle>? exclusions = null)
    {
        using var dst = new Bitmap(Math.Max(1, outWidth), Math.Max(1, outHeight), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height), crop, GraphicsUnit.Pixel);
            if (exclusions is { Count: > 0 })
            {
                using var brush = new SolidBrush(Color.Black);
                foreach (var mask in exclusions)
                {
                    var r = Rectangle.Intersect(mask, crop);
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    var mapped = new Rectangle(
                        (r.X - crop.X) * dst.Width / crop.Width,
                        (r.Y - crop.Y) * dst.Height / crop.Height,
                        Math.Max(1, (int)Math.Ceiling(r.Width * dst.Width / (double)crop.Width)),
                        Math.Max(1, (int)Math.Ceiling(r.Height * dst.Height / (double)crop.Height)));
                    mapped.Inflate(1, 1);
                    mapped = Rectangle.Intersect(mapped, new Rectangle(0, 0, dst.Width, dst.Height));
                    if (mapped.Width > 0 && mapped.Height > 0) g.FillRectangle(brush, mapped);
                }
            }
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 10, 95));
        using var ms = new MemoryStream();
        dst.Save(ms, codec, parameters);
        return ms.ToArray();
    }

    /// <summary>Downscaled luma grid used to compare frames cheaply. Exclusion regions are physical window pixels.</summary>
    public static byte[] Signature(Bitmap bmp, Rectangle? region = null, IReadOnlyList<Rectangle>? exclusions = null)
    {
        var source = Rectangle.Intersect(region ?? new Rectangle(0, 0, bmp.Width, bmp.Height), new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (source.Width <= 0 || source.Height <= 0) return [];
        using var small = new Bitmap(SigWidth, SigHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, new Rectangle(0, 0, SigWidth, SigHeight), source, GraphicsUnit.Pixel);
            MaskObservations(g, exclusions, source, SigWidth, SigHeight);
        }
        var data = small.LockBits(new Rectangle(0, 0, SigWidth, SigHeight), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var luma = new byte[SigWidth * SigHeight];
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < SigHeight; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < SigWidth; x++)
                    luma[y * SigWidth + x] = (byte)((row[x * 3 + 2] * 299 + row[x * 3 + 1] * 587 + row[x * 3] * 114) / 1000);
            }
            return luma;
        }
        finally { small.UnlockBits(data); }
    }

    /// <summary>Fraction (0-1) of signature cells whose luma moved by at least <paramref name="threshold"/>.</summary>
    public static double ChangedFraction(byte[] a, byte[] b, int threshold = 12)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) >= threshold) changed++;
        return changed / (double)a.Length;
    }

    public const int MotionWidth = 192, MotionHeight = 108;

    private static void MaskObservations(Graphics g, IReadOnlyList<Rectangle>? exclusions, Rectangle source, int targetWidth, int targetHeight)
    {
        if (exclusions == null || exclusions.Count == 0) return;
        using var brush = new SolidBrush(Color.Black);
        foreach (var mask in exclusions)
        {
            var r = Rectangle.Intersect(mask, source);
            if (r.Width <= 0 || r.Height <= 0) continue;
            var mapped = new Rectangle(
                (r.X - source.X) * targetWidth / source.Width,
                (r.Y - source.Y) * targetHeight / source.Height,
                Math.Max(1, (int)Math.Ceiling(r.Width * targetWidth / (double)source.Width)),
                Math.Max(1, (int)Math.Ceiling(r.Height * targetHeight / (double)source.Height)));
            mapped.Inflate(1, 1);
            mapped = Rectangle.Intersect(mapped, new Rectangle(0, 0, targetWidth, targetHeight));
            if (mapped.Width > 0 && mapped.Height > 0) g.FillRectangle(brush, mapped);
        }
    }

    /// <summary>Grayscale MotionWidth x MotionHeight copy of the frame, used to measure how the picture moved.</summary>
    public static byte[] Gray(Bitmap bmp, Rectangle? region = null, IReadOnlyList<Rectangle>? exclusions = null)
    {
        var source = Rectangle.Intersect(region ?? new Rectangle(0, 0, bmp.Width, bmp.Height), new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (source.Width <= 0 || source.Height <= 0) return [];
        using var small = new Bitmap(MotionWidth, MotionHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, new Rectangle(0, 0, MotionWidth, MotionHeight), source, GraphicsUnit.Pixel);
            MaskObservations(g, exclusions, source, MotionWidth, MotionHeight);
        }
        var data = small.LockBits(new Rectangle(0, 0, MotionWidth, MotionHeight), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var gray = new byte[MotionWidth * MotionHeight];
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < MotionHeight; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < MotionWidth; x++)
                    gray[y * MotionWidth + x] = (byte)((row[x * 3 + 2] * 299 + row[x * 3 + 1] * 587 + row[x * 3] * 114) / 1000);
            }
            return gray;
        }
        finally { small.UnlockBits(data); }
    }

    /// <summary>How far the world scrolled between two Gray frames, in grid cells (dx,dy: content moved by that much from a to b).
    /// Block matching over the middle of the frame, which skips the HUD at the edges. kind: none (same), shift (a clean
    /// match, confidence 0-1), or changed (the picture differs and no shift explains it: a turn, a cut, or a new screen).</summary>
    public static (string Kind, int Dx, int Dy, double Confidence) EstimateMotion(byte[] a, byte[] b, int radius = 18)
    {
        int w = MotionWidth, h = MotionHeight;
        int x0 = w / 5 + radius, x1 = w - w / 5 - radius, y0 = h / 5 + radius / 2, y1 = h - h / 5 - radius / 2;
        if (a.Length != w * h || b.Length != a.Length || x1 <= x0 || y1 <= y0) return ("none", 0, 0, 0);
        double Cost(int dx, int dy)
        {
            long sum = 0; int n = 0;
            for (int y = y0; y < y1; y += 2)
                for (int x = x0; x < x1; x += 2)
                {
                    sum += Math.Abs(a[y * w + x] - b[(y + dy) * w + x + dx]);
                    n++;
                }
            return n == 0 ? 0 : sum / (double)n;
        }
        double still = Cost(0, 0);
        if (still < 1.5) return ("none", 0, 0, 1);
        double best = still; int bx = 0, by = 0;
        int ry = radius / 2;
        for (int dy = -ry; dy <= ry; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                double c = Cost(dx, dy);
                if (c < best) { best = c; bx = dx; by = dy; }
            }
        double gain = (still - best) / still;
        if ((bx == 0 && by == 0) || gain < 0.35 || best > 12) return ("changed", 0, 0, Math.Round(1 - Math.Min(1, best / Math.Max(1, still)), 2));
        // a is sampled at (x, y); the best match in b is at (x+dx, y+dy), so the content moved by (dx, dy) toward b.
        return ("shift", bx, by, Math.Round(Math.Min(1, gain), 2));
    }

    /// <summary>frame_changed (at least 1.5% of cells moved), minor_change (0.2%-1.5%), no_change.</summary>
    public static string ClassifyChange(double fraction)
        => fraction >= 0.015 ? "frame_changed" : fraction >= 0.002 ? "minor_change" : "no_change";

    public static bool IsVisualChange(string effect) => effect is "frame_changed" or "minor_change";

    /// <summary>OCR of a window-pixel region, upscaled for small HUD text. Boxes come back in image space.</summary>
    public static async Task<(IReadOnlyList<OcrLine> Lines, string? Unsupported)> ReadTextAsync(
        Bitmap window, NativeMethods.RECT rect, Rectangle region, double imageScale, int upscale)
    {
        int factor = Math.Clamp(upscale, 1, 4);
        while (factor > 1 && (region.Width * factor > 4096 || region.Height * factor > 4096)) factor--;
        using var crop = new Bitmap(region.Width * factor, region.Height * factor, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.Clear(Color.White);
            g.InterpolationMode = factor > 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(window, new Rectangle(0, 0, crop.Width, crop.Height), region, GraphicsUnit.Pixel);
        }
        IReadOnlyList<(string Text, Rectangle Box)> lines;
        string? unsupported;
        await OcrLock.WaitAsync().ConfigureAwait(false);
        try
        {
            try { (lines, unsupported) = await VisualGroundingService.RecognizeBitmapAsync(crop).ConfigureAwait(false); }
            catch (Exception)
            {
                VisualGroundingService.ResetOcrEngine();
                (lines, unsupported) = await VisualGroundingService.RecognizeBitmapAsync(crop).ConfigureAwait(false);
            }
        }
        finally { OcrLock.Release(); }
        var mapped = lines.Select(l =>
        {
            double px = region.X + l.Box.X / (double)factor, py = region.Y + l.Box.Y / (double)factor;
            double pw = l.Box.Width / (double)factor, ph = l.Box.Height / (double)factor;
            return new OcrLine(l.Text,
                (int)Math.Round(px * imageScale), (int)Math.Round(py * imageScale),
                (int)Math.Round(pw * imageScale), (int)Math.Round(ph * imageScale),
                rect.Left + (int)Math.Round(px + pw / 2), rect.Top + (int)Math.Round(py + ph / 2));
        }).ToList();
        return (mapped, unsupported);
    }

    /// <summary>Fill level of a bar in a window-pixel region. Unmatched pixels are not assumed to mean 0%.</summary>
    public static BarReading MeasureBar(Bitmap window, Rectangle region, BarSpec spec)
    {
        bool calibrated = !string.IsNullOrWhiteSpace(spec.Color);
        Color? target = null;
        if (calibrated)
        {
            if (spec.Color is not { Length: 7 } color || color[0] != '#'
                || !int.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return new BarReading(spec.Name, null, null, $"color '{spec.Color}' is not a valid #RRGGBB value", 0, "invalid_color");
            target = Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        }
        if (spec.Tolerance is < 0 or > 441)
            return new BarReading(spec.Name, null, spec.Color, "tolerance must be between 0 and 441", 0, "invalid_tolerance");
        bool vertical = string.Equals(spec.Direction, "vertical", StringComparison.OrdinalIgnoreCase);
        if (!vertical && !string.Equals(spec.Direction, "horizontal", StringComparison.OrdinalIgnoreCase))
            return new BarReading(spec.Name, null, spec.Color, "direction must be horizontal or vertical", 0, "invalid_direction");
        using var crop = window.Clone(region, PixelFormat.Format32bppArgb);
        var data = crop.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[Math.Abs(data.Stride) * crop.Height];
        try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { crop.UnlockBits(data); }

        int w = crop.Width, h = crop.Height, stride = Math.Abs(data.Stride);
        int length = vertical ? h : w, across = vertical ? w : h;
        long sumR = 0, sumG = 0, sumB = 0, onCount = 0;
        bool On(int x, int y, bool collect)
        {
            int i = y * stride + x * 4;
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            bool on = target is { } t
                ? (r - t.R) * (r - t.R) + (g - t.G) * (g - t.G) + (b - t.B) * (b - t.B) <= spec.Tolerance * spec.Tolerance
                : Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 60 && Math.Max(r, Math.Max(g, b)) >= 70;
            if (on && collect) { sumR += r; sumG += g; sumB += b; onCount++; }
            return on;
        }

        var filled = new bool[length];
        for (int k = 0; k < length; k++)
        {
            int on = 0;
            for (int j = 0; j < across; j++)
                if (vertical ? On(j, h - 1 - k, false) : On(k, j, false)) on++;
            filled[k] = on >= Math.Max(1, (int)Math.Ceiling(across * 0.4));
        }

        int maxGap = Math.Max(2, length / 25);
        int last = -1, gap = 0;
        for (int k = 0; k < length; k++)
        {
            if (filled[k]) { last = k; gap = 0; }
            else if (++gap > maxGap && last >= 0) break;
        }
        if (last < 0)
            return new BarReading(spec.Name, null, spec.Color, calibrated
                ? "no pixels matched the calibrated color; the bar may be empty, miscalibrated, or hidden"
                : "no vivid pixels found; pass a color for grey or white bars. The bar may be empty or hidden",
                0.1, calibrated ? "calibrated_no_match" : "uncalibrated_no_match");

        // A real bar is one solid run. Scattered matches inside the run, or matches beyond it, mean the bar is covered or the colour
        // also matches something else, so the level stays unknown instead of reporting a confident percentage.
        int inRun = 0, stray = 0;
        for (int k = 0; k < length; k++)
            if (filled[k]) { if (k <= last) inRun++; else stray++; }
        if (inRun < (last + 1) * 0.85 || stray > Math.Max(2, length / 10))
            return new BarReading(spec.Name, null, spec.Color,
                "matched pixels are scattered, so the bar may be covered or the colour also matches other HUD elements; recalibrate on a clear, full bar",
                0.2, calibrated ? "calibrated_fragmented" : "uncalibrated_fragmented");

        if (!calibrated)
            for (int k = 0; k <= last; k++)
                for (int j = 0; j < across; j++)
                    _ = vertical ? On(j, h - 1 - k, true) : On(k, j, true);
        string? detected = !calibrated && onCount > 0
            ? $"#{sumR / onCount:X2}{sumG / onCount:X2}{sumB / onCount:X2}"
            : spec.Color;
        return new BarReading(spec.Name, Math.Round((last + 1) * 100.0 / length, 1), detected, null,
            calibrated ? 0.95 : 0.65, calibrated ? "calibrated_color" : "auto_detected_unverified");
    }

    public static bool IsReliableBar(BarReading reading)
        => reading.Percent != null && reading.Confidence >= 0.8 && reading.CalibrationStatus == "calibrated_color";
}
