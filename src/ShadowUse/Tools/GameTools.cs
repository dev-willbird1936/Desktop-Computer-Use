using System.ComponentModel;
using System.Drawing;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Config;
using ShadowUse.Native;
using ShadowUse.Overlay;
using ShadowUse.Safety;
using ShadowUse.Vision;
using InputResult = ShadowUse.Automation.BackgroundInput.InputResult;

namespace ShadowUse.Tools;

/// <summary>Game/native-surface tools. Always callable; input mode is explicitly selected.</summary>
[McpServerToolType]
public sealed class GameTools(UiaService uia, BackgroundInput background, ShadowSettings settings, GameFrameStore frames)
{

    private static CallToolResult Reply(object payload, byte[]? image = null, bool error = false)
    {
        List<ContentBlock> content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }];
        if (image != null) content.Add(ImageContentBlock.FromBytes(image, "image/jpeg"));
        return new() { Content = content, IsError = error };
    }
    private static CallToolResult Fail(string message) => Reply(new { ok = false, error = message }, error: true);

    private GameFrameMetadata Record(AppTarget target, Bitmap bitmap, double scale)
        => frames.Record(target.WindowId, bitmap.Width, bitmap.Height, scale);

    private double Scale(AppTarget target, NativeMethods.RECT bounds, string coordSpace, string? sourceFrameId)
    {
        if (coordSpace == "screen") return 1;
        if (coordSpace != "image") throw new ArgumentException("coord_space must be image or screen");
        if (sourceFrameId == null) throw new ArgumentException("Pass source_frame_id from game_screenshot for image coordinates, or coord_space=screen.");
        return frames.Resolve(sourceFrameId, target.WindowId, bounds.Width, bounds.Height).Scale;
    }

    private async Task<CallToolResult> Act(string app, string? inputMode, string? pace, bool thenScreenshot,
        Func<AppTarget, InputMode, Task<InputResult>> action, CancellationToken ct)
    {
        try
        {
            var mode = InputMode.Resolve(settings, inputMode);
            if (!InputPace.IsValidPace(pace)) return Fail("pace must be fast, human or normal");
            var target = await uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            if (settings.EnableDesktopCheck && Guard.CheckInteractiveDesktop() is { } desktopError) return Fail(desktopError);
            using var timing = InputPace.Scope(pace ?? (settings.FastInput ? "fast" : "normal"));
            return await ActionCoordinator.RunAsync(target, mode.RealCursor, async () =>
            {
                var focus = settings.EnableFocusGuard && !(mode.RealCursor && settings.GameMode) ? FocusGuard.Capture() : null;
                try
                {
                    byte[]? before = null;
                    if (GameVision.Capture(target.Hwnd) is { } initial)
                    {
                        using (initial.Bitmap) before = GameVision.Signature(initial.Bitmap);
                    }
                    var result = await action(target, mode).ConfigureAwait(false);
                    if (ct.IsCancellationRequested || result.InterruptionReason != null)
                        return Reply(new { ok = result.Success, method = result.Method, input_mode = mode.Name,
                            input_sent = result.InputSent, effect = "unverified", detail = result.Detail,
                            interruption_reason = result.InterruptionReason }, error: !result.Success);
                    // Observe once, never repeat a successful dispatch because the game
                    // did not expose a change. Cancellation still releases input/locks.
                    await Task.Delay(InputPace.Ms(settings.PostActionDelayMs), ct).ConfigureAwait(false);
                    string effect = "unverified";
                    byte[]? jpeg = null;
                    GameFrameMetadata? frame = null;
                    if (GameVision.Capture(target.Hwnd) is { } after)
                    {
                        using (after.Bitmap)
                        {
                            if (before != null) effect = GameVision.ClassifyChange(GameVision.ChangedFraction(before, GameVision.Signature(after.Bitmap)));
                            if (thenScreenshot)
                            {
                                double scale = Math.Min(1, GameVision.DefaultMaxWidth / (double)after.Bitmap.Width);
                                frame = Record(target, after.Bitmap, scale);
                                jpeg = GameVision.EncodeJpeg(after.Bitmap, new(0, 0, after.Bitmap.Width, after.Bitmap.Height),
                                    (int)Math.Round(after.Bitmap.Width * scale), (int)Math.Round(after.Bitmap.Height * scale), 60);
                            }
                        }
                    }
                    return Reply(new { ok = result.Success, method = result.Method, input_mode = mode.Name,
                        input_sent = result.InputSent, effect, detail = result.Detail, interruption_reason = result.InterruptionReason,
                        source_frame_id = frame?.Id, image_scale = frame?.Scale }, jpeg, !result.Success);
                }
                finally { focus?.Restore((uint)target.Pid); }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    private (int X, int Y) Point(AppTarget target, int x, int y, string coordSpace, string? frameId)
    {
        if (!NativeMethods.GetWindowRect(target.Hwnd, out var rect)) throw new ArgumentException("Target window no longer exists");
        double scale = Scale(target, rect, coordSpace, frameId);
        var point = coordSpace == "screen" ? (X: x, Y: y) : GameVision.ToScreen(rect, scale, x, y);
        if (point.X < rect.Left || point.Y < rect.Top || point.X >= rect.Right || point.Y >= rect.Bottom)
            throw new ArgumentException("Point is outside the selected window");
        return point;
    }

    [McpServerTool(Name = "game_screenshot"), Description("Capture a native/game window without a UIA tree. No activation by default; bring_to_front=true explicitly activates the selected window for GPU capture. Returns JPEG, source_frame_id and image_scale. Always available.")]
    public async Task<CallToolResult> Screenshot(string app, int max_width = 1280, bool bring_to_front = false, CancellationToken ct = default)
    {
        if (max_width is < 128 or > 3840) return Fail("max_width must be 128-3840");
        var target = await uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        async Task<CallToolResult> Capture()
        {
        var focus = bring_to_front && !settings.GameMode && settings.EnableFocusGuard ? FocusGuard.Capture() : null;
        try
        {
        if (bring_to_front && !await InteractiveInput.EnsureForegroundAsync(target.Hwnd, ct))
            return Fail("Windows refused foreground activation; check desktop access and elevation.");
        if (GameVision.Capture(target.Hwnd) is not { } capture)
            return Fail("Window capture failed. Restore a minimized window; GPU capture may require the selected window to already be foreground.");
        using (capture.Bitmap)
        {
            double scale = Math.Min(1, max_width / (double)capture.Bitmap.Width);
            var frame = Record(target, capture.Bitmap, scale);
            var jpeg = GameVision.EncodeJpeg(capture.Bitmap, new(0, 0, capture.Bitmap.Width, capture.Bitmap.Height),
                (int)Math.Round(capture.Bitmap.Width * scale), (int)Math.Round(capture.Bitmap.Height * scale), 60);
            return Reply(new { ok = true, window_id = target.WindowId, source_frame_id = frame.Id, image_scale = scale,
                width = (int)Math.Round(capture.Bitmap.Width * scale), height = (int)Math.Round(capture.Bitmap.Height * scale),
                origin = capture.Origin, bounds = new { capture.Rect.Left, capture.Rect.Top, capture.Rect.Right, capture.Rect.Bottom } }, jpeg);
        }
        }
        finally { focus?.Restore((uint)target.Pid); }
        }
        return bring_to_front
            ? await ActionCoordinator.RunAsync(target, true, Capture, ct).ConfigureAwait(false)
            : await Capture().ConfigureAwait(false);
    }

    [McpServerTool(Name = "game_click"), Description("Click a game/native surface. input_mode=background uses posted messages and never takes the real cursor; foreground uses SendInput. Image coordinates require source_frame_id.")]
    public Task<CallToolResult> Click(string app, int x, int y, string button = "left", int click_count = 1,
        string coord_space = "image", string? source_frame_id = null, string? input_mode = null, string? pace = null,
        bool then_screenshot = false, CancellationToken ct = default)
        => Act(app, input_mode, pace, then_screenshot, async (target, mode) =>
        {
            if (ToolValidation.ValidateClick(button, click_count) is { } error) throw new ArgumentException(error);
            var point = Point(target, x, y, coord_space, source_frame_id);
            var mouse = Enum.Parse<BackgroundInput.MouseButton>(button, true);
            if (!mode.RealCursor)
            {
                if (settings.ShowVirtualCursor) VirtualCursorOverlay.Instance.MoveTo(point.X, point.Y, true);
                return await background.ClickAtAsync(target.Hwnd, point.X, point.Y, mouse, click_count, ct);
            }
            return mode.Humanized
                ? await InteractiveInput.HumanClickAsync(target.Hwnd, point.X, point.Y, null, mouse, click_count, HumanizedOptions.From(settings), ct)
                : await InteractiveInput.ClickAsync(target.Hwnd, point.X, point.Y, mouse, click_count, ct);
        }, ct);

    [McpServerTool(Name = "game_move"), Description("Move the background virtual pointer or the real cursor. Relative dx/dy mouse-look requires explicit foreground input.")]
    public Task<CallToolResult> Move(string app, int x = 0, int y = 0, int? dx = null, int? dy = null,
        string coord_space = "image", string? source_frame_id = null, string? input_mode = null, string? pace = null,
        bool then_screenshot = false, CancellationToken ct = default)
        => Act(app, input_mode, pace, then_screenshot, async (target, mode) =>
        {
            bool relative = dx != null || dy != null;
            if (relative && !mode.RealCursor) throw new ArgumentException("Relative mouse-look needs input_mode=foreground; background mode does not move the system cursor.");
            var point = relative ? (X: dx ?? 0, Y: dy ?? 0) : Point(target, x, y, coord_space, source_frame_id);
            if (mode.RealCursor) return await InteractiveInput.GameMoveAsync(target.Hwnd, point.X, point.Y, relative,
                mode.Humanized ? HumanizedOptions.From(settings) : null, ct);
            if (settings.ShowVirtualCursor) VirtualCursorOverlay.Instance.MoveTo(point.X, point.Y);
            var client = new NativeMethods.POINT { X = point.X, Y = point.Y };
            NativeMethods.ScreenToClient(target.Hwnd, ref client);
            bool sent = NativeMethods.PostMessageW(target.Hwnd, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((client.Y << 16) | (client.X & 0xffff)));
            return new(sent, "postmessage_move", "Background pointer moved; system cursor unchanged", sent);
        }, ct);

    [McpServerTool(Name = "game_drag"), Description("Drag on a game/native surface using the selected background or foreground input mode.")]
    public Task<CallToolResult> Drag(string app, int from_x, int from_y, int to_x, int to_y,
        string coord_space = "image", string? source_frame_id = null, string? input_mode = null, string? pace = null,
        bool then_screenshot = false, CancellationToken ct = default)
        => Act(app, input_mode, pace, then_screenshot, async (target, mode) =>
        {
            var from = Point(target, from_x, from_y, coord_space, source_frame_id);
            var to = Point(target, to_x, to_y, coord_space, source_frame_id);
            if (!mode.RealCursor) return await background.DragAsync(target.Hwnd, from.X, from.Y, to.X, to.Y, ct);
            return mode.Humanized
                ? await InteractiveInput.HumanDragAsync(target.Hwnd, from.X, from.Y, to.X, to.Y, HumanizedOptions.From(settings), ct)
                : await InteractiveInput.DragAsync(target.Hwnd, from.X, from.Y, to.X, to.Y, ct);
        }, ct);

    [McpServerTool(Name = "game_scroll"), Description("Scroll a game/native surface without a UIA tree, using the selected background or foreground mode.")]
    public Task<CallToolResult> Scroll(string app, int x, int y, string direction = "down", double pages = 1,
        string coord_space = "image", string? source_frame_id = null, string? input_mode = null, string? pace = null,
        bool then_screenshot = false, CancellationToken ct = default)
        => Act(app, input_mode, pace, then_screenshot, async (target, mode) =>
        {
            if (direction is not ("up" or "down" or "left" or "right") || ToolValidation.ValidatePositiveFinite(pages, 100, "pages") != null)
                throw new ArgumentException("Invalid direction or pages (positive finite, at most 100)");
            var point = Point(target, x, y, coord_space, source_frame_id);
            if (!mode.RealCursor) return await background.ScrollAsync(target.Hwnd, null,
                new ElementInfo { Id = "game-point", ScreenX = point.X, ScreenY = point.Y, Width = 1, Height = 1 }, direction, pages, ct);
            return mode.Humanized
                ? await InteractiveInput.HumanScrollAsync(target.Hwnd, point.X, point.Y, 1, 1, direction, pages, HumanizedOptions.From(settings), ct)
                : await InteractiveInput.ScrollAsync(target.Hwnd, point.X, point.Y, direction, pages, ct);
        }, ct);

    [McpServerTool(Name = "game_key"), Description("Tap, hold (0-60000ms), or repeat a simultaneous key/chord; foreground also supports lmb/rmb/mmb. Attempts to release keys before returning, including on interruption; rejected cleanup is reported. No permanent controller ownership.")]
    public Task<CallToolResult> Key(string app, string key, string action = "tap", int duration_ms = 100, int interval_ms = 100,
        string? input_mode = null, string? pace = null, bool then_screenshot = false, CancellationToken ct = default)
        => Act(app, input_mode, pace, then_screenshot, async (target, mode) =>
        {
            var keys = InteractiveInput.ParseKeySet(key);
            if (action is not ("tap" or "hold" or "repeat") || duration_ms is < 0 or > 60000 || interval_ms is < 15 or > 10000)
                throw new ArgumentException("Use tap, hold or repeat with duration_ms 0-60000 and interval_ms 15-10000");
            if (mode.RealCursor) return await InteractiveInput.GameKeyAsync(target.Hwnd, keys, action, duration_ms, interval_ms, mode.Humanized, ct);
            if (keys.Any(k => k is 1 or 2 or 4)) throw new ArgumentException("Background mouse buttons use game_click; mouse-button chords need input_mode=foreground.");
            async Task Tap(int duration)
            {
                var pressed = new List<int>();
                try
                {
                    foreach (int vk in keys)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!NativeMethods.PostMessageW(target.Hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero))
                            throw new InvalidOperationException("Target rejected a posted key");
                        pressed.Add(vk);
                    }
                    await Task.Delay(duration, ct).ConfigureAwait(false);
                }
                finally { foreach (int vk in pressed.AsEnumerable().Reverse()) NativeMethods.PostMessageW(target.Hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, IntPtr.Zero); }
            }
            if (action != "repeat") await Tap(action == "hold" ? duration_ms : InputPace.Ms(40));
            else
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < duration_ms)
                {
                    await Tap(InputPace.Ms(35));
                    await Task.Delay(Math.Max(0, InputPace.Ms(interval_ms) - InputPace.Ms(35)), ct);
                }
            }
            return new(true, "postmessage_game_key", "Posted input; raw-input games require input_mode=foreground", true);
        }, ct);

    [McpServerTool(Name = "game_ocr"), Description("Read Windows OCR text from a captured native/game window. No JEV or model download. Missing OCR language returns an explicit unsupported result.")]
    public async Task<CallToolResult> Ocr(string app, CancellationToken ct = default)
    {
        var target = await uia.ResolveAppAsync(app, ct);
        if (GameVision.Capture(target.Hwnd) is not { } capture) return Fail("Window capture failed");
        using (capture.Bitmap)
        {
            var (lines, unsupported) = await GameVision.ReadTextAsync(capture.Bitmap, new(0, 0, capture.Bitmap.Width, capture.Bitmap.Height), 1, ct);
            return Reply(new { ok = unsupported == null, lines, unsupported, coord_space = "window" }, error: unsupported != null);
        }
    }

    [McpServerTool(Name = "game_bars"), Description("Measure calibrated HUD bars from bars_json [{name,x,y,width,height,color:#RRGGBB}]. Coordinates are window pixels. Unknown/miscalibrated fill is not reported as zero.")]
    public async Task<CallToolResult> Bars(string app, string bars_json, CancellationToken ct = default)
    {
        BarSpec[] bars;
        try { bars = JsonSerializer.Deserialize<BarSpec[]>(bars_json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
        catch (JsonException ex) { return Fail(ex.Message); }
        if (bars.Length is < 1 or > 20 || bars.Any(b => b == null || b.Width < 1 || b.Height < 1)) return Fail("Provide 1-20 bars with positive width and height");
        var target = await uia.ResolveAppAsync(app, ct);
        if (GameVision.Capture(target.Hwnd) is not { } capture) return Fail("Window capture failed");
        using (capture.Bitmap)
        {
            var readings = bars.Select(b => GameVision.PhysicalRegion(capture.Bitmap, 1, b.X, b.Y, b.Width, b.Height) is { } region
                ? GameVision.MeasureBar(capture.Bitmap, region, b) : new BarReading(b.Name, null, b.Color, "outside window"));
            return Reply(new { ok = true, bars = readings.ToArray() });
        }
    }

    [McpServerTool(Name = "game_wait"), Description("Wait without repeating input: frame_changes, frame_still, text_appears, text_gone or foreground. Returns the final screenshot even on timeout.")]
    public async Task<CallToolResult> Wait(string app, string condition = "frame_changes", string? text = null,
        double timeout_s = 10, int stable_ms = 500, int poll_ms = 100, CancellationToken ct = default)
    {
        if (ToolValidation.ValidatePositiveFinite(timeout_s, 300, "timeout_s") != null || stable_ms is < 0 or > 60000 || poll_ms is < 50 or > 2000)
            return Fail("Invalid timeout, stable_ms or poll_ms");
        if (condition is not ("frame_changes" or "frame_still" or "text_appears" or "text_gone" or "foreground")) return Fail("Unknown wait condition");
        if (condition.StartsWith("text_") && string.IsNullOrEmpty(text)) return Fail("Text condition requires nonempty text");
        var target = await uia.ResolveAppAsync(app, ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long stableSince = 0;
        byte[]? initial = null, previous = null, last = null;
        bool found = false;
        string? unsupported = null;
        while (clock.Elapsed.TotalSeconds < timeout_s)
        {
            ct.ThrowIfCancellationRequested();
            if (GameVision.Capture(target.Hwnd) is { } capture)
            {
                using (capture.Bitmap)
                {
                    var signature = GameVision.Signature(capture.Bitmap);
                    initial ??= signature;
                    double scale = Math.Min(1, GameVision.DefaultMaxWidth / (double)capture.Bitmap.Width);
                    last = GameVision.EncodeJpeg(capture.Bitmap, new(0, 0, capture.Bitmap.Width, capture.Bitmap.Height),
                        (int)Math.Round(capture.Bitmap.Width * scale), (int)Math.Round(capture.Bitmap.Height * scale), 60);
                    if (previous != null && GameVision.ChangedFraction(previous, signature) >= 0.002) stableSince = clock.ElapsedMilliseconds;
                    found = condition switch
                    {
                        "frame_changes" => GameVision.ChangedFraction(initial, signature) >= 0.015,
                        "frame_still" => clock.ElapsedMilliseconds - stableSince >= stable_ms,
                        "foreground" => InteractiveInput.RootWindow(NativeMethods.GetForegroundWindow()) == target.Hwnd,
                        _ => false
                    };
                    if (condition.StartsWith("text_"))
                    {
                        var ocr = await GameVision.ReadTextAsync(capture.Bitmap, new(0, 0, capture.Bitmap.Width, capture.Bitmap.Height), 1, ct);
                        unsupported = ocr.Unsupported;
                        if (unsupported != null) break;
                        bool present = ocr.Lines.Any(line => line.Text.Contains(text!, StringComparison.OrdinalIgnoreCase));
                        found = condition == "text_appears" ? present : !present;
                    }
                    previous = signature;
                    if (found) break;
                }
            }
            await Task.Delay(poll_ms, ct);
        }
        return Reply(new { ok = found, found, condition, unsupported, timed_out = !found && unsupported == null,
            capture_available = last != null }, last, unsupported != null);
    }
}
