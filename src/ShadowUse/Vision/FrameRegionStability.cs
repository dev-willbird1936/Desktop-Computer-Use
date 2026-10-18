// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShadowUse.Vision;

internal readonly record struct FrameRegionDifference(Rectangle Region, double ChangedPixelFraction, double MeanAbsoluteChannelDifference);
internal readonly record struct FrameRegionDiagnostic(Rectangle Region, string Role, string Status,
    double? ChangedPixelFraction, double? MeanAbsoluteChannelDifference, bool? WithinLimits);

internal static class GameFrameStabilityPolicy
{
    public static bool AllowsDispatch(CameraMotion motion, bool regionsProvided, bool regionsCompared, bool regionsStable)
        => motion.Available && !motion.Moved
            && (!regionsProvided || regionsCompared && regionsStable)
            && (!motion.Unclassified || regionsProvided && regionsCompared && regionsStable);
}

/// <summary>Compares caller-bounded image regions after normalizing both frames through the same JPEG pipeline.</summary>
internal static class FrameRegionStability
{
    public const double MaxChangedPixelFraction = 0.002;
    public const double MaxMeanAbsoluteChannelDifference = 2.0;
    public const int ChangedChannelThreshold = 12;

    /// <summary>Describes what was measured without treating skipped regions as stable.</summary>
    public static IReadOnlyList<FrameRegionDiagnostic> Describe(IReadOnlyList<Rectangle> regions,
        IReadOnlyList<FrameRegionDifference> differences, bool comparisonAttempted)
    {
        bool priorRegionFailed = false;
        var result = new List<FrameRegionDiagnostic>(regions.Count);
        for (int index = 0; index < regions.Count; index++)
        {
            Rectangle region = regions[index];
            var measurement = differences.FirstOrDefault(difference => difference.Region == region);
            bool measured = differences.Any(difference => difference.Region == region);
            if (!comparisonAttempted)
            {
                result.Add(new(region, RegionRole(index), "unavailable", null, null, null));
                continue;
            }
            if (measured)
            {
                bool withinLimits = measurement.ChangedPixelFraction < MaxChangedPixelFraction
                    && measurement.MeanAbsoluteChannelDifference <= MaxMeanAbsoluteChannelDifference;
                result.Add(new(region, RegionRole(index), withinLimits ? "measured" : "failed",
                    measurement.ChangedPixelFraction, measurement.MeanAbsoluteChannelDifference, withinLimits));
                priorRegionFailed |= !withinLimits;
                continue;
            }
            result.Add(new(region, RegionRole(index), priorRegionFailed ? "not_evaluated" : "unavailable",
                null, null, null));
        }
        return result;
    }

    private static string RegionRole(int index) => index switch
    {
        0 => "target_box",
        1 => "clicked_action_approved_region",
        2 => "separate_menu_landmark",
        _ => $"additional_context_region_{index - 2}",
    };
    private const int MaxRegions = 12;

    public static bool TryCompare(byte[] sourceJpeg, Bitmap currentFrame, int imageWidth, int imageHeight,
        int jpegQuality, IReadOnlyList<Rectangle> regions, out IReadOnlyList<FrameRegionDifference> differences)
    {
        differences = [];
        if (sourceJpeg.Length is 0 or > 4 * 1024 * 1024 || imageWidth < 1 || imageHeight < 1
            || jpegQuality is < 10 or > 95 || regions.Count is < 1 or > MaxRegions
            || regions.Any(region => region.X < 0 || region.Y < 0 || region.Width < 2 || region.Height < 2
                || (long)region.Right > imageWidth || (long)region.Bottom > imageHeight
                || (long)region.Width * region.Height * 4 > (long)imageWidth * imageHeight))
            return false;

        try
        {
            using var sourceStream = new MemoryStream(sourceJpeg, writable: false);
            using var source = new Bitmap(sourceStream);
            if (source.Width != imageWidth || source.Height != imageHeight) return false;

            byte[] currentJpeg = GameVision.EncodeJpeg(currentFrame,
                new Rectangle(0, 0, currentFrame.Width, currentFrame.Height), imageWidth, imageHeight, jpegQuality);
            using var currentStream = new MemoryStream(currentJpeg, writable: false);
            using var current = new Bitmap(currentStream);
            if (current.Width != imageWidth || current.Height != imageHeight) return false;

            var measurements = new List<FrameRegionDifference>(regions.Count);
            foreach (var region in regions)
            {
                byte[] before = ReadRgb24(source, region);
                byte[] after = ReadRgb24(current, region);
                if (before.Length == 0 || before.Length != after.Length) return false;

                int changed = 0;
                long totalDifference = 0;
                int pixels = before.Length / 3;
                for (int i = 0; i < before.Length; i += 3)
                {
                    int d0 = Math.Abs(before[i] - after[i]);
                    int d1 = Math.Abs(before[i + 1] - after[i + 1]);
                    int d2 = Math.Abs(before[i + 2] - after[i + 2]);
                    if (Math.Max(d0, Math.Max(d1, d2)) >= ChangedChannelThreshold) changed++;
                    totalDifference += d0 + d1 + d2;
                }
                var measurement = new FrameRegionDifference(region, changed / (double)pixels,
                    totalDifference / (3.0 * pixels));
                measurements.Add(measurement);
                if (measurement.ChangedPixelFraction >= MaxChangedPixelFraction
                    || measurement.MeanAbsoluteChannelDifference > MaxMeanAbsoluteChannelDifference)
                {
                    differences = measurements;
                    return false;
                }
            }
            differences = measurements;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return false;
        }
    }

    private static byte[] ReadRgb24(Bitmap source, Rectangle region)
    {
        using var copy = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(copy))
            graphics.DrawImage(source, new Rectangle(0, 0, region.Width, region.Height), region, GraphicsUnit.Pixel);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var row = new byte[stride];
            var pixels = new byte[copy.Width * copy.Height * 3];
            for (int y = 0; y < copy.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, stride);
                Buffer.BlockCopy(row, 0, pixels, y * copy.Width * 3, copy.Width * 3);
            }
            return pixels;
        }
        finally { copy.UnlockBits(data); }
    }
}
