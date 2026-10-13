// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ShadowUse.Native;

namespace ShadowUse.Vision;

public sealed record GameUiCandidate(
    string Id,
    string Key,
    string Role,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    int CenterX,
    int CenterY,
    bool PixelAnchorDetected,
    double EvidenceFraction,
    string EvidenceMetric,
    string Actionability,
    string IdentityStatus,
    int? Row = null,
    int? Column = null,
    string? IdentitySource = null,
    string? RegionHash = null);

public sealed record GameUiContextRegion(string Key, string Label, int X, int Y, int Width, int Height, string Note);

public sealed record GameUiObservation(
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
    string LayoutId,
    string Scope,
    bool CandidateSetComplete,
    string? CandidateSetIssue,
    IReadOnlyList<string> ObservedText,
    IReadOnlyList<GameUiCandidate> Candidates,
    IReadOnlyList<GameUiContextRegion> ContextRegions,
    string CandidateSetCompletenessSource = "calibrated_pixel_anchors",
    string? CoverageEvidence = null,
    string? CandidateGenerator = null,
    string? MappedScreenId = null);

internal static class GameUiObservationCache
{
    private const int Capacity = 24;
    private static readonly object Sync = new();
    private static readonly Dictionary<string, GameUiObservation> Observations = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    public static void Store(GameUiObservation observation)
    {
        lock (Sync)
        {
            Observations[observation.ObservationId] = observation;
            Order.Enqueue(observation.ObservationId);
            while (Order.Count > Capacity)
                Observations.Remove(Order.Dequeue());
        }
    }

    public static bool TryGet(string observationId, out GameUiObservation? observation)
    {
        lock (Sync) return Observations.TryGetValue(observationId, out observation);
    }
}

/// <summary>
/// Calibrated geometry for one Bloons TD Battles 2 hero-selection layout. It is deliberately
/// not a general game detector: pixel anchors must pass before the scoped candidate set is
/// marked complete, and image-derived actionability/hero identity remain unverified.
/// </summary>
public static class BloonsTdb2SelectionGrounding
{
    public const string LayoutId = "bloons-tdb2-hero-select-1722x999-v1";
    public const string Scope = "Visible English hero-selection page: 16 hero cards, two possible profile banners, Cancel, Ready, previous/next map arrows, and next Heroes-page arrow. The map preview and large character art remain context-only; this is scoped layout coverage, not other-page coverage. Actionability and game-enabled state are unverified.";

    private const int ReferenceWidth = 1722;
    private const int ReferenceHeight = 999;
    private static readonly int[] CardCentersX = [291, 408, 526, 644, 762, 880, 998, 1116];

    private enum PixelPattern { BrightYellow, White, Green }
    private sealed record CalibratedBox(string Key, string Role, string Label, Rectangle Bounds,
        PixelPattern Pattern, double MinimumFraction, int? Row = null, int? Column = null);

    private static readonly CalibratedBox[] Controls =
    [
        new("button-cancel", "button", "Cancel", new(1196, 582, 290, 121), PixelPattern.BrightYellow, 0.25),
        new("button-ready", "button", "Ready!", new(1196, 757, 290, 122), PixelPattern.BrightYellow, 0.25),
        new("map-previous", "navigation arrow", "Previous map", new(620, 128, 47, 70), PixelPattern.White, 0.10),
        new("map-next", "navigation arrow", "Next map", new(1057, 128, 44, 70), PixelPattern.White, 0.10),
        new("heroes-next-page", "navigation arrow", "Next Heroes page", new(865, 862, 26, 26), PixelPattern.White, 0.08),
        new("player-profile-banner", "possible profile control", "Player profile banner", new(229, 49, 459, 64), PixelPattern.Green, 0.035),
        new("opponent-profile-banner", "possible profile control", "Opponent profile banner", new(1035, 49, 460, 64), PixelPattern.Green, 0.035),
    ];

    private static readonly (string Key, string Label, Rectangle Bounds, string Note)[] Context =
    [
        ("map-preview", "Map preview", new(605, 122, 513, 321), "Context region; only the calibrated map arrows are included as targets."),
        ("player-hero-preview", "Player hero preview", new(322, 225, 323, 287), "Large character art; interaction is not established."),
        ("opponent-hero-preview", "Opponent hero preview", new(1100, 224, 224, 290), "Large character art; interaction is not established."),
    ];

    public static async Task<GameUiObservation> DetectAsync(
        Bitmap frame,
        string observationId,
        DateTimeOffset capturedAtUtc,
        string? targetLifetime,
        double imageScale,
        uint dpiAtCapture,
        NativeMethods.RECT windowRect,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var buttonPanel = ToPhysicalBounds(new Rectangle(1170, 570, 330, 315), frame.Width, frame.Height);
        var (lines, unsupported) = await GameVision.ReadTextAsync(
            frame, windowRect, buttonPanel, imageScale, upscale: 2).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var observedText = lines.Select(line => line.Text).ToArray();
        var observation = Detect(frame, observationId, capturedAtUtc, targetLifetime, imageScale, dpiAtCapture, windowRect, observedText);
        return unsupported is null ? observation : observation with
        {
            CandidateSetComplete = false,
            CandidateSetIssue = string.Join("; ", new[] { observation.CandidateSetIssue, "button-panel OCR unsupported: " + unsupported }.Where(issue => !string.IsNullOrWhiteSpace(issue))),
        };
    }

    public static GameUiObservation Detect(
        Bitmap frame,
        string observationId,
        DateTimeOffset capturedAtUtc,
        string? targetLifetime,
        double imageScale,
        uint dpiAtCapture,
        NativeMethods.RECT windowRect,
        IReadOnlyList<string>? observedText = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var text = observedText ?? [];
        var issues = new List<string>();
        var aspect = frame.Width / (double)Math.Max(1, frame.Height);
        if (Math.Abs(aspect - ReferenceWidth / (double)ReferenceHeight) > 0.04)
            issues.Add($"unsupported frame aspect ratio {aspect:F3}");
        if (frame.Width < 800 || frame.Height < 450)
            issues.Add("frame is too small for this calibration");

        using var pixels = new PixelView(frame);
        var candidates = new List<GameUiCandidate>(23);
        for (var row = 1; row <= 2; row++)
        {
            int y = row == 1 ? 570 : 727;
            int bottom = row == 1 ? 710 : 862;
            for (var column = 1; column <= 8; column++)
            {
                var key = $"hero-r{row}-c{column}";
                var reference = new Rectangle(CardCentersX[column - 1] - 52, y, 104, bottom - y);
                AddCandidate(key, "hero card", $"Unknown hero option, row {row} column {column}", reference,
                    PixelPattern.BrightYellow, 0.02, row, column);
            }
        }

        foreach (var control in Controls)
            AddCandidate(control.Key, control.Role, control.Label, control.Bounds, control.Pattern, control.MinimumFraction);

        bool hasCancel = text.Any(line => line.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        bool hasReady = text.Any(line => line.Contains("ready", StringComparison.OrdinalIgnoreCase));
        if (!hasCancel) issues.Add("OCR anchor 'Cancel' was not found in the button panel");
        if (!hasReady) issues.Add("OCR anchor 'Ready' was not found in the button panel");

        var contexts = Context.Select(region =>
        {
            var rect = ToImageBounds(region.Bounds);
            return new GameUiContextRegion(region.Key, region.Label, rect.X, rect.Y, rect.Width, rect.Height, region.Note);
        }).ToArray();

        return new GameUiObservation(
            observationId,
            capturedAtUtc,
            targetLifetime,
            frame.Width,
            frame.Height,
            Math.Max(1, (int)Math.Round(frame.Width * imageScale)),
            Math.Max(1, (int)Math.Round(frame.Height * imageScale)),
            imageScale,
            dpiAtCapture,
            windowRect,
            LayoutId,
            Scope,
            issues.Count == 0,
            issues.Count == 0 ? null : string.Join("; ", issues),
            text,
            candidates,
            contexts,
            CandidateSetCompletenessSource: "calibrated_pixel_anchors",
            CoverageEvidence: issues.Count == 0
                ? "All 23 calibrated candidate anchors passed; both Cancel/Ready OCR anchors matched within the named hero-selection layout."
                : "Coverage check reported: " + string.Join("; ", issues));

        void AddCandidate(string key, string role, string label, Rectangle reference,
            PixelPattern pattern, double minimumFraction, int? row = null, int? column = null)
        {
            var physical = ToPhysicalBounds(reference);
            double fraction = pixels.Fraction(physical, pattern);
            bool verified = fraction >= minimumFraction;
            if (!verified) issues.Add($"expected visual anchor '{key}' was missing (fraction={fraction:F3}, required={minimumFraction:F3})");
            var image = ToImageBounds(reference);
            candidates.Add(new GameUiCandidate(
                $"{observationId}:{key}", key, role, label,
                image.X, image.Y, image.Width, image.Height,
                image.X + image.Width / 2, image.Y + image.Height / 2,
                verified, Math.Round(fraction, 4), pattern switch
                {
                    PixelPattern.BrightYellow => "bright_yellow_pixel_fraction",
                    PixelPattern.White => "white_pixel_fraction",
                    _ => "green_pixel_fraction",
                },
                "unverified", row is null ? "not_applicable" : "unknown", row, column));
        }

        Rectangle ToPhysicalBounds(Rectangle reference) => new(
            (int)Math.Round(reference.X * frame.Width / (double)ReferenceWidth),
            (int)Math.Round(reference.Y * frame.Height / (double)ReferenceHeight),
            Math.Max(1, (int)Math.Round(reference.Width * frame.Width / (double)ReferenceWidth)),
            Math.Max(1, (int)Math.Round(reference.Height * frame.Height / (double)ReferenceHeight)));

        Rectangle ToImageBounds(Rectangle reference)
        {
            var physical = ToPhysicalBounds(reference);
            return new Rectangle(
                (int)Math.Round(physical.X * imageScale),
                (int)Math.Round(physical.Y * imageScale),
                Math.Max(1, (int)Math.Round(physical.Width * imageScale)),
                Math.Max(1, (int)Math.Round(physical.Height * imageScale)));
        }
    }

    private static Rectangle ToPhysicalBounds(Rectangle reference, int frameWidth, int frameHeight) => new(
        (int)Math.Round(reference.X * frameWidth / (double)ReferenceWidth),
        (int)Math.Round(reference.Y * frameHeight / (double)ReferenceHeight),
        Math.Max(1, (int)Math.Round(reference.Width * frameWidth / (double)ReferenceWidth)),
        Math.Max(1, (int)Math.Round(reference.Height * frameHeight / (double)ReferenceHeight)));

    private sealed class PixelView : IDisposable
    {
        private readonly Bitmap bitmap;
        private readonly BitmapData data;
        private readonly byte[] bytes;
        private readonly int stride;

        public PixelView(Bitmap source)
        {
            bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.DrawImageUnscaled(source, 0, 0);
            data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            stride = Math.Abs(data.Stride);
            bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        }

        public double Fraction(Rectangle region, PixelPattern pattern)
        {
            var clipped = Rectangle.Intersect(region, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (clipped.Width == 0 || clipped.Height == 0) return 0;
            int matches = 0, total = 0;
            const int step = 2;
            for (int y = clipped.Top; y < clipped.Bottom; y += step)
            {
                int row = (data.Stride >= 0 ? y : bitmap.Height - 1 - y) * stride;
                for (int x = clipped.Left; x < clipped.Right; x += step)
                {
                    int i = row + x * 3;
                    byte b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
                    bool match = pattern switch
                    {
                        PixelPattern.BrightYellow => r >= 210 && g >= 150 && b <= 80,
                        PixelPattern.White => r >= 210 && g >= 210 && b >= 210,
                        _ => g >= 120 && g >= r * 1.25 && g >= b * 1.12,
                    };
                    if (match) matches++;
                    total++;
                }
            }
            return total == 0 ? 0 : matches / (double)total;
        }

        public void Dispose()
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
        }
    }
}
