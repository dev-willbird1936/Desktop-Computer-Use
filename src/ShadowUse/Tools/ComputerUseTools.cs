// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Capture;
using ShadowUse.Config;
using ShadowUse.Overlay;
using ShadowUse.Safety;

namespace ShadowUse.Tools;

/// <summary>
/// Background computer control. All actions are focus-free: they use UI Automation
/// patterns or direct window messages, so the real cursor never moves, the foreground
/// never changes, and the target app never counts itself as focused. The user can keep
/// working in other apps while you drive this one. A cosmetic virtual cursor shows
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
    private static readonly SemaphoreSlim _mutationLock = new(1, 1); // serialized mutations

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
            apps = apps.Select(a => new { name = a.Process.ProcessName, pid = a.Process.Id, title = a.Title }),
            hint = "Next: get_app_state(app) to snapshot one of these."
        };
    }

    [McpServerTool(Name = "get_app_state"), Description("Snapshot an app's accessibility tree: interactive elements with stable ids, their frames, and supported actions. Optionally includes an annotated screenshot (Set-of-Marks labels matching element ids). Works on background/occluded windows.")]
    public async Task<object> GetAppState(
        [Description("App name, substring of title, or pid")] string app,
        [Description("Include annotated screenshot image")] bool include_screenshot = true,
        [Description("Max elements in tree")] int max_elements = 300,
        CancellationToken ct = default)
    {
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        var snap = await _uia.SnapshotAsync(target, maxNodes: Math.Max(100, max_elements * 3), ct: ct).ConfigureAwait(false);
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
                id = e.Id, type = e.ControlType, name = e.Name, automation_id = e.AutomationId,
                x = e.X, y = e.Y, w = e.Width, h = e.Height, actions = e.Actions,
                value = string.IsNullOrEmpty(e.Value) ? null : e.Value,
            }),
            screenshot_png_base64 = img != null ? Convert.ToBase64String(img) : null,
            hint = "Act via click/type_text/etc. with element ids. If elements shift, re-snapshot."
        };
    }

    [McpServerTool(Name = "click"), Description("Focus-free click. Prefer element_id from the latest snapshot; or use screen x,y. Uses UIA Invoke/Toggle/Select patterns when available (left clicks), else posts window messages. Never moves the real cursor or changes focus.")]
    public async Task<object> Click(
        [Description("App name, substring of title, or pid")] string app,
        [Description("Element id from get_app_state, e.g. 'e12'")] string? element_id = null,
        [Description("Screen X (if no element_id)")] int? x = null,
        [Description("Screen Y (if no element_id)")] int? y = null,
        [Description("left | right | middle")] string button = "left",
        [Description("Click count (2 = double)")] int click_count = 1,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                var cachedSnap = _uia.GetSnapshot(target);
                ElementInfo? info = null;
                Interop.UIAutomationClient.IUIAutomationElement? element = null;
                Snapshot snap;

                if (element_id != null)
                {
                    // A cache miss here can never be recovered by taking a fresh snapshot —
                    // element ids are minted from a monotonic counter, so a brand-new snapshot
                    // can never contain an id from an older one. Fail clearly instead of
                    // silently walking the tree just to report a confusing "unknown id".
                    if (cachedSnap == null)
                        return Error("No cached snapshot for this app (or the window changed since it was taken). Call get_app_state first.");
                    snap = cachedSnap;
                    info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
                    if (info == null)
                        return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");
                    element = await _uia.ResolveElementAsync(target.Hwnd, info, ct).ConfigureAwait(false);
                    if (element == null)
                        return Error($"Element '{element_id}' no longer exists at its last known position. Re-run get_app_state.");
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
                }

                var btn = button.ToLowerInvariant() switch
                {
                    "right" => BackgroundInput.MouseButton.Right,
                    "middle" => BackgroundInput.MouseButton.Middle,
                    _ => BackgroundInput.MouseButton.Left,
                };

                // Cosmetic virtual cursor — show the user where the action lands
                int vx = x ?? info!.ScreenX + info.Width / 2;
                int vy = y ?? info!.ScreenY + info.Height / 2;
                Ghost(vx, vy, pulse: true);
                await Task.Delay(140, ct).ConfigureAwait(false); // let the pulse be visible

                BackgroundInput.InputResult result = element_id != null
                    ? await _input.ClickElementAsync(target.Hwnd, element, info, btn, click_count, ct).ConfigureAwait(false)
                    : await _input.ClickAtAsync(target.Hwnd, x!.Value, y!.Value, btn, click_count, ct).ConfigureAwait(false);

                if (!result.Success) return Error($"Click failed: {result.Detail}");

                await Task.Delay(_settings.PostActionDelayMs, ct).ConfigureAwait(false);
                var after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
                return new
                {
                    ok = true, method = result.Method, revision = after.Revision,
                    changed_elements = DeltaOf(snap, after),
                    hint = "Snapshot refreshed. If the UI changed a lot, call get_app_state for full detail."
                };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "type_text"), Description("Focus-free text entry into an app. Tries the edit control's EM_REPLACESEL (no focus needed), then WM_CHAR stream. Never steals keyboard focus; the user can keep typing elsewhere.")]
    public async Task<object> TypeText(
        [Description("App name, substring of title, or pid")] string app,
        [Description("Text to enter")] string text,
        [Description("Allow UIA SetValue append (default: settings file, on unless disabled)")] bool? allow_uia_fallback = null,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                var result = await _input.TypeTextAsync(target.Hwnd, text, ct, allow_uia_fallback ?? _settings.AllowUiaTextFallback).ConfigureAwait(false);
                await Task.Delay(_settings.PostActionDelayMs, ct).ConfigureAwait(false);
                var after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method, revision = after.Revision };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "press_key"), Description("Focus-free key press (e.g. 'Return', 'ctrl+s', 'F5', 'Tab') posted to the app's window. Does not move keyboard focus.")]
    public async Task<object> PressKey(
        [Description("App name, substring of title, or pid")] string app,
        [Description("Key: name, char, or modifier+key (ctrl/alt/shift/win)")] string key,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                var result = await _input.PressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "scroll"), Description("Focus-free scroll. UIA ScrollPattern first, wheel messages as fallback.")]
    public async Task<object> Scroll(
        [Description("App name, substring of title, or pid")] string app,
        [Description("up | down | left | right")] string direction,
        [Description("Element id to scroll within (optional)")] string? element_id = null,
        [Description("Pages to scroll")] double pages = 1.0,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                var snap = _uia.GetSnapshot(target);
                ElementInfo? info = null;
                Interop.UIAutomationClient.IUIAutomationElement? element = null;
                if (element_id != null && snap != null)
                {
                    info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
                    if (info != null) element = await _uia.ResolveElementAsync(target.Hwnd, info, ct).ConfigureAwait(false);
                }
                var result = await _input.ScrollAsync(target.Hwnd, element, info, direction, pages, ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method, detail = result.Detail };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "drag"), Description("Focus-free drag between two screen points via window messages. Real cursor untouched.")]
    public async Task<object> Drag(
        [Description("App name, substring of title, or pid")] string app,
        [Description("From screen X")] int from_x,
        [Description("From screen Y")] int from_y,
        [Description("To screen X")] int to_x,
        [Description("To screen Y")] int to_y,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            var snap = _uia.GetSnapshot(target);
            if (snap != null && _settings.EnableBoundsGuard)
            {
                err = Guard.CheckBounds(snap, target.Hwnd);
                if (err != null) return Error(err);
            }
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                Ghost(from_x, from_y);
                await Task.Delay(_settings.PostActionDelayMs, ct).ConfigureAwait(false);
                Ghost(to_x, to_y);
                var result = await _input.DragAsync(target.Hwnd, from_x, from_y, to_x, to_y, ct).ConfigureAwait(false);
                return new { ok = result.Success, method = result.Method };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "set_value"), Description("Set an element's value directly via UIA (edits, sliders, toggles with value 'true'/'false').")]
    public async Task<object> SetValue(
        [Description("App name, substring of title, or pid")] string app,
        [Description("Element id from get_app_state")] string element_id,
        [Description("Value to set")] string value,
        CancellationToken ct = default)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            var snap = _uia.GetSnapshot(target);
            if (snap == null)
                return Error("No cached snapshot for this app (or the window changed since it was taken). Call get_app_state first.");
            var info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
            if (info == null)
                return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");
            var element = await _uia.ResolveElementAsync(target.Hwnd, info, ct).ConfigureAwait(false);
            if (element == null)
                return Error($"Element '{element_id}' no longer exists. Re-run get_app_state.");
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                var result = await _input.SetValueAsync(element, value, ct).ConfigureAwait(false);
                if (!result.Success) return Error(result.Detail);
                return new { ok = true, method = result.Method };
            }
            finally { focusGuard?.Restore(); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "wait_for"), Description("Server-side wait: polls until text appears in the app (or an element with that name exists), or the window title changes. Avoids snapshot round-trips.")]
    public async Task<object> WaitFor(
        [Description("App name, substring of title, or pid")] string app,
        [Description("text_exists | element_exists | window_active")] string condition,
        [Description("Text/element name to wait for")] string? text = null,
        [Description("Timeout seconds")] double timeout_s = 10,
        CancellationToken ct = default)
    {
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

    [McpServerTool(Name = "execute_sequence"), Description("Run a batch of actions server-side in order. Steps: [{\"tool\":\"click\",\"args\":{...}}, ...]. Supports click, type_text, press_key, scroll, drag, set_value, wait_for. Stops on first error unless stop_on_error=false.")]
    public async Task<object> ExecuteSequence(
        [Description("App name, substring of title, or pid")] string app,
        [Description("JSON array of steps: [{\"tool\":\"click\",\"args\":{\"element_id\":\"e3\"}}, ...]")] string steps_json,
        [Description("Stop on first failed step")] bool stop_on_error = true,
        CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(steps_json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Error("steps_json must be a JSON array");
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
                        Str(args, "button") ?? "left", Int(args, "click_count") ?? 1, ct).ConfigureAwait(false),
                    "type_text" => await TypeText(app, Str(args, "text") ?? "", ct: ct).ConfigureAwait(false),
                    "press_key" => await PressKey(app, Str(args, "key") ?? "", ct).ConfigureAwait(false),
                    "scroll" => await Scroll(app, Str(args, "direction") ?? "down", Str(args, "element_id"),
                        Dbl(args, "pages") ?? 1.0, ct).ConfigureAwait(false),
                    "drag" => await Drag(app, Int(args, "from_x") ?? 0, Int(args, "from_y") ?? 0,
                        Int(args, "to_x") ?? 0, Int(args, "to_y") ?? 0, ct).ConfigureAwait(false),
                    "set_value" => await SetValue(app, Str(args, "element_id") ?? "", Str(args, "value") ?? "", ct).ConfigureAwait(false),
                    "wait_for" => await WaitFor(app, Str(args, "condition") ?? "text_exists", Str(args, "text"),
                        Dbl(args, "timeout_s") ?? 10, ct).ConfigureAwait(false),
                    _ => Error($"Unknown tool '{tool}'")
                };
            }
            catch (Exception ex) { r = Error(ex.Message); }
            results.Add(new { step = i, tool, result = r });
            if (HasFailed(r) && stop_on_error)
                return new { ok = false, stopped_at = i, results };
        }
        return new { ok = true, steps_run = i, results };
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
            input = "UIA patterns + window messages (no SendInput — focus-free)",
            capture = "PrintWindow(PW_RENDERFULLCONTENT) → CopyFromScreen fallback"
        };
    }

    private static object Error(string message) => new { error = message };

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

    private static string? Str(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
