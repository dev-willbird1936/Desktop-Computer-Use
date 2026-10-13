// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Capture;
using ShadowUse.Config;
using ShadowUse.Native;
using ShadowUse.Overlay;
using ShadowUse.Safety;

namespace ShadowUse.Tools;

/// <summary>
/// Windows computer control with an explicit background or foreground input mode.
/// Background input uses UIA/messages; foreground input uses the system cursor.
/// Actions coordinate per window, and foreground input shares one cursor lock.
/// No permanent agent ownership is required. A cosmetic virtual cursor shows
/// what you're doing.
///
/// Workflow: list_apps → get_app_state (snapshot with element ids) → act with
/// element ids → re-snapshot.
/// </summary>
[McpServerToolType]
public sealed class ComputerUseTools
{
    private readonly UiaService _uia;
    private readonly BackgroundInput _input;
    private readonly ShadowSettings _settings;
    private async Task<object> RunActionAsync(string app, string? inputMode, string? pace,
        Func<AppTarget, InputMode, Task<object>> action, CancellationToken ct)
    {
        InputMode mode;
        try { mode = InputMode.Resolve(_settings, inputMode); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        if (!InputPace.IsValidPace(pace)) return Error("pace must be fast, human or normal");
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        using var timing = InputPace.Scope(pace ?? (_settings.FastInput ? "fast" : "normal"));
        return await ActionCoordinator.RunAsync(target, mode.RealCursor, () => action(target, mode), ct).ConfigureAwait(false);
    }

    public ComputerUseTools(UiaService uia, BackgroundInput input, ShadowSettings settings)
    {
        _uia = uia;
        _input = input;
        _settings = settings;
    }

    private string? DesktopError() => _settings.EnableDesktopCheck ? Guard.CheckInteractiveDesktop() : null;

    private void Ghost(int x, int y, bool pulse = false)
    {
        if (_settings.ShowVirtualCursor)
            VirtualCursorOverlay.Instance.MoveTo(x, y, pulse);
    }

    [McpServerTool(Name = "list_apps"), Description("List running apps that have a visible main window. Returns name, pid, title.")]
    public async Task<object> ListApps(CancellationToken ct = default)
    {
        var apps = await _uia.ListAppsAsync(ct).ConfigureAwait(false);
        return new
        {
            apps = apps.Select(a => new
            {
                name = a.ProcessName,
                pid = a.Pid,
                window_id = a.WindowId,
                hwnd = $"0x{a.Hwnd.ToInt64():X}",
                title = a.Title,
            }),
            hint = "Use window_id when one process owns multiple windows. Next: get_app_state(app)."
        };
    }

    [McpServerTool(Name = "get_app_state"), Description("Snapshot accessibility elements with instance-specific ids retained across recent observations. For games with no tree use game_screenshot. Optionally includes an annotated background-window screenshot.")]
    public async Task<object> GetAppState(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("Include annotated screenshot image")] bool include_screenshot = true,
        [Description("Max elements in tree")] int max_elements = 300,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidateMaxElements(max_elements);
        if (validationError != null) return Error(validationError);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        Snapshot snap;
        try { snap = await _uia.SnapshotAsync(target, maxNodes: Math.Max(100, max_elements * 3), ct: ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return Error($"Accessibility snapshot failed: {ex.Message}. Try a fresh observation or game_screenshot for a native surface.");
        }
        // Truncate for THIS response only — never mutate the cached snapshot's own element
        // list; click/scroll/set_value re-resolve element ids against that same cached
        // object later and would lose access to anything trimmed here.
        var limited = snap.Elements.Count > max_elements ? snap.Elements.Take(max_elements).ToList() : snap.Elements;
        byte[]? img = include_screenshot ? ScreenshotService.CaptureWindow(target.Hwnd, limited) : null;
        return new
        {
            revision = snap.Revision,
            app = snap.App,
            pid = snap.Pid,
            title = snap.Title,
            bounds = new { snap.Bounds.Left, snap.Bounds.Top, snap.Bounds.Right, snap.Bounds.Bottom },
            foreground_free = true,
            elements = limited.Select(e => new
            {
                id = e.Id,
                type = e.ControlType,
                name = e.Name,
                automation_id = e.AutomationId,
                x = e.X,
                y = e.Y,
                w = e.Width,
                h = e.Height,
                actions = e.Actions,
                value = string.IsNullOrEmpty(e.Value) ? null : e.Value,
            }),
            screenshot_png_base64 = img != null ? Convert.ToBase64String(img) : null,
            hint = "Act via click/type_text/etc. with element ids. If elements shift, re-snapshot."
        };
    }

    [McpServerTool(Name = "click"), Description("Click using input_mode=background (UIA/messages) or foreground/humanized (system cursor). Defaults to UseRealCursor setting. Prefer element_id; current bounds are re-resolved. Foreground dispatch does not prove a game effect.")]
    public async Task<object> Click(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("Element id from get_app_state, e.g. 'e12'")] string? element_id = null,
        [Description("Screen X (if no element_id)")] int? x = null,
        [Description("Screen Y (if no element_id)")] int? y = null,
        [Description("left | right | middle")] string button = "left",
        [Description("Click count (2 = double)")] int click_count = 1,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        var validationError = ToolValidation.ValidateClick(button, click_count);
        if (validationError != null) return Error(validationError);
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
                if (mode.RealCursor && element_id == null)
                {
                    if (x == null || y == null) return Error("Provide element_id or both x and y.");
                    if (_settings.EnableBoundsGuard && _uia.GetSnapshot(target) is { } observed
                        && Guard.CheckBounds(observed, target.Hwnd) is { } boundsError)
                        return Error(boundsError);
                    if (!NativeMethods.GetWindowRect(target.Hwnd, out var liveBounds)
                        || x < liveBounds.Left || x >= liveBounds.Right || y < liveBounds.Top || y >= liveBounds.Bottom)
                        return Error("Foreground click coordinates must be inside the selected window; reobserve its bounds.");
                    var buttonKind = Enum.Parse<BackgroundInput.MouseButton>(button, true);
                    var dispatched = mode.Humanized
                        ? await InteractiveInput.HumanClickAsync(target.Hwnd, x.Value, y.Value, null, buttonKind, click_count, HumanizedOptions.From(_settings), ct)
                        : await InteractiveInput.ClickAsync(target.Hwnd, x.Value, y.Value, buttonKind, click_count, ct);
                    return new { ok = dispatched.Success, method = dispatched.Method, input_mode = mode.Name,
                        input_sent = dispatched.InputSent, effect = "unverified", detail = dispatched.Detail,
                        interruption_reason = dispatched.InterruptionReason };
                }
                // Element ids belong to the exact HWND that produced their snapshot.
                // Chromium can temporarily make a popup its process MainWindowHandle,
                // so resolving the PID again is not a reliable element-cache key.
                var cachedSnap = element_id != null
                    ? _uia.GetSnapshotForElement(target, element_id)
                    : _uia.GetSnapshot(target);
                ElementInfo? info = null;
                Interop.UIAutomationClient.IUIAutomationElement? element = null;
                ElementFrame? currentFrame = null;
                Snapshot snap;
                IntPtr actionHwnd = target.Hwnd;
                int? actionX = x;
                int? actionY = y;

                if (element_id != null)
                {
                    // A cache miss here can never be recovered by taking a fresh snapshot —
                    // element ids are minted from a monotonic counter, so a brand-new snapshot
                    // can never contain an id from an older one. Fail clearly instead of
                    // silently walking the tree just to report a confusing "unknown id".
                    if (cachedSnap == null)
                        return Error("No cached snapshot for this app. Call get_app_state first.");
                    snap = cachedSnap;
                    info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
                    if (info == null)
                        return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");
                    actionHwnd = snap.Hwnd;
                    if (!NativeMethods.IsWindow(actionHwnd))
                        return Error($"Element '{element_id}' belonged to a window that no longer exists. Re-run get_app_state.");
                    element = await _uia.ResolveElementAsync(actionHwnd, info, ct).ConfigureAwait(false);
                    if (element == null)
                        return Error($"Element '{element_id}' no longer exists at its last known position. Re-run get_app_state.");
                    currentFrame = await _uia.GetElementFrameAsync(element, ct, actionHwnd).ConfigureAwait(false);
                    if (currentFrame == null)
                        return Error($"Element '{element_id}' no longer has usable screen bounds. Re-run get_app_state.");
                    actionX = currentFrame.Value.CenterX;
                    actionY = currentFrame.Value.CenterY;
                }
                else
                {
                    if (x == null || y == null) return Error("Provide element_id or both x and y.");
                    snap = cachedSnap ?? await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
                    if (_settings.EnableBoundsGuard)
                    {
                        err = Guard.CheckBounds(snap, target.Hwnd);
                        if (err != null) return Error(err);
                    }
                    if (!NativeMethods.GetWindowRect(target.Hwnd, out var currentBounds))
                        return Error("Target window no longer exists; call get_app_state to re-resolve.");
                    var rebased = WindowCoordinateMapper.RebaseFromSnapshot(
                        snap.Bounds,
                        currentBounds,
                        x.Value,
                        y.Value);
                    actionX = rebased.X;
                    actionY = rebased.Y;
                }

                var btn = button.ToLowerInvariant() switch
                {
                    "right" => BackgroundInput.MouseButton.Right,
                    "middle" => BackgroundInput.MouseButton.Middle,
                    _ => BackgroundInput.MouseButton.Left,
                };

                // Cosmetic virtual cursor — show the user where the action lands
                int vx = actionX ?? throw new InvalidOperationException("Click X coordinate was not resolved.");
                int vy = actionY ?? throw new InvalidOperationException("Click Y coordinate was not resolved.");
                if (!mode.RealCursor) Ghost(vx, vy, pulse: true);
                await Task.Delay(InputPace.Ms(140), ct).ConfigureAwait(false); // let the pulse be visible

                BackgroundInput.InputResult result = mode.RealCursor
                    ? mode.Humanized
                        ? await InteractiveInput.HumanClickAsync(actionHwnd, vx, vy, currentFrame, btn, click_count, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                        : await InteractiveInput.ClickAsync(actionHwnd, vx, vy, btn, click_count, ct).ConfigureAwait(false)
                    : element_id != null
                        ? await _input.ClickElementAsync(actionHwnd, element, info, btn, click_count, ct, currentFrame).ConfigureAwait(false)
                        : await _input.ClickAtAsync(actionHwnd, vx, vy, btn, click_count, ct).ConfigureAwait(false);
                if (mode.RealCursor)
                    return new { ok = result.Success, method = result.Method, input_mode = mode.Name,
                        input_sent = result.InputSent, effect = "unverified", detail = result.Detail,
                        interruption_reason = result.InterruptionReason };

                if (!result.Success) return Error($"Click failed: {result.Detail}");

                await Task.Delay(InputPace.Ms(_settings.PostActionDelayMs), ct).ConfigureAwait(false);
                Snapshot? after = null;
                Exception? refreshError = null;
                bool rootDisappeared = !NativeMethods.IsWindow(actionHwnd);

                if (!rootDisappeared)
                {
                    try
                    {
                        var originalRoot = new AppTarget
                        {
                            Pid = target.Pid,
                            ProcessName = target.ProcessName,
                            Hwnd = actionHwnd,
                            Title = snap.Title,
                        };
                        after = await _uia.SnapshotAsync(originalRoot, ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                    {
                        // A semantic Invoke can synchronously destroy a Chromium popup.
                        // Re-resolve below; never turn that observed effect into an MCP error.
                        refreshError = ex;
                        rootDisappeared = !NativeMethods.IsWindow(actionHwnd);
                    }
                }

                if (after == null)
                {
                    try
                    {
                        var currentTarget = await _uia.ResolveAppAsync(target.Pid.ToString(), ct).ConfigureAwait(false);
                        rootDisappeared |= currentTarget.Hwnd != actionHwnd;
                        after = await _uia.SnapshotAsync(currentTarget, ct: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                    {
                        refreshError ??= ex;
                    }
                }

                bool elementDisappeared = info != null && (rootDisappeared ||
                    (after != null && !ContainsRuntimeId(after, info.RuntimeId)));
                bool stateChanged = after != null && HasActionDelta(snap, after);
                bool expansionObserved = result.Method == "uia_expand"
                    && !rootDisappeared
                    && !elementDisappeared
                    && stateChanged
                    && (info?.ControlType != "MenuItem" || (after != null && HasAddedMenuElement(snap, after)));
                string effect = result.Method == "uia_expand"
                    ? expansionObserved
                        ? info?.ControlType == "MenuItem" ? "submenu_expanded" : "expanded"
                        : "none"
                    : rootDisappeared ? "transient_root_disappeared"
                    : elementDisappeared ? "element_disappeared"
                    : stateChanged ? "uia_state_changed"
                    : "none";

                if (effect == "none")
                {
                    return new
                    {
                        ok = false,
                        method = result.Method,
                        effect,
                        revision = after?.Revision,
                        changed_elements = after != null ? DeltaOf(snap, after) : null,
                        detail = refreshError != null
                            ? $"Click was dispatched, but the follow-up snapshot failed and no effect was observable: {refreshError.Message}"
                            : "Click was dispatched, but the element remained and no UI Automation state change was observable."
                    };
                }

                return new
                {
                    ok = true,
                    method = result.Method,
                    effect,
                    revision = after?.Revision,
                    changed_elements = after != null ? DeltaOf(snap, after) : null,
                    hint = "Snapshot refreshed. If the UI changed a lot, call get_app_state for full detail."
                };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "type_text"), Description("Type using background UIA/window messages or explicit foreground/humanized input. Background UIA text fallback may briefly activate some apps. Mode and pace can be selected per call.")]
    public async Task<object> TypeText(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("Text to enter")] string text,
        [Description("Allow UIA SetValue append (default: settings file, on unless disabled)")] bool? allow_uia_fallback = null,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
                var result = mode.RealCursor
                    ? mode.Humanized
                        ? await InteractiveInput.HumanTypeTextAsync(target.Hwnd, text, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                        : await InteractiveInput.TypeTextAsync(target.Hwnd, text, ct).ConfigureAwait(false)
                    : await _input.TypeTextAsync(target.Hwnd, text, ct, allow_uia_fallback ?? _settings.AllowUiaTextFallback).ConfigureAwait(false);
                if (mode.RealCursor) return new { ok = result.Success, method = result.Method, input_mode = mode.Name,
                    input_sent = result.InputSent, detail = result.Detail, interruption_reason = result.InterruptionReason };
                await Task.Delay(InputPace.Ms(_settings.PostActionDelayMs), ct).ConfigureAwait(false);
                var after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method, revision = after.Revision };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "press_key"), Description("Press a key/chord using background messages or explicit foreground/humanized input. Raw-input targets generally require foreground. Use game_key for bounded holds/repeats.")]
    public async Task<object> PressKey(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("Key: name, char, or modifier+key (ctrl/alt/shift/win)")] string key,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
                var result = mode.RealCursor
                    ? mode.Humanized
                        ? await InteractiveInput.HumanPressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false)
                        : await InteractiveInput.PressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false)
                    : await _input.PressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false);
                return new
                {
                    ok = result.Success,
                    method = result.Method,
                    detail = string.IsNullOrEmpty(result.Detail) ? null : result.Detail
                };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scroll"), Description("Scroll using background UIA/messages or explicit foreground/humanized wheel input.")]
    public async Task<object> Scroll(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("up | down | left | right")] string direction,
        [Description("Element id to scroll within (optional)")] string? element_id = null,
        [Description("Pages to scroll")] double pages = 1.0,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        var validationError = ToolValidation.ValidatePositiveFinite(pages, 100, nameof(pages));
        if (validationError != null) return Error(validationError);
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
                var snap = element_id != null
                    ? _uia.GetSnapshotForElement(target, element_id)
                    : _uia.GetSnapshot(target);
                ElementInfo? info = null;
                Interop.UIAutomationClient.IUIAutomationElement? element = null;
                ElementFrame? currentFrame = null;
                IntPtr actionHwnd = target.Hwnd;
                if (element_id != null)
                {
                    try { info = ResolveRequiredElement(snap, element_id); }
                    catch (InvalidOperationException ex) { return Error(ex.Message); }
                    actionHwnd = snap!.Hwnd;
                    if (!NativeMethods.IsWindow(actionHwnd))
                        return Error($"Element '{element_id}' belonged to a window that no longer exists. Re-run get_app_state.");
                    element = await _uia.ResolveElementAsync(actionHwnd, info, ct).ConfigureAwait(false);
                    if (element == null)
                        return Error($"Element '{element_id}' no longer exists. Re-run get_app_state.");
                    currentFrame = await _uia.GetElementFrameAsync(element, ct, actionHwnd).ConfigureAwait(false);
                    if (currentFrame == null)
                        return Error($"Element '{element_id}' no longer has usable screen bounds. Re-run get_app_state.");
                }
                if (direction is not ("up" or "down" or "left" or "right")) return Error("Invalid scroll direction");
                NativeMethods.GetWindowRect(actionHwnd, out var bounds);
                int left = currentFrame?.ScreenX ?? bounds.Left, top = currentFrame?.ScreenY ?? bounds.Top;
                int width = currentFrame?.Width ?? bounds.Width, height = currentFrame?.Height ?? bounds.Height;
                var result = mode.RealCursor
                    ? mode.Humanized
                        ? await InteractiveInput.HumanScrollAsync(actionHwnd, left, top, width, height, direction, pages, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                        : await InteractiveInput.ScrollAsync(actionHwnd, left + width / 2, top + height / 2, direction, pages, ct).ConfigureAwait(false)
                    : await _input.ScrollAsync(actionHwnd, element, info, direction, pages, ct, currentFrame).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method, detail = result.Detail };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "drag"), Description("Drag between screen points in background messages or explicit foreground/humanized mode. Background coordinates follow window movement.")]
    public async Task<object> Drag(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("From screen X")] int from_x,
        [Description("From screen Y")] int from_y,
        [Description("To screen X")] int to_x,
        [Description("To screen Y")] int to_y,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var snap = _uia.GetSnapshot(target);
            if (snap != null && _settings.EnableBoundsGuard)
            {
                err = Guard.CheckBounds(snap, target.Hwnd);
                if (err != null) return Error(err);
            }
            int actionFromX = from_x;
            int actionFromY = from_y;
            int actionToX = to_x;
            int actionToY = to_y;
            if (snap != null)
            {
                if (!NativeMethods.GetWindowRect(target.Hwnd, out var currentBounds))
                    return Error("Target window no longer exists; call get_app_state to re-resolve.");
                var rebasedFrom = WindowCoordinateMapper.RebaseFromSnapshot(
                    snap.Bounds,
                    currentBounds,
                    from_x,
                    from_y);
                var rebasedTo = WindowCoordinateMapper.RebaseFromSnapshot(
                    snap.Bounds,
                    currentBounds,
                    to_x,
                    to_y);
                actionFromX = rebasedFrom.X;
                actionFromY = rebasedFrom.Y;
                actionToX = rebasedTo.X;
                actionToY = rebasedTo.Y;
            }
            var focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
            try
            {
                if (!mode.RealCursor) Ghost(actionFromX, actionFromY);
                await Task.Delay(InputPace.Ms(_settings.PostActionDelayMs), ct).ConfigureAwait(false);
                if (!mode.RealCursor) Ghost(actionToX, actionToY);
                var result = mode.RealCursor
                    ? mode.Humanized
                        ? await InteractiveInput.HumanDragAsync(target.Hwnd, actionFromX, actionFromY, actionToX, actionToY, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                        : await InteractiveInput.DragAsync(target.Hwnd, actionFromX, actionFromY, actionToX, actionToY, ct).ConfigureAwait(false)
                    : await _input.DragAsync(target.Hwnd, actionFromX, actionFromY, actionToX, actionToY, ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method };
            }
            finally { focusGuard?.Restore((uint)target.Pid); }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "set_value"), Description("Set an element's value directly via UIA (edits, sliders, toggles with value 'true'/'false').")]
    public async Task<object> SetValue(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("Element id from get_app_state")] string element_id,
        [Description("Value to set")] string value,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized (default: UseRealCursor setting)")] string? input_mode = null,
        [Description("fast | human | normal (default: FastInput setting)")] string? pace = null)
    {
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var snap = _uia.GetSnapshotForElement(target, element_id);
            if (snap == null)
                return Error("No cached snapshot for this app (or the window changed since it was taken). Call get_app_state first.");
            var info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
            if (info == null)
                return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");
            var element = await _uia.ResolveElementAsync(target.Hwnd, info, ct).ConfigureAwait(false);
            if (element == null)
                return Error($"Element '{element_id}' no longer exists. Re-run get_app_state.");
            var focusGuard = _settings.EnableFocusGuard && !(mode.RealCursor && _settings.GameMode) ? FocusGuard.Capture() : null;
            try
            {
                var result = await _input.SetValueAsync(element, value, ct, target.Hwnd).ConfigureAwait(false);
                if (!result.Success) return Error(result.Detail);
                return new { ok = true, method = result.Method };
            }
            finally { focusGuard?.Restore((uint)target.Pid); }
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "wait_for"), Description("Server-side wait: polls until text appears in the app (or an element with that name exists), or the window title changes. Avoids snapshot round-trips.")]
    public async Task<object> WaitFor(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("text_exists | element_exists | window_active")] string condition,
        [Description("Text/element name to wait for")] string? text = null,
        [Description("Timeout seconds")] double timeout_s = 10,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidatePositiveFinite(timeout_s, 300, nameof(timeout_s));
        if (validationError != null) return Error(validationError);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeout_s);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            switch (condition)
            {
                case "text_exists":
                case "element_exists":
                    if (text == null) return Error("text is required for this condition");
                    var el = await _uia.FindByTextAsync(target.Hwnd, text, ct).ConfigureAwait(false);
                    if (el != null) return new { ok = true, found = true, condition };
                    break;
                case "window_active":
                    if (Native.NativeMethods.GetForegroundWindow() == target.Hwnd)
                        return new { ok = true, found = true, condition };
                    break;
                default:
                    return Error($"Unknown condition '{condition}'. Use text_exists | element_exists | window_active.");
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return new { ok = false, found = false, condition, timeout_s };
    }

    [McpServerTool(Name = "execute_sequence"), Description("Run an atomic same-window batch; other windows may continue in background. Steps: [{\"tool\":\"click\",\"args\":{...}}, ...]. Supports click, type_text, press_key, scroll, drag, set_value, wait_for. Stops on first error unless stop_on_error=false.")]
    public async Task<object> ExecuteSequence(
        [Description("App name, title substring, PID, or window_id")] string app,
        [Description("JSON array of steps: [{\"tool\":\"click\",\"args\":{\"element_id\":\"e3\"}}, ...]")] string steps_json,
        [Description("Stop on first failed step")] bool stop_on_error = true,
        CancellationToken ct = default,
        [Description("background | foreground | foreground_plain | humanized for the entire reserved sequence")] string? input_mode = null,
        [Description("fast | human | normal")] string? pace = null)
    {
        using var doc = JsonDocument.Parse(steps_json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Error("steps_json must be a JSON array");
        return await RunActionAsync(app, input_mode, pace, async (target, mode) =>
        {
        app = target.WindowId;
        foreach (var step in doc.RootElement.EnumerateArray())
            if (step.TryGetProperty("args", out var args) && Str(args, "input_mode") is { } stepMode && InputMode.Resolve(_settings, stepMode) != mode)
                return Error("Set input_mode on execute_sequence; steps cannot change its reserved input mode.");
        var results = new List<object>();
        int i = 0;
        foreach (var step in doc.RootElement.EnumerateArray())
        {
            i++;
            ct.ThrowIfCancellationRequested();
            string tool = step.GetProperty("tool").GetString() ?? "";
            var args = step.TryGetProperty("args", out var a) ? a : default;
            object r;
            try
            {
                r = tool switch
                {
                    "click" => await Click(app,
                        Str(args, "element_id"), Int(args, "x"), Int(args, "y"),
                        Str(args, "button") ?? "left", Int(args, "click_count") ?? 1, ct, mode.DispatchName, pace).ConfigureAwait(false),
                    "type_text" => await TypeText(app, Str(args, "text") ?? "", ct: ct, input_mode: mode.DispatchName, pace: pace).ConfigureAwait(false),
                    "press_key" => await PressKey(app, Str(args, "key") ?? "", ct, mode.DispatchName, pace).ConfigureAwait(false),
                    "scroll" => await Scroll(app, Str(args, "direction") ?? "down", Str(args, "element_id"),
                        Dbl(args, "pages") ?? 1.0, ct, mode.DispatchName, pace).ConfigureAwait(false),
                    "drag" => await Drag(app, Int(args, "from_x") ?? 0, Int(args, "from_y") ?? 0,
                        Int(args, "to_x") ?? 0, Int(args, "to_y") ?? 0, ct, mode.DispatchName, pace).ConfigureAwait(false),
                    "set_value" => await SetValue(app, Str(args, "element_id") ?? "", Str(args, "value") ?? "", ct, mode.DispatchName, pace).ConfigureAwait(false),
                    "wait_for" => await WaitFor(app, Str(args, "condition") ?? "text_exists", Str(args, "text"),
                        Dbl(args, "timeout_s") ?? 10, ct).ConfigureAwait(false),
                    _ => Error($"Unknown tool '{tool}'")
                };
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, ct)) { r = Error(ex.Message); }
            results.Add(new { step = i, tool, result = r });
            if (HasFailed(r) && stop_on_error)
                return new { ok = false, stopped_at = i, results };
        }
        return new { ok = true, steps_run = i, results };
        }, ct).ConfigureAwait(false);
    }

    [McpServerTool(Name = "hide_cursor"), Description("Hide the cosmetic virtual cursor overlay.")]
    public object HideCursor()
    {
        VirtualCursorOverlay.Instance.Hide();
        return new { ok = true };
    }

    [McpServerTool(Name = "health_check"), Description("Diagnostics: desktop session, UIA availability, app count, overlay status.")]
    public async Task<object> HealthCheck(CancellationToken ct = default)
    {
        var err = Guard.CheckInteractiveDesktop();
        var apps = err == null ? await _uia.ListAppsAsync(ct).ConfigureAwait(false) : [];
        return new
        {
            ok = err == null,
            interactive_desktop = err == null,
            desktop_error = err,
            apps_with_windows = apps.Length,
            overlay = "available (layered, click-through, capture-excluded)",
            input = InputMode.Resolve(_settings).Name,
            use_real_cursor = _settings.UseRealCursor,
            game_mode = _settings.GameMode,
            humanized_input = _settings.HumanizedInput,
            fast_input = _settings.FastInput,
            coordination = "cross-process per-window actions; shared foreground cursor; atomic same-target sequences",
            snapshots = "64 observations per window; ids must originate in this server instance",
            capture = "PrintWindow; visible-screen capture only for the exact foreground game window"
        };
    }

    private static object Error(string message) => new { error = message };

    private static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static ElementInfo ResolveRequiredElement(Snapshot? snapshot, string elementId)
    {
        if (snapshot == null)
            throw new InvalidOperationException(
                "No cached snapshot for this app. Call get_app_state first.");
        return snapshot.Elements.FirstOrDefault(element => element.Id == elementId)
            ?? throw new InvalidOperationException(
                $"Unknown element id '{elementId}' in snapshot r{snapshot.Revision}. Call get_app_state for fresh ids.");
    }

    /// <summary>True if a tool result represents failure — either the {error:...} shape,
    /// or an explicit ok:false (e.g. wait_for's timeout return, which has no "error" field).</summary>
    private static bool HasFailed(object r)
    {
        var t = r.GetType();
        if (t.GetProperty("error") != null) return true;
        var okProp = t.GetProperty("ok");
        return okProp?.GetValue(r) is bool ok && !ok;
    }

    private static object DeltaOf(Snapshot? before, Snapshot after)
    {
        if (before == null) return new { note = "no prior snapshot" };
        // Session element ids are minted fresh on every snapshot (monotonic counter), so
        // comparing by Id would always report the whole tree as added+removed. RuntimeId is
        // UIA's own stable identity for "the same element across snapshots while it exists".
        static string Key(ElementInfo e) => string.Join(",", e.RuntimeId);
        var b = before.Elements.Select(Key).ToHashSet();
        var a = after.Elements.Select(Key).ToHashSet();
        return new { added = a.Except(b).Count(), removed = b.Except(a).Count(), total = after.Elements.Count };
    }

    private static bool ContainsRuntimeId(Snapshot snapshot, int[] runtimeId)
        => snapshot.Elements.Any(element => element.RuntimeId.SequenceEqual(runtimeId));

    private static bool HasAddedMenuElement(Snapshot before, Snapshot after)
    {
        static string Key(ElementInfo element) => string.Join(",", element.RuntimeId);
        var priorRuntimeIds = before.Elements.Select(Key).ToHashSet();
        return after.Elements.Any(element =>
            element.ControlType is "Menu" or "MenuBar" or "MenuItem"
            && !priorRuntimeIds.Contains(Key(element)));
    }

    private static bool HasActionDelta(Snapshot before, Snapshot after)
    {
        static string Key(ElementInfo element) => string.Join(",", element.RuntimeId);
        var beforeByRuntimeId = before.Elements.ToDictionary(Key);
        var afterByRuntimeId = after.Elements.ToDictionary(Key);
        if (!beforeByRuntimeId.Keys.ToHashSet().SetEquals(afterByRuntimeId.Keys)) return true;

        foreach (var (runtimeId, oldElement) in beforeByRuntimeId)
        {
            var newElement = afterByRuntimeId[runtimeId];
            if (oldElement.Name != newElement.Name
                || oldElement.Value != newElement.Value
                || oldElement.ControlType != newElement.ControlType
                || oldElement.AutomationId != newElement.AutomationId
                || !oldElement.Actions.SequenceEqual(newElement.Actions))
                return true;
        }
        return false;
    }

    private static string? Str(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
