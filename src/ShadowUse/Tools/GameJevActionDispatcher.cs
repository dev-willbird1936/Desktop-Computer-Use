// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Drawing;
using ModelContextProtocol.Protocol;

namespace ShadowUse.Tools;

/// <summary>Dispatch seam for Jev game actions. Implementations preserve DCU's frame-bound guards.</summary>
public interface IGameJevActionDispatcher
{
    Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality,
        IReadOnlyList<Rectangle>? sourceFrameStableRegions, bool verify, bool thenScreenshot,
        string? appId, string? actionId, CancellationToken ct);
    Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId,
        bool verify, bool thenScreenshot, string? appId, string? actionId, CancellationToken ct);
}

/// <summary>Routes Jev actions through DCU's normal guarded game input pipeline.</summary>
public sealed class GameJevActionDispatcher(GameTools gameTools) : IGameJevActionDispatcher
{
    public Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality,
        IReadOnlyList<Rectangle>? sourceFrameStableRegions, bool verify, bool thenScreenshot,
        string? appId, string? actionId, CancellationToken ct)
        => gameTools.GameJevClick(app, x, y, sourceFrameId, sourceFrameJpegQuality, sourceFrameStableRegions,
            verify, thenScreenshot, appId, actionId, ct);

    public Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId,
        bool verify, bool thenScreenshot, string? appId, string? actionId, CancellationToken ct)
        => gameTools.GameKey(app, key: key, action: "tap", verify: verify, then_screenshot: thenScreenshot,
            source_frame_id: sourceFrameId, app_id: appId, action_id: actionId, ct: ct);
}
