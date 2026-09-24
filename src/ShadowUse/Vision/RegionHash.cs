// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace ShadowUse.Vision;

/// <summary>Cheap perceptual hash (average hash, 8x8) used to detect that a visual candidate's
/// captured region changed since the observation was taken — without keeping the original
/// bitmap in memory. Average hash (bit = pixel brighter than the region's own mean) was chosen
/// over a directional difference hash because a single monotonic brightness edge (e.g. one
/// plain-colored region replacing another) can otherwise hash identically to a uniform region.
/// Not a security hash: collisions are expected and tolerated by callers via a
/// Hamming-distance threshold.</summary>
internal static class RegionHash
{
    private const int GridSize = 8;

    /// <summary>Compute a 64-bit average hash of the region, clamped to the bitmap's own
    /// bounds. Returns 0 for a degenerate (empty) region.</summary>
    public static ulong Compute(Bitmap bmp, Rectangle region)
    {
        var clamped = Rectangle.Intersect(region, new Rectangle(0, 0, bmp.Width, bmp.Height));
        if (clamped.Width <= 0 || clamped.Height <= 0) return 0;

        using var cropped = new Bitmap(clamped.Width, clamped.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
            g.DrawImage(bmp, new Rectangle(0, 0, clamped.Width, clamped.Height), clamped, GraphicsUnit.Pixel);

        using var gray = new Bitmap(GridSize, GridSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(gray))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(cropped, 0, 0, GridSize, GridSize);
        }

        Span<int> luminance = stackalloc int[GridSize * GridSize];
        int sum = 0;
        for (int y = 0; y < GridSize; y++)
            for (int x = 0; x < GridSize; x++)
            {
                var p = gray.GetPixel(x, y);
                int l = (p.R * 299 + p.G * 587 + p.B * 114) / 1000;
                luminance[y * GridSize + x] = l;
                sum += l;
            }
        int mean = sum / (GridSize * GridSize);

        ulong hash = 0;
        for (int i = 0; i < GridSize * GridSize; i++)
            if (luminance[i] > mean) hash |= 1UL << i;
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
