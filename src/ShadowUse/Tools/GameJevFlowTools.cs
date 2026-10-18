// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Jev.Flow;
using ShadowUse.Vision;

namespace ShadowUse.Tools;

/// <summary>Dispatch seam for the flow loop: single click or key tap, no verification frame, screen identity already checked by the flow.</summary>
public interface IGameJevFlowDispatcher
{
    Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality, string? appId, string? actionId, CancellationToken ct);
    Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId, string? appId, string? actionId, CancellationToken ct);
}

public sealed class GameJevFlowDispatcher(GameTools gameTools) : IGameJevFlowDispatcher
{
    public Task<CallToolResult> ClickAsync(string app, int x, int y, string sourceFrameId, int sourceFrameJpegQuality, string? appId, string? actionId, CancellationToken ct)
        => gameTools.GameJevFlowClick(app, x, y, sourceFrameId, sourceFrameJpegQuality, appId, actionId, ct);

    public Task<CallToolResult> TapKeyAsync(string app, string key, string sourceFrameId, string? appId, string? actionId, CancellationToken ct)
        => gameTools.GameJevFlowKeyTap(app, key, sourceFrameId, appId, actionId, ct);
}

/// <summary>48x27 luminance grid of a frame: the free, local identity check that lets the flow skip images and verification screenshots.</summary>
internal static class FrameFingerprint
{
    public const int Columns = 48, Rows = 27;

    public static byte[]? FromJpeg(byte[] jpeg)
    {
        try
        {
            using var stream = new MemoryStream(jpeg);
            using var source = new Bitmap(stream);
            using var small = new Bitmap(Columns, Rows, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, Columns, Rows));
            }
            var data = small.LockBits(new Rectangle(0, 0, Columns, Rows), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var result = new byte[Columns * Rows];
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < Rows; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, Columns * 3);
                    for (int x = 0; x < Columns; x++)
                        result[y * Columns + x] = (byte)((row[x * 3 + 2] * 299 + row[x * 3 + 1] * 587 + row[x * 3] * 114) / 1000);
                }
                return result;
            }
            finally { small.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException or InvalidOperationException)
        {
            return null;   // no fingerprint: the engine falls back to prediction plus periodic image sync
        }
    }
}

internal sealed class GameJevFlowEnvironment(McpServer server, string app, string? appId, IGameJevActionFrameSource frames, IGameJevFlowDispatcher dispatcher) : IJevFlowEnvironment
{
    private const int MaxFrameAgeMs = 5_000;
    private readonly Dictionary<string, (GameScreenshotFrameBinding Binding, int Quality)> _frames = new(StringComparer.Ordinal);

    public async Task<FlowFrame> CaptureAsync(CancellationToken ct)
    {
        var capture = await frames.CaptureAsync(server, app, ct).ConfigureAwait(false);
        if (capture.Failure is not null)
            throw new InvalidOperationException(ReadText(capture.Failure) ?? "Screenshot failed.");
        var frame = capture.Frame ?? throw new InvalidOperationException("Screenshot returned no complete frame binding.");
        var b = frame.Binding;
        if (string.IsNullOrWhiteSpace(b.ObservationId) || !b.IsFullFrame || b.MaskedImageBoxes is { Count: > 0 }
            || b.ImageWidth < 280 || b.ImageHeight < 280 || frame.Jpeg.Length is 0 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("The screenshot is not a complete unmasked frame of a usable size.");
        _frames[b.ObservationId] = (b, frame.JpegQuality);
        if (_frames.Count > 16) _frames.Remove(_frames.Keys.First());
        return new FlowFrame(b.ObservationId, frame.Jpeg, b.ImageWidth, b.ImageHeight, FrameFingerprint.FromJpeg(frame.Jpeg), b.CapturedAtUtc);
    }

    public async Task<FlowDispatchResult> ClickAsync(FlowFrame frame, NormBox target, CancellationToken ct)
    {
        if (await PreflightAsync(frame, ct).ConfigureAwait(false) is { } refusal) return refusal;
        var (binding, quality) = _frames[frame.FrameId];
        int x = Math.Clamp((int)Math.Round(target.CenterX * binding.ImageWidth), 0, binding.ImageWidth - 1);
        int y = Math.Clamp((int)Math.Round(target.CenterY * binding.ImageHeight), 0, binding.ImageHeight - 1);
        try { return Interpret(await dispatcher.ClickAsync(app, x, y, frame.FrameId, quality, appId, null, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FlowDispatchResult(false, $"Guarded dispatch threw after it started: {ex.Message}", DoNotRetry: true);
        }
    }

    public async Task<FlowDispatchResult> TapKeyAsync(FlowFrame frame, string key, CancellationToken ct)
    {
        if (await PreflightAsync(frame, ct).ConfigureAwait(false) is { } refusal) return refusal;
        try { return Interpret(await dispatcher.TapKeyAsync(app, key, frame.FrameId, appId, null, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new FlowDispatchResult(false, $"Guarded dispatch threw after it started: {ex.Message}", DoNotRetry: true);
        }
    }

    private async Task<FlowDispatchResult?> PreflightAsync(FlowFrame frame, CancellationToken ct)
    {
        if (!_frames.TryGetValue(frame.FrameId, out var entry))
            return new FlowDispatchResult(false, "The frame is not tracked by this flow; reobserve.", Reobserve: true);
        if (DateTimeOffset.UtcNow - entry.Binding.CapturedAtUtc > TimeSpan.FromMilliseconds(MaxFrameAgeMs))
            return new FlowDispatchResult(false, "The frame exceeded its 5-second action lifetime; reobserve.", Reobserve: true);
        if (await frames.ValidateBindingAsync(app, entry.Binding, ct).ConfigureAwait(false) is { } bindingError)
            return new FlowDispatchResult(false, bindingError, Reobserve: true);
        return null;
    }

    /// <summary>Maps the guarded tool result to the flow contract. Anything unclear means "do not retry": input may have been sent.</summary>
    private static FlowDispatchResult Interpret(CallToolResult result)
    {
        string? text = ReadText(result);
        bool? inputSent = null;
        bool reobserve = false, doNotRetry = false;
        if (text is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.TryGetProperty("input_sent", out var sent) && sent.ValueKind is JsonValueKind.True or JsonValueKind.False) inputSent = sent.GetBoolean();
                reobserve = root.TryGetProperty("reobserve", out var r) && r.ValueKind == JsonValueKind.True;
                doNotRetry = root.TryGetProperty("do_not_retry", out var d) && d.ValueKind == JsonValueKind.True;
            }
            catch (JsonException) { }
        }
        if (inputSent == true && result.IsError != true) return new FlowDispatchResult(true);
        string error = text is null ? "Dispatch returned no result." : text.Length > 300 ? text[..300] : text;
        // A refusal that provably sent nothing and asks to reobserve (stale frame, moved view) is recoverable; everything else stops the run.
        return new FlowDispatchResult(inputSent == true, error, DoNotRetry: inputSent != false || doNotRetry && !reobserve, Reobserve: inputSent == false && reobserve);
    }

    private static string? ReadText(CallToolResult result) => result.Content.OfType<TextContentBlock>().Select(t => t.Text).FirstOrDefault();
}

/// <summary>Maps an unknown screen using the owned local edge/OCR proposer. Proposals are unverified, labelled by OCR text where available.</summary>
internal sealed class GameJevFlowDiscoverer(McpServer server, string app, GameTools gameTools, string method) : IJevFlowDiscoverer
{
    public async Task<IReadOnlyList<FlowAction>> DiscoverAsync(FlowFrame frame, CancellationToken ct)
    {
        if (method == "off") return [];
        try
        {
            var result = await gameTools.GameScreenshot(server, app, max_width: 1280, quality: 75, bring_to_front: false,
                include_local_fast_candidates: true, local_candidate_method: method, ct: ct).ConfigureAwait(false);
            if (result.IsError == true) return [];
            string? json = result.Content.OfType<TextContentBlock>().Select(t => t.Text).FirstOrDefault();
            if (json is null) return [];
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("frame_id", out var frameId) || frameId.GetString() is not { } id
                || !GameUiObservationCache.TryGet(id, out var observation) || observation is null) return [];

            var found = new List<FlowAction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in observation.Candidates.OrderBy(c => c.Y).ThenBy(c => c.X).Take(40))
            {
                string label = string.IsNullOrWhiteSpace(c.Label) ? "" : c.Label.Trim();
                string position = $"UI element in the {(c.CenterY < observation.ImageHeight / 3 ? "upper" : c.CenterY > observation.ImageHeight * 2 / 3 ? "lower" : "middle")} {(c.CenterX < observation.ImageWidth / 3 ? "left" : c.CenterX > observation.ImageWidth * 2 / 3 ? "right" : "center")}";
                string description = label.Length > 0 ? $"{label} ({position})" : position;
                string idBase = "c" + new string((label.Length > 0 ? label : "ui").ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').Take(24).ToArray())
                    + $"_{c.CenterX * 100 / Math.Max(1, observation.ImageWidth):D2}_{c.CenterY * 100 / Math.Max(1, observation.ImageHeight):D2}";
                if (!seen.Add(idBase)) continue;
                found.Add(new FlowAction
                {
                    Id = idBase,
                    Kind = FlowActionKind.Click,
                    Description = description[..Math.Min(description.Length, 280)],
                    TargetDescription = description[..Math.Min(description.Length, 280)],
                    Box = new NormBox(c.X / (double)observation.ImageWidth, c.Y / (double)observation.ImageHeight,
                        (c.X + c.Width) / (double)observation.ImageWidth, (c.Y + c.Height) / (double)observation.ImageHeight),
                    BoxConfidence = c.EvidenceFraction,
                    Source = "discovered",
                });
            }
            return found;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }
}

[McpServerToolType]
public sealed class GameJevFlowTools(ShadowSettings settings, JevController jev, GameTools gameTools,
    IGameJevActionFrameSource? frames = null, IGameJevFlowDispatcher? dispatcher = null, DecisionBackendRouter? decisionBackends = null)
{
    private readonly ConcurrentDictionary<string, JevActionBook> _books = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private JevActionBook BookFor(string appId, string version, string profile, string layout)
    {
        string key = string.Join("__", new[] { appId, version, profile, layout }.Select(Sanitize));
        return _books.GetOrAdd(key, k => new JevActionBook(Path.Combine(ShadowSettings.LocalApplicationDataFolder(), "shadow-use", "jev-flow", k + ".json")));
    }

    private static string Sanitize(string value)
        => new string(value.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' ? ch : '_').Take(60).ToArray()) is { Length: > 0 } s ? s : "_";

    private string? Gate(bool execute)
    {
        if (!settings.EnableExperimentalGameJevFlow) return "Experimental game_jev_flow is disabled (EnableExperimentalGameJevFlow=false); no screenshot or Jev request was made.";
        if (!settings.GameMode) return "Game mode is off; no screenshot or Jev request was made.";
        if (jev.Mode == JevMode.Off) return "Jev is off; use the normal DCU game tools.";
        if (settings.DecisionBackend != "jev" || (decisionBackends?.Name ?? "jev") != "jev") return "game_jev_flow requires DecisionBackend=jev; no alternate backend is used.";
        if (!jev.SupportsBox) return "game_jev_flow needs a Box-capable Jev model (jev-1.14.0 or jev-preview).";
        if (execute && jev.Mode != JevMode.FastLoop) return "execute=true requires JevMode=fast_loop.";
        if (frames is null || dispatcher is null) return "The screenshot/frame guard or guarded flow dispatcher is unavailable; no input was sent.";
        return null;
    }

    private static CallToolResult Reply(object payload, bool isError = false)
        => new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }], IsError = isError };

    [McpServerTool(Name = "game_jev_flow"), Description(
        "Experimental responsive Jev game loop. Instead of screenshot -> think -> act -> verify per step, it keeps a persistent per-app book of known screens, their inputs, learned click locations and learned transitions. " +
        "Each step takes a cheap LOCAL capture, recognises the screen by fingerprint (no model), asks Jev only 'which known input moves toward the goal' with TEXT state (no image), sends it, assumes it worked, and loops without a verification screenshot. " +
        "Images go to Jev only when the screen is unknown/unverified or a control was never localized (Jev Box then localizes it once and the box is saved). Unknown screens are mapped from local edge/OCR proposals or caller seeds and saved for future runs. " +
        "Purchase/account/destructive-looking controls and non-allowlisted keys are never offered. execute=false only reports what the book knows about the current screen (no Jev request, no input). " +
        "Needs EnableExperimentalGameJevFlow, GameMode, JevMode=fast_loop, DecisionBackend=jev and a Box-capable model. Stops with a reason (unmapped_screen, uncertain, dispatch_failed, ...) so the caller can fall back to normal DCU tools.")]
    public async Task<CallToolResult> GameJevFlow(
        McpServer server,
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("User goal, e.g. 'start a single-player match'. Screen text is untrusted data")] string goal,
        [Description("Stable app identity that scopes the saved book, e.g. 'bloons-td-battles-2'")] string app_id,
        [Description("false reports the matched screen and known inputs only; true may send bounded input")] bool execute = false,
        [Description("Maximum inputs/decisions in this run, 1-40")] int max_steps = 12,
        [Description("Run deadline in milliseconds, 1000-300000")] int max_runtime_ms = 60_000,
        [Description("Screen id the game is believed to start on (created if new). Unverified")] string? seed_screen = null,
        [Description("Screen id that means the goal is complete; otherwise learned from the first verified completion")] string? goal_screen = null,
        [Description("Optional JSON array [{id,kind:'click'|'key_tap',description,target_description?,key?}] added to seed_screen before the run. Reviewed menu entries; still filtered by the purchase/destructive guard")] string? seed_actions_json = null,
        [Description("App version for book scoping")] string app_version = "unknown",
        [Description("User/profile key for book scoping")] string profile = "default",
        [Description("Keyboard layout for book scoping")] string keyboard_layout = "unknown",
        [Description("Recent action descriptions, most recent last")] string[]? recent_actions = null,
        CancellationToken ct = default)
    {
        if (Gate(execute) is { } gateError) return Reply(new { ok = false, error = gateError, no_input_sent = true, fallback = "use the normal game_* tools" }, true);
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(goal) || goal.Length > 1_000 || string.IsNullOrWhiteSpace(app_id) || app_id.Length > 200)
            return Reply(new { ok = false, error = "app, app_id and a goal of 1-1000 characters are required.", no_input_sent = true }, true);
        if (max_steps is < 1 or > 40 || max_runtime_ms is < 1_000 or > 300_000)
            return Reply(new { ok = false, error = "max_steps must be 1-40 and max_runtime_ms 1000-300000.", no_input_sent = true }, true);
        if (seed_screen is { Length: > 64 } || goal_screen is { Length: > 64 } || recent_actions is { Length: > 12 })
            return Reply(new { ok = false, error = "seed_screen/goal_screen exceed 64 characters or recent_actions exceeds 12 entries.", no_input_sent = true }, true);

        List<FlowAction>? seedActions = null;
        if (!string.IsNullOrWhiteSpace(seed_actions_json))
        {
            if (string.IsNullOrWhiteSpace(seed_screen)) return Reply(new { ok = false, error = "seed_actions_json requires seed_screen.", no_input_sent = true }, true);
            if (!TryParseSeedActions(seed_actions_json, out seedActions, out var parseError))
                return Reply(new { ok = false, error = parseError, no_input_sent = true }, true);
        }

        if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            return Reply(new { ok = false, error = "Another game_jev_flow run is active; wait for it to finish.", no_input_sent = true }, true);
        try
        {
            var book = BookFor(app_id, app_version, profile, keyboard_layout);
            var env = new GameJevFlowEnvironment(server, app, app_id, frames!, dispatcher!);

            if (!execute)
            {
                FlowFrame frame;
                try { frame = await env.CaptureAsync(ct).ConfigureAwait(false); }
                catch (InvalidOperationException ex) { return Reply(new { ok = false, error = ex.Message, no_input_sent = true }, true); }
                var match = frame.Fingerprint is { } fp ? book.Match(fp, settings.JevFlowFingerprintTolerance) : null;
                var menu = match is { } m ? book.MenuFor(m.ScreenId) : [];
                return Reply(new
                {
                    ok = true, preview_only = true, no_input_sent = true, no_jev_request = true,
                    fingerprint_available = frame.Fingerprint is not null,
                    matched_screen = match?.ScreenId, match_distance = match?.Distance,
                    known_screens = book.Screens.Count(s => s.Id != JevActionBook.GlobalScreenId),
                    text_only_possible = menu.Any(a => a.ExecutableWithoutVision),
                    menu = menu.Select(a => new { a.Id, a.Kind, a.Description, a.Key, located = a.Box is not null, expected_next_screen = a.ExpectedNextScreen, a.Uses }),
                    book_load_error = book.LoadError,
                });
            }

            var options = new JevFlowOptions
            {
                MaxSteps = max_steps,
                MaxRuntime = TimeSpan.FromMilliseconds(max_runtime_ms),
                SyncEvery = settings.JevFlowSyncEvery,
                ChoiceThreshold = settings.ExperimentalGameJevActChoiceThreshold,
                BoxThreshold = settings.ExperimentalGameJevActClickThreshold,
                AmbiguityMargin = settings.ExperimentalGameJevActAmbiguityMargin,
                FingerprintTolerance = settings.JevFlowFingerprintTolerance,
            };
            var discoverer = new GameJevFlowDiscoverer(server, app, gameTools, settings.JevFlowDiscovery);
            var engine = new JevFlowEngine(book, env, jev, options, discoverer);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(max_runtime_ms + 5_000);
            var result = await engine.RunAsync(goal.Trim(), new FlowSeed(seed_screen, goal_screen, seedActions, recent_actions), deadline.Token).ConfigureAwait(false);

            bool failed = result.StopReason is "error" or "dispatch_failed" or "cancelled";
            return Reply(new
            {
                ok = !failed,
                goal_reached = result.GoalReached,
                stop_reason = result.StopReason,
                error = result.Error,
                final_screen = result.FinalScreen,
                total_ms = result.TotalMs,
                stats = result.Stats,
                steps = result.Steps,
                outcome = "Inputs were sent without per-step verification; confirm the final state before relying on it.",
                book_saved = book.SaveError is null,
                book_save_error = book.SaveError,
                fallback = result.GoalReached ? null : "if the stop reason is unmapped_screen/uncertain/box_rejected/stuck_loop/unstable_screen, continue with the normal game_* tools",
            }, failed);
        }
        finally { _runGate.Release(); }
    }

    [McpServerTool(Name = "game_jev_flow_book"), Description(
        "Inspect or edit the persistent Jev flow book for one app identity: list screens and their inputs, seed inputs on a screen, rename/describe a screen, or forget a screen. " +
        "The book stores only screen ids/descriptions, tiny luminance fingerprints, allowlisted key taps, click target descriptions, learned normalized boxes and transition counts; it never stores screenshots. Seeded inputs are filtered by the purchase/destructive guard. Sends no input and makes no network request.")]
    public CallToolResult GameJevFlowBook(
        [Description("Stable app identity scoping the book")] string app_id,
        [Description("show | seed | describe | forget_screen | forget_action")] string operation = "show",
        [Description("Screen id for seed/describe/forget operations")] string? screen = null,
        [Description("seed: JSON array [{id,kind,description,target_description?,key?}]. describe: unused")] string? actions_json = null,
        [Description("describe: new description Jev uses to recognise the screen (max 400 chars)")] string? description = null,
        [Description("forget_action: action id on the screen")] string? action_id = null,
        [Description("App version for book scoping")] string app_version = "unknown",
        [Description("User/profile key for book scoping")] string profile = "default",
        [Description("Keyboard layout for book scoping")] string keyboard_layout = "unknown")
    {
        if (string.IsNullOrWhiteSpace(app_id) || app_id.Length > 200) return Reply(new { ok = false, error = "app_id is required (max 200 characters)." }, true);
        var book = BookFor(app_id, app_version, profile, keyboard_layout);
        switch (operation)
        {
            case "show":
                return Reply(new
                {
                    ok = true, load_error = book.LoadError, save_error = book.SaveError,
                    screens = book.Screens.Select(s => new
                    {
                        s.Id, s.Description, s.Visits, fingerprints = s.Fingerprints.Count,
                        actions = s.Actions.Select(a => new { a.Id, a.Kind, a.Description, a.Key, a.Source, located = a.Box is not null, a.Uses, next = a.ExpectedNextScreen, transitions = a.Transitions }),
                    }),
                });
            case "seed":
            {
                if (string.IsNullOrWhiteSpace(screen) || screen.Length > 64 || string.IsNullOrWhiteSpace(actions_json))
                    return Reply(new { ok = false, error = "seed needs screen and actions_json." }, true);
                if (!TryParseSeedActions(actions_json, out var actions, out var error)) return Reply(new { ok = false, error }, true);
                book.EnsureScreen(screen, screen);
                var refused = new List<object>();
                int added = 0;
                foreach (var action in actions!)
                {
                    if (book.Upsert(screen, action, out var reason)) added++;
                    else refused.Add(new { action.Id, reason });
                }
                book.Save();
                return Reply(new { ok = true, added, refused, save_error = book.SaveError });
            }
            case "describe":
                if (string.IsNullOrWhiteSpace(screen) || book.Get(screen) is null || string.IsNullOrWhiteSpace(description) || description.Length > 400)
                    return Reply(new { ok = false, error = "describe needs an existing screen and a description of 1-400 characters." }, true);
                book.SetDescription(screen, description.Trim());
                book.Save();
                return Reply(new { ok = true, save_error = book.SaveError });
            case "forget_screen":
                if (string.IsNullOrWhiteSpace(screen)) return Reply(new { ok = false, error = "forget_screen needs screen." }, true);
                bool removedScreen = book.RemoveScreen(screen);
                book.Save();
                return Reply(new { ok = removedScreen, save_error = book.SaveError });
            case "forget_action":
                if (string.IsNullOrWhiteSpace(screen) || string.IsNullOrWhiteSpace(action_id)) return Reply(new { ok = false, error = "forget_action needs screen and action_id." }, true);
                bool removedAction = book.RemoveAction(screen, action_id);
                book.Save();
                return Reply(new { ok = removedAction, save_error = book.SaveError });
            default:
                return Reply(new { ok = false, error = "operation must be show, seed, describe, forget_screen or forget_action." }, true);
        }
    }

    private static bool TryParseSeedActions(string json, out List<FlowAction>? actions, out string? error)
    {
        actions = null; error = null;
        if (json.Length > 20_000) { error = "actions_json exceeds 20000 characters."; return false; }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is 0 or > JevActionBook.MaxActionsPerScreen)
            { error = $"actions_json must be an array of 1-{JevActionBook.MaxActionsPerScreen} actions."; return false; }
            var parsed = new List<FlowAction>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { error = "each action must be an object."; return false; }
                foreach (var property in item.EnumerateObject())
                    if (property.Name is not ("id" or "kind" or "description" or "target_description" or "key"))
                    { error = $"unknown action property '{property.Name}'."; return false; }
                string? Text(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var action = new FlowAction
                {
                    Id = Text("id") ?? "", Kind = Text("kind") ?? "", Description = Text("description") ?? "",
                    TargetDescription = Text("target_description"), Key = Text("key"), Source = "caller",
                };
                if (!ids.Add(action.Id)) { error = $"duplicate action id '{action.Id}'."; return false; }
                if (JevFlowGuard.Refusal(action) is { } refusal && !refusal.StartsWith("description looks like", StringComparison.Ordinal))
                { error = $"action '{action.Id}': {refusal}."; return false; }
                parsed.Add(action);
            }
            actions = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"actions_json is not valid JSON: {ex.Message}";
            return false;
        }
    }
}
