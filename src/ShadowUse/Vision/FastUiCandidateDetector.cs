// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using ShadowUse.Native;

namespace ShadowUse.Vision;

public sealed record FastUiCandidate(
    string Key,
    string Role,
    string Label,
    int X,
    int Y,
    int Width,
    int Height,
    string Method,
    double ProposalScore);

public sealed record FastUiDetectionResult(
    string Method,
    bool CandidateSetComplete,
    string? CandidateSetIssue,
    IReadOnlyList<string> ObservedText,
    IReadOnlyList<FastUiCandidate> Candidates,
    double PerceptionMs,
    string? UnsupportedReason = null);

/// <summary>
/// Local, low-cost candidate proposer for static screenshots. It combines Windows OCR text
/// boxes with a downsampled edge-component pass. Boxes are proposals, not verified controls.
/// </summary>
public static class FastUiCandidateDetector
{
    private const int WorkingMaxWidth = 640;

    public static async Task<FastUiDetectionResult> DetectAsync(
        Bitmap bitmap,
        string method,
        string frameId,
        CancellationToken ct = default,
        int maxCandidates = 128,
        IReadOnlyList<string>? observedText = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ct.ThrowIfCancellationRequested();
        if (maxCandidates is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        if (method is not ("ocr" or "edge" or "combined" or "bloons-template"))
            throw new ArgumentException("method must be ocr, edge, combined, or bloons-template.", nameof(method));

        long started = Stopwatch.GetTimestamp();
        if (method == "bloons-template")
        {
            var windowRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = bitmap.Width, Bottom = bitmap.Height };
            var observation = observedText is null
                ? await BloonsTdb2SelectionGrounding.DetectAsync(
                    bitmap, frameId, DateTimeOffset.UtcNow, "static-image", 1, 96, windowRect, ct).ConfigureAwait(false)
                : BloonsTdb2SelectionGrounding.Detect(
                    bitmap, frameId, DateTimeOffset.UtcNow, "static-image", 1, 96, windowRect, observedText);
            var templateCandidates = observation.Candidates.Where(candidate => candidate.PixelAnchorDetected).Take(maxCandidates)
                .Select(candidate => new FastUiCandidate(
                    candidate.Key, candidate.Role, candidate.Label, candidate.X, candidate.Y, candidate.Width, candidate.Height,
                    "bloons_calibrated_pixel_anchor", candidate.EvidenceFraction)).ToArray();
            bool templateComplete = observation.CandidateSetComplete && templateCandidates.Length == observation.Candidates.Count;
            string? templateIssue = templateComplete ? null
                : observation.CandidateSetComplete
                    ? "The calibrated inventory was truncated by maxCandidates; coverage is incomplete."
                    : $"The named Bloons TD Battles 2 layout did not fully match; {templateCandidates.Length}/23 anchors passed. {observation.CandidateSetIssue}";
            return new(method, templateComplete, templateIssue,
                observation.ObservedText, templateCandidates,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        IReadOnlyList<OcrLine> ocrLines = [];
        string? unsupported = null;
        if (method is "ocr" or "combined")
        {
            ct.ThrowIfCancellationRequested();
            var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = bitmap.Width, Bottom = bitmap.Height };
            var read = await GameVision.ReadTextAsync(bitmap, rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height), 1, 1).ConfigureAwait(false);
            ocrLines = read.Lines;
            unsupported = read.Unsupported;
        }

        ct.ThrowIfCancellationRequested();
        var candidates = new List<FastUiCandidate>();
        var regions = method is "edge" or "combined"
            ? await Task.Run(() => FindEdgeRegions(bitmap, ct), ct).ConfigureAwait(false)
            : [];
        if (method is "ocr" or "combined")
        {
            foreach (var (line, index) in ocrLines.Select((line, index) => (line, index)))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line.Text) || line.Width <= 0 || line.Height <= 0) continue;
                // Keep text boxes even when they overlap an edge proposal: downstream fusion uses
                // their geometry to attach the unverified text clue to visual candidates.
                var box = Expand(new Rectangle(line.X, line.Y, line.Width, line.Height), bitmap.Width, bitmap.Height,
                    Math.Max(4, line.Height / 3), Math.Max(3, line.Height / 4));
                candidates.Add(new FastUiCandidate(
                    $"ocr-{index + 1:D3}", "text_region", line.Text[..Math.Min(240, line.Text.Length)],
                    box.X, box.Y, box.Width, box.Height, "windows_media_ocr", 0.65));
            }
        }
        candidates.AddRange(regions);
        var result = candidates
            .OrderByDescending(candidate => candidate.ProposalScore)
            .ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.X)
            .Take(maxCandidates).ToArray();
        string issue = unsupported is null
            ? "Fast OCR/edge proposals can miss controls and include decorative regions; interactivity and coverage are unknown."
            : "OCR unavailable: " + unsupported + " Edge proposals are heuristic and incomplete.";
        return new(method, false, issue, ocrLines.Select(line => line.Text[..Math.Min(250, line.Text.Length)]).Take(40).ToArray(), result,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, unsupported);
    }

    private static List<FastUiCandidate> FindEdgeRegions(Bitmap source, CancellationToken ct)
    {
        double scale = Math.Min(1, WorkingMaxWidth / (double)Math.Max(1, source.Width));
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var data = small.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        byte[] gray = new byte[width * height];
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                    gray[y * width + x] = (byte)((row[x * 3 + 2] * 299 + row[x * 3 + 1] * 587 + row[x * 3] * 114) / 1000);
            }
        }
        finally { small.UnlockBits(data); }

        int count = width * height;
        bool[] edge = new bool[count];
        for (int y = 1; y < height - 1; y++)
        {
            ct.ThrowIfCancellationRequested();
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int index = row + x;
                int gx = Math.Abs(gray[index + 1] - gray[index - 1]);
                int gy = Math.Abs(gray[index + width] - gray[index - width]);
                edge[index] = gx + gy >= 74;
            }
        }

        // One-pixel dilation joins anti-aliased border fragments while keeping work O(pixels).
        bool[] mask = new bool[count];
        for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int index = y * width + x;
                if (!edge[index]) continue;
                mask[index] = mask[index - 1] = mask[index + 1] = true;
                mask[index - width] = mask[index + width] = true;
                mask[index - width - 1] = mask[index - width + 1] = true;
                mask[index + width - 1] = mask[index + width + 1] = true;
            }

        bool[] visited = new bool[count];
        int[] queue = new int[count];
        var proposals = new List<(Rectangle Box, double Score)>();
        for (int start = 0; start < count; start++)
        {
            if (!mask[start] || visited[start]) continue;
            ct.ThrowIfCancellationRequested();
            int head = 0, tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            int minX = width, minY = height, maxX = 0, maxY = 0, pixels = 0;
            while (head < tail)
            {
                int index = queue[head++];
                pixels++;
                int x = index % width, y = index / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(height - 1, y + 1);
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(width - 1, x + 1);
                for (int ny = y0; ny <= y1; ny++)
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int next = ny * width + nx;
                        if (mask[next] && !visited[next])
                        {
                            visited[next] = true;
                            queue[tail++] = next;
                        }
                    }
            }

            int boxWidth = maxX - minX + 1, boxHeight = maxY - minY + 1;
            int boxArea = boxWidth * boxHeight;
            if (boxWidth < 9 || boxHeight < 9 || boxWidth > width * 0.40 || boxHeight > height * 0.38
                || boxArea < 100 || boxArea > width * height * 0.09) continue;
            double density = pixels / (double)boxArea;
            if (density < 0.025 || density > 0.85) continue;
            double border = BorderSupport(edge, width, height, minX, minY, maxX, maxY);
            bool peripheral = minX < width * 0.18 || maxX > width * 0.82 || minY < height * 0.18 || maxY > height * 0.82;
            bool iconSized = boxWidth <= 70 && boxHeight <= 70;
            if (border < 0.18 && !(peripheral && iconSized && density > 0.16)) continue;
            double score = Math.Clamp(border * 0.7 + Math.Min(density, 0.6) * 0.35 + (peripheral ? 0.12 : 0), 0, 1);
            var original = new Rectangle(
                Math.Max(0, (int)Math.Floor(minX / scale)),
                Math.Max(0, (int)Math.Floor(minY / scale)),
                Math.Max(1, (int)Math.Ceiling(boxWidth / scale)),
                Math.Max(1, (int)Math.Ceiling(boxHeight / scale)));
            original = Rectangle.Intersect(original, new Rectangle(0, 0, source.Width, source.Height));
            if (original.Width > 0 && original.Height > 0) proposals.Add((original, score));
        }

        var ordered = proposals.OrderByDescending(item => item.Score).ToList();
        var kept = new List<FastUiCandidate>();
        foreach (var (box, score) in ordered)
        {
            if (kept.Any(existing => IntersectionOverUnion(box, new Rectangle(existing.X, existing.Y, existing.Width, existing.Height)) > 0.5))
                continue;
            string shape = box.Width > box.Height * 2.2 ? "wide" : box.Height > box.Width * 2.2 ? "tall" : "compact";
            string type = box.Width <= 120 && box.Height <= 120 ? "icon-like" : "outlined panel/control-like";
            kept.Add(new FastUiCandidate($"edge-{kept.Count + 1:D3}", "possible_visual_region",
                $"{shape} {type} region; purpose unknown", box.X, box.Y, box.Width, box.Height,
                "fast_edge_component", Math.Round(score, 3)));
            if (kept.Count >= 255) break;
        }
        return kept;
    }

    private static double BorderSupport(bool[] edge, int width, int height, int x0, int y0, int x1, int y1)
    {
        int top = 0, bottom = 0, left = 0, right = 0;
        int acrossX = Math.Max(1, x1 - x0 + 1), acrossY = Math.Max(1, y1 - y0 + 1);
        for (int x = x0; x <= x1; x += Math.Max(1, acrossX / 24))
        {
            if (NearEdge(edge, width, height, x, y0, horizontal: true)) top++;
            if (NearEdge(edge, width, height, x, y1, horizontal: true)) bottom++;
        }
        for (int y = y0; y <= y1; y += Math.Max(1, acrossY / 24))
        {
            if (NearEdge(edge, width, height, x0, y, horizontal: false)) left++;
            if (NearEdge(edge, width, height, x1, y, horizontal: false)) right++;
        }
        double hx = Math.Ceiling(acrossX / (double)Math.Max(1, acrossX / 24));
        double hy = Math.Ceiling(acrossY / (double)Math.Max(1, acrossY / 24));
        return (top / hx + bottom / hx + left / hy + right / hy) / 4;
    }

    private static bool NearEdge(bool[] edge, int width, int height, int x, int y, bool horizontal)
    {
        for (int offset = -2; offset <= 2; offset++)
        {
            int px = horizontal ? x : x + offset;
            int py = horizontal ? y + offset : y;
            if (px >= 0 && px < width && py >= 0 && py < height && edge[py * width + px]) return true;
        }
        return false;
    }

    private static Rectangle Expand(Rectangle box, int width, int height, int padX, int padY)
    {
        int x = Math.Max(0, box.X - padX), y = Math.Max(0, box.Y - padY);
        int right = Math.Min(width, box.Right + padX), bottom = Math.Min(height, box.Bottom + padY);
        return Rectangle.FromLTRB(x, y, right, bottom);
    }

    private static double IntersectionOverUnion(Rectangle a, Rectangle b)
    {
        var intersection = Rectangle.Intersect(a, b);
        if (intersection.Width <= 0 || intersection.Height <= 0) return 0;
        double area = (double)a.Width * a.Height + (double)b.Width * b.Height - (double)intersection.Width * intersection.Height;
        return area <= 0 ? 0 : intersection.Width * intersection.Height / area;
    }
}
