// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Native;
using ShadowUse.Vision;

namespace ShadowUse.Tools;

public sealed record GameJevActionFrame(GameScreenshotFrameBinding Binding, byte[] Jpeg, int JpegQuality = 75);
public sealed record GameJevActionFrameCapture(GameJevActionFrame? Frame, CallToolResult? Failure);

/// <summary>Captures and validates the exact full frame used by one experimental Jev action.</summary>
public interface IGameJevActionFrameSource
{
    Task<GameJevActionFrameCapture> CaptureAsync(McpServer server, string app, CancellationToken ct);
    Task<string?> ValidateBindingAsync(string app, GameScreenshotFrameBinding binding, CancellationToken ct);
}

/// <summary>Uses the ordinary GameTools screenshot pipeline and the same UIA/frame identity checks as Jev decisions.</summary>
public sealed class GameJevActionFrameSource(GameTools gameTools, UiaService uia) : IGameJevActionFrameSource
{
    private const int MaxObservationAgeMs = 10_000;
    private const int ActionJpegQuality = 75;

    public async Task<GameJevActionFrameCapture> CaptureAsync(McpServer server, string app, CancellationToken ct)
    {
        CallToolResult result = await gameTools.GameScreenshot(server, app, max_width: 1280, quality: ActionJpegQuality,
            bring_to_front: false, ct: ct).ConfigureAwait(false);
        if (result.IsError == true) return new(null, result);
        string? json = result.Content.OfType<TextContentBlock>().Select(block => block.Text).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) return new(null, Failure("Screenshot returned no frame metadata; reobserve."));
        string observationId;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("frame_id", out var frame) || frame.ValueKind != JsonValueKind.String)
                return new(null, Failure("Screenshot metadata is incomplete; reobserve."));
            observationId = frame.GetString()!;
        }
        catch (JsonException ex)
        {
            return new(null, Failure($"Screenshot metadata is invalid: {ex.Message}"));
        }
        if (!GameScreenshotFrameCache.TryGet(observationId, out var binding) || binding is null
            || !GameScreenshotFrameCache.TryGetJpeg(observationId, out var jpeg) || jpeg is null)
            return new(null, Failure("The fresh screenshot has no cached frame binding and JPEG; reobserve."));
        return new(new GameJevActionFrame(binding, jpeg, ActionJpegQuality), null);
    }

    public async Task<string?> ValidateBindingAsync(string app, GameScreenshotFrameBinding binding, CancellationToken ct)
    {
        if (!GameVision.TryGetFrame(binding.ObservationId, out var source))
            return "The screenshot frame expired; take a fresh screenshot.";
        if (DateTimeOffset.UtcNow - source.Timestamp > TimeSpan.FromMilliseconds(MaxObservationAgeMs))
            return "The screenshot is older than 10 seconds; reobserve before asking Jev.";
        if (source.Timestamp != binding.CapturedAtUtc || source.TargetLifetime != binding.TargetLifetime
            || source.Width != binding.FrameWidth || source.Height != binding.FrameHeight || source.Scale != binding.ImageScale)
            return "Screenshot-frame metadata is inconsistent; reobserve.";

        var target = await uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!uia.TryValidateTarget(target, out var targetError)) return targetError ?? "The screenshot target could not be validated.";
        if (TargetLifetime(target!) != binding.TargetLifetime)
            return "The screenshot belongs to a different target lifetime; take a fresh screenshot.";
        if (!NativeMethods.GetWindowRect(target!.Hwnd, out var currentRect)
            || currentRect.Left != binding.WindowRect.Left || currentRect.Top != binding.WindowRect.Top
            || currentRect.Right != binding.WindowRect.Right || currentRect.Bottom != binding.WindowRect.Bottom)
            return "The target window moved or resized after the screenshot; reobserve.";
        if (NativeMethods.GetDpiForWindow(target.Hwnd) != binding.DpiAtCapture)
            return "The target window DPI changed after the screenshot; reobserve.";
        return null;
    }

    private static CallToolResult Failure(string message) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { ok = false, error = message, no_input_sent = true }) }],
        IsError = true,
    };

    private static string TargetLifetime(AppTarget target)
        => target.TargetReference is { } identity
            ? $"target:{identity.TargetId}:{identity.LifetimeEpoch}:{identity.ProcessCreationToken}"
            : $"legacy:{target.Pid}:{target.Hwnd.ToInt64():X}";
}
