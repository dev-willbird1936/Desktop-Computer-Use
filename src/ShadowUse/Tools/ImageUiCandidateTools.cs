// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Vision;

namespace ShadowUse.Tools;

/// <summary>Local, model-free screenshot candidate proposals for image files.</summary>
[McpServerToolType]
public sealed class ImageUiCandidateTools
{
    private const long MaxImageBytes = 32 * 1024 * 1024;
    private const long MaxImagePixels = 20_000_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = "ui_candidates_detect_image"), Description(
        "Fast local screenshot analysis: OCR text boxes, edge-based icon/panel proposals, or the calibrated BTD2 template. Accepts an absolute local PNG/JPEG/BMP path; does not call a model or send the image externally. Returns image-space boxes and an annotated image. General proposals are incomplete heuristics; only a matched named BTD2 layout may report scoped coverage. Clickability and enabled state remain unknown.")]
    public async Task<CallToolResult> DetectImage(
        [Description("Absolute local path to a PNG, JPEG, or BMP screenshot; maximum 32 MiB and 20 megapixels")] string image_path,
        [Description("ocr | edge | combined | bloons-template (the known BTD2 menu only)")] string method = "combined",
        [Description("Maximum returned candidates, 1-255")] int max_candidates = 128,
        CancellationToken ct = default)
    {
        if (method is not ("ocr" or "edge" or "combined" or "bloons-template"))
            return Fail("method must be ocr, edge, combined, or bloons-template.");
        if (max_candidates is < 1 or > 255) return Fail("max_candidates must be between 1 and 255.");
        if (string.IsNullOrWhiteSpace(image_path) || !Path.IsPathFullyQualified(image_path))
            return Fail("image_path must be an absolute local path.");
        try
        {
            var info = new FileInfo(image_path);
            if (!info.Exists) return Fail("The image file does not exist.");
            if (info.Length is <= 0 or > MaxImageBytes) return Fail($"The image must be between 1 byte and {MaxImageBytes} bytes.");
            byte[] fileBytes = await File.ReadAllBytesAsync(image_path, ct).ConfigureAwait(false);
            string frameId = "image-" + Convert.ToHexString(SHA256.HashData(fileBytes))[..20].ToLowerInvariant();
            using var stream = new MemoryStream(fileBytes, writable: false);
            using var decoded = new Bitmap(stream);
            if ((long)decoded.Width * decoded.Height > MaxImagePixels)
                return Fail($"The image exceeds the {MaxImagePixels}-pixel limit.");
            using var bitmap = new Bitmap(decoded);

            var detected = await FastUiCandidateDetector.DetectAsync(bitmap, method, frameId, ct, max_candidates).ConfigureAwait(false);
            var candidateSet = new
            {
                frame_id = frameId,
                image_width = bitmap.Width,
                image_height = bitmap.Height,
                scope = method == "bloons-template" ? BloonsTdb2SelectionGrounding.Scope : "Visible UI-like regions proposed by local OCR and/or fast edge geometry",
                candidate_set_complete = detected.CandidateSetComplete,
                candidate_set_issue = detected.CandidateSetIssue,
                coverage_evidence = detected.Method == "bloons-template" && detected.CandidateSetComplete
                    ? "All 23 named-layout candidates passed local pixel/OCR anchors; this coverage is limited to that calibrated BTD2 screen."
                    : null,
                observed_text = detected.ObservedText,
                candidates = detected.Candidates.Select(candidate => new
                {
                    key = candidate.Key,
                    role = candidate.Role,
                    label = candidate.Label,
                    identity_source = candidate.Method,
                    bbox = new { x = candidate.X, y = candidate.Y, width = candidate.Width, height = candidate.Height },
                    proposal_score = candidate.ProposalScore,
                }).ToArray(),
            };
            string candidateJson = JsonSerializer.Serialize(candidateSet, Json);
            byte[] annotated = Annotate(bitmap, detected);
            return Reply(new
            {
                ok = true,
                frame_id = frameId,
                image_width = bitmap.Width,
                image_height = bitmap.Height,
                method = detected.Method,
                perception_ms = Math.Round(detected.PerceptionMs, 2),
                candidate_count = detected.Candidates.Count,
                candidate_set_complete = detected.CandidateSetComplete,
                candidate_set_issue = detected.CandidateSetIssue,
                coverage_evidence = detected.Method == "bloons-template" && detected.CandidateSetComplete
                    ? "All 23 named-layout candidates passed local pixel/OCR anchors; this coverage is limited to that calibrated BTD2 screen."
                    : null,
                ocr_unsupported_reason = detected.UnsupportedReason,
                observed_text = detected.ObservedText,
                candidates = candidateSet.candidates,
                candidate_set_json = candidateJson,
                note = "Local heuristic proposals only; clickability, enabled state, semantic identity, and exhaustive coverage are unverified.",
            }, annotated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or IOException or UnauthorizedAccessException or OutOfMemoryException or InvalidOperationException)
        {
            return Fail($"Image analysis failed: {ex.GetType().Name}: {ex.Message[..Math.Min(ex.Message.Length, 240)]}");
        }
    }

    private static byte[] Annotate(Bitmap source, FastUiDetectionResult result)
    {
        using var annotated = new Bitmap(source);
        using var graphics = Graphics.FromImage(annotated);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        using var likely = new Pen(Color.FromArgb(240, 239, 83, 80), Math.Max(2, source.Width / 900));
        using var uncertain = new Pen(Color.FromArgb(235, 255, 195, 55), Math.Max(2, source.Width / 900));
        using var context = new Pen(Color.FromArgb(235, 41, 194, 218), Math.Max(2, source.Width / 900));
        using var tagBrush = new SolidBrush(Color.FromArgb(220, 12, 17, 23));
        using var whiteBrush = new SolidBrush(Color.White);
        using var font = new Font("Arial", Math.Max(10, source.Width / 110), FontStyle.Bold, GraphicsUnit.Pixel);
        int index = 0;
        foreach (var candidate in result.Candidates)
        {
            index++;
            var box = Rectangle.Intersect(new Rectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height),
                new Rectangle(0, 0, source.Width, source.Height));
            if (box.Width <= 0 || box.Height <= 0) continue;
            bool text = candidate.Method == "windows_media_ocr";
            var pen = result.Method == "ocr" || text ? likely : candidate.Method == "bloons_calibrated_pixel_anchor" ? context : uncertain;
            graphics.DrawRectangle(pen, box);
            string tag = $"{index:D2} {candidate.Label[..Math.Min(candidate.Label.Length, 42)]}";
            SizeF size = graphics.MeasureString(tag, font);
            float tagY = Math.Max(0, box.Y - size.Height - 3);
            var tagRect = new RectangleF(box.X, tagY, Math.Min(source.Width - box.X, size.Width + 8), size.Height + 3);
            graphics.FillRectangle(tagBrush, tagRect);
            graphics.DrawString(tag, font, whiteBrush, tagRect.X + 3, tagRect.Y + 1);
        }
        string coverage = result.CandidateSetComplete ? "named-layout scope only" : "coverage incomplete";
        string header = $"{result.Method} | {result.Candidates.Count} proposals | {result.PerceptionMs:F1} ms | {coverage}; actionability unknown";
        using var headerFont = new Font("Arial", Math.Max(12, source.Width / 85), FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF headerSize = graphics.MeasureString(header, headerFont);
        var headerRect = new RectangleF(8, 8, Math.Min(source.Width - 16, headerSize.Width + 18), headerSize.Height + 10);
        graphics.FillRectangle(tagBrush, headerRect);
        graphics.DrawString(header, headerFont, whiteBrush, headerRect.X + 5, headerRect.Y + 3);
        using var output = new MemoryStream();
        annotated.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static CallToolResult Reply(object payload, byte[]? image = null, bool isError = false)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) } };
        if (image is not null) content.Add(ImageContentBlock.FromBytes(image, "image/png"));
        return new CallToolResult { Content = content, IsError = isError };
    }

    private static CallToolResult Fail(string message) => Reply(new { ok = false, error = message }, isError: true);
}
