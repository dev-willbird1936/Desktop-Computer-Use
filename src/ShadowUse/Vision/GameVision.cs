using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ShadowUse.Automation;
using ShadowUse.Capture;
using ShadowUse.Native;

namespace ShadowUse.Vision;

public sealed record BarSpec(string Name, int X, int Y, int Width, int Height, string? Color = null, int Tolerance = 60, string Direction = "horizontal");
public sealed record BarReading(string Name, double? Percent, string? Color, string? Note, double Confidence = 0, string CalibrationStatus = "uncalibrated");
public sealed record OcrLine(string Text, int X, int Y, int Width, int Height);

public static class GameVision
{
    public const int DefaultMaxWidth = 1280;
    private const int SigWidth = 64, SigHeight = 36;

    public static (Bitmap Bitmap, NativeMethods.RECT Rect, string Origin)? Capture(IntPtr hwnd)
    {
        if (ScreenshotService.CaptureWindowForVision(hwnd) is { } own)
            return (own.Bitmap, own.Rect, "print_window");
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)
            || InteractiveInput.RootWindow(NativeMethods.GetForegroundWindow()) != hwnd
            || !NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            return (bitmap, rect, "foreground_screen");
        }
        catch { bitmap?.Dispose(); return null; }
    }

    public static string ClassifyChange(double fraction)
        => fraction >= 0.015 ? "frame_changed" : fraction >= 0.002 ? "minor_change" : "no_change";

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


    private static void MaskObservations(Graphics graphics, IReadOnlyList<Rectangle>? exclusions, Rectangle source, int width, int height)
    {
        if (exclusions == null) return;
        foreach (var mask in exclusions)
        {
            var region = Rectangle.Intersect(mask, source);
            if (region.Width <= 0 || region.Height <= 0) continue;
            graphics.FillRectangle(Brushes.Black, (region.X - source.X) * width / source.Width,
                (region.Y - source.Y) * height / source.Height,
                Math.Max(1, region.Width * width / source.Width), Math.Max(1, region.Height * height / source.Height));
        }
    }

    public static async Task<(IReadOnlyList<OcrLine> Lines, string? Unsupported)> ReadTextAsync(Bitmap bitmap, Rectangle region, double scale, CancellationToken ct)
    {
        using var crop = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        using var memory = new MemoryStream();
        crop.Save(memory, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var output = stream.GetOutputStreamAt(0))
        {
            using var writer = new DataWriter(output);
            writer.WriteBytes(memory.ToArray());
            await writer.StoreAsync().AsTask(ct).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(ct).ConfigureAwait(false);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        using var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);
        // Each request owns its engine; unrelated windows need no global OCR lock.
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return ([], "No Windows OCR language is installed.");
            if (software.PixelWidth > OcrEngine.MaxImageDimension || software.PixelHeight > OcrEngine.MaxImageDimension)
                return ([], "OCR region is too large; provide a smaller crop.");
            var result = await engine.RecognizeAsync(software).AsTask(ct).ConfigureAwait(false);
            return (result.Lines.Where(line => line.Words.Count > 0).Select(line =>
            {
                double x = line.Words.Min(w => w.BoundingRect.X), y = line.Words.Min(w => w.BoundingRect.Y);
                double right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new OcrLine(line.Text, (int)Math.Round((region.X + x) * scale), (int)Math.Round((region.Y + y) * scale),
                    (int)Math.Round((right - x) * scale), (int)Math.Round((bottom - y) * scale));
            }).ToArray(), null);
        }
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
