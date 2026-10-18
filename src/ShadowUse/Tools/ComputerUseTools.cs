// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Automation;
using ShadowUse.Browser;
using ShadowUse.Capture;
using ShadowUse.Config;
using ShadowUse.Jev;
using ShadowUse.Native;
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
/// Workflow: list_apps → select by target_id for a lifetime-bound window, or window_id
/// for exact-window compatibility → get_app_state (snapshot with element ids) → act with
/// element ids → re-snapshot.
/// </summary>
[McpServerToolType]
public sealed class ComputerUseTools
{
    private readonly UiaService _uia;
    private readonly BackgroundInput _input;
    private readonly ShadowSettings _settings;
    private readonly JevController _jev;
    private readonly DecisionBackendRouter _decisionBackends;
    private readonly WorkflowValueStore _workflowValues;
    private readonly ActionResolutionCache _actionCache;
    private readonly LoopGuard _loopGuard;
    private readonly ExperienceStore _experience;
    private readonly JevInspector _inspector;
    private readonly RunTimeline _timeline;
    private readonly WorkflowRunner _workflows;
    private readonly ResourceLeaseRegistry _leases;
    private readonly DemonstrationStore _demonstrations;
    private readonly HelperStore _helpers;
    private readonly WorkspaceStore _workspaces;
    private readonly SearchSequencer _search;
    private readonly ModelRoutingRegistry _modelRouting;
    private readonly SubgoalStore _subgoals;
    private readonly CompositionStore _compositions;
    private readonly ReviewRoomStore _reviewRooms;
    private readonly TextGenerationCache _textGeneration;
    private readonly GridPredictionStore _gridPredictions;
    private readonly VoiceTurnController _voiceTurn;
    private readonly IsolatedEnvironmentRegistry _isolatedEnvironments;
    private readonly BrowserUseTools? _browser;
    private readonly GameTools? _game;
    private readonly AppActionCatalog? _appActionCatalog;
    private readonly ScreenStateStore? _screenStates;
    internal static readonly SemaphoreSlim MutationLock = new(1, 1); // serialized mutations, shared with GameTools
    private static SemaphoreSlim _mutationLock => MutationLock;

    private const int NativeJevMaxCandidates = 255;

    // Bumped whenever the native fallback resolution contract (AutomationId/ControlType/Name
    // matching semantics) changes, so an old cached recipe naturally misses instead of being
    // silently reinterpreted under a new contract (F31-R01 "keyed by adapter/version").
    private const string NativeAdapterVersion = "dcu-native-uia-v1";

    private static readonly IReadOnlyDictionary<string, string> NativeJevOperationDescriptions = new Dictionary<string, string>
    {
        ["click"] = "Click, invoke, toggle, select, or expand a visible interactive control.",
        ["click_visual"] = "Click an OCR-labelled text region only after DCU revalidates the same captured region.",
        ["set_value"] = "Set an edit, slider, or toggle's value directly via UI Automation, including passwords and other sensitive fields.",
    };

    private static readonly IReadOnlyDictionary<string, string> NativeJevKeyBindings = new Dictionary<string, string>
    {
        ["PRESS_KEY_ENTER"] = "Return",
        ["PRESS_KEY_TAB"] = "Tab",
        ["PRESS_KEY_ESCAPE"] = "Escape",
        ["PRESS_KEY_SPACE"] = "Space",
        ["PRESS_KEY_UP"] = "Up",
        ["PRESS_KEY_DOWN"] = "Down",
        ["PRESS_KEY_LEFT"] = "Left",
        ["PRESS_KEY_RIGHT"] = "Right",
    };

    private static readonly IReadOnlyList<JevControlOption> NativeJevControlOptions =
    [
        new("WAIT", "No action now; the interface may still be changing."),
        new("REOBSERVE", "No action; request a fresh observation before deciding again."),
        new("ASK_USER", "No action; ask the user for missing intent, authorization, or app-specific key meaning."),
        new("STOP", "No action; stop this task."),
        new("DONE", "Suggest that the goal appears complete; this is not verified completion."),
        new("BLOCKED", "No supported listed operation can progress the goal from this observation."),
        new("PRESS_KEY_ENTER", "Press Return/Enter. Often activates the focused/default control; actual effect depends on the app."),
        new("PRESS_KEY_TAB", "Press Tab. Often moves focus to the next control; actual effect depends on the app."),
        new("PRESS_KEY_ESCAPE", "Press Escape. Often cancels or closes a modal; actual effect depends on the app."),
        new("PRESS_KEY_SPACE", "Press Space. Often activates or toggles the focused control; actual effect depends on the app."),
        new("PRESS_KEY_UP", "Press Up. Often moves selection or caret up; actual effect depends on the app."),
        new("PRESS_KEY_DOWN", "Press Down. Often moves selection or caret down; actual effect depends on the app."),
        new("PRESS_KEY_LEFT", "Press Left. Often moves selection or caret left; actual effect depends on the app."),
        new("PRESS_KEY_RIGHT", "Press Right. Often moves selection or caret right; actual effect depends on the app."),
    ];

    // MCP serializes object results with WhenWritingNull. Build the observation as a
    // JsonNode with nulls retained so unsupported v1 capabilities stay explicit on the wire.
    private static readonly JsonSerializerOptions ObservationWireJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ComputerUseTools(UiaService uia, BackgroundInput input, ShadowSettings settings, JevController jev, WorkflowValueStore workflowValues, ActionResolutionCache? actionCache = null, LoopGuard? loopGuard = null, ExperienceStore? experience = null, JevInspector? inspector = null, RunTimeline? timeline = null, WorkflowRunner? workflows = null, BrowserUseTools? browser = null, ResourceLeaseRegistry? leases = null, DemonstrationStore? demonstrations = null, HelperStore? helpers = null, WorkspaceStore? workspaces = null, SearchSequencer? search = null, ModelRoutingRegistry? modelRouting = null, SubgoalStore? subgoals = null, CompositionStore? compositions = null, ReviewRoomStore? reviewRooms = null, TextGenerationCache? textGeneration = null, GridPredictionStore? gridPredictions = null, VoiceTurnController? voiceTurn = null, IsolatedEnvironmentRegistry? isolatedEnvironments = null, GameTools? gameTools = null,
        AppActionCatalog? appActionCatalog = null, ScreenStateStore? screenStates = null,
        DecisionBackendRouter? decisionBackends = null)
    {
        _uia = uia;
        _input = input;
        _workflowValues = workflowValues;
        _actionCache = actionCache ?? new ActionResolutionCache();
        _loopGuard = loopGuard ?? new LoopGuard();
        _experience = experience ?? new ExperienceStore();
        _settings = settings;
        _jev = jev;
        _decisionBackends = decisionBackends ?? new DecisionBackendRouter(settings, jev);
        _inspector = inspector ?? new JevInspector();
        _timeline = timeline ?? new RunTimeline(false, null);
        _workflows = workflows ?? new WorkflowRunner();
        _browser = browser;
        _game = gameTools;
        _appActionCatalog = appActionCatalog;
        _screenStates = screenStates;
        _leases = leases ?? new ResourceLeaseRegistry();
        _demonstrations = demonstrations ?? new DemonstrationStore();
        _helpers = helpers ?? new HelperStore();
        _workspaces = workspaces ?? new WorkspaceStore();
        _search = search ?? new SearchSequencer();
        _modelRouting = modelRouting ?? new ModelRoutingRegistry();
        _subgoals = subgoals ?? new SubgoalStore();
        _compositions = compositions ?? new CompositionStore();
        _reviewRooms = reviewRooms ?? new ReviewRoomStore();
        _textGeneration = textGeneration ?? new TextGenerationCache();
        _gridPredictions = gridPredictions ?? new GridPredictionStore();
        _voiceTurn = voiceTurn ?? new VoiceTurnController(_subgoals);
        _isolatedEnvironments = isolatedEnvironments ?? new IsolatedEnvironmentRegistry();
    }

    /// <summary>Best-effort app version for F28-R01 experience records. Some processes
    /// (elevated, cross-bitness, or already exited) refuse MainModule access; that is a
    /// real environmental boundary, not a bug, so it degrades to "unknown" rather than
    /// throwing out of an experience-recording call.</summary>
    private static string NativeAppVersion(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileVersionInfo.FileVersion ?? "unknown";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return "unknown";
        }
    }

    private static string LoopGuardScope(AppTarget target, string? elementId, int? x, int? y) =>
        $"{target.WindowId}|{elementId ?? $"{x}:{y}"}";

    private static object? LoopGuardPayload(LoopGuardVerdict verdict) => verdict.NoProgressCount == 0 && !verdict.ShouldStop
        ? null
        : new { no_progress_count = verdict.NoProgressCount, should_stop = verdict.ShouldStop, diagnosis = verdict.Diagnosis };

    private void RecordInspectorEntry(string app, string goal, JevDecision decision, string decisionSource, bool executed) => _inspector.Record(new JevInspectorEntry(
        "native", app, goal, decision.Operation, decision.TargetElementId, decision.Confidence, decision.Probabilities,
        decision.GoalReached, decision.GoalReachedProbability, decisionSource, executed, decision.LatencyMs, decision.Model, DateTimeOffset.UtcNow,
        decision.TargetConfidence, decision.OperationProbabilities, decision.DisambiguatedByScore, decision.TopChoiceMargin));

    private string? DesktopError() => _settings.EnableDesktopCheck ? Guard.CheckInteractiveDesktop() : null;

    private static string FingerprintOf(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..12];

    private void Ghost(int x, int y, bool pulse = false)
    {
        if (_settings.ShowVirtualCursor)
            VirtualCursorOverlay.Instance.MoveTo(x, y, pulse);
    }

    [McpServerTool(Name = "list_apps"), Description("List running apps that have a visible main window. Returns name, pid, title, window_id, and a lifetime-bound target_id when native identity is available.")]
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
                target_id = a.TargetId,
                lifetime_epoch = a.LifetimeEpoch,
                native_identity_error = a.NativeIdentityError,
                hwnd = $"0x{a.Hwnd.ToInt64():X}",
                title = a.Title,
            }),
            hint = "Use target_id for a lifetime-bound selector, or window_id for exact-window compatibility, when one process owns multiple windows. Next: get_app_state(app)."
        };
    }

    [McpServerTool(Name = "get_app_state"), Description("Snapshot an app selected by name, title substring, PID, window_id, or target_id. Returns the accessibility tree: interactive elements with stable ids, their frames, and supported actions. Optionally includes an annotated screenshot (Set-of-Marks labels matching element ids). Works on background/occluded windows. A continuation token pages the same cached observation without taking a new snapshot.")]
    public async Task<object> GetAppState(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Include annotated screenshot image")] bool include_screenshot = true,
        [Description("Max elements in tree")] int max_elements = 300,
        [Description("Continuation token from a limited response; pages the same cached observation")] string? continuation = null,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidateMaxElements(max_elements);
        if (validationError != null) return Error(validationError);

        if (!string.IsNullOrWhiteSpace(continuation))
        {
            if (!_uia.TryGetContinuation(continuation, out var cachedSnapshot, out var nextIndex))
                return Error("Continuation token expired or invalid. Call get_app_state for a fresh observation.");

            var page = cachedSnapshot.Elements
                .Skip(nextIndex)
                .Take(max_elements)
                .ToArray();
            int pageEnd = nextIndex + page.Length;
            bool responseTruncated = pageEnd < cachedSnapshot.Elements.Count;
            string? nextToken = null;
            if (responseTruncated)
            {
                try
                {
                    nextToken = _uia.CreateContinuationToken(cachedSnapshot, pageEnd);
                }
                catch (InvalidOperationException)
                {
                    return Error("Continuation token expired or invalid. Call get_app_state for a fresh observation.");
                }
            }

            // Continuation pages never capture a new screenshot or re-run OCR; they only
            // reflect whatever visual observation the original snapshot already carried.
            var pageVisual = BuildVisualGroundingResponse(false, "cached_page", cachedSnapshot.Visual, null);
            return BuildObservationResponse(
                cachedSnapshot,
                page,
                max_elements,
                responseTruncated,
                pageEnd,
                nextToken,
                Capture.CaptureEnvelope.CachedPage(),
                visualGrounding: pageVisual,
                includeVisualSource: cachedSnapshot.Visual != null);
        }

        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        var snap = await _uia.SnapshotAsync(target, maxNodes: Math.Max(100, max_elements * 3), ct: ct).ConfigureAwait(false);
        if (!_uia.TryValidateSnapshot(snap, out var snapshotIdentityError))
            return Error(snapshotIdentityError);
        // Truncate for THIS response only — never mutate the cached snapshot's own element
        // list; click/scroll/set_value re-resolve element ids against that same cached
        // object later and would lose access to anything trimmed here.
        var limited = snap.Elements.Count > max_elements ? snap.Elements.Take(max_elements).ToArray() : snap.Elements.ToArray();
        bool responseTruncatedForInitial = limited.Length < snap.Elements.Count;
        string? initialContinuation = null;
        if (responseTruncatedForInitial)
        {
            try
            {
                initialContinuation = _uia.CreateContinuationToken(snap, limited.Length);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
        }
        var capture = include_screenshot ? ScreenshotService.CaptureWindow(target.Hwnd, limited) : Capture.CaptureEnvelope.NotRequested();

        // F05: invoke OCR-based visual grounding only when structured UIA evidence is
        // inadequate (F05-R01) — there must be at least one NAMED, ACTIONABLE descendant for
        // UIA evidence to count as adequate. A lone Pane/Custom container covering the whole
        // surface (even with its own accessible name) does not count — it has nothing a
        // caller could click through UIA. Every top-level window also volunteers its own
        // non-client caption chrome (system menu, minimize/maximize/close) as named,
        // actionable UIA elements regardless of what the app itself draws, so those are
        // excluded here — otherwise no window could ever be judged UIA-inadequate.
        bool structuredEvidenceInadequate = !snap.Elements.Any(e =>
            e.Actions.Length > 0
            && !string.IsNullOrWhiteSpace(e.Name)
            && e.ControlType is not ("MenuBar" or "MenuItem")
            && e.AutomationId is not ("Minimize-Restore" or "Maximize-Restore" or "Close" or "SystemMenuBar"));
        object visualGrounding;
        if (structuredEvidenceInadequate)
        {
            var outcome = await ShadowUse.Vision.VisualGroundingService.RecognizeAsync(target.Hwnd, ct).ConfigureAwait(false);
            if (outcome.Observation != null)
            {
                snap.Visual = outcome.Observation;
                visualGrounding = BuildVisualGroundingResponse(true, "structured_evidence_inadequate", outcome.Observation, null);
            }
            else
            {
                visualGrounding = BuildVisualGroundingResponse(false, "structured_evidence_inadequate", null, outcome.UnsupportedReason);
            }
        }
        else
        {
            visualGrounding = BuildVisualGroundingResponse(false, "structured_evidence_present", null, null);
        }

        return BuildObservationResponse(
            snap,
            limited,
            max_elements,
            responseTruncatedForInitial,
            limited.Length,
            initialContinuation,
            capture,
            visualGrounding: visualGrounding,
            includeVisualSource: snap.Visual != null);
    }

    [McpServerTool(Name = "click"), Description("Focus-free click on an app selected by name, title substring, PID, window_id, or target_id. Prefer element_id from the latest snapshot; or use screen x,y. Optional app_id/source_observation_id/action_id records an app-agnostic, unverified screen transition. Uses UIA Invoke/Toggle/Select patterns when available (left clicks), else posts window messages. Never moves the real cursor or changes focus UNLESS background_mode=\"humanized\" (takes foreground, real SendInput, human timing).")]
    public async Task<object> Click(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Element id from get_app_state, e.g. 'e12'")] string? element_id = null,
        [Description("Screen X (if no element_id)")] int? x = null,
        [Description("Screen Y (if no element_id)")] int? y = null,
        [Description("left | right | middle")] string button = "left",
        [Description("Click count (2 = double)")] int click_count = 1,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        [Description("Stable app identity for optional screen transition history; requires source_observation_id")] string? app_id = null,
        [Description("Observation id returned by jev_decide with the same app_id, used to verify the current screen before input")] string? source_observation_id = null,
        [Description("Semantic action id for optional transition history; defaults to element_id or click coordinates")] string? action_id = null,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidateClick(button, click_count);
        if (validationError != null) return Error(validationError);
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        bool humanized = background_mode == HumanizedMode;
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        var clickStarted = Stopwatch.GetTimestamp();
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                    return Error(targetIdentityError);
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
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
                    var resolution = await _uia.ResolveElementWithStatusAsync(actionHwnd, info, ct).ConfigureAwait(false);
                    if (resolution.Status != ElementResolutionStatus.Found || resolution.Element == null)
                        return Error(ElementResolutionMessage(element_id, resolution.Status));
                    element = resolution.Element;
                    // F10: refuse a LIVE-confirmed disabled element before ever dispatching --
                    // only on an explicit false; null (unreadable) is treated as not-disabled,
                    // matching this codebase's nullable-property honesty convention.
                    if (await _uia.IsElementCurrentlyEnabledAsync(element, ct).ConfigureAwait(false) == false)
                        return Error($"Element '{element_id}' is disabled; refusing to dispatch.");
                    currentFrame = await _uia.GetElementFrameAsync(element, ct).ConfigureAwait(false);
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
                if (app_id is not null || source_observation_id is not null)
                {
                    if (string.IsNullOrWhiteSpace(app_id) || string.IsNullOrWhiteSpace(source_observation_id))
                        return Error("Pass both app_id and source_observation_id to record a screen transition.");
                    if (snap.ObservationId != source_observation_id || snap.CreatedAt + NativeJevObservationLifetime <= DateTimeOffset.UtcNow)
                        return Error("The app screen changed or the supplied observation expired; reobserve before clicking.");
                    if (await ValidateNativeActionFrameAsync(app_id, source_observation_id, target, ct).ConfigureAwait(false) is { } stateError)
                        return Error(stateError);
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
                Ghost(vx, vy, pulse: true);
                await Task.Delay(140, ct).ConfigureAwait(false); // let the pulse be visible

                if (element_id != null)
                {
                    if (!_uia.TryValidateSnapshot(snap, out var snapshotError))
                        return Error(snapshotError);
                }
                else if (!_uia.TryValidateTarget(target, out var targetErrorBeforeDispatch))
                {
                    return Error(targetErrorBeforeDispatch);
                }

                var dispatchStarted = Stopwatch.GetTimestamp();
                BackgroundInput.InputResult result = humanized
                    ? await InteractiveInput.HumanClickAsync(actionHwnd, vx, vy, currentFrame, btn, click_count, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                    : interactive
                    ? await InteractiveInput.ClickAsync(actionHwnd, vx, vy, btn, click_count, ct).ConfigureAwait(false)
                    : element_id != null
                    ? await _input.ClickElementAsync(actionHwnd, element, info, btn, click_count, ct, currentFrame).ConfigureAwait(false)
                    : await _input.ClickAtAsync(actionHwnd, vx, vy, btn, click_count, ct).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;

                if (!result.Success) return Error($"Click failed: {result.Detail}");

                var settleStarted = Stopwatch.GetTimestamp();
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
                            TargetReference = snap.TargetReference,
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
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;

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
                bool screenHistoryRecorded = app_id is not null && _screenStates?.RecordInput(app_id,
                    source_observation_id, action_id ?? element_id ?? $"click:{vx},{vy}", "click", result.Success, effect) == true;

                var clickDurationMs = (long)Stopwatch.GetElapsedTime(clickStarted).TotalMilliseconds;
                var clickTiming = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = clickDurationMs };
                var clickLoopVerdict = _loopGuard.Record(LoopGuardScope(target, element_id, x, y), $"click:{button}:{click_count}", progressed: effect != "none");
                if (effect == "none")
                {
                    _timeline.Record(new RunTimelineEvent("native", app, "click", element_id, false, effect, clickDurationMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                    return new
                    {
                        ok = false,
                        method = result.Method,
                        background_mode,
                        effect,
                        revision = after?.Revision,
                        changed_elements = after != null ? DeltaOf(snap, after) : null,
                        detail = refreshError != null
                            ? $"Click was dispatched, but the follow-up snapshot failed and no effect was observable: {refreshError.Message}"
                            : "Click was dispatched, but the element remained and no UI Automation state change was observable.",
                        loop_guard = LoopGuardPayload(clickLoopVerdict),
                        screen_history_recorded = screenHistoryRecorded,
                        timing = clickTiming,
                    };
                }

                _timeline.Record(new RunTimelineEvent("native", app, "click", element_id, true, effect, clickDurationMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                return new
                {
                    ok = true,
                    method = result.Method,
                    background_mode,
                    effect,
                    revision = after?.Revision,
                    changed_elements = after != null ? DeltaOf(snap, after) : null,
                    timing = clickTiming,
                    loop_guard = LoopGuardPayload(clickLoopVerdict),
                    screen_history_recorded = screenHistoryRecorded,
                    hint = "Snapshot refreshed. If the UI changed a lot, call get_app_state for full detail."
                };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "type_text"), Description("Focus-free text entry into an app selected by name, title substring, PID, window_id, or target_id. Tries the edit control's EM_REPLACESEL (no focus needed), then WM_CHAR stream. Never steals keyboard focus UNLESS background_mode=\"humanized\" (takes foreground, real keyboard input, human typing timing with typos).")]
    public async Task<object> TypeText(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Text to enter")] string? text = null,
        [Description("Name of a value stored via set_workflow_value/capture_workflow_value to enter instead of text")] string? value_ref = null,
        [Description("Environment variable whose value should be entered instead of text")] string? value_env = null,
        [Description("Allow UIA SetValue append (default: settings file, on unless disabled)")] bool? allow_uia_fallback = null,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        CancellationToken ct = default)
    {
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        bool humanized = background_mode == HumanizedMode;
        string resolvedText;
        try { resolvedText = WorkflowValueStore.Resolve(text, value_ref, value_env, _workflowValues); }
        catch (InvalidOperationException ex) { return Error(ex.Message); }
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                    return Error(targetIdentityError);
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
                if (!_uia.TryValidateTarget(target, out targetIdentityError))
                    return Error(targetIdentityError);
                var beforeSnap = _uia.GetSnapshot(target);
                var dispatchStarted = Stopwatch.GetTimestamp();
                var result = humanized
                    ? await InteractiveInput.HumanTypeTextAsync(target.Hwnd, resolvedText,
                        HumanizedOptions.From(_settings) with { TypoRate = value_ref != null || value_env != null ? 0 : _settings.HumanizedTypoRate }, ct).ConfigureAwait(false)
                    : interactive
                    ? await InteractiveInput.TypeTextAsync(target.Hwnd, resolvedText, ct).ConfigureAwait(false)
                    : await _input.TypeTextAsync(target.Hwnd, resolvedText, ct, allow_uia_fallback ?? _settings.AllowUiaTextFallback).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;
                var settleStarted = Stopwatch.GetTimestamp();
                var (after, effect, changedElements, refreshDetail) = await CaptureEffectAsync(target, beforeSnap, "Text entry", ct).ConfigureAwait(false);
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
                var totalMs = dispatchMs + settleMs;
                _timeline.Record(new RunTimelineEvent("native", app, "type_text", null, result.Success, effect ?? refreshDetail, totalMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                return new { ok = result.Success, method = result.Method, background_mode, revision = after?.Revision, effect, changed_elements = changedElements, detail = refreshDetail, timing = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = totalMs } };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "press_key"), Description("Focus-free key press in an app selected by name, title substring, PID, window_id, or target_id (e.g. 'Return', 'ctrl+s', 'F5', 'Tab'). Optional app_id/source_observation_id/action_id records an app-agnostic, unverified screen transition. Posted to the app's window without moving keyboard focus UNLESS background_mode=\"humanized\" (takes foreground, real keyboard SendInput, human key-hold timing).")]
    public async Task<object> PressKey(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Key: name, char, or modifier+key (ctrl/alt/shift/win)")] string key,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        [Description("Stable app identity for optional screen transition history; requires source_observation_id")] string? app_id = null,
        [Description("Observation id returned by jev_decide with the same app_id, used to verify the current screen before input")] string? source_observation_id = null,
        [Description("Semantic action id for optional transition history; defaults to the key string")] string? action_id = null,
        CancellationToken ct = default)
    {
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        bool humanized = background_mode == HumanizedMode;
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                    return Error(targetIdentityError);
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
                if (!_uia.TryValidateTarget(target, out targetIdentityError))
                    return Error(targetIdentityError);
                var beforeSnap = _uia.GetSnapshot(target);
                if (app_id is not null || source_observation_id is not null)
                {
                    if (string.IsNullOrWhiteSpace(app_id) || string.IsNullOrWhiteSpace(source_observation_id))
                        return Error("Pass both app_id and source_observation_id to record a screen transition.");
                    if (beforeSnap?.ObservationId != source_observation_id
                        || beforeSnap.CreatedAt + NativeJevObservationLifetime <= DateTimeOffset.UtcNow)
                        return Error("The app screen changed or the supplied observation expired; reobserve before pressing a key.");
                    if (await ValidateNativeActionFrameAsync(app_id, source_observation_id, target, ct).ConfigureAwait(false) is { } stateError)
                        return Error(stateError);
                }
                var dispatchStarted = Stopwatch.GetTimestamp();
                var result = humanized
                    ? await InteractiveInput.HumanPressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false)
                    : interactive
                    ? await InteractiveInput.PressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false)
                    : await _input.PressKeyAsync(target.Hwnd, key, ct).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;
                var settleStarted = Stopwatch.GetTimestamp();
                var (after, effect, changedElements, refreshDetail) = await CaptureEffectAsync(target, beforeSnap, "Key press", ct).ConfigureAwait(false);
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
                var totalMs = dispatchMs + settleMs;
                bool screenHistoryRecorded = app_id is not null && _screenStates?.RecordInput(app_id,
                    source_observation_id, action_id ?? key, "key", result.Success, effect) == true;
                _timeline.Record(new RunTimelineEvent("native", app, "press_key", null, result.Success, effect ?? result.Detail, totalMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                return new
                {
                    ok = result.Success,
                    method = result.Method,
                    background_mode,
                    detail = string.IsNullOrEmpty(result.Detail) ? refreshDetail : result.Detail,
                    revision = after?.Revision,
                    effect,
                    changed_elements = changedElements,
                    screen_history_recorded = screenHistoryRecorded,
                    timing = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = totalMs },
                };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "scroll"), Description("Focus-free scroll in an app selected by name, title substring, PID, window_id, or target_id. UIA ScrollPattern first, wheel messages as fallback. Never moves the real cursor UNLESS background_mode=\"humanized\" (takes foreground, real wheel SendInput, human-paced scrolling).")]
    public async Task<object> Scroll(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("up | down | left | right")] string direction,
        [Description("Element id to scroll within (optional)")] string? element_id = null,
        [Description("Pages to scroll")] double pages = 1.0,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidatePositiveFinite(pages, 100, nameof(pages));
        if (validationError != null) return Error(validationError);
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        HumanizedOptions? human = background_mode == HumanizedMode ? HumanizedOptions.From(_settings) : null;
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            FocusGuard? focusGuard = null;
            uint targetProcessId = 0;
            try
            {
                var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
                if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                    return Error(targetIdentityError);
                targetProcessId = (uint)target.Pid;
                focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
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
                    var resolution = await _uia.ResolveElementWithStatusAsync(actionHwnd, info, ct).ConfigureAwait(false);
                    if (resolution.Status != ElementResolutionStatus.Found || resolution.Element == null)
                        return Error(ElementResolutionMessage(element_id, resolution.Status));
                    element = resolution.Element;
                    currentFrame = await _uia.GetElementFrameAsync(element, ct).ConfigureAwait(false);
                    if (currentFrame == null)
                        return Error($"Element '{element_id}' no longer has usable screen bounds. Re-run get_app_state.");
                }
                if (element_id != null)
                {
                    if (snap == null)
                        return Error("No cached snapshot for this app. Call get_app_state first.");
                    if (!_uia.TryValidateSnapshot(snap, out var snapshotIdentityError))
                        return Error(snapshotIdentityError);
                }
                else if (!_uia.TryValidateTarget(target, out targetIdentityError))
                {
                    return Error(targetIdentityError);
                }

                var dispatchStarted = Stopwatch.GetTimestamp();
                var result = interactive
                    ? await InteractiveScrollAsync(actionHwnd, currentFrame, direction, pages, human, ct).ConfigureAwait(false)
                    : await _input.ScrollAsync(
                    actionHwnd,
                    element,
                    info,
                    direction,
                    pages,
                    ct,
                    currentFrame).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;
                var settleStarted = Stopwatch.GetTimestamp();
                var (after, effect, changedElements, refreshDetail) = await CaptureEffectAsync(target, snap, "Scroll", ct).ConfigureAwait(false);
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
                var totalMs = dispatchMs + settleMs;
                _timeline.Record(new RunTimelineEvent("native", app, "scroll", element_id, result.Success, effect ?? result.Detail, totalMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                return new { ok = result.Success, method = result.Method, background_mode, detail = string.IsNullOrEmpty(result.Detail) ? refreshDetail : result.Detail, revision = after?.Revision, effect, changed_elements = changedElements, timing = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = totalMs } };
            }
            finally
            {
                if (targetProcessId != 0) focusGuard?.Restore(targetProcessId);
            }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "drag"), Description("Focus-free drag in an app selected by name, title substring, PID, window_id, or target_id, between two screen points via window messages. Real cursor untouched UNLESS background_mode=\"humanized\" (takes foreground, real SendInput drag, human path with overshoot).")]
    public async Task<object> Drag(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("From screen X")] int from_x,
        [Description("From screen Y")] int from_y,
        [Description("To screen X")] int to_x,
        [Description("To screen Y")] int to_y,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        CancellationToken ct = default)
    {
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        bool humanized = background_mode == HumanizedMode;
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                return Error(targetIdentityError);
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
            var focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
            try
            {
                Ghost(actionFromX, actionFromY);
                await Task.Delay(InputPace.Ms(_settings.PostActionDelayMs), ct).ConfigureAwait(false);
                Ghost(actionToX, actionToY);
                if (!_uia.TryValidateTarget(target, out var dispatchIdentityError))
                    return Error(dispatchIdentityError);
                var dispatchStarted = Stopwatch.GetTimestamp();
                var result = humanized
                    ? await InteractiveInput.HumanDragAsync(target.Hwnd, actionFromX, actionFromY, actionToX, actionToY, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                    : interactive
                    ? await InteractiveInput.DragAsync(target.Hwnd, actionFromX, actionFromY, actionToX, actionToY, ct).ConfigureAwait(false)
                    : await _input.DragAsync(
                    target.Hwnd,
                    actionFromX,
                    actionFromY,
                    actionToX,
                    actionToY,
                    ct).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;
                var settleStarted = Stopwatch.GetTimestamp();
                var (after, effect, changedElements, refreshDetail) = await CaptureEffectAsync(target, snap, "Drag", ct).ConfigureAwait(false);
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
                var totalMs = dispatchMs + settleMs;
                _timeline.Record(new RunTimelineEvent("native", app, "drag", null, result.Success, effect, totalMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));
                return new { ok = result.Success, method = result.Method, background_mode, revision = after?.Revision, effect, changed_elements = changedElements, detail = refreshDetail, timing = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = totalMs } };
            }
            finally { focusGuard?.Restore((uint)target.Pid); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "set_value"), Description("Set an element's value directly via UIA in an app selected by name, title substring, PID, window_id, or target_id (edits, sliders, toggles with value 'true'/'false'). Supply exactly one of value, value_ref, or value_env.")]
    public async Task<object> SetValue(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Element id from get_app_state")] string element_id,
        [Description("Value to set")] string? value = null,
        [Description("Name of a value stored via set_workflow_value/capture_workflow_value to set instead of value")] string? value_ref = null,
        [Description("Environment variable whose value should be set instead of value")] string? value_env = null,
        [Description("strict_background (default, only implemented mode for set_value) | best_effort_background | isolated_interactive. Other modes return UNSUPPORTED_INPUT_MODE instead of silently falling back to strict_background.")] string background_mode = "strict_background",
        CancellationToken ct = default)
    {
        if (CheckBackgroundMode(background_mode) is { } modeError) return modeError;
        string resolvedValue;
        try { resolvedValue = WorkflowValueStore.Resolve(value, value_ref, value_env, _workflowValues); }
        catch (InvalidOperationException ex) { return Error(ex.Message); }
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        var setValueStarted = Stopwatch.GetTimestamp();
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                return Error(targetIdentityError);
            var snap = _uia.GetSnapshot(target);
            if (snap == null)
                return Error("No cached snapshot for this app (or the window changed since it was taken). Call get_app_state first.");
            var info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
            if (info == null)
                return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");
            var resolution = await _uia.ResolveElementWithStatusAsync(target.Hwnd, info, ct).ConfigureAwait(false);
            if (resolution.Status != ElementResolutionStatus.Found || resolution.Element == null)
                return Error(ElementResolutionMessage(element_id, resolution.Status));
            var element = resolution.Element;
            // F10 parity: refuse a LIVE-confirmed disabled element before ever dispatching --
            // only on an explicit false; null (unreadable) is treated as not-disabled, matching
            // Click()'s own pre-dispatch check and this codebase's nullable-property convention.
            if (await _uia.IsElementCurrentlyEnabledAsync(element, ct).ConfigureAwait(false) == false)
                return Error($"Element '{element_id}' is disabled; refusing to dispatch.");
            if (await _uia.IsElementCurrentlyReadOnlyAsync(element, ct).ConfigureAwait(false) == true)
                return Error($"Element '{element_id}' is read-only; refusing to dispatch.");
            var focusGuard = _settings.EnableFocusGuard ? FocusGuard.Capture() : null;
            try
            {
                if (!_uia.TryValidateSnapshot(snap, out var dispatchIdentityError))
                    return Error(dispatchIdentityError);
                var dispatchStarted = Stopwatch.GetTimestamp();
                var result = await _input.SetValueAsync(element, resolvedValue, ct).ConfigureAwait(false);
                var dispatchMs = (long)Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds;
                if (!result.Success) return Error(result.Detail);

                var settleStarted = Stopwatch.GetTimestamp();
                Snapshot? after = null;
                string? refreshDetail = null;
                try
                {
                    after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    refreshDetail = $"Value was set, but the follow-up snapshot failed and the updated value could not be observed: {ex.Message}";
                }
                var settleMs = (long)Stopwatch.GetElapsedTime(settleStarted).TotalMilliseconds;
                var updated = after?.Elements.FirstOrDefault(e => e.Id == element_id);
                var setValueDurationMs = (long)Stopwatch.GetElapsedTime(setValueStarted).TotalMilliseconds;
                var setValueTiming = new { dispatch_ms = dispatchMs, settle_ms = settleMs, total_ms = setValueDurationMs };
                _timeline.Record(new RunTimelineEvent("native", app, "set_value", element_id, true, refreshDetail, setValueDurationMs, DateTimeOffset.UtcNow, DispatchMs: dispatchMs, SettleMs: settleMs));

                // Only recorded when the resulting value was actually readable back (UIA blocks
                // reading password fields via E_ACCESSDENIED regardless of how they were set);
                // an unreadable result is genuinely unknown progress, not a no-progress signal.
                object? loopGuardPayload = null;
                if (updated?.Value != null)
                {
                    var verdict = _loopGuard.Record(LoopGuardScope(target, element_id, null, null), "set_value:" + FingerprintOf(resolvedValue), progressed: updated.Value == resolvedValue);
                    loopGuardPayload = LoopGuardPayload(verdict);
                }

                return new
                {
                    ok = true,
                    method = result.Method,
                    background_mode,
                    value = updated?.Value,
                    revision = after?.Revision,
                    changed_elements = after != null ? DeltaOf(snap, after) : null,
                    detail = refreshDetail,
                    loop_guard = loopGuardPayload,
                    timing = setValueTiming,
                };
            }
            finally { focusGuard?.Restore((uint)target.Pid); }
        }
        finally { _mutationLock.Release(); }
    }

    [McpServerTool(Name = "click_visual_candidate"), Description("F05 visual-grounding fallback: click a labelled text region returned by get_app_state's visual_grounding.candidates (OCR, used only when that snapshot had no adequate UIA evidence) in an app selected by name, title substring, PID, window_id, or target_id. Re-validates window geometry, DPI and the captured region's pixels before dispatching; returns 'reobserve' if the window moved/resized or the region changed since the observation. Optional app_id/source_observation_id/action_id records an unverified transition. Never verified beyond dispatch — re-run get_app_state to confirm the effect.")]
    public async Task<object> ClickVisualCandidate(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Visual candidate id from get_app_state's visual_grounding.candidates, e.g. 'v1'")] string candidate_id,
        [Description("left | right | middle")] string button = "left",
        [Description("Click count (2 = double)")] int click_count = 1,
        [Description(InteractiveModeDescription)] string? background_mode = null,
        [Description("Stable app identity for optional screen transition history; requires source_observation_id")] string? app_id = null,
        [Description("Observation id returned by jev_decide with the same app_id, used to verify the current screen before input")] string? source_observation_id = null,
        [Description("Semantic action id for optional transition history; defaults to the visual candidate id")] string? action_id = null,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidateClick(button, click_count);
        if (validationError != null) return Error(validationError);
        background_mode = ResolveInputMode(background_mode);
        if (CheckBackgroundMode(background_mode, interactiveSupported: true) is { } modeError) return modeError;
        bool interactive = IsRealInputMode(background_mode);
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var err = DesktopError();
            if (err != null) return Error(err);
            var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
            if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                return Error(targetIdentityError);
            var snap = _uia.GetSnapshot(target);
            if (snap?.Visual == null)
                return Error("No cached visual observation for this app. Call get_app_state first — visual grounding only runs when UIA evidence was inadequate.");
            var candidate = snap.Visual.Candidates.FirstOrDefault(c => c.Id == candidate_id);
            if (candidate == null)
                return Error($"Unknown visual candidate id '{candidate_id}'. Call get_app_state for fresh candidates.");
            if (app_id is not null || source_observation_id is not null)
            {
                if (string.IsNullOrWhiteSpace(app_id) || string.IsNullOrWhiteSpace(source_observation_id))
                    return Error("Pass both app_id and source_observation_id to record a screen transition.");
                if (snap.ObservationId != source_observation_id || snap.CreatedAt + NativeJevObservationLifetime <= DateTimeOffset.UtcNow)
                    return Error("The app screen changed or the supplied observation expired; reobserve before clicking.");
                if (await ValidateNativeActionFrameAsync(app_id, source_observation_id, target, ct).ConfigureAwait(false) is { } stateError)
                    return Error(stateError);
            }

            var (ok, reason) = await ShadowUse.Vision.VisualGroundingService.TryRevalidateAsync(target.Hwnd, snap.Visual, candidate, ct).ConfigureAwait(false);
            if (!ok)
                return new { error = "reobserve", detail = reason, hint = "Call get_app_state for a fresh visual observation." };

            var btn = button.ToLowerInvariant() switch
            {
                "right" => BackgroundInput.MouseButton.Right,
                "middle" => BackgroundInput.MouseButton.Middle,
                _ => BackgroundInput.MouseButton.Left,
            };
            FocusGuard? focusGuard = _settings.EnableFocusGuard || interactive ? FocusGuard.Capture() : null;
            try
            {
                Ghost(candidate.ScreenX, candidate.ScreenY, pulse: true);
                await Task.Delay(140, ct).ConfigureAwait(false);
                var result = background_mode == HumanizedMode
                    ? await InteractiveInput.HumanClickAsync(target.Hwnd, candidate.ScreenX, candidate.ScreenY, null, btn, click_count, HumanizedOptions.From(_settings), ct).ConfigureAwait(false)
                    : interactive
                    ? await InteractiveInput.ClickAsync(target.Hwnd, candidate.ScreenX, candidate.ScreenY, btn, click_count, ct).ConfigureAwait(false)
                    : await _input.ClickAtAsync(target.Hwnd, candidate.ScreenX, candidate.ScreenY, btn, click_count, ct).ConfigureAwait(false);
                bool screenHistoryRecorded = app_id is not null && _screenStates?.RecordInput(app_id,
                    source_observation_id, action_id ?? candidate_id, "click", result.Success,
                    "OCR-region click sent; effect unverified") == true;
                return new
                {
                    ok = result.Success,
                    method = result.Method,
                    background_mode,
                    detail = result.Detail,
                    actionability = "unverified",
                    screen_history_recorded = screenHistoryRecorded,
                    hint = "OCR-based click is not semantically verified. Call get_app_state to confirm the effect.",
                };
            }
            finally { focusGuard?.Restore((uint)target.Pid); }
        }
        finally { _mutationLock.Release(); }
    }

    /// <summary>F42-R02: adds a "route" field to an existing tool's result without duplicating
    /// that tool's own response shape.</summary>
    private static object WithRoute(string route, object result)
    {
        var node = JsonSerializer.SerializeToNode(result, ObservationWireJsonOptions) as JsonObject ?? new JsonObject();
        node["route"] = route;
        return node;
    }

    [McpServerTool(Name = "route_click"), Description("F42 capability-aware routing: click through ONE high-level intent regardless of backend. Supply either a browser target (session_id+candidate_id) or a native target (app selected by name, title substring, PID, window_id, or target_id, plus element_id). Optional app_id/source_observation_id/action_id records native screen-transition history and rejects a replaced observation. A native element that does not support click via UIA falls back to visual/OCR grounding (F05) automatically when text_hint matches a cached visual_grounding candidate -- no silent adapter skip, the response always names the route actually used ('browser' | 'native_uia' | 'visual_ocr').")]
    public async Task<object> RouteClick(
        [Description("App name, title substring, PID, window_id, or target_id (native target)")] string? app = null,
        [Description("Element id from get_app_state (native target)")] string? element_id = null,
        [Description("Text to match against get_app_state's visual_grounding.candidates when the native element does not support click")] string? text_hint = null,
        [Description("Adapter session id (browser target)")] string? session_id = null,
        [Description("Candidate id from the latest browser observation (browser target)")] string? candidate_id = null,
        [Description("left | right | middle")] string button = "left",
        [Description("Click count (2 = double)")] int click_count = 1,
        [Description("Native target only; passed to click. strict_background (default) | isolated_interactive (real input) | humanized (real input, human-seeming) | best_effort_background (not implemented)")] string? background_mode = null,
        [Description("Stable app identity for optional screen transition history")] string? app_id = null,
        [Description("Observation id returned by jev_decide with the same app_id; rechecks the current semantic screen before input")] string? source_observation_id = null,
        [Description("Semantic action id for optional transition history")] string? action_id = null,
        CancellationToken ct = default)
    {
        if (session_id != null || candidate_id != null)
        {
            if (session_id == null || candidate_id == null)
                return Error("Provide both session_id and candidate_id for a browser target.");
            if (_browser is null) return Error("Browser tools are unavailable in this process.");
            // F42-R02: structural pre-check (mirrors the native branch's SupportedOperations check
            // below) decides the route BEFORE dispatch, never by parsing an exception message.
            var readiness = await _browser.CheckClickReady(session_id, candidate_id, ct).ConfigureAwait(false);
            if (!readiness.candidateFound)
                return Error("Browser session or candidate was not found. Call browser_open/browser_observe.");
            if (readiness.ready)
            {
                var browserResult = await _browser.Click(session_id, candidate_id, ct).ConfigureAwait(false);
                return WithRoute("browser", browserResult);
            }
            // A genuinely detached element has nothing to click at any coordinate; every other
            // readiness failure (hidden/disabled/obscured/moving/actionability) is still worth
            // trying the coordinate-bridge fallback, since its own probe gate aborts safely if the
            // element truly can't be hit -- see PointClickAsync's structural verification.
            if (readiness.failure == nameof(BrowserReadinessFailure.Detached))
                return Error("Browser candidate is detached (no longer in the DOM); no coordinate fallback is possible for a missing element.");
            var pointResult = await _browser.PointClick(session_id, candidate_id, null, null, ct).ConfigureAwait(false);
            return WithRoute("browser_point_click", pointResult);
        }
        if (app == null || element_id == null)
            return Error("Provide session_id+candidate_id (browser target) or app+element_id (native target).");

        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        var snap = _uia.GetSnapshotForElement(target, element_id);
        if (snap == null)
            return Error("No cached snapshot for this app. Call get_app_state first.");
        var info = snap.Elements.FirstOrDefault(e => e.Id == element_id);
        if (info == null)
            return Error($"Unknown element id '{element_id}' in snapshot r{snap.Revision}. Call get_app_state for fresh ids.");

        if (info.SupportedOperations.Contains("click"))
        {
            var nativeResult = await Click(app, element_id, null, null, button, click_count, background_mode,
                app_id, source_observation_id, action_id, ct).ConfigureAwait(false);
            return WithRoute("native_uia", nativeResult);
        }

        // UIA reports no click-capable pattern on this element -- try the visual/OCR adapter
        // (F05) against the SAME cached snapshot instead of terminating the task.
        if (snap.Visual == null)
            return Error($"Element '{element_id}' does not support click via UIA (supported_operations: [{string.Join(",", info.SupportedOperations)}]), and no visual-grounding observation is cached to fall back to.");
        if (string.IsNullOrWhiteSpace(text_hint))
            return Error($"Element '{element_id}' does not support click via UIA (supported_operations: [{string.Join(",", info.SupportedOperations)}]). Supply text_hint to fall back to visual/OCR grounding.");
        var candidate = snap.Visual.Candidates.FirstOrDefault(c => c.Text.Contains(text_hint, StringComparison.OrdinalIgnoreCase));
        if (candidate == null)
            return Error($"Element '{element_id}' does not support click via UIA, and no visual-grounding candidate matched text_hint '{text_hint}'.");
        var visualResult = await ClickVisualCandidate(app, candidate.Id, button, click_count, background_mode,
            app_id, source_observation_id, action_id, ct).ConfigureAwait(false);
        return WithRoute("visual_ocr", visualResult);
    }

    [McpServerTool(Name = "route_set_text"), Description("F42 capability-aware routing: set text through ONE high-level intent regardless of backend. Supply either a browser target (session_id+candidate_id, routes to browser_fill) or a native target (app selected by name, title substring, PID, window_id, or target_id, plus element_id, routes to set_value via UIA/message input). Supply exactly one of value, value_ref, or value_env. The response always names the route actually used ('browser' | 'native_uia').")]
    public async Task<object> RouteSetText(
        [Description("App name, title substring, PID, window_id, or target_id (native target)")] string? app = null,
        [Description("Element id from get_app_state (native target)")] string? element_id = null,
        [Description("Adapter session id (browser target)")] string? session_id = null,
        [Description("Candidate id from the latest browser observation (browser target)")] string? candidate_id = null,
        [Description("Exact text to enter")] string? value = null,
        [Description("Name of a value stored via set_workflow_value/capture_workflow_value/browser_capture_value to enter instead of value")] string? value_ref = null,
        [Description("Environment variable whose value should be entered instead of value")] string? value_env = null,
        [Description("Native target only; passed to set_value, which supports only strict_background (default).")] string background_mode = "strict_background",
        CancellationToken ct = default)
    {
        if (session_id != null || candidate_id != null)
        {
            if (session_id == null || candidate_id == null)
                return Error("Provide both session_id and candidate_id for a browser target.");
            if (_browser is null) return Error("Browser tools are unavailable in this process.");
            // F42-R02: same structural pre-check pattern as RouteClick's browser branch.
            var readiness = await _browser.CheckFillReady(session_id, candidate_id, ct).ConfigureAwait(false);
            if (!readiness.candidateFound)
                return Error("Browser session or candidate was not found. Call browser_open/browser_observe.");
            if (readiness.ready)
            {
                var browserResult = await _browser.Fill(session_id, candidate_id, value, value_ref, value_env, ct).ConfigureAwait(false);
                return WithRoute("browser", browserResult);
            }
            if (readiness.failure == nameof(BrowserReadinessFailure.Detached))
                return Error("Browser candidate is detached (no longer in the DOM); no coordinate fallback is possible for a missing element.");
            var pointResult = await _browser.PointFill(session_id, candidate_id, value, value_ref, value_env, ct).ConfigureAwait(false);
            return WithRoute("browser_point_fill", pointResult);
        }
        if (app == null || element_id == null)
            return Error("Provide session_id+candidate_id (browser target) or app+element_id (native target).");
        var nativeResult = await SetValue(app, element_id, value, value_ref, value_env, background_mode, ct).ConfigureAwait(false);
        return WithRoute("native_uia", nativeResult);
    }

    [McpServerTool(Name = "set_workflow_value"), Description("Store a value under a name for later use with type_text/set_value/browser_fill's value_ref argument. Source for F39 flexible value/credential sources: the caller supplies whatever it already has (env-sourced secret, generated text, another tool's output).")]
    public object SetWorkflowValue(
        [Description("Name to store the value under")] string name,
        [Description("Value to store")] string value)
    {
        try { _workflowValues.Set(name, value); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return new { ok = true, name };
    }

    [McpServerTool(Name = "capture_workflow_value"), Description("Read an element's current value from a fresh UIA snapshot in an app selected by name, title substring, PID, window_id, or target_id, and store it under a name for later use with type_text/set_value/browser_fill's value_ref. Moves a value between apps without Jev ever generating or seeing the text.")]
    public async Task<object> CaptureWorkflowValue(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Element id from get_app_state")] string element_id,
        [Description("Name to store the captured value under")] string name,
        CancellationToken ct = default)
    {
        var err = DesktopError();
        if (err != null) return Error(err);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        var after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
        var element = after.Elements.FirstOrDefault(e => e.Id == element_id);
        if (element == null)
            return Error($"Element '{element_id}' was not found in a fresh snapshot. Call get_app_state to re-resolve.");
        if (element.Value == null)
            return Error($"Element '{element_id}' has no readable value to capture.");
        try { _workflowValues.Set(name, element.Value); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return new { ok = true, name, length = element.Value.Length, revision = after.Revision };
    }

    [McpServerTool(Name = "remember_action_resolution"), Description("Cache how to find an element again under a task_intent name, for an app selected by name, title substring, PID, window_id, or target_id (F31). Stores identifying attributes (AutomationId/ControlType/Name), never the live element id and never a claim the action will keep succeeding. A later resolve_cached_action call always re-resolves against the current UI and misses if the app's structure changed.")]
    public async Task<object> RememberActionResolution(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Element id from get_app_state to remember")] string element_id,
        [Description("Caller-chosen name for this resolved action, e.g. 'login_submit_button'")] string task_intent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task_intent))
            return Error("task_intent must not be empty.");
        var err = DesktopError();
        if (err != null) return Error(err);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        var snap = _uia.GetSnapshotForElement(target, element_id) ?? await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
        var element = snap.Elements.FirstOrDefault(e => e.Id == element_id);
        if (element == null)
            return Error($"Element '{element_id}' was not found in a cached snapshot. Call get_app_state first.");
        var schema = ActionResolutionCache.ComputeNativeSchema(snap.Elements.Select(e => (e.AutomationId, e.ControlType)));
        var key = new ActionResolutionKey("native", NativeAdapterVersion, target.ProcessName, schema, task_intent);
        _actionCache.Remember(key, new ActionResolutionRecipe(element.AutomationId, element.ControlType, element.Name, null, DateTimeOffset.UtcNow));
        return new { ok = true, task_intent, target_identity = target.ProcessName, schema };
    }

    [McpServerTool(Name = "resolve_cached_action"), Description("Look up a recipe cached via remember_action_resolution for an app selected by name, title substring, PID, window_id, or target_id, and re-resolve it against a fresh snapshot right now (F31). Returns hit:true with a usable element_id only if exactly one current element still matches; otherwise returns hit:false and invalidates the stale entry. Speed never comes from trusting a stale selector — every hit is still a live re-resolution, not a replayed result.")]
    public async Task<object> ResolveCachedAction(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Name previously passed to remember_action_resolution")] string task_intent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task_intent))
            return Error("task_intent must not be empty.");
        var err = DesktopError();
        if (err != null) return Error(err);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        var snap = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
        var schema = ActionResolutionCache.ComputeNativeSchema(snap.Elements.Select(e => (e.AutomationId, e.ControlType)));
        var key = new ActionResolutionKey("native", NativeAdapterVersion, target.ProcessName, schema, task_intent);
        if (!_actionCache.TryGet(key, out var recipe))
            return new { hit = false, reason = "no_cached_recipe", target_identity = target.ProcessName, schema };
        var matches = snap.Elements.Where(e =>
            string.Equals(e.AutomationId, recipe.AutomationId, StringComparison.Ordinal) &&
            string.Equals(e.ControlType, recipe.ControlType, StringComparison.Ordinal) &&
            string.Equals(e.Name, recipe.Name, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1)
        {
            _actionCache.Invalidate(key);
            return new { hit = false, reason = matches.Count == 0 ? "not_found" : "ambiguous", target_identity = target.ProcessName, schema, revision = snap.Revision };
        }
        return new { hit = true, element_id = matches[0].Id, control_type = matches[0].ControlType, name = matches[0].Name, revision = snap.Revision, cached_at = recipe.CachedAt };
    }

    [McpServerTool(Name = "resolve_field_text"), Description("F24-R01/R02: resolve text for a field, preferring an exact supplied string over any generated value -- exact_text always wins and never touches the cache or generator path (F24-T01). Without exact_text, checks the cache for context_key keyed by a hash of generation_input; unchanged input reuses the cached value across stale-observation retries (F24-R02). A changed generation_input (or a first-ever call) is a cache miss: DCU never generates text itself -- no live model key is configured anywhere in this build (same constraint as F27/F46/F47/F55) -- so the caller must supply generated_text from its own configured generator (see select_model_provider), which is then cached under the new input hash for reuse (F24-T02). Calling with a changed/missing generation_input and no generated_text, or with no TextGenerator provider configured at all, returns a clean structured error rather than fabricating content.")]
    public object ResolveFieldText(
        [Description("Stable name for this field/slot, e.g. 'login_bio_field'")] string context_key,
        [Description("Exact text to use verbatim; if supplied, bypasses the cache and generator entirely")] string? exact_text = null,
        [Description("Deterministic input the generated text depends on (goal + field context); a changed value invalidates the cached text")] string? generation_input = null,
        [Description("Text already produced by the caller's own configured generator, to be cached for this input")] string? generated_text = null)
    {
        if (!string.IsNullOrEmpty(exact_text))
            return new { text = exact_text, used_generator = false, cache_hit = false, source = "exact" };

        if (string.IsNullOrEmpty(generation_input))
            return Error("generation_input is required when exact_text is not supplied.");

        var key = new TextGenerationKey(context_key, TextGenerationCache.HashInput(generation_input));
        if (_textGeneration.TryGet(key, out var cached))
            return new { text = cached.Text, used_generator = false, cache_hit = true, source = "cache", cached_at = cached.CachedAt };

        if (string.IsNullOrEmpty(generated_text))
        {
            var selection = _modelRouting.Select(ModelRole.TextGenerator, new RequiredCapabilities(false, [], false));
            return Error(selection.Found
                ? $"Provider '{selection.ProviderId}' is configured for text generation, but DCU never makes the live model call itself -- supply generated_text produced by that provider."
                : $"No text-generation provider is configured and no generated_text was supplied (reason: {selection.Reason}). DCU never fabricates prose -- supply generated_text from a configured generator, or use exact_text.");
        }

        _textGeneration.Remember(key, generated_text);
        return new { text = generated_text, used_generator = true, cache_hit = false, source = "generated" };
    }

    [McpServerTool(Name = "preview_grid_schema"), Description("F57-R01: heading-to-schema preview for an optional predictive-grid module. Deterministically classifies each column heading (and, if supplied, its sample values) into a coarse type (date, number, email, boolean, text) using rule-based keyword/pattern matching -- no model call, so this never blocks on missing model configuration.")]
    public object PreviewGridSchema(
        [Description("Column headings in order")] string[] headings,
        [Description("Optional sample value per heading (same order/length as headings) used to refine the type guess")] string?[]? sample_values = null)
    {
        var columns = headings.Select((heading, i) => new
        {
            heading,
            inferred_type = GridSchemaPreview.InferType(heading, sample_values != null && i < sample_values.Length ? sample_values[i] : null),
        }).ToArray();
        return new { columns };
    }

    [McpServerTool(Name = "record_grid_cell_edit"), Description("F57-T01: records a direct user edit to a grid cell, bumping its edit_version. Any prediction later submitted via resolve_grid_cell against an older edit_version is rejected as stale rather than overwriting this newer edit.")]
    public object RecordGridCellEdit(
        [Description("Stable name for this grid/workbook, e.g. 'invoices_sheet'")] string context_key,
        [Description("Row identifier, e.g. a stable row id or 1-based row number as a string")] string row_id,
        [Description("Column name")] string column,
        [Description("The cell's new value after this edit")] string? value)
    {
        var edit_version = _gridPredictions.RecordEdit(new GridCellKey(context_key, row_id, column), value);
        return new { edit_version };
    }

    [McpServerTool(Name = "resolve_grid_cell"), Description("F57-R01/R02/T01/T02: resolve a predicted value for a grid cell. Checks the cache for (content_input, rubric, model_id) first, so identical row inputs never trigger a redundant request (F57-T02). On a cache miss, DCU never generates the value itself (same no-live-model-key constraint as resolve_field_text) -- the caller supplies predicted_value from its own configured generator, captured against the cell's edit_version at the time inference started. If the cell was edited since (edit_version changed), the result is rejected as stale rather than overwriting the newer edit (F57-T01); the pre-run value is otherwise recorded under batch_id for undo_grid_batch (F57-T02).")]
    public object ResolveGridCell(
        [Description("Stable name for this grid/workbook, e.g. 'invoices_sheet'")] string context_key,
        [Description("Row identifier")] string row_id,
        [Description("Column name")] string column,
        [Description("edit_version captured from get_grid_cell/record_grid_cell_edit before inference started")] int edit_version,
        [Description("Deterministic row content this prediction depends on")] string content_input,
        [Description("Rubric/prompt identifier the prediction was made under")] string rubric,
        [Description("Identifier of the model/provider that produced (or would produce) the value")] string model_id,
        [Description("Identifier for this batch of predictions, used by undo_grid_batch/keep_grid_batch")] string batch_id,
        [Description("Value already produced by the caller's own configured generator, to be cached and applied")] string? predicted_value = null)
    {
        var predictionKey = new GridPredictionKey(GridPredictionStore.HashContent(content_input), rubric, model_id);
        string value;
        bool cache_hit;
        if (_gridPredictions.TryGetCachedPrediction(predictionKey, out var cached))
        {
            value = cached.Value;
            cache_hit = true;
        }
        else if (!string.IsNullOrEmpty(predicted_value))
        {
            _gridPredictions.RememberPrediction(predictionKey, predicted_value);
            value = predicted_value;
            cache_hit = false;
        }
        else
        {
            return Error("No cached prediction for this content/rubric/model, and no predicted_value was supplied. DCU never generates grid predictions itself -- supply predicted_value from a configured generator.");
        }

        var cellKey = new GridCellKey(context_key, row_id, column);
        if (!_gridPredictions.TryApplyPrediction(cellKey, edit_version, value, batch_id))
            return new { applied = false, reason = "stale_edit_version", cache_hit, current = _gridPredictions.GetCell(cellKey) };
        return new { applied = true, value, cache_hit, edit_version = _gridPredictions.GetCell(cellKey).EditVersion };
    }

    [McpServerTool(Name = "undo_grid_batch"), Description("F57-T02: restores every cell touched by batch_id to its exact pre-run value and edit_version, then discards the batch's undo record.")]
    public object UndoGridBatch([Description("batch_id previously passed to resolve_grid_cell")] string batch_id)
    {
        if (!_gridPredictions.TryUndo(batch_id, out var restored))
            return Error("No undo record for this batch_id (already undone, kept, or never applied).");
        return new { restored_cells = restored.Count };
    }

    [McpServerTool(Name = "keep_grid_batch"), Description("F57-R01: accepts batch_id's predictions permanently by discarding its undo record.")]
    public object KeepGridBatch([Description("batch_id previously passed to resolve_grid_cell")] string batch_id) =>
        new { kept = _gridPredictions.Keep(batch_id) };

    [McpServerTool(Name = "record_task_experience"), Description("Record the outcome of a task/step (F28-R01) for an app selected by name, title substring, PID, window_id, or target_id: outcome, an optional failure_category, an optional environment note, and (automatically) the app's current file version. Requires the caller to have already verified the outcome -- DCU never infers success from an apparently-completed action, so this never promotes an unverified successful-looking run to reusable memory. Optionally also remembers element_id as the task's recipe, same shape as remember_action_resolution.")]
    public async Task<object> RecordTaskExperience(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Caller-chosen name for this task/step, e.g. 'login_submit_button'")] string task_intent,
        [Description("'success' or 'failure', asserted by the caller after verifying the real outcome")] string outcome,
        [Description("Category of failure, e.g. 'element_not_found', 'validation_rejected'; must be omitted when outcome is 'success'")] string? failure_category = null,
        [Description("Free-text note about the environment this was observed under")] string? environment = null,
        [Description("Optional element id from get_app_state to also remember as this task's recipe")] string? element_id = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task_intent))
            return Error("task_intent must not be empty.");
        if (outcome != "success" && outcome != "failure")
            return Error("outcome must be 'success' or 'failure'.");
        if (outcome == "success" && failure_category != null)
            return Error("failure_category must not be set when outcome is 'success'.");
        var err = DesktopError();
        if (err != null) return Error(err);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);

        ActionResolutionRecipe? recipe = null;
        if (element_id != null)
        {
            var snap = _uia.GetSnapshotForElement(target, element_id) ?? await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
            var element = snap.Elements.FirstOrDefault(e => e.Id == element_id);
            if (element == null)
                return Error($"Element '{element_id}' was not found in a cached snapshot. Call get_app_state first.");
            recipe = new ActionResolutionRecipe(element.AutomationId, element.ControlType, element.Name, null, DateTimeOffset.UtcNow);
        }

        var appVersion = NativeAppVersion(target.Pid);
        var key = new ExperienceKey("native", target.ProcessName, task_intent);
        _experience.Record(key, new ExperienceRecord(appVersion, environment, recipe, outcome, failure_category, DateTimeOffset.UtcNow));
        return new { ok = true, task_intent, target_identity = target.ProcessName, app_version = appVersion };
    }

    [McpServerTool(Name = "recall_task_experience"), Description("Look up experience recorded via record_task_experience for an app selected by name, title substring, PID, window_id, or target_id (F28-R01/F28-T02). If the app's live file version differs from the recorded one, returns advisory:true with no re-resolved recipe -- the caller must revalidate targets and postconditions against the current app before trusting it. Otherwise, if a recipe was stored, it is re-resolved live against the current UI exactly like resolve_cached_action.")]
    public async Task<object> RecallTaskExperience(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Name previously passed to record_task_experience")] string task_intent,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(task_intent))
            return Error("task_intent must not be empty.");
        var err = DesktopError();
        if (err != null) return Error(err);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);

        var appVersion = NativeAppVersion(target.Pid);
        var key = new ExperienceKey("native", target.ProcessName, task_intent);
        var lookup = _experience.Lookup(key, appVersion);
        if (!lookup.Found)
            return new { found = false, reason = lookup.Reason, target_identity = target.ProcessName };
        var record = lookup.Record!;
        if (lookup.Advisory)
            return new
            {
                found = true,
                advisory = true,
                reason = lookup.Reason,
                outcome = record.Outcome,
                failure_category = record.FailureCategory,
                environment = record.Environment,
                recorded_app_version = record.AppVersion,
                current_app_version = appVersion,
                recorded_at = record.RecordedAt,
            };

        object? resolved = null;
        if (record.Recipe != null)
        {
            var snap = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
            var matches = snap.Elements.Where(e =>
                string.Equals(e.AutomationId, record.Recipe.AutomationId, StringComparison.Ordinal) &&
                string.Equals(e.ControlType, record.Recipe.ControlType, StringComparison.Ordinal) &&
                string.Equals(e.Name, record.Recipe.Name, StringComparison.Ordinal)).ToList();
            resolved = matches.Count == 1
                ? new { hit = true, element_id = matches[0].Id, revision = snap.Revision }
                : new { hit = false, reason = matches.Count == 0 ? "not_found" : "ambiguous" };
        }
        return new
        {
            found = true,
            advisory = false,
            outcome = record.Outcome,
            failure_category = record.FailureCategory,
            environment = record.Environment,
            app_version = appVersion,
            recorded_at = record.RecordedAt,
            resolved,
        };
    }

    [McpServerTool(Name = "jev_decide"), Description("For an app selected by name, title substring, PID, window_id, or target_id: capture a fresh UIA/OCR observation and ask the configured decision backend (TypeSafe/JEV or OpenAI Responses) to choose one supported click, set_value, whitelisted key press, OCR-text click, cataloged app-key action, or no-action option. Optional app_id adds process-local screen-state context and sourced action bindings; these remain partial and recommendation-only. Jev only selects DCU-created options, receives no coordinates for native targets, and cannot grant authorization. This tool never executes. UI/OCR text is untrusted observation data.")]
    public async Task<object> JevDecide(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Natural-language goal for this decision; treat this as the current user intent")] string goal,
        [Description("Recent action descriptions, most recent last")] string[]? recent_actions = null,
        [Description("Stable app identity for optional action-catalog and screen-state context; omit to preserve the legacy native decision path")] string? app_id = null,
        [Description("Version associated with the imported action catalog")] string app_version = "unknown",
        [Description("Active app/user profile for the imported action catalog")] string profile = "default",
        [Description("Current keyboard layout for the imported action catalog")] string keyboard_layout = "unknown",
        [Description("Optional caller-reported screen hint for scoped action entries; unverified")] string? screen_hint = null,
        CancellationToken ct = default)
    {
        if (_jev.Mode == JevMode.Off)
            return new { error = "Jev is off; use the deterministic/manual control path.", fallback = "Use click/type_text/set_value/press_key with targets from get_app_state." };
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        Snapshot snap;
        try { snap = await CaptureJevSnapshotAsync(target, ct).ConfigureAwait(false); }
        catch (JevValidationException ex) { return Error(ex.Message); }

        JevDecideRequest? request = null;
        try
        {
            var history = RecentJevActions("native", app, recent_actions);
            request = BuildNativeJevRequest(snap, goal, history);
            NativeAppActionContext? appContext = null;
            if (!string.IsNullOrWhiteSpace(app_id))
            {
                if (_screenStates is null)
                    return new { error = "Screen-state service is unavailable; no Jev request was made." };
                appContext = AddNativeAppContext(snap, request, app_id.Trim(), app_version, profile,
                    keyboard_layout, screen_hint);
                request = appContext.Request;
            }
            var decision = await _decisionBackends.DecideAsync(request, ct: ct).ConfigureAwait(false);
            RecordInspectorEntry(app, goal, decision, $"{_decisionBackends.Name}_decide", executed: false);
            NativeKeyAction? appAction = null;
            if (appContext is not null) appContext.Actions.TryGetValue(decision.Operation, out appAction);
            return new
            {
                ok = true,
                observation_id = decision.ObservationId,
                operation = decision.Operation,
                element_id = decision.Operation == "click_visual" ? null : decision.TargetElementId,
                candidate_id = decision.Operation == "click_visual" ? decision.TargetElementId : null,
                key = NativeJevKeyBindings.TryGetValue(decision.Operation, out var suggestedKey) ? suggestedKey : null,
                app_action = appAction is null ? null : new
                {
                    action_id = appAction.ActionId,
                    label = appAction.Label,
                    key_sequence = appAction.KeySequence,
                    evidence_status = appAction.EvidenceStatus,
                    source_kind = appAction.SourceKind,
                    preconditions = appAction.Preconditions,
                    dispatch_enabled = false,
                    recommendation_only = true,
                },
                screen_state = appContext?.ScreenState,
                decision_backend = _decisionBackends.Name,
                confidence = decision.Confidence,
                target_confidence = decision.TargetConfidence,
                execution_confidence = decision.TargetConfidence is { } targetConfidence ? Math.Min(decision.Confidence, targetConfidence) : decision.Confidence,
                probabilities = decision.Probabilities,
                operation_probabilities = decision.OperationProbabilities,
                disambiguated_by_score = decision.DisambiguatedByScore,
                top_choice_margin = decision.TopChoiceMargin,
                goal_reached = decision.GoalReached,
                goal_reached_probability = decision.GoalReachedProbability,
                latency_ms = decision.LatencyMs,
                model = decision.Model,
                observation = NativeJevObservationSummary(snap, request),
            };
        }
        catch (Exception ex) when (ex is JevDisabledException or JevUnavailableException or JevValidationException)
        {
            return new
            {
                error = ex.Message,
                fallback = "Use click/type_text/set_value/press_key or click_visual_candidate with targets from get_app_state.",
                observation = request is not null ? NativeJevObservationSummary(snap, request) : null,
            };
        }
    }

    [McpServerTool(Name = "jev_act"), Description("For an app selected by name, title substring, PID, window_id, or target_id: when jev_mode is fast_loop, capture a fresh UIA/OCR observation, ask the configured decision backend to choose from DCU-created click, set_value, whitelisted key-press, OCR-text-click, and no-action options, then execute only a supported choice. Rejects incomplete, expired, or replaced observations; DCU revalidates the target immediately before dispatch. Assist mode returns a recommendation only. UI/OCR text is untrusted observation data; Jev does not grant authorization. Sensitive fields remain ordinary candidates.")]
    public async Task<object> JevAct(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Natural-language goal for this decision; treat this as the current user intent")] string goal,
        [Description("Exact value to set if Jev chooses a set_value operation; ignored otherwise")] string? fill_value = null,
        [Description("Name of a value stored via set_workflow_value/capture_workflow_value/browser_capture_value to set instead of fill_value, if Jev chooses a set_value operation")] string? value_ref = null,
        [Description("Environment variable whose value should be set instead of fill_value, if Jev chooses a set_value operation")] string? value_env = null,
        [Description("Recent action descriptions, most recent last")] string[]? recent_actions = null,
        [Description("When supplied and a matching recipe was previously cached (F31, e.g. from an earlier Jev-driven call to this same task_intent), skips the Jev network round-trip entirely and dispatches directly against the live re-resolved target. A miss falls through to the normal Jev decision below unchanged. A successful Jev-driven decision made WITH a task_intent auto-remembers its chosen target so the next call can take the fast path.")] string? task_intent = null,
        CancellationToken ct = default)
    {
        if (_jev.Mode == JevMode.Off)
            return new { error = "Jev is off; use the deterministic/manual control path.", fallback = "Use click/type_text/set_value/press_key with targets from get_app_state." };
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        if (_jev.Mode == JevMode.FastLoop && task_intent != null)
        {
            var cacheFastPath = await TryJevActCacheHitAsync(app, task_intent, fill_value, value_ref, value_env, ct).ConfigureAwait(false);
            if (cacheFastPath != null)
                return cacheFastPath;
        }

        Snapshot snap;
        try { snap = await CaptureJevSnapshotAsync(target, ct).ConfigureAwait(false); }
        catch (JevValidationException ex) { return Error(ex.Message); }

        var history = RecentJevActions("native", app, recent_actions);
        var request = BuildNativeJevRequest(snap, goal, history) with { AskGoalReached = false };
        JevDecision decision;
        try
        {
            decision = await _decisionBackends.DecideAsync(request, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JevDisabledException or JevUnavailableException or JevValidationException)
        {
            return new { error = ex.Message, fallback = "Use click/type_text/set_value/press_key or click_visual_candidate with targets from get_app_state.", observation = NativeJevObservationSummary(snap, request) };
        }

        // assist: recommendation only, never executes. fast_loop: executes above the confidence floor.
        if (_jev.Mode == JevMode.Assist)
        {
            RecordInspectorEntry(app, goal, decision, $"{_decisionBackends.Name}_assist_recommendation", executed: false);
            return new
            {
                ok = true,
                decision_source = $"{_decisionBackends.Name}_assist_recommendation",
                decision_backend = _decisionBackends.Name,
                operation = decision.Operation,
                element_id = decision.Operation == "click_visual" ? null : decision.TargetElementId,
                candidate_id = decision.Operation == "click_visual" ? decision.TargetElementId : null,
                key = NativeJevKeyBindings.TryGetValue(decision.Operation, out var suggestedKey) ? suggestedKey : null,
                confidence = decision.Confidence,
                target_confidence = decision.TargetConfidence,
                execution_confidence = decision.TargetConfidence is { } assistTargetConfidence ? Math.Min(decision.Confidence, assistTargetConfidence) : decision.Confidence,
                probabilities = decision.Probabilities,
                operation_probabilities = decision.OperationProbabilities,
                disambiguated_by_score = decision.DisambiguatedByScore,
                top_choice_margin = decision.TopChoiceMargin,
                goal_reached = decision.GoalReached,
                latency_ms = decision.LatencyMs,
                model = decision.Model,
                observation = NativeJevObservationSummary(snap, request),
                note = "Jev is in assist mode: this is a recommendation only. Execute a supported choice yourself, or switch jev_mode to fast_loop for Jev to act directly.",
            };
        }

        if (IsNoActionOperation(decision.Operation))
        {
            RecordInspectorEntry(app, goal, decision, decision.Operation, executed: false);
            return new
            {
                ok = true,
                operation = decision.Operation,
                confidence = decision.Confidence,
                target_confidence = decision.TargetConfidence,
                execution_confidence = decision.Confidence,
                probabilities = decision.Probabilities,
                operation_probabilities = decision.OperationProbabilities,
                disambiguated_by_score = decision.DisambiguatedByScore,
                top_choice_margin = decision.TopChoiceMargin,
                goal_reached = decision.GoalReached,
                completion_suggested = decision.Operation == "DONE",
                latency_ms = decision.LatencyMs,
                model = decision.Model,
                observation = NativeJevObservationSummary(snap, request),
            };
        }

        if (decision.IsExpired())
            return new { error = "The Jev decision expired before dispatch; reobserve and retry." };

        var executionConfidence = decision.TargetConfidence is { } chosenTargetConfidence
            ? Math.Min(decision.Confidence, chosenTargetConfidence)
            : decision.Confidence;
        if (executionConfidence < _jev.MinExecuteConfidence)
        {
            RecordInspectorEntry(app, goal, decision, $"{_decisionBackends.Name}_low_confidence_escalation", executed: false);
            var suggestedElement = request.Elements.FirstOrDefault(element => element.Id == decision.TargetElementId);
            return new
            {
                ok = true,
                decision_source = $"{_decisionBackends.Name}_low_confidence_escalation",
                operation = decision.Operation,
                selected_candidate = suggestedElement is null ? null : new { role = suggestedElement.Role, name = suggestedElement.Name },
                element_id = decision.Operation == "click_visual" ? null : decision.TargetElementId,
                candidate_id = decision.Operation == "click_visual" ? decision.TargetElementId : null,
                key = NativeJevKeyBindings.TryGetValue(decision.Operation, out var suggestedKey) ? suggestedKey : null,
                confidence = decision.Confidence,
                target_confidence = decision.TargetConfidence,
                execution_confidence = executionConfidence,
                probabilities = decision.Probabilities,
                operation_probabilities = decision.OperationProbabilities,
                disambiguated_by_score = decision.DisambiguatedByScore,
                top_choice_margin = decision.TopChoiceMargin,
                threshold = _jev.MinExecuteConfidence,
                model = decision.Model,
                decision_backend = _decisionBackends.Name,
                observation = NativeJevObservationSummary(snap, request),
                note = "Confidence below the fast_loop execute threshold; nothing was executed. Reobserve and retry, or use the matching manual DCU action if authorized.",
            };
        }

        var currentSnap = _uia.GetSnapshot(target);
        if (currentSnap is null || decision.IsStaleAgainst(currentSnap.ObservationId))
            return new { error = "The snapshot changed since Jev decided; the stale decision was rejected. Reobserve and retry." };

        if (NativeJevKeyBindings.TryGetValue(decision.Operation, out var key))
        {
            var keyResult = await PressKey(app, key, ct: ct).ConfigureAwait(false);
            bool keySucceeded = !HasFailed(keyResult);
            RecordInspectorEntry(app, goal, decision, keySucceeded ? _decisionBackends.Name + "_key" : _decisionBackends.Name + "_dispatch_failed", executed: keySucceeded);
            return new { decision_source = keySucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", decision_backend = _decisionBackends.Name, operation = "press_key", key, confidence = decision.Confidence, target_confidence = decision.TargetConfidence, execution_confidence = executionConfidence, probabilities = decision.Probabilities, operation_probabilities = decision.OperationProbabilities, disambiguated_by_score = decision.DisambiguatedByScore, top_choice_margin = decision.TopChoiceMargin, latency_ms = decision.LatencyMs, model = decision.Model, observation = NativeJevObservationSummary(snap, request), result = keyResult };
        }

        if (decision.Operation == "click_visual")
        {
            if (decision.TargetElementId is null || currentSnap.Visual is null || currentSnap.Visual.Candidates.All(c => c.Id != decision.TargetElementId))
                return new { error = "Jev's OCR candidate is no longer present in this observation. Reobserve and retry." };
            var visualResult = await ClickVisualCandidate(app, decision.TargetElementId, ct: ct).ConfigureAwait(false);
            bool visualSucceeded = !HasFailed(visualResult);
            RecordInspectorEntry(app, goal, decision, visualSucceeded ? _decisionBackends.Name + "_visual" : _decisionBackends.Name + "_dispatch_failed", executed: visualSucceeded);
            return new { decision_source = visualSucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", decision_backend = _decisionBackends.Name, operation = decision.Operation, candidate_id = decision.TargetElementId, confidence = decision.Confidence, target_confidence = decision.TargetConfidence, execution_confidence = executionConfidence, probabilities = decision.Probabilities, operation_probabilities = decision.OperationProbabilities, disambiguated_by_score = decision.DisambiguatedByScore, top_choice_margin = decision.TopChoiceMargin, latency_ms = decision.LatencyMs, model = decision.Model, observation = NativeJevObservationSummary(snap, request), result = visualResult };
        }

        if (decision.TargetElementId is null || currentSnap.Elements.All(e => e.Id != decision.TargetElementId))
            return new { error = "Jev's chosen UIA element is no longer present. Reobserve and retry." };

        // F51-R01: a separate timeline entry for the decision phase itself (Click/SetValue already
        // record their own dispatch+settle entry) -- a caller reconstructing a fast-loop step's
        // breakdown pairs this jev_decide entry with the click/set_value entry immediately after it.
        _timeline.Record(new RunTimelineEvent("native", app, "jev_decide", decision.TargetElementId, true, null, decision.LatencyMs, DateTimeOffset.UtcNow, DecisionMs: decision.LatencyMs));

        if (decision.Operation == "set_value")
        {
            string resolvedValue;
            try { resolvedValue = WorkflowValueStore.Resolve(fill_value, value_ref, value_env, _workflowValues); }
            catch (InvalidOperationException ex) { return new { error = ex.Message, element_id = decision.TargetElementId }; }
            var setResult = await SetValue(app, decision.TargetElementId, resolvedValue, ct: ct).ConfigureAwait(false);
            bool setSucceeded = !HasFailed(setResult);
            RecordInspectorEntry(app, goal, decision, setSucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", executed: setSucceeded);
            if (task_intent != null && setSucceeded) await RememberActionResolution(app, decision.TargetElementId, task_intent, ct).ConfigureAwait(false);
            return new { decision_source = setSucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", decision_backend = _decisionBackends.Name, operation = decision.Operation, element_id = decision.TargetElementId, confidence = decision.Confidence, target_confidence = decision.TargetConfidence, execution_confidence = executionConfidence, probabilities = decision.Probabilities, operation_probabilities = decision.OperationProbabilities, disambiguated_by_score = decision.DisambiguatedByScore, top_choice_margin = decision.TopChoiceMargin, latency_ms = decision.LatencyMs, model = decision.Model, observation = NativeJevObservationSummary(snap, request), result = setResult };
        }

        var clickResult = await Click(app, element_id: decision.TargetElementId, ct: ct).ConfigureAwait(false);
        bool clickSucceeded = !HasFailed(clickResult);
        RecordInspectorEntry(app, goal, decision, clickSucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", executed: clickSucceeded);
        if (task_intent != null && clickSucceeded) await RememberActionResolution(app, decision.TargetElementId, task_intent, ct).ConfigureAwait(false);
        return new { decision_source = clickSucceeded ? _decisionBackends.Name : _decisionBackends.Name + "_dispatch_failed", decision_backend = _decisionBackends.Name, operation = decision.Operation, element_id = decision.TargetElementId, confidence = decision.Confidence, target_confidence = decision.TargetConfidence, execution_confidence = executionConfidence, probabilities = decision.Probabilities, operation_probabilities = decision.OperationProbabilities, disambiguated_by_score = decision.DisambiguatedByScore, top_choice_margin = decision.TopChoiceMargin, latency_ms = decision.LatencyMs, model = decision.Model, observation = NativeJevObservationSummary(snap, request), result = clickResult };
    }

    // F28/F31 convergence: the jev_act cache-hit fast path. Returns null on a cache miss (caller
    // falls through to the normal Jev decision), or the final tool response on a hit. Reuses
    // ResolveCachedAction/RememberActionResolution directly -- no separate resolution logic.
    private async Task<object?> TryJevActCacheHitAsync(string app, string task_intent, string? fill_value, string? value_ref, string? value_env, CancellationToken ct)
    {
        var checkStarted = Stopwatch.GetTimestamp();
        var cacheResult = await ResolveCachedAction(app, task_intent, ct).ConfigureAwait(false);
        var checkMs = (long)Stopwatch.GetElapsedTime(checkStarted).TotalMilliseconds;
        var node = JsonSerializer.SerializeToNode(cacheResult)!;
        if (node["hit"]?.GetValue<bool>() != true)
            return null;
        var elementId = node["element_id"]!.GetValue<string>();
        // A real, measured (not zeroed) decision-phase duration for this fast path -- distinct from
        // "jev_decide"'s own entries in get_benchmark_report, so a caller can directly compare the
        // network-decision cost against the live-recipe-recheck cost for the same task_intent.
        _timeline.Record(new RunTimelineEvent("native", app, "jev_act_cache_hit", elementId, true, null, checkMs, DateTimeOffset.UtcNow, DecisionMs: checkMs));

        if (fill_value != null || value_ref != null || value_env != null)
        {
            string resolvedValue;
            try { resolvedValue = WorkflowValueStore.Resolve(fill_value, value_ref, value_env, _workflowValues); }
            catch (InvalidOperationException ex) { return new { error = ex.Message, element_id = elementId }; }
            var setResult = await SetValue(app, elementId, resolvedValue, ct: ct).ConfigureAwait(false);
            return new { decision_source = "cache_hit_fast_path", operation = "set_value", element_id = elementId, task_intent, decision_ms = checkMs, result = setResult };
        }
        var clickResult = await Click(app, element_id: elementId, ct: ct).ConfigureAwait(false);
        return new { decision_source = "cache_hit_fast_path", operation = "click", element_id = elementId, task_intent, decision_ms = checkMs, result = clickResult };
    }

    [McpServerTool(Name = "jev_inspect"), Description("Read-only: the most recent jev_decide/jev_act decisions (target, decision source, operation, element, probabilities, goal_reached, latency, whether it executed). Non-activating: never changes the selected action, never steals focus, and calling it does not itself execute or decide anything. Useful for comparing jev_mode off/assist/fast_loop.")]
    public object JevInspect([Description("Maximum recent decisions to return, most recent first")] int count = 10)
        => new { entries = _inspector.Recent(count) };

    [McpServerTool(Name = "get_run_timeline"), Description("Read-only: recent recorded actions (click/set_value on the native side) from the local run timeline JSON-lines file, if EnableRunTimeline is on (off by default). Lets a failed fixture be inspected after the run without reproducing it (F48). Returns enabled:false and an empty list when tracing is off.")]
    public object GetRunTimeline([Description("Maximum recent events to return, most recent last")] int count = 50)
        => new { enabled = _timeline.Enabled, entries = _timeline.ReadRecent(count) };

    [McpServerTool(Name = "get_benchmark_report"), Description("F53: aggregates the last N run-timeline entries (same source as get_run_timeline, requires EnableRunTimeline) into per-action groups -- count, ok_count, avg/min/max total_ms, avg decision/dispatch/settle_ms where present, and which phase dominated. Groups jev-driven calls (a real DecisionMs was recorded) separately from non-jev calls of the same action, so a jev=off vs jev-assist/fast-loop comparison for the SAME action is a direct read, not a manual computation. Returns enabled:false and an empty list when tracing is off.")]
    public object GetBenchmarkReport([Description("How many recent run-timeline entries to aggregate over")] int count = 200)
    {
        if (!_timeline.Enabled) return new { enabled = false, groups = Array.Empty<object>() };
        var entries = _timeline.ReadRecent(count);
        return new { enabled = true, entries_considered = entries.Count, groups = RunTimelineBenchmark.Summarize(entries) };
    }

    [McpServerTool(Name = "wait_for"), Description("Server-side wait in an app selected by name, title substring, PID, window_id, or target_id: polls until text appears (or an element with that name exists), or the window title changes. Avoids snapshot round-trips.")]
    public async Task<object> WaitFor(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("text_exists | element_exists | window_active")] string condition,
        [Description("Text/element name to wait for")] string? text = null,
        [Description("Timeout seconds")] double timeout_s = 10,
        CancellationToken ct = default)
    {
        var validationError = ToolValidation.ValidatePositiveFinite(timeout_s, 300, nameof(timeout_s));
        if (validationError != null) return Error(validationError);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeout_s);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!_uia.TryValidateTarget(target, out targetIdentityError))
                return Error(targetIdentityError);
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

    [McpServerTool(Name = "wait_for_visual_state"), Description("F47: semantic/visual completion, waiting and disambiguation check via the F05 OCR pass, for use when DOM/UIA structure is insufficient -- especially canvas/custom-drawn UIs where get_app_state's structured evidence is empty. App is selected by name, title substring, PID, window_id, or target_id. Polls (not a fixed sleep) until a recognized line of text containing `text` appears (mode=appears, default) or is no longer present (mode=disappears), or timeout_s elapses. Honest scope: exact/substring text matching against real OCR output only, not a free-form natural-language question -- a configured judge/VLM model for richer semantic questions (F27) is not wired up in this build.")]
    public async Task<object> WaitForVisualState(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("Text (or substring, case-insensitive) to wait for in the OCR-recognized text")] string text,
        [Description("appears (default) | disappears")] string mode = "appears",
        [Description("Timeout seconds")] double timeout_s = 10,
        CancellationToken ct = default)
    {
        if (mode != "appears" && mode != "disappears")
            return Error("mode must be 'appears' or 'disappears'.");
        var validationError = ToolValidation.ValidatePositiveFinite(timeout_s, 300, nameof(timeout_s));
        if (validationError != null) return Error(validationError);
        var target = await _uia.ResolveAppAsync(app, ct).ConfigureAwait(false);
        if (!_uia.TryValidateTarget(target, out var targetIdentityError))
            return Error(targetIdentityError);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeout_s);
        string? lastUnsupportedReason = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_uia.TryValidateTarget(target, out targetIdentityError))
                return Error(targetIdentityError);
            var outcome = await ShadowUse.Vision.VisualGroundingService.RecognizeAsync(target.Hwnd, ct).ConfigureAwait(false);
            if (outcome.Observation != null)
            {
                var match = outcome.Observation.Candidates.FirstOrDefault(c => c.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
                var present = match != null;
                if ((mode == "appears" && present) || (mode == "disappears" && !present))
                    return new { ok = true, found = present, mode, source = "visual_ocr", matched_text = match?.Text, confidence = match?.Confidence };
            }
            else
            {
                lastUnsupportedReason = outcome.UnsupportedReason;
            }
            if (DateTime.UtcNow >= deadline)
                return new { ok = false, found = false, mode, source = "visual_ocr", timeout_s, unsupported_reason = lastUnsupportedReason };
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    [McpServerTool(Name = "execute_sequence"), Description("Run bounded server-side sequences for an app selected by name, title substring, PID, window_id, or target_id, or for a browser session. Game sequences contain only game_* steps and are limited to 12 steps and 120 seconds. Game examples: game_key action=down, game_wait condition=camera_settled, game_screenshot; then a later inspected sequence can game_move, game_key fire, game_key action=up, and game_screenshot. Sequence-owned held keys/buttons are released on cancellation or sequence failure. Game results are compact by default: target metadata identical on every step is reported once under shared, null fields are omitted, and each step keeps its interruption details, frame ids and image_count (images are attached in step order). When a game sequence stops on a failed step, the last frame is attached (a game_wait timeout supplies its own; otherwise one is captured) and stop_frame names its image_index, so no further screenshot step is needed. Steps: [{\"tool\":\"click\",\"args\":{...}}, ...].")]
    public async Task<CallToolResult> ExecuteSequence(
        [Description("App name, title substring, PID, window_id, or target_id")] string app,
        [Description("JSON array of steps: [{\"tool\":\"click\",\"args\":{\"element_id\":\"e3\"}}, ...]")] string steps_json,
        [Description("Stop on first failed step")] bool stop_on_error = true,
        [Description("1-based step index to resume from; earlier steps are skipped entirely, not just fast-forwarded")] int start_at_step = 1,
        [Description("Game sequences only: report shared target metadata once and omit null fields (default true). false returns every step in full")] bool compact = true,
        CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(steps_json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return SequenceReply(new { error = "steps_json must be a JSON array" }, isError: true);
        if (start_at_step < 1)
            return SequenceReply(new { error = "start_at_step must be 1 or greater." }, isError: true);
        var steps = doc.RootElement.EnumerateArray().ToArray();
        bool hasGame = steps.Any(step => Str(step, "tool")?.StartsWith("game_", StringComparison.Ordinal) == true);
        if (hasGame)
        {
            if (steps_json.Length > 65_536) return SequenceReply(new { error = "Game sequences are limited to 64 KiB of JSON." }, isError: true);
            if (steps.Length > 12) return SequenceReply(new { error = "Game sequences are limited to 12 steps." }, isError: true);
            if (steps.Any(step => ToolCatalog.Get(Str(step, "tool") ?? "") is not { Game: true }))
                return SequenceReply(new { error = "A game sequence can contain only game_* tools." }, isError: true);
            return await ExecuteGameSequenceAsync(app, steps, stop_on_error, start_at_step, compact, ct).ConfigureAwait(false);
        }

        var results = new List<object>();
        int i = 0;
        foreach (var step in steps)
        {
            i++;
            if (i < start_at_step) continue;
            ct.ThrowIfCancellationRequested();
            string tool = step.GetProperty("tool").GetString() ?? "";
            var args = step.TryGetProperty("args", out var a) ? a : default;
            var r = await DispatchStep(app, tool, args, ct).ConfigureAwait(false);
            results.Add(new { step = i, tool, result = r });
            if (HasFailed(r) && stop_on_error)
                return SequenceReply(new { ok = false, stopped_at = i, resumed_from = start_at_step, results });
        }
        return SequenceReply(new { ok = true, steps_run = results.Count, resumed_from = start_at_step, results });
    }

    private async Task<CallToolResult> ExecuteGameSequenceAsync(string app, JsonElement[] steps, bool stopOnError, int startAtStep, bool compact, CancellationToken ct)
    {
        if (_game is null) return SequenceReply(new { error = "Game tools are unavailable in this process." }, isError: true);
        var (hwnd, resolveError) = await _game.ResolveSequenceTargetAsync(app, ct).ConfigureAwait(false);
        if (resolveError != null) return resolveError;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(120));
        var rows = new List<(int Step, string Tool, JsonElement Payload, int ImageCount)>();
        var images = new List<ContentBlock>();
        object? stopFrame = null;
        var ownedKeys = new HashSet<int>(GameTools.HeldSequenceInputs(hwnd));
        var clock = Stopwatch.StartNew();
        System.Text.Json.Nodes.JsonObject? sharedMetadata = null;
        System.Text.Json.Nodes.JsonObject[]? compacted = null;
        void Build()
        {
            if (compacted != null || !compact) return;
            (sharedMetadata, compacted) = GameSequenceResult.Compact(rows.Select(r => (r.Tool, r.Payload)).ToArray());
        }
        object? Shared() { Build(); return sharedMetadata is { Count: > 0 } ? sharedMetadata : null; }
        object[] Results()
        {
            Build();
            return rows.Select((r, i) => (object)new { step = r.Step, tool = r.Tool, result = compact ? compacted![i] : (object)r.Payload, image_count = r.ImageCount }).ToArray();
        }
        CallToolResult Done(Dictionary<string, object?> head)
        {
            head["compact"] = compact;
            head["shared"] = Shared();
            head["results"] = Results();
            head["stop_frame"] = stopFrame;
            foreach (var key in head.Where(p => p.Value is null).Select(p => p.Key).ToArray()) head.Remove(key);
            return SequenceReply(head, images);
        }
        bool failed = false;
        int? stoppedAt = null;
        try
        {
            for (int i = 0; i < steps.Length; i++)
            {
                int stepNumber = i + 1;
                if (stepNumber < startAtStep) continue;
                budget.Token.ThrowIfCancellationRequested();
                var step = steps[i];
                string tool = Str(step, "tool") ?? "";
                var args = step.TryGetProperty("args", out var a) ? a : default;
                var requestedKeys = tool == "game_key" && string.Equals(Str(args, "action"), "down", StringComparison.OrdinalIgnoreCase)
                    ? InteractiveInput.ParseKeySet(Str(args, "key") ?? Str(args, "keys") ?? "") : [];
                foreach (var vk in requestedKeys) ownedKeys.Add(vk);
                var result = await DispatchGameStep(app, tool, args, budget.Token).ConfigureAwait(false);
                var payload = GameResultPayload(result);
                int imageStart = images.Count;
                images.AddRange(result.Content.Where(c => c is ImageContentBlock));
                bool stepFailed = GameResultFailed(result, payload);
                rows.Add((stepNumber, tool, payload, images.Count - imageStart));
                if (!stepFailed && tool == "game_key" && string.Equals(Str(args, "action"), "up", StringComparison.OrdinalIgnoreCase))
                    foreach (var vk in InteractiveInput.ParseKeySet(Str(args, "key") ?? Str(args, "keys") ?? "")) ownedKeys.Remove(vk);
                if (!stepFailed && tool == "game_release_keys") ownedKeys.Clear();
                if (stepFailed)
                {
                    failed = true;
                    if (stopOnError)
                    {
                        stoppedAt = stepNumber;
                        stopFrame = await StopFrameAsync(app, images, imageStart, ct).ConfigureAwait(false);
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            int released = GameTools.ReleaseSequenceInputs(hwnd, ownedKeys);
            if (ct.IsCancellationRequested) throw;
            return Done(new() { ["ok"] = false, ["error"] = "Game sequence exceeded its 120-second time limit.", ["steps_run"] = rows.Count, ["released_on_timeout"] = released });
        }
        catch (Exception ex)
        {
            int released = GameTools.ReleaseSequenceInputs(hwnd, ownedKeys);
            return Done(new() { ["ok"] = false, ["error"] = $"Game sequence failed: {ex.GetType().Name}: {ex.Message}", ["steps_run"] = rows.Count, ["released_on_failure"] = released });
        }
        int releasedOnFailure = failed ? GameTools.ReleaseSequenceInputs(hwnd, ownedKeys) : 0;
        return Done(new() { ["ok"] = !failed, ["stopped_at"] = stoppedAt, ["steps_run"] = rows.Count, ["resumed_from"] = startAtStep,
            ["elapsed_ms"] = clock.ElapsedMilliseconds, ["released_on_failure"] = releasedOnFailure });
    }

    /// <summary>The frame to look at after a stopped sequence: the failed step's own image when it returned one (a game_wait timeout does), otherwise one fresh capture. Null when no frame could be read.</summary>
    private async Task<object?> StopFrameAsync(string app, List<ContentBlock> images, int failedStepImageStart, CancellationToken ct)
    {
        if (_game is null) return null;
        if (images.Count > failedStepImageStart) return new { image_index = images.Count - 1, source = "failed_step" };
        try
        {
            var (jpeg, frameId) = await _game.CaptureStopFrameAsync(app, ct).ConfigureAwait(false);
            if (jpeg == null) return null;
            images.Add(ImageContentBlock.FromBytes(jpeg, "image/jpeg"));
            return new { image_index = images.Count - 1, source = "captured_after_failure", frame_id = frameId };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    private async Task<CallToolResult> DispatchGameStep(string app, string tool, JsonElement args, CancellationToken ct)
    {
        if (_game is null) return SequenceReply(new { ok = false, error = "Game tools are unavailable in this process." }, isError: true);
        return tool switch
        {
            "game_screenshot" => await _game.GameScreenshot(null!, app, Int(args, "max_width") ?? 1280, Int(args, "quality") ?? 60,
                Int(args, "x"), Int(args, "y"), Int(args, "width"), Int(args, "height"), Dbl(args, "zoom") ?? 1,
                Bool(args, "bring_to_front") ?? true, Str(args, "exclude_boxes"), Str(args, "bars_json"),
                Bool(args, "include_ui_candidates") ?? false, Bool(args, "include_vlm_candidates") ?? false,
                Bool(args, "include_local_fast_candidates") ?? false, Bool(args, "include_local_yolo_candidates") ?? false,
                Str(args, "local_candidate_method") ?? "edge", Str(args, "visual_scope") ?? "All visible UI-like regions",
                Bool(args, "use_cached_candidates") ?? false, Str(args, "app_id"), Str(args, "app_version") ?? "unknown",
                Str(args, "profile") ?? "default", Str(args, "keyboard_layout") ?? "unknown", Str(args, "screen_hint"), ct).ConfigureAwait(false),
            "game_click" => await _game.GameClick(app, Int(args, "x") ?? 0, Int(args, "y") ?? 0,
                Str(args, "button") ?? "left", Int(args, "click_count") ?? 1, Int(args, "hold_ms") ?? Int(args, "duration_ms") ?? 0,
                Str(args, "coord_space") ?? "image", Str(args, "pace"), Int(args, "max_latency_ms") ?? 0, Str(args, "observe"), Str(args, "observe_box"),
                Bool(args, "verify") ?? true, Bool(args, "then_screenshot") ?? false, Str(args, "source_frame_id"), Str(args, "progress_box"),
                Str(args, "expected_effect"), Str(args, "bars_json"), Str(args, "exclude_boxes"),
                Int(args, "observe_timeout_ms") ?? -1, Int(args, "observe_settle_ms") ?? -1, Int(args, "observe_poll_ms") ?? -1,
                Str(args, "app_id"), Str(args, "action_id"), ct).ConfigureAwait(false),
            "game_move" => await _game.GameMove(app, Int(args, "x"), Int(args, "y"), Int(args, "dx"), Int(args, "dy"),
                Int(args, "dwell_ms") ?? 0, Str(args, "coord_space") ?? "image", Str(args, "pace"), Int(args, "max_latency_ms") ?? 0,
                Str(args, "observe"), Str(args, "observe_box"), Bool(args, "verify") ?? true, Bool(args, "then_screenshot") ?? false,
                Str(args, "source_frame_id"), Str(args, "progress_box"), Str(args, "expected_effect"), Str(args, "bars_json"), Str(args, "exclude_boxes"),
                Int(args, "observe_timeout_ms") ?? -1, Int(args, "observe_settle_ms") ?? -1, Int(args, "observe_poll_ms") ?? -1, ct).ConfigureAwait(false),
            "game_drag" => await _game.GameDrag(app, Int(args, "from_x") ?? 0, Int(args, "from_y") ?? 0, Int(args, "to_x") ?? 0, Int(args, "to_y") ?? 0,
                Str(args, "coord_space") ?? "image", Str(args, "pace"), Int(args, "max_latency_ms") ?? 0, Str(args, "observe"), Str(args, "observe_box"),
                Bool(args, "verify") ?? true, Bool(args, "then_screenshot") ?? false, Str(args, "source_frame_id"), Str(args, "progress_box"),
                Str(args, "expected_effect"), Str(args, "bars_json"), Str(args, "exclude_boxes"),
                Int(args, "observe_timeout_ms") ?? -1, Int(args, "observe_settle_ms") ?? -1, Int(args, "observe_poll_ms") ?? -1, ct).ConfigureAwait(false),
            "game_scroll" => await _game.GameScroll(app, Str(args, "direction") ?? "down", Dbl(args, "pages") ?? 1,
                Int(args, "x"), Int(args, "y"), Str(args, "coord_space") ?? "image", Str(args, "pace"), Int(args, "max_latency_ms") ?? 0,
                Str(args, "observe"), Str(args, "observe_box"), Bool(args, "verify") ?? true, Bool(args, "then_screenshot") ?? false,
                Str(args, "source_frame_id"), Str(args, "progress_box"), Str(args, "expected_effect"), Str(args, "bars_json"), Str(args, "exclude_boxes"),
                Int(args, "observe_timeout_ms") ?? -1, Int(args, "observe_settle_ms") ?? -1, Int(args, "observe_poll_ms") ?? -1, ct).ConfigureAwait(false),
            "game_key" => await _game.GameKey(app, Str(args, "key"), Str(args, "action") ?? "tap", Int(args, "duration_ms") ?? 0,
                Int(args, "interval_ms") ?? 100, Str(args, "keys"), Str(args, "pace"), Int(args, "max_latency_ms") ?? 0,
                Str(args, "observe"), Str(args, "observe_box"), Bool(args, "verify") ?? true, Bool(args, "then_screenshot") ?? false,
                Str(args, "progress_box"), Str(args, "expected_effect"), Str(args, "bars_json"), Str(args, "exclude_boxes"),
                Int(args, "observe_timeout_ms") ?? -1, Int(args, "observe_settle_ms") ?? -1, Int(args, "observe_poll_ms") ?? -1,
                Str(args, "source_frame_id"), Str(args, "app_id"), Str(args, "action_id"), ct).ConfigureAwait(false),
            "game_release_keys" => await _game.GameReleaseKeys(app, ct).ConfigureAwait(false),
            "game_ocr" => await _game.GameOcr(app, Int(args, "x"), Int(args, "y"), Int(args, "width"), Int(args, "height"),
                Int(args, "upscale") ?? 0, Str(args, "contains"), Bool(args, "bring_to_front") ?? true, ct).ConfigureAwait(false),
            "game_bars" => await _game.GameBars(app, Str(args, "bars_json") ?? "[]", Bool(args, "bring_to_front") ?? true, ct).ConfigureAwait(false),
            "game_wait" => await _game.GameWait(app, Str(args, "condition") ?? "frame_changes", Str(args, "text"), Int(args, "x"), Int(args, "y"),
                Int(args, "width"), Int(args, "height"), Dbl(args, "timeout_s") ?? 10, Int(args, "stable_ms") ?? -1,
                Int(args, "poll_ms") ?? 250, Str(args, "exclude_boxes"), Bool(args, "final_screenshot") ?? true, ct).ConfigureAwait(false),
            "game_track" => await _game.GameTrack(app, Str(args, "operation") ?? "start", Str(args, "track_id"), Str(args, "source_frame_id"),
                Int(args, "x"), Int(args, "y"), Int(args, "width"), Int(args, "height"), ct).ConfigureAwait(false),
            "game_follow" => await _game.GameFollow(app, Str(args, "track_id") ?? "", Int(args, "duration_ms") ?? 1000,
                Bool(args, "dry_run") ?? true, Int(args, "poll_ms") ?? 100, ct).ConfigureAwait(false),
            _ => SequenceReply(new { ok = false, error = $"Unknown game tool '{tool}'." }, isError: true),
        };
    }

    private static JsonElement GameResultPayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        return doc.RootElement.Clone();
    }

    private static bool GameResultFailed(CallToolResult result, JsonElement payload)
        => result.IsError == true || payload.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;

    private static CallToolResult SequenceReply(object payload, IEnumerable<ContentBlock>? attachments = null, bool isError = false)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(payload) } };
        if (attachments != null) content.AddRange(attachments);
        return new CallToolResult { Content = content, IsError = isError };
    }

    /// <summary>Shared step-dispatch table for execute_sequence and run_workflow (F17/F29) -- one
    /// routing table, not two. Never throws for a caller-triggerable failure (target/element/value
    /// errors all come back as an {error:...} result); only genuine cancellation propagates.</summary>
    /// <summary>F18-T02: the exact set of tool names DispatchStep/DispatchBrowserStep's switch
    /// statements recognise. invoke_helper checks every node's tool against this BEFORE
    /// dispatching anything, so a helper referencing an unrecognised tool (there is no
    /// process-launch tool in DCU at all, by construction) is refused up front for the whole
    /// invocation rather than failing mid-run on whichever node happens to reach it first.</summary>
    private static readonly IReadOnlySet<string> KnownWorkflowTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "click", "type_text", "press_key", "scroll", "drag", "set_value", "wait_for",
        "capture_workflow_value", "set_workflow_value", "read_file", "write_file", "http_request", "transform_data",
        "browser_open", "browser_observe", "browser_click", "browser_fill", "browser_capture_value", "browser_wait",
    };

    private async Task<object> DispatchStep(string app, string tool, JsonElement args, CancellationToken ct)
    {
        try
        {
            return tool switch
            {
                "click" => await Click(app,
                    Str(args, "element_id"), Int(args, "x"), Int(args, "y"),
                    Str(args, "button") ?? "left", Int(args, "click_count") ?? 1,
                    Str(args, "background_mode"), ct: ct).ConfigureAwait(false),
                "type_text" => await TypeText(app, Str(args, "text") ?? "", background_mode: Str(args, "background_mode"), ct: ct).ConfigureAwait(false),
                "press_key" => await PressKey(app, Str(args, "key") ?? "", Str(args, "background_mode"), ct: ct).ConfigureAwait(false),
                "scroll" => await Scroll(app, Str(args, "direction") ?? "down", Str(args, "element_id"),
                    Dbl(args, "pages") ?? 1.0, Str(args, "background_mode"), ct).ConfigureAwait(false),
                "drag" => await Drag(app, Int(args, "from_x") ?? 0, Int(args, "from_y") ?? 0,
                    Int(args, "to_x") ?? 0, Int(args, "to_y") ?? 0, Str(args, "background_mode"), ct).ConfigureAwait(false),
                "set_value" => await SetValue(app, Str(args, "element_id") ?? "", Str(args, "value") ?? "", background_mode: Str(args, "background_mode") ?? "strict_background", ct: ct).ConfigureAwait(false),
                "wait_for" => await WaitFor(app, Str(args, "condition") ?? "text_exists", Str(args, "text"),
                    Dbl(args, "timeout_s") ?? 10, ct).ConfigureAwait(false),
                "capture_workflow_value" => await CaptureWorkflowValue(app, Str(args, "element_id") ?? "", Str(args, "name") ?? "", ct).ConfigureAwait(false),
                "set_workflow_value" => SetWorkflowValue(Str(args, "name") ?? "", Str(args, "value") ?? ""),
                "read_file" => ReadFile(Str(args, "allowed_root") ?? "", Str(args, "path") ?? "", Str(args, "name") ?? "", ct),
                "write_file" => WriteFile(Str(args, "allowed_root") ?? "", Str(args, "path") ?? "", Str(args, "value"), Str(args, "value_ref"), Str(args, "value_env"), ct),
                "http_request" => await HttpRequest(Str(args, "url") ?? "", Str(args, "allowed_hosts") ?? "", Str(args, "name") ?? "",
                    Str(args, "method") ?? "GET", Str(args, "value"), Str(args, "value_ref"), Str(args, "value_env"), Int(args, "timeout_ms") ?? 15_000, ct).ConfigureAwait(false),
                "transform_data" => TransformData(Str(args, "value_ref") ?? "", Str(args, "op") ?? "", Str(args, "name") ?? ""),
                "game_screenshot" or "game_click" or "game_move" or "game_drag" or "game_scroll" or "game_key"
                    or "game_release_keys" or "game_ocr" or "game_bars" or "game_wait" or "game_track" or "game_follow"
                    => await DispatchGameStep(app, tool, args, ct).ConfigureAwait(false),
                "browser_open" or "browser_observe" or "browser_click" or "browser_fill"
                    or "browser_capture_value" or "browser_wait" => await DispatchBrowserStep(tool, args, ct).ConfigureAwait(false),
                _ => Error($"Unknown tool '{tool}'")
            };
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct)) { return Error(ex.Message); }
    }

    /// <summary>F29: the browser half of DispatchStep's shared routing table -- a workflow node can
    /// mix native window-scoped steps and browser session-scoped steps in the same DAG. Browser
    /// steps carry their own session_id inside args (there is no app-name concept for a page), so
    /// the node's own "app" field is unused for these tool names. Only the subset of browser tools
    /// useful inside a linear/DAG step (open/observe/click/fill/capture_value/wait) is wired here;
    /// download/upload/dialog/extract/webmcp/jev_act remain callable directly via MCP but not yet
    /// as workflow node tools.</summary>
    private async Task<object> DispatchBrowserStep(string tool, JsonElement args, CancellationToken ct)
    {
        if (_browser is null)
            return Error("Browser tools are unavailable in this process.");
        return tool switch
        {
            "browser_open" => await _browser.Open(
                Str(args, "url"), Str(args, "mode") ?? "fresh_isolated", Str(args, "channel") ?? "msedge",
                Str(args, "cdp_endpoint"), Str(args, "profile_path"), Str(args, "download_root"), Str(args, "upload_root"),
                Bool(args, "headless") ?? true, Int(args, "debugging_port"), ct).ConfigureAwait(false),
            "browser_observe" => await _browser.Observe(Str(args, "session_id") ?? "", ct).ConfigureAwait(false),
            "browser_click" => await _browser.Click(Str(args, "session_id") ?? "", Str(args, "candidate_id") ?? "", ct).ConfigureAwait(false),
            "browser_fill" => await _browser.Fill(Str(args, "session_id") ?? "", Str(args, "candidate_id") ?? "",
                Str(args, "value"), Str(args, "value_ref"), Str(args, "value_env"), ct).ConfigureAwait(false),
            "browser_capture_value" => await _browser.CaptureValue(Str(args, "session_id") ?? "", Str(args, "candidate_id") ?? "", Str(args, "name") ?? "").ConfigureAwait(false),
            "browser_wait" => await _browser.Wait(Str(args, "session_id") ?? "", Str(args, "condition") ?? "navigation_committed",
                Str(args, "candidate_id"), Str(args, "expected"), Int(args, "timeout_ms") ?? 15_000, ct).ConfigureAwait(false),
            _ => Error($"Unknown browser tool '{tool}'"),
        };
    }

    private static readonly HttpClient WorkflowHttpClient = new(new HttpClientHandler { AllowAutoRedirect = false });

    [McpServerTool(Name = "read_file"), Description("F32: read a UTF-8 text file that must be strictly inside allowed_root (path traversal is rejected before the read happens), and store its content under a name for later use with value_ref (e.g. transform_data, browser_fill, set_value). Bounded: rejects a path outside allowed_root or a missing file.")]
    public object ReadFile(
        [Description("Absolute directory the file must stay inside")] string allowed_root,
        [Description("File path, absolute or relative to allowed_root")] string path,
        [Description("Name to store the file's content under")] string name,
        CancellationToken ct = default)
    {
        try
        {
            var full = WorkflowFilePaths.Resolve(allowed_root, path, mustExist: true);
            var content = File.ReadAllText(full);
            _workflowValues.Set(name, content);
            return new { ok = true, name, length = content.Length };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "write_file"), Description("F32: write UTF-8 text to a file that must be strictly inside allowed_root (path traversal is rejected before the write happens). Supply exactly one of value, value_ref, or value_env. Creates parent directories inside allowed_root if needed; never writes outside it.")]
    public object WriteFile(
        [Description("Absolute directory the file must stay inside")] string allowed_root,
        [Description("File path, absolute or relative to allowed_root")] string path,
        [Description("Exact text to write")] string? value = null,
        [Description("Name of a value stored via set_workflow_value/capture_workflow_value/read_file to write instead of value")] string? value_ref = null,
        [Description("Environment variable whose value should be written instead of value")] string? value_env = null,
        CancellationToken ct = default)
    {
        try
        {
            var resolvedValue = WorkflowValueStore.Resolve(value, value_ref, value_env, _workflowValues);
            var full = WorkflowFilePaths.Resolve(allowed_root, path, mustExist: false);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, resolvedValue);
            return new { ok = true, path = full, length = resolvedValue.Length };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
        catch (InvalidOperationException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "http_request"), Description("F32: a bounded HTTP call whose target host, and every redirect hop's host, must be in allowed_hosts (an unapproved redirect is rejected before it is followed). Response body is capped at 2MB and stored under name for value_ref use elsewhere. GET or POST only; POST body is exactly one of value/value_ref/value_env if supplied.")]
    public async Task<object> HttpRequest(
        [Description("Absolute URL to request")] string url,
        [Description("Comma-separated hostnames this request (and every redirect hop) may target")] string allowed_hosts,
        [Description("Name to store the response body under")] string name,
        [Description("GET | POST")] string method = "GET",
        [Description("POST body text")] string? value = null,
        [Description("Name of a stored value to use as the POST body instead of value")] string? value_ref = null,
        [Description("Environment variable whose value should be used as the POST body instead of value")] string? value_env = null,
        [Description("Request timeout in milliseconds")] int timeout_ms = 15_000,
        CancellationToken ct = default)
    {
        try
        {
            string? body = null;
            if (value != null || value_ref != null || value_env != null)
                body = WorkflowValueStore.Resolve(value, value_ref, value_env, _workflowValues);
            var hosts = allowed_hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var (status, responseBody, finalUrl) = await WorkflowHttpSteps.ExecuteAsync(WorkflowHttpClient, url, hosts, method, body, timeout_ms, ct).ConfigureAwait(false);
            _workflowValues.Set(name, responseBody);
            return new { ok = true, name, status_code = status, final_url = finalUrl, length = responseBody.Length };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
        catch (InvalidOperationException ex) { return Error(ex.Message); }
        catch (HttpRequestException ex) { return Error($"http_request failed: {ex.Message}"); }
        catch (OperationCanceledException ex) when (!IsCallerCancellation(ex, ct)) { return Error("http_request timed out."); }
    }

    [McpServerTool(Name = "transform_data"), Description("F32: apply one deterministic, non-generated transform (trim | uppercase | lowercase | csv_cell:row,col) to a stored value and store the result under a new name. Never executes generated code -- only this fixed, reviewed operation set (F32-R02).")]
    public object TransformData(
        [Description("Name of a value stored via read_file/set_workflow_value/capture_workflow_value/http_request")] string value_ref,
        [Description("trim | uppercase | lowercase | csv_cell:row,col")] string op,
        [Description("Name to store the transformed result under")] string name)
    {
        try
        {
            if (!_workflowValues.TryGet(value_ref, out var input))
                return Error($"No workflow value named '{value_ref}'. Call read_file/set_workflow_value/capture_workflow_value/http_request first.");
            var result = WorkflowDataTransform.Apply(op, input);
            _workflowValues.Set(name, result);
            return new { ok = true, name, length = result.Length };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "create_workflow"), Description("F29: create a versioned DAG workflow of typed nodes dispatched to existing tools -- native (click, type_text, press_key, scroll, drag, set_value, wait_for, capture_workflow_value, set_workflow_value) AND browser (browser_open, browser_observe, browser_click, browser_fill, browser_capture_value, browser_wait), so one workflow can genuinely mix a desktop app and a browser session, including moving a value captured from one node into another via value_ref. Nodes: [{\"id\":\"n1\",\"app\":\"Notepad\",\"tool\":\"click\",\"args\":{...},\"prerequisites\":[]}, ...]. Native steps select the app via the node's app field; browser steps carry their own session_id inside args instead and ignore app. F32-R01 bounded-loop node: {\"id\":\"loop1\",\"kind\":\"bounded_loop\",\"items\":[\"a\",\"b\"] (or \"count\":N),\"template\":{\"app\":..,\"tool\":..,\"args\":{...with \"{{loop_item}}\"/\"{{loop_index}}\" tokens...}},\"prerequisites\":[]} expands into N ordinary nodes ('loop1#0','loop1#1',...) at creation time, hard-capped at 50 iterations -- an unbounded/oversized loop is refused before a single node is created, never merely stopped mid-run. A minimal deterministic runner: validates for unknown/duplicate node ids and dependency cycles before accepting the workflow.")]
    public object CreateWorkflow(
        [Description("Unique id for this workflow run")] string workflow_id,
        [Description("JSON array of nodes: [{\"id\":str,\"app\":str,\"tool\":str,\"args\":{...},\"prerequisites\":[str,...]}] or a bounded-loop node {\"id\":str,\"kind\":\"bounded_loop\",\"items\":[str,...] | \"count\":int,\"template\":{\"app\":str,\"tool\":str,\"args\":{...}},\"prerequisites\":[str,...]}")] string nodes_json,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<WorkflowNodeSpec> specs;
        Dictionary<string, string[]> loopExpansions;
        try
        {
            (specs, loopExpansions) = ParseWorkflowNodes(nodes_json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Error($"Invalid nodes_json: {ex.Message}");
        }
        try
        {
            var run = _workflows.Create(workflow_id, specs);
            return new { ok = true, workflow_id, node_count = run.Nodes.Count, loop_expansions = loopExpansions.Count == 0 ? null : loopExpansions };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    /// <summary>Shared node/loop parser behind both create_workflow and F18's invoke_helper --
    /// one parsing implementation, so a helper's substituted template is interpreted exactly the
    /// same way an ordinary create_workflow call would interpret it. Throws JsonException/
    /// ArgumentException/InvalidOperationException/KeyNotFoundException on malformed input,
    /// matching create_workflow's original inline catch clause.</summary>
    private static (List<WorkflowNodeSpec> Specs, Dictionary<string, string[]> LoopExpansions) ParseWorkflowNodes(string nodesJson)
    {
        List<WorkflowNodeSpec> specs = [];
        var loopExpansions = new Dictionary<string, string[]>();
        using var doc = JsonDocument.Parse(nodesJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("nodes_json must be a JSON array");
        foreach (var node in doc.RootElement.EnumerateArray())
        {
            var id = node.GetProperty("id").GetString() ?? throw new ArgumentException("Every node needs an id.");
            if (node.TryGetProperty("kind", out var kindEl) && kindEl.GetString() == "bounded_loop")
            {
                var generated = WorkflowLoopExpansion.Expand(id, node);
                specs.AddRange(generated);
                loopExpansions[id] = generated.Select(spec => spec.Id).ToArray();
                continue;
            }
            var toolName = node.GetProperty("tool").GetString() ?? throw new ArgumentException($"Node '{id}' needs a tool.");
            var appName = node.TryGetProperty("app", out var appEl) ? appEl.GetString() ?? "" : "";
            var argsJson = node.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
            var prereqs = node.TryGetProperty("prerequisites", out var prereqEl) && prereqEl.ValueKind == JsonValueKind.Array
                ? prereqEl.EnumerateArray().Select(p => p.GetString() ?? "").ToArray()
                : [];
            specs.Add(new WorkflowNodeSpec(id, appName, toolName, argsJson, prereqs));
        }
        return (specs, loopExpansions);
    }

    [McpServerTool(Name = "run_workflow"), Description("F29: run every currently-ready wave of a workflow created via create_workflow to completion. Independent nodes whose prerequisites are all verified run concurrently; a node never dispatches until every one of its prerequisites is verified. Stops when no node is ready (finished, or blocked on a failed/uncertain node).")]
    public async Task<object> RunWorkflow(
        [Description("Workflow id from create_workflow")] string workflow_id,
        CancellationToken ct = default)
    {
        try
        {
            await _workflows.RunAsync(workflow_id, DispatchWorkflowNode, HasFailed, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return WorkflowStatusOf(workflow_id);
    }

    // F35-R01/T01: acquire a per-target resource lease before dispatching a DAG node, so two nodes
    // in the same concurrent wave (WorkflowRunner.RunAsync's Task.WhenAll) that target the identical
    // element/candidate genuinely serialize instead of racing, while nodes on different targets
    // never block each other. Released in finally regardless of outcome -- cancellation or a thrown
    // exception never leaves a stuck lease blocking a later resumed/retried node (F35-T02).
    private async Task<object> DispatchWorkflowNode(WorkflowNodeSpec node, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(node.ArgsJson);
        var resourceKey = WorkflowResourceKey(node, doc.RootElement);
        if (resourceKey is null)
            return await DispatchStep(node.App, node.Tool, doc.RootElement, ct).ConfigureAwait(false);
        var lease = await _leases.AcquireAsync(resourceKey, ct).ConfigureAwait(false);
        try { return await DispatchStep(node.App, node.Tool, doc.RootElement, ct).ConfigureAwait(false); }
        finally { await lease.DisposeAsync().ConfigureAwait(false); }
    }

    private static string? WorkflowResourceKey(WorkflowNodeSpec node, JsonElement args)
    {
        if (node.Tool.StartsWith("browser_", StringComparison.Ordinal))
        {
            var sessionId = Str(args, "session_id");
            if (sessionId is null) return null;
            var candidateId = Str(args, "candidate_id");
            return candidateId is null ? $"browser:{sessionId}" : $"browser:{sessionId}:{candidateId}";
        }
        if (string.IsNullOrWhiteSpace(node.App)) return null;
        var elementId = Str(args, "element_id");
        return elementId is null ? $"native:{node.App}" : $"native:{node.App}:{elementId}";
    }

    [McpServerTool(Name = "get_workflow_status"), Description("F29: read-only status of every node in a workflow (pending/running/verified/failed/uncertain), each node's last result, and the ids currently Uncertain (needing reconcile_workflow_node before the workflow can progress further).")]
    public object GetWorkflowStatus([Description("Workflow id from create_workflow")] string workflow_id)
        => WorkflowStatusOf(workflow_id);

    private object WorkflowStatusOf(string workflowId)
    {
        var run = _workflows.Find(workflowId);
        if (run is null) return Error($"Workflow '{workflowId}' was not found.");
        var nodes = run.States.Select(kv => new
        {
            id = kv.Key,
            status = kv.Value.Status.ToString().ToLowerInvariant(),
            result = kv.Value.Result,
            error = kv.Value.Error,
        }).ToArray();
        return new
        {
            ok = true,
            workflow_id = workflowId,
            nodes,
            uncertain = nodes.Where(n => n.status == "uncertain").Select(n => n.id).ToArray(),
            done = nodes.All(n => n.status is "verified" or "failed" or "cancelled"),
        };
    }

    [McpServerTool(Name = "pause_for_takeover"), Description("F54-R02/T02: pause a workflow for human takeover -- every node still Pending (queued but not yet dispatched) is cancelled so it can never run, structurally the same way RunAsync only ever dispatches Pending nodes. Already-Running/terminal nodes are untouched (an in-flight action cannot be un-dispatched). Call get_app_state/capture_workflow_value afterward to read whatever the user actually left on screen -- neither ever serves a stale pre-takeover value, since a fresh call always re-snapshots live UIA state (F54-R01).")]
    public object PauseForTakeover([Description("Workflow id from create_workflow")] string workflow_id)
    {
        try
        {
            var cancelled = _workflows.PauseForTakeover(workflow_id);
            return new { ok = true, workflow_id, cancelled_node_ids = cancelled };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "resume_workflow"), Description("F29-T02: resume a workflow after an interruption. Any node still marked Running from a prior run (its dispatch never got to record an outcome -- genuinely unknown whether the underlying action landed) becomes Uncertain instead of being silently re-executed. Returns the now-Uncertain node ids; call reconcile_workflow_node for each before run_workflow can make further progress through them.")]
    public object ResumeWorkflow([Description("Workflow id from create_workflow")] string workflow_id)
    {
        try
        {
            var uncertain = _workflows.MarkInterruptedNodesUncertain(workflow_id);
            return new { ok = true, workflow_id, newly_uncertain = uncertain };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "reconcile_workflow_node"), Description("F29-T02: the only way an Uncertain node leaves that state -- never automatic. resolution=verified treats the prior interrupted attempt as having actually succeeded and unblocks dependents; failed blocks dependents like any other failure; pending re-queues the node itself for a plain retry on the next run_workflow.")]
    public object ReconcileWorkflowNode(
        [Description("Workflow id from create_workflow")] string workflow_id,
        [Description("Node id currently Uncertain")] string node_id,
        [Description("verified | failed | pending")] string resolution)
    {
        if (!Enum.TryParse<WorkflowNodeStatus>(resolution, ignoreCase: true, out var status))
            return Error("resolution must be verified, failed, or pending.");
        try
        {
            _workflows.Reconcile(workflow_id, node_id, status);
            return new { ok = true, workflow_id, node_id, resolution = status.ToString().ToLowerInvariant() };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "register_helper"), Description("F18-R01: register (or re-register, e.g. after editing) a reusable helper in the dedicated helper workspace -- a create_workflow-shaped nodes_json template where any string value may contain {{param_name}} tokens, a caller-documented parameter_schema_json, and declared_apps_json listing the exact app names (and/or the literal \"browser\" for browser_* nodes) this helper is allowed to target. Always lands un-admitted: any edit to nodes_json/parameter_schema_json/declared_apps_json/dependency_lock changes the source hash and invalidates a prior admit_helper call (F18-T01) -- call admit_helper again after review before invoke_helper will run it.")]
    public object RegisterHelper(
        [Description("Unique id for this helper")] string helper_id,
        [Description("JSON array of nodes, same shape as create_workflow's nodes_json (including bounded_loop), with optional {{param_name}} tokens inside any string value")] string nodes_json,
        [Description("Caller-documented JSON schema/description of the params_json this helper expects")] string parameter_schema_json,
        [Description("JSON array of the exact app names (and/or the literal \"browser\") this helper is allowed to target -- invoke_helper refuses any node outside this list, even after admission")] string declared_apps_json,
        [Description("Optional free-text dependency/version lock tag, included in the admission hash")] string dependency_lock = "")
    {
        try
        {
            using var doc = JsonDocument.Parse(declared_apps_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("declared_apps_json must be a JSON array of strings.");
            var apps = doc.RootElement.EnumerateArray()
                .Select(e => e.GetString() ?? throw new ArgumentException("declared_apps_json entries must all be strings."))
                .ToArray();
            var record = _helpers.Register(helper_id, nodes_json, parameter_schema_json ?? "", apps, dependency_lock);
            return new { ok = true, helper_id, source_hash = record.SourceHash, admitted = false, declared_apps = record.DeclaredApps };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "admit_helper"), Description("F18-R01: explicitly admit a registered helper for its CURRENT source hash after the caller has reviewed/tested it. test_evidence is required and caller-asserted -- DCU never infers admission from a helper merely existing or having run once, same convention as record_task_experience's outcome field. Any later register_helper call for the same id (an edit) invalidates this admission; invoke_helper refuses until admit_helper is called again (F18-T01).")]
    public object AdmitHelper(
        [Description("Helper id from register_helper")] string helper_id,
        [Description("Required: what was actually reviewed/tested before admitting this helper")] string test_evidence)
    {
        try
        {
            var record = _helpers.Admit(helper_id, test_evidence);
            return new { ok = true, helper_id, admitted = true, admitted_hash = record.AdmittedHash };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "get_helper_status"), Description("F18: read-only status of a registered helper -- whether it is currently admitted (its admitted_hash matches its current source_hash), declared_apps, dependency_lock, and when it was registered/admitted. A mismatch between source_hash and admitted_hash means an edit happened since the last admit_helper call.")]
    public object GetHelperStatus([Description("Helper id from register_helper")] string helper_id)
    {
        var record = _helpers.Find(helper_id);
        if (record is null) return Error($"Helper '{helper_id}' was not registered.");
        return new
        {
            ok = true,
            helper_id,
            admitted = record.Admitted && record.AdmittedHash == record.SourceHash,
            source_hash = record.SourceHash,
            admitted_hash = record.AdmittedHash,
            declared_apps = record.DeclaredApps,
            dependency_lock = record.DependencyLock,
            registered_at = record.RegisteredAt,
            admitted_at = record.AdmittedAt,
            test_evidence = record.TestEvidence,
        };
    }

    [McpServerTool(Name = "invoke_helper"), Description("F18-R02/F18-T02: execute an admitted helper -- substitutes {{param_name}} tokens from params_json into its stored nodes_json, then dispatches the result through the SAME WorkflowRunner create_workflow/run_workflow use (no separate execution path for helpers). Refuses before dispatching ANY node if: the helper was never admitted or was edited since its last admit_helper call (hash mismatch, F18-T01), any node's app/browser destination is outside the helper's declared_apps (undeclared destination), or any node's tool is not one of DCU's own known dispatchable workflow tools (there is no process-launch tool in DCU at all) -- blocked even though the helper itself was approved.")]
    public async Task<object> InvokeHelper(
        [Description("Helper id from register_helper")] string helper_id,
        [Description("JSON object of {\"param_name\":\"value\",...} substituted into the helper's {{param_name}} tokens")] string params_json = "{}",
        CancellationToken ct = default)
    {
        HelperRecord record;
        try { record = _helpers.RequireAdmitted(helper_id); }
        catch (ArgumentException ex) { return Error(ex.Message); }

        Dictionary<string, string> parameters;
        try
        {
            using var doc = JsonDocument.Parse(params_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Error("params_json must be a JSON object.");
            parameters = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
        }
        catch (JsonException ex) { return Error($"Invalid params_json: {ex.Message}"); }

        List<WorkflowNodeSpec> specs;
        try
        {
            var substituted = HelperStore.SubstituteParams(record.NodesJson, parameters);
            (specs, _) = ParseWorkflowNodes(substituted);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Error($"Invalid helper nodes_json after substitution: {ex.Message}");
        }

        // F18-T02: every node is validated BEFORE any node executes -- an undeclared destination
        // or an unrecognised tool blocks the whole invocation, not just the one offending node.
        var validationError = ValidateHelperNodes(helper_id, specs, record.DeclaredApps);
        if (validationError is not null) return Error(validationError);

        var invocationId = $"helper:{helper_id}:{Guid.NewGuid():N}";
        try
        {
            _workflows.Create(invocationId, specs);
            await _workflows.RunAsync(invocationId, DispatchWorkflowNode, HasFailed, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return new { ok = true, helper_id, workflow_id = invocationId, status = WorkflowStatusOf(invocationId) };
    }

    /// <summary>Pure F18-T02 validation, extracted so it is unit-testable without any live UIA
    /// dependency: every node's tool must be one of DCU's own known dispatchable workflow tools,
    /// and every node's destination (its app for native tools, or the literal "browser" for any
    /// browser_* tool) must be in the helper's declared_apps. Returns the first violation found,
    /// or null if every node is allowed.</summary>
    private static string? ValidateHelperNodes(string helperId, IReadOnlyList<WorkflowNodeSpec> specs, IReadOnlyList<string> declaredApps)
    {
        foreach (var spec in specs)
        {
            if (!KnownWorkflowTools.Contains(spec.Tool))
                return $"Helper '{helperId}' node '{spec.Id}' uses tool '{spec.Tool}', which is not one of DCU's own dispatchable workflow tools. Blocked even though the helper is admitted.";
            var isBrowserTool = spec.Tool.StartsWith("browser_", StringComparison.Ordinal);
            var destination = isBrowserTool ? "browser" : spec.App;
            if (string.IsNullOrWhiteSpace(destination) || !declaredApps.Contains(destination, StringComparer.OrdinalIgnoreCase))
                return $"Helper '{helperId}' node '{spec.Id}' targets undeclared destination '{destination}'. declared_apps was [{string.Join(", ", declaredApps)}]. Blocked even though the helper is admitted.";
        }
        return null;
    }

    [McpServerTool(Name = "save_workspace"), Description("F56-R01/T02: persist a named, ordered set of user-reviewed workspace members. Each member is a pure typed REFERENCE (kind: 'native_app' -- any app selector accepted by app elsewhere: name, title substring, PID, window_id, or target_id; or kind: 'url'), never an action -- there is no tool/args field anywhere in a member, so a suggestion can never encode a purchase, delete, or shell-command (structurally, not by a runtime check). Saving always replaces the workspace's membership with exactly what is supplied, in the order supplied.")]
    public object SaveWorkspace(
        [Description("Unique id for this workspace")] string workspace_id,
        [Description("JSON array: [{\"kind\":\"native_app\"|\"url\",\"reference\":str}, ...], in the order they should open")] string members_json)
    {
        try
        {
            using var doc = JsonDocument.Parse(members_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("members_json must be a JSON array.");
            var members = doc.RootElement.EnumerateArray().Select(m =>
            {
                var kind = WorkspaceStore.ParseKind(m.GetProperty("kind").GetString() ?? "");
                var reference = m.GetProperty("reference").GetString() ?? throw new ArgumentException("Every workspace member needs a reference.");
                return new WorkspaceMember(kind, reference);
            }).ToArray();
            var record = _workspaces.Save(workspace_id, members);
            return new
            {
                ok = true,
                workspace_id,
                member_count = record.Members.Count,
                saved_at = record.SavedAt,
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "get_workspace"), Description("F56-R02: read-only editable preview of a saved workspace's current membership, order, and reference kind -- without resolving or opening anything.")]
    public object GetWorkspace([Description("Workspace id from save_workspace")] string workspace_id)
    {
        var record = _workspaces.Find(workspace_id);
        if (record is null) return Error($"Workspace '{workspace_id}' was not found.");
        return new
        {
            ok = true,
            workspace_id,
            saved_at = record.SavedAt,
            members = record.Members.Select(m => new { kind = m.Kind == WorkspaceMemberKind.NativeApp ? "native_app" : "url", reference = m.Reference }),
        };
    }

    [McpServerTool(Name = "open_workspace"), Description("F56-R01/T01: opening a workspace is distinct from executing a privileged workflow -- for every member, IN ORDER, this only resolves/reports whether it is currently available; it never clicks, launches a process, or navigates a browser. A native_app member is checked against the SAME app-selector resolution every other tool uses (name/title/PID/window_id/target_id) and reported found (with window_id/target_id) or missing -- a missing/moved app is reported as missing, never silently replaced with a different lookalike window. A url member is reported ready_to_open with its exact URL; the caller decides whether to actually call browser_open for it.")]
    public async Task<object> OpenWorkspace(
        [Description("Workspace id from save_workspace")] string workspace_id,
        CancellationToken ct = default)
    {
        var record = _workspaces.Find(workspace_id);
        if (record is null) return Error($"Workspace '{workspace_id}' was not found.");
        var results = new List<object>();
        foreach (var member in record.Members)
        {
            if (member.Kind == WorkspaceMemberKind.Url)
            {
                results.Add(new { kind = "url", reference = member.Reference, status = "ready_to_open" });
                continue;
            }
            try
            {
                var target = await _uia.ResolveAppAsync(member.Reference, ct).ConfigureAwait(false);
                if (!_uia.TryValidateTarget(target, out var targetIdentityError))
                {
                    results.Add(new { kind = "native_app", reference = member.Reference, status = "missing", detail = targetIdentityError });
                    continue;
                }
                results.Add(new { kind = "native_app", reference = member.Reference, status = "found", window_id = target.WindowId, target_id = target.TargetId });
            }
            catch (InvalidOperationException ex)
            {
                // Not found / ambiguous / stale -- SelectApp's own exact-match failure. Reported
                // as missing, never guessed at with a different running app (F56-T01).
                results.Add(new { kind = "native_app", reference = member.Reference, status = "missing", detail = ex.Message });
            }
        }
        return new { ok = true, workspace_id, members = results };
    }

    [McpServerTool(Name = "search_local_files"), Description("F55: lexical filename search under allowed_root (bounded, recursive), scoped local retrieval only -- browser history and email are never read by this or any other DCU tool, and this tool never calls Jev/any model, so F55-R02's 'keep data local unless explicitly permitted' holds by construction. Each candidate reports opened_at/modified_at/added_at as three SEPARATE fields, never substituted for one another (F55-T02); sort_by picks which one to rank by, and sort_fallback_used/sort_fallback_reason are set explicitly (never silently) only when that exact dimension is unavailable for every candidate. scope ties this call into a monotonic per-scope query sequence (F55-T01): query_sequence is this call's number, and stale:true means a strictly newer call for the same scope has already begun since this one started -- the caller must never let a stale response overwrite a newer query's result or a manual selection.")]
    public object SearchLocalFiles(
        [Description("Caller-chosen scope key so concurrent/rapid re-queries can be sequenced against each other, e.g. a UI panel id")] string scope,
        [Description("Absolute directory the search must stay inside")] string allowed_root,
        [Description("Substring to match against file names, case-insensitive; empty matches every file")] string query,
        [Description("opened_at | modified_at | added_at")] string sort_by = "modified_at",
        [Description("Max results, 1-200")] int max_results = 20)
    {
        var sequence = _search.Begin(scope);
        RecencyKind kind;
        try { kind = ParseRecencyKind(sort_by); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        FileSearchResult result;
        try { result = LocalFileSearch.Search(allowed_root, query ?? "", kind, max_results); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        var fresh = _search.IsFresh(scope, sequence);
        return new
        {
            ok = true,
            scope,
            query_sequence = sequence,
            stale = !fresh,
            sort_by,
            sort_fallback_used = result.SortFallbackUsed,
            sort_fallback_reason = result.SortFallbackReason,
            candidates = result.Candidates.Select(c => new
            {
                path = c.Path,
                file_name = c.FileName,
                opened_at = c.OpenedAt,
                modified_at = c.ModifiedAt,
                added_at = c.AddedAt,
            }),
        };
    }

    private static RecencyKind ParseRecencyKind(string sortBy) => sortBy switch
    {
        "opened_at" => RecencyKind.OpenedAt,
        "modified_at" => RecencyKind.ModifiedAt,
        "added_at" => RecencyKind.AddedAt,
        _ => throw new ArgumentException("sort_by must be 'opened_at', 'modified_at', or 'added_at'."),
    };

    [McpServerTool(Name = "configure_model_provider"), Description("F27-R01/R02: register (or replace) a provider for one independently-configured role (planner | text_generator | judge | perception), with declared capabilities (structured output, modalities) and data_destination (local | cloud). pinned_model_id is required -- floating aliases are not accepted, matching F27-R02's evaluation-reproducibility requirement; an optional resolved_alias records what a caller-side alias resolution actually produced, for provenance only. This registry never makes a network/provider call itself -- select_model_provider only ever answers which configured provider may be used.")]
    public object ConfigureModelProvider(
        [Description("planner | text_generator | judge | perception")] string role,
        [Description("Unique id for this provider within the role")] string provider_id,
        [Description("Lower runs first in the failover chain")] int priority,
        [Description("Whether this provider supports structured/typed output")] bool supports_structured_output,
        [Description("JSON array of supported modality strings, e.g. [\"text\"]")] string modalities_json,
        [Description("'local' or 'cloud'")] string data_destination,
        [Description("Required: pinned model identifier for evaluation reproducibility")] string pinned_model_id,
        [Description("Optional: what a floating alias actually resolved to, for provenance")] string? resolved_alias = null)
    {
        try
        {
            var parsedRole = ParseModelRole(role);
            using var doc = JsonDocument.Parse(modalities_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("modalities_json must be a JSON array of strings.");
            var modalities = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException("modalities_json entries must all be strings.")).ToArray();
            var capabilities = new ProviderCapabilities(supports_structured_output, modalities, data_destination);
            var registration = _modelRouting.Configure(parsedRole, provider_id, priority, capabilities, pinned_model_id, resolved_alias);
            return new { ok = true, role, provider_id, priority, pinned_model_id, resolved_alias = registration.ResolvedAlias };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "attach_compatibility_adapter"), Description("F27-T02: explicitly attach a validated compatibility adapter to one already-configured provider, bridging EXACTLY ONE declared capability gap ('structured_output' or 'modality:<name>'). Never auto-applied, never bridges an undeclared gap -- a provider missing a required capability with no matching adapter is never silently substituted.")]
    public object AttachCompatibilityAdapter(
        [Description("planner | text_generator | judge | perception")] string role,
        [Description("Provider id from configure_model_provider")] string provider_id,
        [Description("'structured_output' or 'modality:<name>' -- the exact single gap this adapter bridges")] string bridges_capability,
        [Description("Human-readable description of how the adapter bridges this gap")] string description)
    {
        try
        {
            var parsedRole = ParseModelRole(role);
            _modelRouting.AttachCompatibilityAdapter(parsedRole, provider_id, new CompatibilityAdapter(bridges_capability, description));
            return new { ok = true, role, provider_id, bridges_capability };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "record_provider_failure"), Description("F27-T01: mark a configured provider as having just failed (timeout/rate limit/error) with a caller-asserted reason. select_model_provider skips a provider inside its failure cooldown, proving failover reaches the next configured candidate in the same role's chain rather than reusing the one still down. Never touches any task/workflow state -- a caller continuing a task after failover resumes from whatever WorkflowRunner/ExperienceStore state already existed, completely unaffected by this call.")]
    public object RecordProviderFailure(
        [Description("planner | text_generator | judge | perception")] string role,
        [Description("Provider id from configure_model_provider")] string provider_id,
        [Description("Required: what failed, e.g. 'timeout' or 'rate_limited'")] string reason)
    {
        try
        {
            var parsedRole = ParseModelRole(role);
            _modelRouting.RecordFailure(parsedRole, provider_id, reason);
            return new { ok = true, role, provider_id, reason };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "select_model_provider"), Description("F27-R01/R02: select the first-priority configured provider for one role that genuinely satisfies every required capability -- never a silent substitution. local_only refuses every cloud-destination candidate outright, even as a last resort (F27-R02); a provider still inside its failure cooldown (record_provider_failure) is skipped. Returns found:false with reason 'no_provider_configured' or 'no_compatible_provider' rather than ever returning an incompatible provider.")]
    public object SelectModelProvider(
        [Description("planner | text_generator | judge | perception")] string role,
        [Description("Require the selected provider to support structured/typed output")] bool require_structured_output = false,
        [Description("JSON array of required modality strings; empty means no modality requirement")] string require_modalities_json = "[]",
        [Description("Refuse any provider whose data_destination is not 'local'")] bool local_only = false)
    {
        try
        {
            var parsedRole = ParseModelRole(role);
            using var doc = JsonDocument.Parse(require_modalities_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("require_modalities_json must be a JSON array of strings.");
            var modalities = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException("require_modalities_json entries must all be strings.")).ToArray();
            var selection = _modelRouting.Select(parsedRole, new RequiredCapabilities(require_structured_output, modalities, local_only));
            return new
            {
                ok = true,
                role,
                found = selection.Found,
                provider_id = selection.ProviderId,
                pinned_model_id = selection.PinnedModelId,
                adapter_used = selection.AdapterUsed,
                reason = selection.Reason,
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
    }

    private static ModelRole ParseModelRole(string role) => role switch
    {
        "planner" => ModelRole.Planner,
        "text_generator" => ModelRole.TextGenerator,
        "judge" => ModelRole.Judge,
        "perception" => ModelRole.Perception,
        _ => throw new ArgumentException("role must be 'planner', 'text_generator', 'judge', or 'perception'."),
    };

    [McpServerTool(Name = "define_subgoal"), Description("F23-R01: represent one explicit subgoal of a task -- an objective (immutable from here on), required_data (WorkflowValueStore names that must be set via set_workflow_value/capture_workflow_value/browser_capture_value before this subgoal's actions are ready), and an optional postcondition (same shape as wait_for). Kept entirely separate from execution: ordinary click/set_value/jev_act/browser_* tools dispatch exactly as before and never reference a subgoal_id at all -- a subgoal is bookkeeping ABOVE them, not a new dispatch path (F23-R02's 'keep planner intent separate from low-level execution details').")]
    public object DefineSubgoal(
        [Description("Unique id for this subgoal")] string subgoal_id,
        [Description("The user objective this subgoal serves; never changes once set, even across escalate_subgoal revisions")] string objective,
        [Description("JSON array of WorkflowValueStore names that must be set before this subgoal is ready, e.g. [\"captured_username\"]")] string required_data_json,
        [Description("Optional postcondition app selector (name/title/PID/window_id/target_id) -- omit together with postcondition_condition for no postcondition")] string? postcondition_app = null,
        [Description("Optional postcondition condition: text_exists | element_exists | window_active")] string? postcondition_condition = null,
        [Description("Text/element name for the postcondition, when applicable")] string? postcondition_text = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(required_data_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("required_data_json must be a JSON array of strings.");
            var requiredData = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException("required_data_json entries must all be strings.")).ToArray();
            var record = _subgoals.Define(subgoal_id, objective, requiredData, postcondition_app, postcondition_condition, postcondition_text);
            return new { ok = true, subgoal_id, objective = record.Objective, status = record.Status.ToString().ToLowerInvariant() };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "get_subgoal"), Description("F23: read-only subgoal state -- objective, required_data, postcondition, status (active | escalated | completed), escalation_reason, and revised_from (the subgoal id this one replaced, if any).")]
    public object GetSubgoal([Description("Subgoal id from define_subgoal or escalate_subgoal")] string subgoal_id)
    {
        var record = _subgoals.Find(subgoal_id);
        if (record is null) return Error($"Subgoal '{subgoal_id}' was not found.");
        return new
        {
            ok = true,
            subgoal_id,
            objective = record.Objective,
            required_data = record.RequiredData,
            postcondition_app = record.PostconditionApp,
            postcondition_condition = record.PostconditionCondition,
            postcondition_text = record.PostconditionText,
            status = record.Status.ToString().ToLowerInvariant(),
            escalation_reason = record.EscalationReason,
            revised_from = record.RevisedFrom,
            created_at = record.CreatedAt,
        };
    }

    [McpServerTool(Name = "check_subgoal_readiness"), Description("F23-R01: whether every required_data name for this subgoal is currently set in WorkflowValueStore -- a caller should confirm readiness before dispatching this subgoal's fast-controller actions.")]
    public object CheckSubgoalReadiness([Description("Subgoal id from define_subgoal or escalate_subgoal")] string subgoal_id)
    {
        try
        {
            var (ready, missing) = _subgoals.CheckReadiness(subgoal_id, _workflowValues);
            return new { ok = true, subgoal_id, ready, missing_required_data = missing };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "check_subgoal_postcondition"), Description("F23: a single live check (not a wait) of this subgoal's postcondition, reusing wait_for's exact condition semantics with a near-zero timeout -- no separate condition-checking logic. Returns met:false with no error if the subgoal has no postcondition configured.")]
    public async Task<object> CheckSubgoalPostcondition(
        [Description("Subgoal id from define_subgoal or escalate_subgoal")] string subgoal_id,
        CancellationToken ct = default)
    {
        var record = _subgoals.Find(subgoal_id);
        if (record is null) return Error($"Subgoal '{subgoal_id}' was not found.");
        if (record.PostconditionApp is null || record.PostconditionCondition is null)
            return new { ok = true, subgoal_id, met = false, reason = "no_postcondition_configured" };
        var waitResult = await WaitFor(record.PostconditionApp, record.PostconditionCondition, record.PostconditionText, timeout_s: 0.2, ct).ConfigureAwait(false);
        // wait_for's own ok:false/found:false timeout shape is a legitimate "not met yet"
        // outcome, not a call failure -- only its {error:...} shape (a genuine argument/target
        // problem) should propagate as this tool's own error.
        if (waitResult.GetType().GetProperty("error") != null) return waitResult;
        var met = waitResult.GetType().GetProperty("found")?.GetValue(waitResult) is true;
        return new { ok = true, subgoal_id, met };
    }

    [McpServerTool(Name = "complete_subgoal"), Description("F23: mark an Active subgoal Completed -- refuses if a configured postcondition is not currently met (a genuine live re-check, not a blind assertion); a subgoal with no postcondition configured completes on the caller's assertion alone. Only an Active subgoal can be completed.")]
    public async Task<object> CompleteSubgoal(
        [Description("Subgoal id from define_subgoal or escalate_subgoal")] string subgoal_id,
        CancellationToken ct = default)
    {
        var record = _subgoals.Find(subgoal_id);
        if (record is null) return Error($"Subgoal '{subgoal_id}' was not found.");
        if (record.PostconditionApp is not null)
        {
            var check = await CheckSubgoalPostcondition(subgoal_id, ct).ConfigureAwait(false);
            if (HasFailed(check)) return check;
            if (check.GetType().GetProperty("met")?.GetValue(check) is not true)
                return Error($"Subgoal '{subgoal_id}' postcondition is not currently met; cannot complete.");
        }
        try { _subgoals.Complete(subgoal_id); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return new { ok = true, subgoal_id, status = "completed" };
    }

    [McpServerTool(Name = "escalate_subgoal"), Description("F23-R02/T02: replan on an explicit blocker, repeated no-progress state (e.g. a LoopGuard should_stop verdict the caller observed), or a failed completion check -- reason is required and caller-asserted, matching this project's 'never infer, always assert' convention for state transitions. Marks the current subgoal permanently Escalated and returns a NEW subgoal (id '{subgoal_id}#r1', '#r2', ...) carrying the IDENTICAL objective forward -- there is no parameter on this tool that can change objective, so the original user objective can never be silently altered by a revision. required_data/postcondition may optionally be revised on the new subgoal (that is the point of replanning); omitted fields inherit the prior subgoal's values unchanged.")]
    public object EscalateSubgoal(
        [Description("Subgoal id to escalate (must be Active)")] string subgoal_id,
        [Description("Required: why this is being escalated, e.g. 'node_failed:submit' or 'loop_guard_should_stop'")] string reason,
        [Description("Optional replacement required_data_json for the new revision; omit to inherit the prior subgoal's required_data")] string? new_required_data_json = null,
        [Description("Optional replacement postcondition app for the new revision")] string? new_postcondition_app = null,
        [Description("Optional replacement postcondition condition for the new revision")] string? new_postcondition_condition = null,
        [Description("Optional replacement postcondition text for the new revision")] string? new_postcondition_text = null)
    {
        try
        {
            IReadOnlyList<string>? newRequiredData = null;
            if (new_required_data_json != null)
            {
                using var doc = JsonDocument.Parse(new_required_data_json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return Error("new_required_data_json must be a JSON array of strings.");
                newRequiredData = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException("new_required_data_json entries must all be strings.")).ToArray();
            }
            var revised = _subgoals.Escalate(subgoal_id, reason, newRequiredData, new_postcondition_app, new_postcondition_condition, new_postcondition_text);
            return new
            {
                ok = true,
                escalated_subgoal_id = subgoal_id,
                revised_subgoal_id = revised.SubgoalId,
                objective = revised.Objective,
                reason,
            };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "create_isolated_environment"), Description("F36-R01: register a new isolated-environment record for parallel or unattended runs. No sandbox/VM infrastructure (Windows Sandbox, Hyper-V, Docker Desktop) is available in this build -- confirmed by investigation, not assumed -- so DCU never launches or connects to anything here; provider_kind is a caller-asserted label (e.g. 'external_vm') and this call is pure bookkeeping from the start, mirroring how F24/F57/F58 scaffold around an externally-supplied resource rather than performing the infrastructure step themselves.")]
    public object CreateIsolatedEnvironment(
        [Description("Unique id for this isolated environment")] string environment_id,
        [Description("Caller-asserted provider label, e.g. 'external_vm' or 'external_sandbox'")] string provider_kind)
    {
        try { var env = _isolatedEnvironments.Create(environment_id, provider_kind); return new { ok = true, environment_id = env.EnvironmentId, state = env.State.ToString() }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "attach_isolated_environment"), Description("Record that an isolated environment is now attached to an externally-supplied connection descriptor (external_handle). DCU treats the handle as opaque -- never dials or validates it; this is bookkeeping only.")]
    public object AttachIsolatedEnvironment(
        [Description("Environment id from create_isolated_environment")] string environment_id,
        [Description("Opaque externally-supplied connection descriptor")] string external_handle)
    {
        try { var env = _isolatedEnvironments.Attach(environment_id, external_handle); return new { ok = true, environment_id = env.EnvironmentId, state = env.State.ToString() }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "acquire_isolated_environment_lease"), Description("F36-T02: acquire an exclusive, time-bounded lease on an isolated environment -- a different actor is refused while a still-valid lease is held by someone else, mirroring F60 ReviewRoomStore's lease shape. The SAME actor re-acquiring extends its own lease (reentrant), matching every other lease mechanism in this project.")]
    public object AcquireIsolatedEnvironmentLease(
        [Description("Environment id from create_isolated_environment")] string environment_id,
        [Description("Requesting actor id")] string actor_id,
        [Description("Lease duration in seconds")] double duration_seconds)
    {
        try
        {
            var (acquired, reason) = _isolatedEnvironments.AcquireLease(environment_id, actor_id, TimeSpan.FromSeconds(duration_seconds));
            return acquired ? new { ok = true, acquired = true } : new { ok = false, acquired = false, reason };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "check_isolated_environment_lease"), Description("F36-T02: an expired sandbox lease blocks further commands -- a pure timestamp comparison against the lease acquired via acquire_isolated_environment_lease, never inferred from any other state. Call this before submit_isolated_environment_command-style dispatch to confirm the lease is still valid for the calling actor.")]
    public object CheckIsolatedEnvironmentLease(
        [Description("Environment id from create_isolated_environment")] string environment_id,
        [Description("Actor id to check")] string actor_id)
    {
        try { return new { ok = true, lease_valid = _isolatedEnvironments.IsLeaseValid(environment_id, actor_id) }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "reset_isolated_environment"), Description("Clear an isolated environment's session/lease state while keeping the environment record itself -- bookkeeping only, never a real reset of anything external.")]
    public object ResetIsolatedEnvironment([Description("Environment id from create_isolated_environment")] string environment_id)
    {
        try { var env = _isolatedEnvironments.Reset(environment_id); return new { ok = true, environment_id = env.EnvironmentId, state = env.State.ToString() }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "destroy_isolated_environment"), Description("F36-T02: teardown records exactly the caller-declared retained_artifact_ids and touches nothing else -- this registry has no code path that deletes any file or resource, so 'does not delete unrelated resources' holds structurally. A destroyed environment refuses any further attach/lease/reset call.")]
    public object DestroyIsolatedEnvironment(
        [Description("Environment id from create_isolated_environment")] string environment_id,
        [Description("JSON array of artifact ids to record as retained, e.g. [\"screenshot-1\",\"log-1\"]")] string retained_artifact_ids_json = "[]")
    {
        List<string> retained;
        try
        {
            using var doc = JsonDocument.Parse(retained_artifact_ids_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("retained_artifact_ids_json must be a JSON array of strings.");
            retained = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException("retained_artifact_ids_json entries must all be strings.")).ToList();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error(ex.Message); }
        try { var env = _isolatedEnvironments.Destroy(environment_id, retained); return new { ok = true, environment_id = env.EnvironmentId, state = env.State.ToString(), retained_artifact_ids = env.RetainedArtifactIds }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "open_voice_session"), Description("F58-R01/R02: open a turn-taking session for one voice utterance. DCU has no microphone/speech-recognition pipeline in this environment (same no-live-external-service constraint as F24/F27/F46/F47/F55/F57) -- the caller's own speech-recognition adapter feeds partial/final transcripts in via submit_voice_partial/submit_voice_final, tagged with a caller-assigned generation_id. fallback_behavior is declared here, up front, and is what report_voice_transcription_failure returns later -- never a hardcoded default (F58-T02).")]
    public object OpenVoiceSession(
        [Description("Unique id for this utterance/session")] string session_id,
        [Description("What should happen on a transcription API failure, e.g. 'resume_listening' or 'cancel'")] string fallback_behavior)
    {
        try { var session = _voiceTurn.OpenSession(session_id, fallback_behavior); return new { ok = true, session_id = session.SessionId, status = session.Status.ToString() }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "submit_voice_partial"), Description("F58-T01: record a partial (not-yet-final) transcript for an open voice session. NEVER finalizes or dispatches anything by itself, however long the pause after it -- a hesitation mid-phrase must not prematurely send or execute. A generation_id at or behind the one already recorded is rejected as stale/out-of-order.")]
    public object SubmitVoicePartial(
        [Description("Session id from open_voice_session")] string session_id,
        [Description("Caller-assigned, monotonically increasing id for this utterance's transcript stream")] int generation_id,
        [Description("Partial transcript text so far")] string text)
    {
        try
        {
            var (accepted, reason) = _voiceTurn.SubmitPartial(session_id, generation_id, text);
            return accepted ? new { ok = true, accepted = true } : new { ok = false, accepted = false, reason };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "submit_voice_final"), Description("F58-R01/T01: the caller's speech-recognition adapter asserts this transcript is complete. Only an explicit final signal (or a later check_voice_silence_timeout crossing its bound) ever finalizes an utterance -- never inferred from a partial alone. On success, hands the transcript off to define_subgoal's EXACT SAME SubgoalStore.Define used for typed goals (F58-R01's 'same planner/controller path'), returning the new subgoal_id.")]
    public object SubmitVoiceFinal(
        [Description("Session id from open_voice_session")] string session_id,
        [Description("Caller-assigned generation id for this transcript")] int generation_id,
        [Description("The final transcript text")] string text)
    {
        try
        {
            var (finalized, reason, subgoalId) = _voiceTurn.SubmitFinal(session_id, generation_id, text);
            return finalized ? new { ok = true, finalized = true, subgoal_id = subgoalId } : new { ok = false, finalized = false, reason };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "check_voice_silence_timeout"), Description("F58-R02: bounded fallback timing as a trigger SEPARATE from submit_voice_final -- for a caller whose adapter never emits an explicit final signal, report elapsed silence since the last partial; only crossing silence_threshold_ms finalizes (using the last partial's text), so the exact bound is provable through a test rather than copied from a demo value. Below the threshold this is a no-op, not a rejection.")]
    public object CheckVoiceSilenceTimeout(
        [Description("Session id from open_voice_session")] string session_id,
        [Description("Milliseconds of silence observed since the last partial transcript")] int silence_ms_since_last_partial,
        [Description("Threshold in milliseconds; crossing it finalizes the last partial as the utterance")] int silence_threshold_ms)
    {
        try
        {
            var (finalized, reason, subgoalId) = _voiceTurn.CheckSilenceTimeout(session_id, silence_ms_since_last_partial, silence_threshold_ms);
            return finalized ? new { ok = true, finalized = true, subgoal_id = subgoalId } : new { ok = true, finalized = false, reason };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "stop_voice_session"), Description("F58-T02: Stop is terminal and distinct from a new request -- marks THIS session refused for any future partial/final/timeout call (even a late-arriving response for a generation_id issued before the stop), while a brand new session_id started afterward is entirely unaffected.")]
    public object StopVoiceSession([Description("Session id from open_voice_session")] string session_id)
    {
        try { _voiceTurn.Stop(session_id); return new { ok = true, session_id, status = "stopped" }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "report_voice_transcription_failure"), Description("F58-T02: the caller's speech-recognition adapter reports its own API failure for this session. Returns the fallback_behavior declared at open_voice_session time -- DCU never invents a default; it only records which caller-declared behavior applies and marks the session terminal so any late-arriving response for it is refused, same as an explicit stop.")]
    public object ReportVoiceTranscriptionFailure(
        [Description("Session id from open_voice_session")] string session_id,
        [Description("What failed, e.g. 'timeout' or 'connection_reset'")] string reason)
    {
        try { var fallback = _voiceTurn.ReportTranscriptionFailure(session_id, reason); return new { ok = true, fallback_behavior = fallback }; }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "render_composition"), Description("F59-R01/T01: validate and render a UI composition against a fixed, versioned component catalogue -- prepared component structures with typed props and allowlisted action-binding tools, never arbitrary/generated UI code. Every node's component_id, prop types, and any action_binding tool are validated BEFORE anything is stored or rendered -- an unknown component, an action binding to a tool outside both DCU's own dispatchable-tool allowlist AND that specific component's own allowed bindings, or a prop value containing an executable-looking pattern (e.g. '<script', 'javascript:') all refuse the WHOLE composition, not just the offending node. F59-R02: gated behind settings.EnableCatalogueComposition (off by default) -- returns an explicit blocked status, never a silent no-op, while disabled.")]
    public object RenderComposition(
        [Description("Unique id for this composition")] string composition_id,
        [Description("JSON array: [{\"node_id\":str,\"component_id\":str,\"props\":{...},\"action_binding\":{\"app\":str,\"tool\":str,\"args\":{...}}|null}, ...]")] string nodes_json)
    {
        if (!_settings.EnableCatalogueComposition)
            return new { ok = false, blocked = true, reason = "catalogue_composition_disabled", detail = "F59-R02: set EnableCatalogueComposition=true in settings.json to use this feature." };
        List<CompositionNodeSpec> specs;
        try
        {
            using var doc = JsonDocument.Parse(nodes_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("nodes_json must be a JSON array.");
            specs = doc.RootElement.EnumerateArray().Select(node =>
            {
                var nodeId = node.GetProperty("node_id").GetString() ?? throw new ArgumentException("Every node needs a node_id.");
                var componentId = node.GetProperty("component_id").GetString() ?? throw new ArgumentException($"Node '{nodeId}' needs a component_id.");
                var props = node.TryGetProperty("props", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object
                    ? propsEl.EnumerateObject().ToDictionary(p => p.Name, p => PropValueToString(p.Value))
                    : new Dictionary<string, string>();
                CompositionActionBinding? binding = null;
                if (node.TryGetProperty("action_binding", out var bindingEl) && bindingEl.ValueKind == JsonValueKind.Object)
                {
                    var app = bindingEl.TryGetProperty("app", out var appEl) ? appEl.GetString() ?? "" : "";
                    var tool = bindingEl.GetProperty("tool").GetString() ?? throw new ArgumentException($"Node '{nodeId}' action_binding needs a tool.");
                    var argsJson = bindingEl.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
                    binding = new CompositionActionBinding(app, tool, argsJson);
                }
                return new CompositionNodeSpec(nodeId, componentId, props, binding);
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Error($"Invalid nodes_json: {ex.Message}");
        }

        foreach (var spec in specs)
        {
            var violation = CompositionValidator.ValidateNode(spec, KnownWorkflowTools);
            if (violation != null) return Error(violation);
        }

        CompositionRecord record;
        try { record = _compositions.Save(composition_id, specs); }
        catch (ArgumentException ex) { return Error(ex.Message); }

        // F59-T02: deterministic render -- a pure function of the stored, already-validated nodes,
        // no randomness/live state involved.
        var rendered = record.Nodes.Select(n => new
        {
            node_id = n.NodeId,
            component_id = n.ComponentId,
            props = n.Props,
            has_action_binding = n.ActionBinding != null,
        }).ToArray();
        return new { ok = true, composition_id, catalogue_version = record.CatalogueVersion, node_count = record.Nodes.Count, rendered };
    }

    private static string PropValueToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => value.GetRawText(),
    };

    [McpServerTool(Name = "dispatch_composition_action"), Description("F59-T02: activate one node's action_binding from an already-rendered composition -- dispatches through the EXACT SAME DispatchStep routing table create_workflow/invoke_helper use, never a separate execution path. Refuses if the node has no action_binding, or if the composition was never rendered. Gated behind the same EnableCatalogueComposition feature flag as render_composition.")]
    public async Task<object> DispatchCompositionAction(
        [Description("Composition id from render_composition")] string composition_id,
        [Description("Node id within that composition")] string node_id,
        CancellationToken ct = default)
    {
        if (!_settings.EnableCatalogueComposition)
            return new { ok = false, blocked = true, reason = "catalogue_composition_disabled" };
        var record = _compositions.Find(composition_id);
        if (record is null) return Error($"Composition '{composition_id}' was not found. Call render_composition first.");
        var node = record.Nodes.FirstOrDefault(n => n.NodeId == node_id);
        if (node is null) return Error($"Node '{node_id}' was not found in composition '{composition_id}'.");
        if (node.ActionBinding is not { } binding)
            return Error($"Node '{node_id}' has no action_binding.");
        using var argsDoc = JsonDocument.Parse(binding.ArgsJson);
        var result = await DispatchStep(binding.App, binding.Tool, argsDoc.RootElement, ct).ConfigureAwait(false);
        return new { ok = true, composition_id, node_id, result };
    }

    [McpServerTool(Name = "join_review_room"), Description("F60-R01: join (or re-join) a named shared review room as actor_id, establishing auth_token for this actor in this room. The FIRST join for an actor_id in a room sets its token; every later call from that actor_id in the same room (posting an annotation, acquiring the execution lease, submitting a command) must supply the identical token or is refused -- a lightweight per-actor shared secret, not a real identity provider, but a genuine authentication gate rather than an open room.")]
    public object JoinReviewRoom(
        [Description("Room id -- shared by every reviewer/agent collaborating on it")] string room_id,
        [Description("This caller's actor id within the room")] string actor_id,
        [Description("This caller's auth token for the room -- established on first join, required on every later call")] string auth_token)
    {
        try
        {
            var room = _reviewRooms.GetOrCreate(room_id);
            if (!room.TryJoin(actor_id, auth_token, out var error)) return Error(error!);
            return new { ok = true, room_id, actor_id };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "post_annotation"), Description("F60-R01/T01: add a review annotation to a room -- every joined member reads the exact same list via list_annotations, so two reviewers see consistent annotations by construction. Requires the caller's auth_token to match its room join.")]
    public object PostAnnotation(
        [Description("Room id from join_review_room")] string room_id,
        [Description("This caller's actor id")] string actor_id,
        [Description("This caller's auth token")] string auth_token,
        [Description("What this annotation is about, e.g. an element id or workflow node id -- free text, DCU does not interpret it")] string target_ref,
        [Description("Annotation text")] string text)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist. Call join_review_room first.");
        if (!room.IsAuthenticated(actor_id, auth_token)) return Error($"Actor '{actor_id}' is not authenticated in room '{room_id}'. Call join_review_room first.");
        try
        {
            var annotation = room.PostAnnotation(actor_id, target_ref, text);
            return new { ok = true, room_id, annotation_id = annotation.AnnotationId, created_at = annotation.CreatedAt };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "list_annotations"), Description("F60-T01: read-only list of every non-deleted annotation in a room, in the same order for every caller -- no per-actor filtering, so two reviewers reading this see identical results. No authentication required for reading (posting/undoing/executing all require it).")]
    public object ListAnnotations([Description("Room id from join_review_room")] string room_id)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist.");
        var annotations = room.ListAnnotations().Select(a => new { annotation_id = a.AnnotationId, actor_id = a.ActorId, target_ref = a.TargetRef, text = a.Text, created_at = a.CreatedAt });
        return new { ok = true, room_id, annotations };
    }

    [McpServerTool(Name = "undo_annotation"), Description("F60-R02/T02: soft-delete one annotation -- affects ONLY collaborative review state. This never touches WorkflowRunner/ExperienceStore/RunTimeline or any already-executed action in any way; undoing a comment never replays or reverses a real external action, even one the same actor performed.")]
    public object UndoAnnotation(
        [Description("Room id from join_review_room")] string room_id,
        [Description("This caller's actor id")] string actor_id,
        [Description("This caller's auth token")] string auth_token,
        [Description("Annotation id from post_annotation")] string annotation_id)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist.");
        if (!room.IsAuthenticated(actor_id, auth_token)) return Error($"Actor '{actor_id}' is not authenticated in room '{room_id}'.");
        if (!room.UndoAnnotation(annotation_id)) return Error($"Annotation '{annotation_id}' was not found (or already undone).");
        return new { ok = true, room_id, annotation_id, undone = true };
    }

    [McpServerTool(Name = "acquire_execution_lease"), Description("F60-T01: claim the single execution lease for a room -- only the current holder may call submit_execution_command. Succeeds if nobody holds it, or the caller already does (reentrant); refuses if a DIFFERENT actor currently holds it (never stolen).")]
    public object AcquireExecutionLease(
        [Description("Room id from join_review_room")] string room_id,
        [Description("This caller's actor id")] string actor_id,
        [Description("This caller's auth token")] string auth_token)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist.");
        if (!room.IsAuthenticated(actor_id, auth_token)) return Error($"Actor '{actor_id}' is not authenticated in room '{room_id}'.");
        var acquired = room.TryAcquireLease(actor_id);
        return new { ok = true, room_id, actor_id, acquired, current_holder = room.LeaseHolderActorId };
    }

    [McpServerTool(Name = "release_execution_lease"), Description("F60: release the execution lease if the caller currently holds it. A no-op error if some other actor holds it or nobody does.")]
    public object ReleaseExecutionLease(
        [Description("Room id from join_review_room")] string room_id,
        [Description("This caller's actor id")] string actor_id,
        [Description("This caller's auth token")] string auth_token)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist.");
        if (!room.IsAuthenticated(actor_id, auth_token)) return Error($"Actor '{actor_id}' is not authenticated in room '{room_id}'.");
        if (!room.ReleaseLease(actor_id)) return Error($"Actor '{actor_id}' does not currently hold the execution lease for room '{room_id}'.");
        return new { ok = true, room_id, actor_id, released = true };
    }

    [McpServerTool(Name = "submit_execution_command"), Description("F60-T01/R02: dispatch a real DCU action against an app selected by name, title substring, PID, window_id, or target_id, but ONLY if the caller currently holds this room's execution lease -- every other authenticated member is refused, even one who can see and post annotations freely. Dispatches through the EXACT SAME DispatchStep routing table create_workflow/invoke_helper/dispatch_composition_action already use -- collaboration state is a GATE in front of normal execution, never a reimplementation of it.")]
    public async Task<object> SubmitExecutionCommand(
        [Description("Room id from join_review_room")] string room_id,
        [Description("This caller's actor id")] string actor_id,
        [Description("This caller's auth token")] string auth_token,
        [Description("App name, title substring, PID, window_id, or target_id for a native tool; ignored for browser_* tools")] string app,
        [Description("Tool name, one of DCU's own dispatchable workflow tools")] string tool,
        [Description("JSON object of tool args")] string args_json,
        CancellationToken ct = default)
    {
        var room = _reviewRooms.Find(room_id);
        if (room is null) return Error($"Room '{room_id}' does not exist.");
        if (!room.IsAuthenticated(actor_id, auth_token)) return Error($"Actor '{actor_id}' is not authenticated in room '{room_id}'.");
        if (!room.HoldsLease(actor_id))
            return Error($"Actor '{actor_id}' does not currently hold the execution lease for room '{room_id}'. Call acquire_execution_lease first.");
        if (!KnownWorkflowTools.Contains(tool))
            return Error($"Tool '{tool}' is not one of DCU's own dispatchable workflow tools.");
        using var argsDoc = JsonDocument.Parse(args_json);
        var result = await DispatchStep(app, tool, argsDoc.RootElement, ct).ConfigureAwait(false);
        return new { ok = true, room_id, actor_id, result };
    }

    [McpServerTool(Name = "create_demonstration"), Description("F30: compile an ordered sequence of ALREADY-remembered task_intent recipes (from remember_action_resolution/browser_remember_action_resolution, F31) into a reusable, versioned demonstration. Steps: [{\"app\":str,\"tool\":str,\"task_intent\":str,\"args\":{...}}], tool limited to click/set_value/browser_click/browser_fill. Native steps select the app via the step's app field; browser steps carry session_id inside args and ignore app. Stores no live element/candidate id and no recorded screen coordinate -- only the task_intent reference, revalidated live at admission and again at every replay. Optional completion_app/completion_condition/completion_text (same shape as wait_for) is checked once after the last step. Starts draft; call admit_demonstration before replay_demonstration will run it.")]
    public object CreateDemonstration(
        [Description("Unique id for this demonstration")] string demonstration_id,
        [Description("JSON array of steps: [{\"app\":str,\"tool\":str,\"task_intent\":str,\"args\":{...}}]")] string steps_json,
        [Description("App for the optional completion check (wait_for's target)")] string? completion_app = null,
        [Description("wait_for condition for the optional completion check")] string? completion_condition = null,
        [Description("wait_for text for the optional completion check")] string? completion_text = null)
    {
        List<DemonstrationStepSpec> specs;
        try
        {
            using var doc = JsonDocument.Parse(steps_json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Error("steps_json must be a JSON array");
            specs = doc.RootElement.EnumerateArray().Select(step =>
            {
                var appName = step.TryGetProperty("app", out var appEl) ? appEl.GetString() ?? "" : "";
                var toolName = step.GetProperty("tool").GetString() ?? throw new ArgumentException("Every step needs a tool.");
                var taskIntent = step.GetProperty("task_intent").GetString() ?? throw new ArgumentException("Every step needs a task_intent.");
                var argsJson = step.TryGetProperty("args", out var argsEl) ? argsEl.GetRawText() : "{}";
                return new DemonstrationStepSpec(appName, toolName, taskIntent, argsJson);
            }).ToList();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Error($"Invalid steps_json: {ex.Message}");
        }
        try
        {
            var demo = _demonstrations.Create(demonstration_id, specs, completion_app, completion_condition, completion_text);
            return new { ok = true, demonstration_id, step_count = demo.Steps.Count, status = demo.Status.ToString().ToLowerInvariant() };
        }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    [McpServerTool(Name = "admit_demonstration"), Description("F30-R02: re-resolves EVERY step's task_intent live, right now, before admitting -- refuses (ok:false, listing which steps missed) rather than admitting a demonstration that would fail on first replay. Admission is one-way: an already-admitted demonstration cannot be re-admitted or edited; create a new demonstration id for any change (F30-T02).")]
    public async Task<object> AdmitDemonstration(
        [Description("Demonstration id from create_demonstration")] string demonstration_id,
        CancellationToken ct = default)
    {
        var demo = _demonstrations.Find(demonstration_id);
        if (demo is null) return Error($"Demonstration '{demonstration_id}' was not found.");
        if (demo.Status == DemonstrationStatus.Admitted)
            return Error($"Demonstration '{demonstration_id}' is already admitted. Create a new demonstration to change it.");

        var checks = new List<object>();
        var allResolved = true;
        for (int i = 0; i < demo.Steps.Count; i++)
        {
            var (resolved, detail, _) = await ResolveStepAsync(demo.Steps[i], ct).ConfigureAwait(false);
            checks.Add(new { step = i + 1, task_intent = demo.Steps[i].TaskIntent, resolved, detail });
            if (!resolved) allResolved = false;
        }
        if (!allResolved)
            return new { ok = false, demonstration_id, reason = "one_or_more_steps_do_not_resolve", checks };

        try { _demonstrations.Admit(demonstration_id); }
        catch (ArgumentException ex) { return Error(ex.Message); }
        return new { ok = true, demonstration_id, status = "admitted", checks };
    }

    [McpServerTool(Name = "replay_demonstration"), Description("F30-R01/T01: replay an ADMITTED demonstration end to end. Every step is re-resolved live right now via the same F31 resolve_cached_action mechanism used at admission -- never a stored element/candidate id, never a recorded screen coordinate. Stops immediately on the first step that fails to resolve or fails to dispatch, reporting stopped_at. If a completion check was configured, verifies it once after the last step; completion_verified stays false if it cannot be positively confirmed, never assumed true from the last step merely succeeding.")]
    public async Task<object> ReplayDemonstration(
        [Description("Demonstration id from create_demonstration, must be admitted")] string demonstration_id,
        CancellationToken ct = default)
    {
        var demo = _demonstrations.Find(demonstration_id);
        if (demo is null) return Error($"Demonstration '{demonstration_id}' was not found.");
        if (demo.Status != DemonstrationStatus.Admitted)
            return Error($"Demonstration '{demonstration_id}' is not admitted. Call admit_demonstration first.");

        var results = new List<object>();
        for (int i = 0; i < demo.Steps.Count; i++)
        {
            var step = demo.Steps[i];
            var (resolved, resolveDetail, resolvedId) = await ResolveStepAsync(step, ct).ConfigureAwait(false);
            if (!resolved || resolvedId is null)
            {
                results.Add(new { step = i + 1, task_intent = step.TaskIntent, resolution = resolveDetail });
                return new { ok = false, demonstration_id, stopped_at = i + 1, reason = "resolution_miss", results };
            }
            var idKey = step.Tool.StartsWith("browser_", StringComparison.Ordinal) ? "candidate_id" : "element_id";
            var mergedArgs = MergeResolvedId(step.ArgsJson, idKey, resolvedId);
            var dispatchResult = await DispatchStep(step.App, step.Tool, mergedArgs, ct).ConfigureAwait(false);
            results.Add(new { step = i + 1, task_intent = step.TaskIntent, resolved_id = resolvedId, result = dispatchResult });
            if (HasFailed(dispatchResult))
                return new { ok = false, demonstration_id, stopped_at = i + 1, reason = "dispatch_failed", results };
        }

        object? completion = null;
        bool completionVerified = false;
        if (demo.CompletionApp != null && demo.CompletionCondition != null)
        {
            var waitResult = await WaitFor(demo.CompletionApp, demo.CompletionCondition, demo.CompletionText, ct: ct).ConfigureAwait(false);
            var node = JsonSerializer.SerializeToNode(waitResult)!;
            completionVerified = node["found"]?.GetValue<bool>() ?? false;
            completion = waitResult;
        }
        return new { ok = true, demonstration_id, steps_run = results.Count, completion_configured = demo.CompletionApp != null, completion_verified = completionVerified, completion, results };
    }

    [McpServerTool(Name = "get_demonstration_status"), Description("F30: read-only demonstration definition and lifecycle status (draft/admitted).")]
    public object GetDemonstrationStatus([Description("Demonstration id from create_demonstration")] string demonstration_id)
    {
        var demo = _demonstrations.Find(demonstration_id);
        if (demo is null) return Error($"Demonstration '{demonstration_id}' was not found.");
        return new
        {
            ok = true,
            demonstration_id,
            status = demo.Status.ToString().ToLowerInvariant(),
            version = demo.Version,
            step_count = demo.Steps.Count,
            steps = demo.Steps.Select(s => new { app = s.App, tool = s.Tool, task_intent = s.TaskIntent }).ToArray(),
            completion_configured = demo.CompletionApp != null,
            created_at = demo.CreatedAt,
            admitted_at = demo.AdmittedAt,
        };
    }

    // F30: resolves one demonstration step's task_intent live via the SAME F31 mechanism
    // resolve_cached_action/browser_resolve_cached_action already expose over MCP -- never a
    // second resolution implementation. Returns the resolved element_id/candidate_id only on a hit.
    private async Task<(bool Resolved, object Detail, string? ResolvedId)> ResolveStepAsync(DemonstrationStepSpec step, CancellationToken ct)
    {
        if (step.Tool.StartsWith("browser_", StringComparison.Ordinal))
        {
            if (_browser is null) return (false, new { reason = "browser_unavailable" }, null);
            using var argsDoc = JsonDocument.Parse(step.ArgsJson);
            var sessionId = Str(argsDoc.RootElement, "session_id") ?? "";
            var result = await _browser.ResolveCachedAction(sessionId, step.TaskIntent, ct).ConfigureAwait(false);
            var node = JsonSerializer.SerializeToNode(result)!;
            var hit = node["hit"]?.GetValue<bool>() ?? false;
            return (hit, result, hit ? node["candidate_id"]?.GetValue<string>() : null);
        }
        else
        {
            var result = await ResolveCachedAction(step.App, step.TaskIntent, ct).ConfigureAwait(false);
            var node = JsonSerializer.SerializeToNode(result)!;
            var hit = node["hit"]?.GetValue<bool>() ?? false;
            return (hit, result, hit ? node["element_id"]?.GetValue<string>() : null);
        }
    }

    private static JsonElement MergeResolvedId(string argsJson, string idKey, string resolvedId)
    {
        var node = JsonNode.Parse(argsJson)?.AsObject() ?? [];
        node[idKey] = resolvedId;
        return JsonDocument.Parse(node.ToJsonString()).RootElement;
    }

    [McpServerTool(Name = "hide_cursor"), Description("Hide the cosmetic virtual cursor overlay.")]
    public object HideCursor()
    {
        VirtualCursorOverlay.Instance.Hide();
        return new { ok = true };
    }

    [McpServerTool(Name = "health_check"), Description(
        "Diagnostics: desktop session, UIA availability, app count, overlay status, active input and capture settings, " +
        "and runtime identity (version, build timestamp, module id, process id, executable-replaced flag, full tool-catalog schema fingerprint). " +
        "Compare runtime and schema_fingerprint with the build you expect; a mismatch or stale_executable=true means reconnect.")]
    public async Task<object> HealthCheck(McpServer server, CancellationToken ct = default)
    {
        var err = Guard.CheckInteractiveDesktop();
        var apps = err == null ? await _uia.ListAppsAsync(ct).ConfigureAwait(false) : [];
        var catalogue = server.ServerOptions.ToolCollection?.ToList() ?? [];
        var backend = InteractiveInput.DefaultBackend;
        var defaultMode = ResolveInputMode(null);
        var backendConfigured = _settings.GameMode ? _settings.InputBackendMode : "SendInput (backend settings apply only with GameMode)";
        var backendMatches = !_settings.GameMode || !Enum.TryParse<InputBackendKind>(_settings.InputBackendMode, true, out var configuredKind) || configuredKind switch
        {
            InputBackendKind.SendInput => backend is SendInputBackend,
            InputBackendKind.KernelDriver => backend is KernelDriverBackend,
            InputBackendKind.HardwareDevice => backend is HardwareDeviceBackend,
            InputBackendKind.MemoryWrite => backend is MemoryWriteBackend,
            _ => true,
        };
        return new
        {
            ok = err == null,
            interactive_desktop = err == null,
            desktop_error = err,
            apps_with_windows = apps.Length,
            overlay = _settings.ShowVirtualCursor
                ? "available (layered, click-through, capture-excluded)"
                : "disabled by ShowVirtualCursor=false",
            input = $"default background_mode={defaultMode}; strict_background uses UIA patterns and posted window messages (no SendInput); " +
                $"isolated_interactive and humanized take the foreground and real cursor and inject through the {backend.Name} backend",
            capture = _settings.GameMode
                ? "get_app_state screenshot: PrintWindow(PW_RENDERFULLCONTENT), visible-screen fallback disabled by default; " +
                    "visual grounding and game_* tools: game frame capture (composited screen read while the window is foreground, otherwise PrintWindow)"
                : "PrintWindow(PW_RENDERFULLCONTENT); visible-screen fallback disabled by default",
            input_settings = new
            {
                default_background_mode = defaultMode,
                game_mode = _settings.GameMode,
                humanized_input = _settings.HumanizedInput,
                fast_input = _settings.FastInput,
                real_input_backend = backend.Name,
                real_input_backend_available = backend.IsAvailable,
                real_input_backend_configured = backendConfigured,
                real_input_backend_matches_configuration = backendMatches,
            },
            decision_settings = new
            {
                decision_backend = _settings.DecisionBackend,
                jev_mode = _settings.JevMode,
                jev_model = _settings.JevModel,
                jev_model_supports_box = string.Equals(_settings.JevModel, "jev-1.14.0", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_settings.JevModel, "jev-preview", StringComparison.OrdinalIgnoreCase),
                experimental_game_jev_act_enabled = _settings.EnableExperimentalGameJevAct,
                experimental_game_jev_act_choice_threshold = _settings.ExperimentalGameJevActChoiceThreshold,
                experimental_game_jev_act_click_threshold = _settings.ExperimentalGameJevActClickThreshold,
                experimental_game_jev_act_ambiguity_margin = _settings.ExperimentalGameJevActAmbiguityMargin,
                experimental_game_jev_flow_enabled = _settings.EnableExperimentalGameJevFlow,
                jev_flow_sync_every = _settings.JevFlowSyncEvery,
                jev_flow_fingerprint_tolerance = _settings.JevFlowFingerprintTolerance,
                jev_flow_discovery = _settings.JevFlowDiscovery,
            },
            tool_exposure = new
            {
                tool_exposure = _settings.ToolExposure,
                game_tools_listed = _settings.GameMode || string.Equals(_settings.ToolExposure, "all", StringComparison.OrdinalIgnoreCase),
            },
            runtime = RuntimeIdentification.Describe(RuntimeIdentification.Current, DateTimeOffset.UtcNow),
            tool_catalog = new
            {
                tool_count = catalogue.Count,
                schema_fingerprint = ToolCatalog.SchemaFingerprint(catalogue),
            },
        };
    }

    /// <summary>
    /// Builds the additive observation contract while preserving the legacy get_app_state
    /// fields. The method is pure with respect to the cached snapshot: paging never trims
    /// or rewrites its element list.
    /// </summary>
    public static object BuildObservationResponse(
        Snapshot snapshot,
        IReadOnlyList<ElementInfo> elements,
        int maxElements,
        bool responseTruncated,
        int nextIndex,
        string? continuationToken,
        Capture.CaptureEnvelope capture,
        object visualGrounding,
        bool includeVisualSource)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(elements);
        if (maxElements < 1)
            throw new ArgumentOutOfRangeException(nameof(maxElements));

        var reference = snapshot.TargetReference;
        var formattedTargetId = reference is { } targetReference
            ? UiaService.FormatTargetId(targetReference.TargetId)
            : null;
        var native = snapshot.NativeCompleteness ?? new NativeSnapshotCompleteness();
        object? continuation = continuationToken is null
            ? null
            : new
            {
                kind = "cached_response_page",
                token = continuationToken,
                observation_id = snapshot.ObservationId,
                next_index = nextIndex,
            };
        bool responseComplete = !responseTruncated;
        bool complete = native.ProjectionComplete
            && !native.TreeTextTruncated
            && responseComplete;

        var response = new
        {
            // Legacy response fields remain alongside the additive envelope.
            revision = snapshot.Revision,
            app = snapshot.App,
            pid = snapshot.Pid,
            title = snapshot.Title,
            target_id = formattedTargetId,
            lifetime_epoch = reference?.LifetimeEpoch,
            bounds = new { snapshot.Bounds.Left, snapshot.Bounds.Top, snapshot.Bounds.Right, snapshot.Bounds.Bottom },
            foreground_free = true,
            elements = elements.Select(element => new
            {
                id = element.Id,
                type = element.ControlType,
                name = element.Name,
                automation_id = element.AutomationId,
                x = element.X,
                y = element.Y,
                w = element.Width,
                h = element.Height,
                actions = element.Actions,
                uia_accelerator_key = string.IsNullOrWhiteSpace(element.AcceleratorKey) ? null : element.AcceleratorKey,
                uia_access_key = string.IsNullOrWhiteSpace(element.AccessKey) ? null : element.AccessKey,
                value = string.IsNullOrEmpty(element.Value) ? null : element.Value,

                // Additive observation identity and actionability metadata.
                element_ref = $"{snapshot.ObservationId}:{element.Id}",
                stable_identity = StableIdentity(snapshot, element),
                enabled = element.Enabled,
                read_only = element.ReadOnly,
                supported_operations = element.SupportedOperations,
                action_metadata = new
                {
                    actionable_now = element.ActionableNow,
                    patterns = element.Actions,
                },
            }).ToArray(),
            // F02-R01: legacy field kept for compatibility; "capture" below is the full
            // provenanced envelope (capture id, timestamp, origin, dimensions, coordinate-space
            // scale, explicit failure reason).
            screenshot_png_base64 = capture.Png != null ? Convert.ToBase64String(capture.Png) : null,
            capture = new
            {
                capture_id = capture.CaptureId,
                captured_at = capture.CapturedAt,
                origin = capture.Origin,
                failure_reason = capture.FailureReason,
                width = capture.Width,
                height = capture.Height,
                coordinate_space = capture.ScaleFromWindow is { } scale
                    ? new { window_relative = true, scale_from_window = scale }
                    : null,
            },
            hint = "Act via click/type_text/etc. with element ids. If elements shift, re-snapshot. Use target_id for lifetime-bound selection; window_id remains supported for exact-window compatibility.",

            schema = "dcu.observation.v1",
            observation_id = snapshot.ObservationId,
            sources = includeVisualSource ? new[] { "uia", "visual_ocr" } : new[] { "uia" },
            visual_grounding = visualGrounding,
            target = new
            {
                kind = "native_window",
                session_id = reference?.SessionId,
                target_id = formattedTargetId,
                lifetime_epoch = reference?.LifetimeEpoch,
                window_id = $"{snapshot.Pid}:0x{snapshot.Hwnd.ToInt64():X}",
                pid = snapshot.Pid,
                hwnd = $"0x{snapshot.Hwnd.ToInt64():X}",
            },
            scope = new
            {
                adapter = "native_uia",
                tree = "uia_children_recursive",
                projection = "visible_interactive_or_informative_with_positive_bounds",
                filtered = true,
            },
            completeness = new
            {
                complete,
                native = new
                {
                    projection_complete = native.ProjectionComplete,
                    node_truncated = native.NodeTruncated,
                    depth_truncated = native.DepthTruncated,
                    tree_text_truncated = native.TreeTextTruncated,
                    property_errors = native.PropertyErrors,
                    collected_elements = snapshot.Elements.Count,
                    limits = new
                    {
                        max_nodes = native.MaxNodes,
                        max_depth = native.MaxDepth,
                        text_limit = native.TextLimit,
                    },
                },
                response = new
                {
                    complete = responseComplete,
                    truncated = responseTruncated,
                    max_elements = maxElements,
                    returned_elements = elements.Count,
                    omitted_collected_elements = Math.Max(0, snapshot.Elements.Count - elements.Count),
                    continuation,
                },
            },
            freshness = new
            {
                observed_at = snapshot.CreatedAt,
                revision = snapshot.Revision,
                target_lifetime_epoch = reference?.LifetimeEpoch,
                window_bounds = new { snapshot.Bounds.Left, snapshot.Bounds.Top, snapshot.Bounds.Right, snapshot.Bounds.Bottom },
                revalidate_target_before_action = true,
                reresolve_element_before_action = true,
                expires_at = (string?)null,
            },
        };

        return JsonSerializer.SerializeToNode(response, ObservationWireJsonOptions)
            ?? throw new InvalidOperationException("Observation response serialization returned null.");
    }

    private static object? StableIdentity(Snapshot snapshot, ElementInfo element)
    {
        if (element.RuntimeId is not { Length: > 0 }
            || snapshot.TargetReference is not { } reference)
            return null;

        var targetId = UiaService.FormatTargetId(reference.TargetId);
        var runtimeId = string.Join(",", element.RuntimeId);
        return new
        {
            kind = "uia_runtime_id",
            scope = "native_target_lifetime",
            runtime_id = element.RuntimeId,
            key = $"uia:{targetId}:{runtimeId}",
        };
    }

    /// <summary>Builds the additive F05 visual_grounding response field. observation is null
    /// unless a fresh capture actually produced candidates; unsupportedReason is null unless
    /// perception was attempted and failed. Candidates are always "unverified" — OCR text
    /// detection is not a confirmed control, and there is no post-action semantic check here.</summary>
    private static object BuildVisualGroundingResponse(
        bool invoked,
        string reason,
        ShadowUse.Vision.VisualObservation? observation,
        string? unsupportedReason)
    {
        return new
        {
            invoked,
            reason,
            gate = "structured_evidence_inadequate",
            parser_id = observation?.ParserId,
            parser_version = observation?.ParserVersion,
            captured_at = observation?.CapturedAtUtc,
            window_rect_at_capture = observation != null
                ? new
                {
                    observation.WindowRectAtCapture.Left,
                    observation.WindowRectAtCapture.Top,
                    observation.WindowRectAtCapture.Right,
                    observation.WindowRectAtCapture.Bottom,
                }
                : null,
            dpi_at_capture = observation?.DpiAtCapture,
            unsupported_reason = unsupportedReason,
            candidates = observation?.Candidates.Select(c => new
            {
                id = c.Id,
                text = c.Text,
                x = c.X,
                y = c.Y,
                width = c.Width,
                height = c.Height,
                screen_x = c.ScreenX,
                screen_y = c.ScreenY,
                confidence = c.Confidence,
                confidence_source = c.ConfidenceSource,
                actionability = "unverified",
            }).ToArray(),
        };
    }

    private static object Error(string message) => new { error = message };

    private const string InteractiveModeDescription =
        "strict_background (default): UIA patterns and window messages; the real cursor and foreground are never touched. " +
        "isolated_interactive: real input for games and apps that ignore messages -- brings the window to the front, moves the real cursor, " +
        "injects SendInput events, then restores your cursor and foreground window. Use only when strict_background had no effect. " +
        "humanized: isolated_interactive played back like a person -- curved, variably paced cursor paths, click-point jitter, dwell before and after presses, " +
        "per-key typing timing with self-corrected typos (never for value_ref/value_env text), paced wheel notches; takes the real cursor and foreground for longer. " +
        "REQUIRED for games (Unreal Engine, Unity, any Raw Input app) -- strict_background posts messages they ignore. " +
        "If input is still ignored: (1) check DCU and the game are at the same integrity level (run both as admin), " +
        "(2) ensure the game window is not minimized. " +
        "Omit to use the HumanizedInput setting (default strict_background). " +
        "best_effort_background: not implemented, returns UNSUPPORTED_INPUT_MODE.";

    private const string HumanizedMode = "humanized";

    /// <summary>An omitted background_mode becomes humanized when the HumanizedInput setting is on; an explicit value always wins.</summary>
    private string ResolveInputMode(string? requested)
        => requested ?? (_settings.HumanizedInput ? HumanizedMode : _settings.GameMode ? "isolated_interactive" : "strict_background");

    private static bool IsRealInputMode(string mode) => mode is "isolated_interactive" or HumanizedMode;

    /// <summary>Wheel input at the element centre, or the window centre when no element is given. Humanized input
    /// scrolls at a jittered point inside the element, or inside the middle of the window.</summary>
    private static Task<BackgroundInput.InputResult> InteractiveScrollAsync(IntPtr hwnd, ElementFrame? frame, string direction, double pages, HumanizedOptions? human, CancellationToken ct)
    {
        direction = direction.ToLowerInvariant();
        if (direction is not ("up" or "down" or "left" or "right"))
            return Task.FromResult(new BackgroundInput.InputResult(false, "none", $"direction must be up/down/left/right, got '{direction}'"));
        int left, top, width, height;
        if (frame is { } f) (left, top, width, height) = (f.ScreenX, f.ScreenY, f.Width, f.Height);
        else
        {
            NativeMethods.GetWindowRect(hwnd, out var r);
            (left, top, width, height) = (r.Left + r.Width * 3 / 10, r.Top + r.Height * 3 / 10, r.Width * 4 / 10, r.Height * 4 / 10);
        }
        return human != null
            ? InteractiveInput.HumanScrollAsync(hwnd, left, top, width, height, direction, pages, human, ct)
            : InteractiveInput.ScrollAsync(hwnd, left + width / 2, top + height / 2, direction, pages, ct);
    }

    /// <summary>F01-R01: background_mode is an explicit, checked contract. strict_background (default)
    /// uses UIA patterns and window messages only. isolated_interactive (click, type_text, press_key,
    /// scroll, drag) uses InteractiveInput: foreground activation, the real cursor, and SendInput,
    /// and runs only when a caller asks for it by name. best_effort_background is named by the spec
    /// but not implemented. An unsupported mode returns UNSUPPORTED_INPUT_MODE rather than silently
    /// running as strict_background (F01-T02).</summary>
    private static readonly string[] KnownBackgroundModes = ["strict_background", "best_effort_background", "isolated_interactive", HumanizedMode];

    private static object? CheckBackgroundMode(string background_mode, bool interactiveSupported = false)
    {
        if (background_mode == "strict_background") return null;
        if (IsRealInputMode(background_mode) && interactiveSupported) return null;
        if (Array.IndexOf(KnownBackgroundModes, background_mode) < 0)
            return Error($"Unknown background_mode '{background_mode}'. Supported: strict_background (default), best_effort_background, isolated_interactive, humanized.");
        return new
        {
            ok = false,
            error = "UNSUPPORTED_INPUT_MODE",
            background_mode,
            detail = $"'{background_mode}' is not supported by this tool. Supported: strict_background (the default) for every input tool, and isolated_interactive and humanized for click, type_text, press_key, scroll, and drag."
        };
    }

    private const int NativeJevObservedTextLimit = 8_192;
    private static readonly TimeSpan NativeJevObservationLifetime = TimeSpan.FromSeconds(15);

    private async Task<Snapshot> CaptureJevSnapshotAsync(AppTarget target, CancellationToken ct)
    {
        Snapshot snapshot;
        try
        {
            snapshot = await _uia.SnapshotAsync(target, maxNodes: NativeJevMaxCandidates * 3, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            throw new JevValidationException($"A fresh native observation could not be captured: {ex.Message}", ex);
        }
        if (!_uia.TryValidateSnapshot(snapshot, out var identityError))
            throw new JevValidationException(identityError);

        snapshot.VisualGroundingRequired = ShouldCaptureJevVisual(snapshot, _settings.GameMode);
        if (snapshot.VisualGroundingRequired)
        {
            snapshot.VisualGroundingAttempted = true;
            var outcome = await ShadowUse.Vision.VisualGroundingService
                .RecognizeAsync(target.Hwnd, ct, preferGameCapture: _settings.GameMode)
                .ConfigureAwait(false);
            snapshot.Visual = outcome.Observation;
            snapshot.VisualGroundingUnsupportedReason = outcome.UnsupportedReason;
        }
        return snapshot;
    }

    internal static bool ShouldCaptureJevVisual(Snapshot snapshot, bool gameMode)
        => gameMode || HasInadequateStructuredEvidence(snapshot);

    private static bool HasInadequateStructuredEvidence(Snapshot snapshot) => !snapshot.Elements.Any(e =>
        e.Actions.Length > 0
        && !string.IsNullOrWhiteSpace(e.Name)
        && e.ControlType is not ("MenuBar" or "MenuItem")
        && e.AutomationId is not ("Minimize-Restore" or "Maximize-Restore" or "Close" or "SystemMenuBar"));

    private sealed record NativeKeyAction(string ActionId, string Label, IReadOnlyList<string> KeySequence,
        string EvidenceStatus, string SourceKind, string? Preconditions = null);

    private sealed record NativeAppActionContext(
        JevDecideRequest Request,
        ScreenStateObservation ScreenState,
        IReadOnlyDictionary<string, NativeKeyAction> Actions);

    private static string[] NativeScreenStateClues(Snapshot snapshot)
        => snapshot.Elements.Where(element => element.ControlType is not ("Edit" or "Document")
                && !string.IsNullOrWhiteSpace(element.Value))
            .Select(element => $"{element.AutomationId}|{element.ControlType}|{element.Value}")
            .Concat(snapshot.Visual?.Candidates.Select(candidate => candidate.Text) ?? [])
            .Take(80).ToArray();

    private static ScreenActionRecipe[] NativeScreenRecipes(Snapshot snapshot)
        => snapshot.Elements.Where(element => element.ActionableNow == true && element.SupportedOperations.Length > 0)
            .Select(element => new ScreenActionRecipe(element.AutomationId ?? element.Id, element.ControlType,
                element.Name ?? element.AutomationId ?? element.ControlType,
                new[] { element.AcceleratorKey, element.AccessKey }.Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), element.Enabled)).Take(255).ToArray();

    private async Task<string?> ValidateNativeActionFrameAsync(string appId, string sourceObservationId,
        AppTarget target, CancellationToken ct)
    {
        if (_screenStates is null || !_screenStates.TryGetForFrame(appId, sourceObservationId, out var sourceState)
            || sourceState is null)
            return "The supplied observation is not stored for this app identity; reobserve with app_id before input.";
        Snapshot current;
        try { current = await CaptureJevSnapshotAsync(target, ct).ConfigureAwait(false); }
        catch (JevValidationException ex) { return "A fresh screen check failed before input: " + ex.Message; }
        var latest = _screenStates.Observe(appId, sourceState.AppVersion, sourceState.Profile,
            sourceState.KeyboardLayout, sourceState.ScreenHint,
            new ScreenStateCapture(current.ObservationId, current.Title, current.NativeCompleteness.ProjectionComplete,
                current.NativeCompleteness.ProjectionComplete ? null : "native UI Automation projection is incomplete",
                NativeScreenRecipes(current), NativeScreenStateClues(current), current.CreatedAt));
        return latest.ScreenId == sourceState.ScreenId
            ? null
            : "The semantic screen changed since the recommendation; no input was sent. Reobserve and decide again.";
    }

    private NativeAppActionContext AddNativeAppContext(Snapshot snapshot, JevDecideRequest request,
        string appId, string appVersion, string profile, string keyboardLayout, string? screenHint)
    {
        var recipes = NativeScreenRecipes(snapshot);
        var screenState = _screenStates!.Observe(appId, appVersion, profile, keyboardLayout, screenHint,
            new ScreenStateCapture(snapshot.ObservationId, snapshot.Title, request.CandidateSetComplete,
                request.CandidateSetIssue, recipes, NativeScreenStateClues(snapshot), snapshot.CreatedAt));
        var lookup = _appActionCatalog?.Lookup(appId, appVersion, profile, keyboardLayout)
            ?? new AppActionCatalogLookup(false, null, false, "catalog_service_unavailable");
        var catalog = lookup.Found ? lookup.Snapshot : null;
        var scoped = catalog is null ? [] : AppActionCatalog.ApplicableActions(catalog, screenHint);
        var conflicts = catalog is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : AppActionCatalog.GetConflicts(catalog.Actions, screenHint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int capacity = Math.Max(0, 255 - (request.ControlOptions?.Count ?? 0) - request.OperationDescriptions.Count);
        var keyActions = new Dictionary<string, NativeKeyAction>(StringComparer.Ordinal);
        var addedControls = new List<JevControlOption>();
        var uiaKeyGroups = snapshot.Elements.Where(element => element.ActionableNow == true && element.SupportedOperations.Length > 0)
            .SelectMany(element => new[]
            {
                (Value: element.AcceleratorKey, Source: "uia_accelerator_key"),
                (Value: element.AccessKey, Source: "uia_access_key"),
            }.Where(item => !string.IsNullOrWhiteSpace(item.Value))
             .Select(item => (ActionId: element.Id,
                 Label: string.IsNullOrWhiteSpace(element.Name) ? element.ControlType : element.Name,
                 Key: item.Value.Trim(), item.Source)))
            .Where(item => ValidNativeKey(item.Key))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        int ambiguousUiaKeys = uiaKeyGroups.Count(group => group.Select(item => item.ActionId)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        var eligibleUiaKeys = uiaKeyGroups.Where(group => group.Select(item => item.ActionId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            .Select(group => group.First()).ToArray();
        var uiaKeyFacts = eligibleUiaKeys.Take(capacity).ToArray();
        int omittedUiaKeys = eligibleUiaKeys.Length - uiaKeyFacts.Length;
        foreach (var fact in uiaKeyFacts)
        {
            string code = "UIA_KEY_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fact.ActionId + "\0" + fact.Key)))[..12];
            string label = fact.Label[..Math.Min(fact.Label.Length, 160)];
            var action = new NativeKeyAction(fact.ActionId, label, [fact.Key], "observed", fact.Source);
            keyActions[code] = action;
            addedControls.Add(new JevControlOption(code,
                $"UI Automation reports {fact.Source} '{fact.Key}' for '{label}'; observed in this snapshot, effect unverified, recommendation only."));
        }
        capacity = Math.Max(0, capacity - uiaKeyFacts.Length);
        int omittedCatalogActions = 0;
        if (catalog is not null && !lookup.Advisory)
        {
            var eligible = scoped.Where(entry => entry.KeySequence.Count > 0
                    && entry.EvidenceStatus != "unverified" && !conflicts.Contains(entry.ActionId))
                .GroupBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(entry => string.Join("\u001f", entry.KeySequence).ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal).Count() == 1)
                .Select(group => group.First()).ToArray();
            var selected = eligible.Take(capacity).ToArray();
            omittedCatalogActions = eligible.Length - selected.Length;
            foreach (var entry in selected)
            {
                string suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(entry.ActionId)))[..12];
                string code = "APP_ACTION_" + suffix;
                string description = $"Cataloged app action '{entry.Label}' via {string.Join(" then ", entry.KeySequence)}; evidence={entry.EvidenceStatus}; source={entry.SourceKind}; preconditions={entry.Preconditions ?? "unspecified"}. This is advisory and does not authorize input.";
                if (keyActions.ContainsKey(code)) continue;
                keyActions[code] = new NativeKeyAction(entry.ActionId, entry.Label, entry.KeySequence,
                    entry.EvidenceStatus, entry.SourceKind, entry.Preconditions);
                addedControls.Add(new JevControlOption(code, description));
            }
        }

        var transitions = (screenState.Stable ? screenState.IncomingTransitions : []).Select(item =>
            $"{item.ActionId} via {item.InputKind} was followed by screen {item.ToScreenId}; causal effect unverified.");
        var catalogSummary = catalog is null
            ? $"No app action catalog available ({lookup.Reason ?? "not found"})."
            : $"Action catalog revision {catalog.Revision}; version match={!lookup.Advisory}; conflicting actions={conflicts.Count}; only sourced, non-conflicting actions are offered. Catalog coverage is partial and caller-attested.";
        string context = string.Join(" ", new[]
        {
            request.Context,
            $"App identity={appId}; version={appVersion}; profile={profile}; keyboard_layout={keyboardLayout}.",
            $"Semantic screen id={screenState.ScreenId}; hint={screenHint ?? "unknown"}; repeated={screenState.Repeated}; state_stable={screenState.Stable}; observation_count={screenState.ObservationCount}; {screenState.Note}",
            catalogSummary,
            transitions.Any() ? "Stable semantic signature; caller-reported prior transitions: " + string.Join(" ", transitions)
                : screenState.IncomingTransitions.Count > 0
                    ? "Prior transitions are withheld until the semantic screen repeats across consecutive observations."
                    : "No prior verified transition evidence is available.",
            $"Current UIA key discovery: {uiaKeyFacts.Length} unambiguous shortcuts offered; {ambiguousUiaKeys} ambiguous and {omittedUiaKeys} overflow shortcut(s) omitted; {omittedCatalogActions} catalog action(s) omitted by the Jev option cap.",
            "Native UIA candidates describe the current observation. UIA accelerator/access keys and imported app-key actions are partial recommendations, not a complete action inventory or authorization. No input will be dispatched by jev_decide.",
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var recent = (_screenStates.RecentActions(appId)).Concat(request.RecentActions ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).TakeLast(ScreenStateStore.MaxRecentActions).ToArray();
        var keyBindings = (request.KeyBindings ?? []).Concat(addedControls)
            .GroupBy(option => option.Code, StringComparer.Ordinal).Select(group => group.First()).Take(255).ToArray();
        var controlOptions = (request.ControlOptions ?? []).Concat(addedControls).Take(255).ToArray();
        var operationDescriptions = request.OperationDescriptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var control in addedControls) operationDescriptions.TryAdd(control.Code, control.Description);
        return new NativeAppActionContext(request with
        {
            Context = context,
            RecentActions = recent,
            ControlOptions = controlOptions,
            KeyBindings = keyBindings,
            OperationDescriptions = operationDescriptions,
        }, screenState, keyActions);
    }

    private static bool ValidNativeKey(string key)
    {
        try { _ = InteractiveInput.ParseKeySet(key); return true; }
        catch (ArgumentException) { return false; }
    }

    private static JevDecideRequest BuildNativeJevRequest(Snapshot snapshot, string goal, string[]? recentActions)
    {
        var elements = snapshot.Elements
            .Where(e => e.ActionableNow == true && e.SupportedOperations.Length > 0)
            .Select(e => new JevElement(
                e.Id,
                e.ControlType,
                string.IsNullOrEmpty(e.Name) ? null : e.Name,
                string.IsNullOrEmpty(e.Value) ? null : e.Value,
                e.Enabled ?? true,
                e.SupportedOperations,
                new Dictionary<string, object?>
                {
                    ["source"] = "uia",
                    ["coordinate_space"] = "screen_pixels",
                    ["bbox"] = new { x = e.ScreenX, y = e.ScreenY, width = e.Width, height = e.Height },
                    ["screen_bbox"] = new { x = e.ScreenX, y = e.ScreenY, width = e.Width, height = e.Height },
                    ["window_bbox"] = new { x = e.X, y = e.Y, width = e.Width, height = e.Height },
                    ["coordinate_provenance"] = "uia_current_bounding_rectangle",
                    ["observation_id"] = snapshot.ObservationId,
                    ["uia_accelerator_key"] = string.IsNullOrWhiteSpace(e.AcceleratorKey) ? null : e.AcceleratorKey,
                    ["uia_access_key"] = string.IsNullOrWhiteSpace(e.AccessKey) ? null : e.AccessKey,
                }))
            .Concat((snapshot.Visual?.Candidates ?? [])
                .Select(candidate => new JevElement(
                    candidate.Id,
                    "unverified OCR text",
                    candidate.Text,
                    null,
                    true,
                    ["click_visual"],
                    new Dictionary<string, object?>
                    {
                        ["source"] = "windows_ocr",
                        ["confidence_heuristic"] = candidate.Confidence,
                        ["confidence_source"] = candidate.ConfidenceSource,
                    })))
            .ToArray();

        var observedText = string.Join("\n", new[]
        {
            snapshot.TreeText,
            snapshot.Visual is null ? null : string.Join("\n", snapshot.Visual.Candidates.Select(c => $"OCR: {c.Text}")),
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
        bool textTruncated = snapshot.NativeCompleteness.TreeTextTruncated || observedText.Length > NativeJevObservedTextLimit;
        if (observedText.Length > NativeJevObservedTextLimit)
            observedText = observedText[..NativeJevObservedTextLimit] + "\n...[truncated]";
        bool visualRequired = snapshot.VisualGroundingRequired || HasInadequateStructuredEvidence(snapshot);
        bool candidateLimitExceeded = elements.Length > NativeJevMaxCandidates;
        bool candidateSetComplete = snapshot.NativeCompleteness.ProjectionComplete
            && !candidateLimitExceeded
            && (!visualRequired || snapshot.Visual is not null);
        string? candidateSetIssue = !snapshot.NativeCompleteness.ProjectionComplete
            ? "native UI Automation projection is incomplete"
            : candidateLimitExceeded
                ? $"native candidate set exceeds the {NativeJevMaxCandidates}-choice limit"
                : visualRequired && snapshot.Visual is null
                    ? snapshot.VisualGroundingUnsupportedReason ?? "required OCR observation is unavailable"
                    : null;
        var keyBindings = NativeJevControlOptions.Where(option => option.Code.StartsWith("PRESS_KEY_", StringComparison.Ordinal)).ToArray();

        return new JevDecideRequest(
            goal,
            snapshot.ObservationId,
            elements,
            NativeJevOperationDescriptions,
            NativeJevControlOptions,
            recentActions,
            Context: snapshot.Title,
            ObservedText: observedText,
            KeyBindings: keyBindings,
            CandidateSetComplete: candidateSetComplete,
            ExpiresAtUtc: snapshot.CreatedAt + NativeJevObservationLifetime,
            ObservedTextTruncated: textTruncated,
            CandidateSetIssue: candidateSetIssue);
    }

    private static object NativeJevObservationSummary(Snapshot snapshot, JevDecideRequest request) => new
    {
        candidate_count = request.Elements.Count,
        uia_candidate_count = snapshot.Elements.Count(e => e.ActionableNow == true && e.SupportedOperations.Length > 0),
        ocr_candidate_count = snapshot.Visual?.Candidates.Count ?? 0,
        ocr_required = snapshot.VisualGroundingRequired || HasInadequateStructuredEvidence(snapshot),
        ocr_attempted = snapshot.VisualGroundingAttempted,
        ocr_available = snapshot.Visual is not null,
        ocr_uses_game_capture = snapshot.Visual?.UsesGameCapture ?? false,
        ocr_unsupported_reason = snapshot.VisualGroundingUnsupportedReason,
        candidate_set_complete = request.CandidateSetComplete,
        candidate_set_issue = request.CandidateSetIssue,
        observed_text_truncated = request.ObservedTextTruncated,
    };

    private string[] RecentJevActions(string surface, string targetId, string[]? supplied)
    {
        var recorded = _inspector.Recent(50)
            .Where(entry => entry.Surface == surface && entry.TargetId == targetId)
            .Reverse()
            .Select(entry => $"{entry.Operation} on {entry.TargetElementId ?? "no target"}; tool execution attempted={entry.Executed}; effect not verified")
            .TakeLast(8);
        return recorded.Concat(supplied ?? []).TakeLast(8).ToArray();
    }

    private static bool IsNoActionOperation(string operation) => operation is "WAIT" or "REOBSERVE" or "ASK_USER" or "STOP" or "DONE" or "BLOCKED";

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

    private static string ElementResolutionMessage(string elementId, ElementResolutionStatus status)
        => status switch
        {
            ElementResolutionStatus.Ambiguous =>
                $"Element '{elementId}' is ambiguous in the current UIA tree. Re-run get_app_state.",
            ElementResolutionStatus.Incomplete =>
                $"Element '{elementId}' could not be resolved because the UIA tree was incomplete or resolution timed out. Re-run get_app_state to reobserve.",
            _ => $"Element '{elementId}' no longer exists. Re-run get_app_state.",
        };

    /// <summary>True if a tool result represents failure — either the {error:...} shape,
    /// or an explicit ok:false (e.g. wait_for's timeout return, which has no "error" field).</summary>
    private static bool HasFailed(object r)
    {
        if (r is CallToolResult callResult) return GameResultFailed(callResult, GameResultPayload(callResult));
        var t = r.GetType();
        if (t.GetProperty("error") != null) return true;
        var okProp = t.GetProperty("ok");
        return okProp?.GetValue(r) is bool ok && !ok;
    }

    /// <summary>F45: shared post-action effect capture for type_text/press_key/scroll/drag,
    /// matching click/set_value's own {effect, revision, changed_elements} shape. Waits the
    /// configured settle delay, re-snapshots, and diffs against the caller's pre-action snapshot
    /// (null if none was cached, e.g. get_app_state was never called for this target) -- a failed
    /// follow-up snapshot degrades to a null effect/after plus a human-readable detail, never an
    /// exception, mirroring Click's own refresh-failure tolerance.</summary>
    private async Task<(Snapshot? After, string? Effect, object? ChangedElements, string? RefreshDetail)> CaptureEffectAsync(
        AppTarget target, Snapshot? before, string actionLabel, CancellationToken ct)
    {
        await Task.Delay(InputPace.Ms(_settings.PostActionDelayMs), ct).ConfigureAwait(false);
        Snapshot? after = null;
        string? refreshDetail = null;
        try
        {
            after = await _uia.SnapshotAsync(target, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            refreshDetail = $"{actionLabel} was dispatched, but the follow-up snapshot failed and no effect was observable: {ex.Message}";
        }
        if (after == null) return (null, null, null, refreshDetail);
        var effect = before != null ? (HasActionDelta(before, after) ? "uia_state_changed" : "none") : null;
        return (after, effect, DeltaOf(before, after), refreshDetail);
    }

    private static object DeltaOf(Snapshot? before, Snapshot after)
    {
        if (before == null) return new { note = "no prior snapshot" };
        // Session element ids are minted fresh on every snapshot (monotonic counter), so
        // comparing by Id would always report the whole tree as added+removed. RuntimeId is
        // UIA's own stable identity for "the same element across snapshots while it exists" (F07-R01).
        static string Key(ElementInfo e) => string.Join(",", e.RuntimeId);
        var beforeByKey = before.Elements.Where(e => e.RuntimeId.Length > 0).ToDictionary(Key);
        var afterByKey = after.Elements.Where(e => e.RuntimeId.Length > 0).ToDictionary(Key);

        var added = afterByKey.Keys.Except(beforeByKey.Keys)
            .Select(k => DescribeDeltaElement(afterByKey[k])).ToArray();
        var removed = beforeByKey.Keys.Except(afterByKey.Keys)
            .Select(k => DescribeDeltaElement(beforeByKey[k])).ToArray();
        var changed = afterByKey.Keys.Intersect(beforeByKey.Keys)
            .Select(k => (now: afterByKey[k], props: ChangedProperties(beforeByKey[k], afterByKey[k])))
            .Where(t => t.props.Length > 0)
            .Select(t => new { id = t.now.Id, name = t.now.Name, control_type = t.now.ControlType, changed_properties = t.props })
            .ToArray();

        return new { added, removed, changed, total = after.Elements.Count };
    }

    private static object DescribeDeltaElement(ElementInfo e) => new { id = e.Id, name = e.Name, control_type = e.ControlType };

    /// <summary>Property-level diff for one element across two snapshots (F07-R01). Ignores frame/
    /// position (cosmetic per F07-R02) and only reports semantically meaningful property changes.</summary>
    private static string[] ChangedProperties(ElementInfo before, ElementInfo after)
    {
        var props = new List<string>(6);
        if (before.Name != after.Name) props.Add("name");
        if (before.Value != after.Value) props.Add("value");
        if (before.ControlType != after.ControlType) props.Add("control_type");
        if (before.AutomationId != after.AutomationId) props.Add("automation_id");
        if (before.Enabled != after.Enabled) props.Add("enabled");
        if (!before.Actions.SequenceEqual(after.Actions)) props.Add("actions");
        return props.ToArray();
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
            if (ChangedProperties(oldElement, afterByRuntimeId[runtimeId]).Length > 0)
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
    private static bool? Bool(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
}
