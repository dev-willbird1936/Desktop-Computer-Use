// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ShadowUse.Automation;
using ShadowUse.Native;

namespace ShadowUse.Capture;

/// <summary>
/// Window capture: PrintWindow(PW_RENDERFULLCONTENT) first — captures the window's own
/// backing surface, so it works even when the window is occluded by other windows.
/// Visible-screen fallback is opt-in because it can capture an unrelated occluding app.
/// Optionally overlays Set-of-Marks labels for snapshot elements.
/// </summary>
public sealed record CaptureEnvelope(string CaptureId, DateTimeOffset CapturedAt, string Origin, string? FailureReason, byte[]? Png, int? Width, int? Height, double? ScaleFromWindow)
{
    public static CaptureEnvelope NotRequested() =>
        new("cap-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow, "not_requested", null, null, null, null, null);

    public static CaptureEnvelope CachedPage() =>
        new("cap-" + Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow, "cached_page", null, null, null, null, null);
}

internal static class ScreenshotService
{
    private static int _printWindowWorkerBusy;

    /// <summary>Capture a window with full provenance (F02-R01/R02): a capture id, timestamp,
    /// origin (not_requested/print_window/screen_fallback/none), dimensions, a scale factor back
    /// to window-relative coordinates, and an explicit failure reason. A missing image is always
    /// an explicit result -- never silently substituted with a different window's or the whole
    /// screen's image unless allowScreenFallback opted in.</summary>
    public static CaptureEnvelope CaptureWindow(
        IntPtr hwnd,
        IReadOnlyList<ElementInfo>? annotateWith = null,
        int maxWidth = 1280,
        bool allowScreenFallback = false)
    {
        var captureId = "cap-" + Guid.NewGuid().ToString("N")[..12];
        var capturedAt = DateTimeOffset.UtcNow;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return new CaptureEnvelope(captureId, capturedAt, "none", "window_not_visible_or_zero_size", null, null, null, null);

        // F02-R02: minimised is a distinct, well-supported case worth its own reason -- PrintWindow
        // on a minimised window is well-known to be unreliable (garbage/blank content), so this is
        // checked up front rather than only discovered after a failed/blank capture attempt.
        if (NativeMethods.IsIconic(hwnd))
            return new CaptureEnvelope(captureId, capturedAt, "none", "window_minimized", null, null, null, null);

        var printWindowBmp = TryPrintWindow(hwnd, rect);
        string origin;
        Bitmap? bmp;
        if (printWindowBmp != null)
        {
            bmp = printWindowBmp;
            origin = "print_window";
        }
        else if (allowScreenFallback)
        {
            bmp = TryCopyFromScreen(rect);
            origin = "screen_fallback";
        }
        else
        {
            bmp = null;
            origin = "none";
        }

        if (bmp == null)
        {
            var reason = allowScreenFallback
                ? "print_window_and_screen_fallback_both_failed"
                : "print_window_failed_and_screen_fallback_disabled";
            return new CaptureEnvelope(captureId, capturedAt, "none", reason, null, null, null, null);
        }

        try
        {
            if (annotateWith != null && annotateWith.Count > 0)
                Annotate(bmp, annotateWith);

            double scale = 1.0;
            if (bmp.Width > maxWidth)
            {
                scale = (double)maxWidth / bmp.Width;
                bmp = Scale(bmp, maxWidth);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return new CaptureEnvelope(captureId, capturedAt, origin, null, ms.ToArray(), bmp.Width, bmp.Height, scale);
        }
        finally { bmp.Dispose(); }
    }

    /// <summary>Raw, unscaled, unannotated window capture for perception (OCR). Returns the
    /// bitmap and the exact GetWindowRect used at capture time, matching the same
    /// window-relative frame of reference as ElementInfo.X/Y. Caller must dispose the bitmap.</summary>
    public static (Bitmap Bitmap, NativeMethods.RECT WindowRect)? CaptureWindowForVision(
        IntPtr hwnd,
        bool allowScreenFallback = false)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;
        var bmp = SelectWindowCapture(
            TryPrintWindow(hwnd, rect),
            () => TryCopyFromScreen(rect),
            allowScreenFallback);
        return bmp == null ? null : (bmp, rect);
    }

    /// <summary>Capture full virtual screen (all monitors).</summary>
    public static byte[]? CaptureScreen()
    {
        try
        {
            int x = (int)SystemInformation.VirtualScreen.X, y = (int)SystemInformation.VirtualScreen.Y;
            int w = (int)SystemInformation.VirtualScreen.Width, h = (int)SystemInformation.VirtualScreen.Height;
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(x, y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>PrintWindow sends WM_PRINT/WM_PRINTCLIENT to hwnd and blocks until it's
    /// handled — a hung target's message queue can wedge this call indefinitely. Run it on
    /// a worker with a timeout so get_app_state can return a clean null result. Only one
    /// worker may remain blocked, and any bitmap returned after the timeout is disposed by
    /// a continuation.</summary>
    private static Bitmap? TryPrintWindow(IntPtr hwnd, NativeMethods.RECT rect)
    {
        try
        {
            return RunPrintWindowWorker(() =>
            {
                var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(bmp);
                var hdc = g.GetHdc();
                bool ok;
                try { ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
                if (!ok || IsBlank(bmp)) { bmp.Dispose(); return null; }
                return bmp;
            }, 2_000);
        }
        catch { return null; }
    }

    private static Bitmap? RunPrintWindowWorker(Func<Bitmap?> capture, int timeoutMs)
    {
        if (Interlocked.CompareExchange(ref _printWindowWorkerBusy, 1, 0) != 0)
            return null;

        Task<Bitmap?> task;
        try
        {
            task = Task.Run(() =>
            {
                try { return capture(); }
                finally { Interlocked.Exchange(ref _printWindowWorkerBusy, 0); }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _printWindowWorkerBusy, 0);
            throw;
        }

        if (task.Wait(TimeSpan.FromMilliseconds(timeoutMs)))
            return task.GetAwaiter().GetResult();

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion)
                    completed.Result?.Dispose();
                else
                    _ = completed.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return null;
    }

    private static Bitmap? TryCopyFromScreen(NativeMethods.RECT rect)
    {
        try
        {
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
            if (IsBlank(bmp)) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch { return null; }
    }

    private static Bitmap? SelectWindowCapture(
        Bitmap? printWindowCapture,
        Func<Bitmap?> screenCapture,
        bool allowScreenFallback)
        => printWindowCapture ?? (allowScreenFallback ? screenCapture() : null);

    private static bool IsBlank(Bitmap bmp)
    {
        // cheap sample: if every probed pixel is identical, assume capture failed (black canvas)
        Color first = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 8))
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 8))
                if (bmp.GetPixel(x, y) != first) return false;
        return true;
    }

    private static Point GetAnnotationPoint(ElementInfo element) => new(element.X, element.Y);

    private static void Annotate(Bitmap bmp, IReadOnlyList<ElementInfo> elements)
    {
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        var palette = new[]
        {
            Color.FromArgb(255, 30, 144, 255), Color.FromArgb(255, 255, 69, 0), Color.FromArgb(255, 50, 205, 50),
            Color.FromArgb(255, 186, 85, 211), Color.FromArgb(255, 255, 140, 0), Color.FromArgb(255, 0, 206, 209),
        };
        int i = 0;
        foreach (var el in elements)
        {
            var color = palette[i++ % palette.Length];
            var point = GetAnnotationPoint(el);
            int x = point.X, y = point.Y;
            using var pen = new Pen(color, 2);
            g.DrawRectangle(pen, x, y, el.Width, el.Height);
            var label = el.Id;
            var size = g.MeasureString(label, font);
            float lx = x, ly = Math.Max(0, y - size.Height - 2);
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, lx, ly, size.Width + 6, size.Height + 2);
            g.DrawString(label, font, Brushes.White, lx + 3, ly + 1);
        }
    }

    private static Bitmap Scale(Bitmap src, int maxWidth)
    {
        double scale = (double)maxWidth / src.Width;
        int w = maxWidth, h = Math.Max(1, (int)(src.Height * scale));
        var dst = new Bitmap(w, h, src.PixelFormat);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        src.Dispose();
        return dst;
    }
}
