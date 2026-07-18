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
/// backing surface, so it works even when the window is occluded by other windows —
/// then GDI CopyFromScreen (visible-region BitBlt) as fallback.
/// Optionally overlays Set-of-Marks labels for snapshot elements.
/// </summary>
internal static class ScreenshotService
{
    /// <summary>Capture a window as PNG bytes. Returns null if all methods fail.</summary>
    public static byte[]? CaptureWindow(IntPtr hwnd, Snapshot? annotateWith = null, int maxWidth = 1280)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;

        Bitmap? bmp = TryPrintWindow(hwnd, rect) ?? TryCopyFromScreen(rect);
        if (bmp == null) return null;

        try
        {
            if (annotateWith != null && annotateWith.Elements.Count > 0)
                Annotate(bmp, annotateWith, rect);

            if (bmp.Width > maxWidth)
                bmp = Scale(bmp, maxWidth);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally { bmp.Dispose(); }
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

    private static Bitmap? TryPrintWindow(IntPtr hwnd, NativeMethods.RECT rect)
    {
        try
        {
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            bool ok;
            try { ok = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT); }
            finally { g.ReleaseHdc(hdc); }
            if (!ok || IsBlank(bmp)) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch { return null; }
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

    private static bool IsBlank(Bitmap bmp)
    {
        // cheap sample: if every probed pixel is identical, assume capture failed (black canvas)
        Color first = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += Math.Max(1, bmp.Height / 8))
            for (int x = 0; x < bmp.Width; x += Math.Max(1, bmp.Width / 8))
                if (bmp.GetPixel(x, y) != first) return false;
        return true;
    }

    private static void Annotate(Bitmap bmp, Snapshot snap, NativeMethods.RECT windowRect)
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
        foreach (var el in snap.Elements)
        {
            var color = palette[i++ % palette.Length];
            int x = el.ScreenX - windowRect.Left, y = el.ScreenY - windowRect.Top;
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
