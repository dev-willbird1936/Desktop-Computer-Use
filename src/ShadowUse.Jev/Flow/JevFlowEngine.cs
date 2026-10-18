// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;

namespace ShadowUse.Jev.Flow;

public sealed class JevFlowOptions
{
    /// <summary>Upper bound on loop iterations (decisions, waits, stale retries), not on inputs.</summary>
    public int MaxSteps { get; init; } = 24;

    /// <summary>Bounded serial run: at most this many dispatched inputs, each (screen, action id) at most once. Chain calls for longer routes.</summary>
    public int MaxActions { get; init; } = 4;

    /// <summary>Stale-screen or stale-frame hold-backs tolerated before the run stops and asks the caller to reobserve. 0 = stop at the first one.</summary>
    public int StaleRetries { get; init; } = 0;

    /// <summary>First time an action is used with an unknown destination: how long to wait for the screen to leave the origin and hold still.</summary>
    public int FirstSettleMs { get; init; } = 600;
    public TimeSpan MaxRuntime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Steps that may rely on a predicted (not locally verified) screen before an image sync is forced.</summary>
    public int SyncEvery { get; init; } = 4;

    public double ChoiceThreshold { get; init; } = 0.85;
    public double BoxThreshold { get; init; } = 0.90;
    public double AmbiguityMargin { get; init; } = 0.10;
    public double GoalThreshold { get; init; } = 0.85;

    /// <summary>Fingerprint distance (see <see cref="JevActionBook.Distance"/>) below which a frame counts as a saved screen.</summary>
    public double FingerprintTolerance { get; init; } = 0.06;

    public int SettleMs { get; init; } = 150;
    public int MaxSettleMs { get; init; } = 1_500;
    public int PollMs { get; init; } = 60;
    public int MaxNeedBoxQuestions { get; init; } = 8;
    public int MaxRepeatsWithoutEffect { get; init; } = 3;

    /// <summary>When no goal screen is known, force an image decision (which asks the goal Noul) after this many unchecked inputs,
    /// so a fingerprint-confirmed run cannot keep pressing buttons after the goal is already met.</summary>
    public int GoalCheckEvery { get; init; } = 5;
}

public sealed record FlowSeed(
    string? ScreenHint = null,
    string? GoalScreenId = null,
    IReadOnlyList<FlowAction>? Actions = null,
    IReadOnlyList<string>? RecentActions = null,
    string? SessionContext = null,
    IReadOnlyList<string>? DynamicScreens = null);

public sealed record FlowStepRecord(
    int Step,
    string Tier,
    string? ScreenBefore,
    string ScreenSource,
    string Outcome,
    string? ActionId,
    string? Kind,
    double? Confidence,
    long JevLatencyMs,
    long StepMs,
    string? Note);

public sealed record FlowStats(
    int Steps,
    int JevCalls,
    int TextCalls,
    int ImageCalls,
    int Escalations,
    int Captures,
    int SettleCaptures,
    int Dispatches,
    int LocalScreenConfirmations,
    int Mispredictions,
    int LearnedBoxes,
    int LearnedScreens,
    int LearnedTransitions,
    long ImageBytesSent,
    long JevLatencyMs);

public sealed record FlowRunResult(
    string StopReason,
    bool GoalReached,
    string? FinalScreen,
    IReadOnlyList<FlowStepRecord> Steps,
    FlowStats Stats,
    long TotalMs,
    string? Error,
    bool Reobserve = false);

/// <summary>
/// The responsive Jev loop: capture locally, decide with the cheapest sufficient evidence, dispatch, assume it worked,
/// and verify only through free local fingerprints. Images go to Jev only for unknown/unverified screens or controls
/// that have never been localized; everything the run learns is written to the <see cref="JevActionBook"/> so later
/// runs get faster.
/// </summary>
public sealed class JevFlowEngine(JevActionBook book, IJevFlowEnvironment env, IJevFlowDecider decider,
    JevFlowOptions? options = null, IJevFlowDiscoverer? discoverer = null)
{
    private readonly JevFlowOptions _o = options ?? new JevFlowOptions();

    private sealed record Pending(string FromScreen, FlowAction Action, NormBox? UnverifiedBox = null, double BoxConfidence = 0);

    public async Task<FlowRunResult> RunAsync(string goal, FlowSeed? seed = null, CancellationToken ct = default)
    {
        seed ??= new FlowSeed();
        var runClock = Stopwatch.StartNew();
        var steps = new List<FlowStepRecord>();
        var recent = new List<string>(seed.RecentActions ?? []);
        string? screen = seed.ScreenHint;
        bool seedPending = seed.Actions is { Count: > 0 };
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dynamicScreen in seed.DynamicScreens ?? [])
        {
            book.EnsureScreen(dynamicScreen, dynamicScreen);
            book.SetDynamic(dynamicScreen, true);
        }
        if (screen is not null)
        {
            book.EnsureScreen(screen, seed.ScreenHint!);
            ApplySeed(screen, seed, ref seedPending);
        }
        string? goalScreenId = seed.GoalScreenId ?? book.GoalScreenFor(goal);
        Pending? pending = null;
        string? previous = null, lastActionId = null, stopReason = "max_steps", error = null;
        bool goalReached = false;
        int unconfirmed = 0, repeats = 0, staleStreak = 0, sinceGoalCheck = 0;
        (string Screen, string Action)? lastDispatch = null;
        FlowFrame? reuse = null;
        int captures = 0, settleCaptures = 0, jevCalls = 0, textCalls = 0, imageCalls = 0, escalations = 0, dispatches = 0,
            localConfirms = 0, mispredictions = 0, learnedBoxes = 0, learnedScreens = 0, learnedTransitions = 0;
        long imageBytes = 0, jevMs = 0;

        void LearnTransition(string from, FlowAction action, string to)
        {
            if (from == to) return;
            bool known = action.Transitions.ContainsKey(to);
            book.RecordTransition(from, action.Id, to);
            if (!known) learnedTransitions++;
        }

        try
        {
            for (int step = 1; step <= _o.MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                if (runClock.Elapsed >= _o.MaxRuntime) { stopReason = "runtime_limit"; break; }
                var stepClock = Stopwatch.StartNew();

                FlowFrame frame;
                if (reuse is not null) { frame = reuse; reuse = null; }
                else { frame = await env.CaptureAsync(ct).ConfigureAwait(false); captures++; }

                // ---- 1. Resolve the screen with free local evidence first. ----
                string? resolved = null, source = "none";
                if (frame.Fingerprint is { } fp && book.Match(fp, _o.FingerprintTolerance) is { } match)
                {
                    resolved = match.ScreenId; source = "fingerprint";
                    localConfirms++;
                    if (screen is not null && screen != resolved && pending is not null) mispredictions++;
                    book.AddFingerprint(resolved, fp);
                    screen = resolved; unconfirmed = 0;
                    ApplyPending(ref pending, resolved);
                }
                else if ((frame.Fingerprint is null || book.IsDynamic(screen ?? "")) && screen is not null)
                {
                    // No usable fingerprint (unsupported environment, or an animated gameplay screen): trust the prediction, but sync with an image periodically.
                    resolved = screen; source = "predicted";
                    unconfirmed++;
                }

                if (screen is not null && goalScreenId is { } goalScreen && source == "fingerprint" && screen == goalScreen)
                {
                    steps.Add(new FlowStepRecord(step, "local", screen, source, "goal_screen", null, null, null, 0, stepClock.ElapsedMilliseconds, "confirmed goal screen by fingerprint"));
                    goalReached = true; stopReason = "goal_screen_reached";
                    break;
                }

                // A predicted (unverified) arrival at the goal screen is confirmed with one image call before the run claims success.
                bool needRecognize = resolved is null
                    || (source == "predicted" && (unconfirmed >= _o.SyncEvery || (goalScreenId is { } gs && screen == gs)));

                // ---- 2. Unknown screen: one image call to recognise it (and learn its fingerprint). ----
                if (needRecognize)
                {
                    var known = book.Screens.Where(s => s.Id != JevActionBook.GlobalScreenId)
                        .Select(s => new FlowScreenSummary(s.Id, s.Description)).ToArray();
                    var menuForPredicted = screen is null ? [] : ExecutableMenu(screen, withVision: true, used);
                    var recognizeRequest = new FlowDecisionRequest(goal, frame.FrameId, frame.Jpeg, screen, previous, lastActionId,
                        recent.TakeLast(8).ToArray(), known, RecognizeScreen: true, menuForPredicted, NeedBoxFor(menuForPredicted),
                        AskGoalReached: true, AllowNeedVision: false, seed.SessionContext);
                    FlowDecision recognized;
                    recognized = await decider.DecideAsync(recognizeRequest, ct).ConfigureAwait(false);
                    jevCalls++; imageCalls++; imageBytes += frame.Jpeg.Length; jevMs += recognized.LatencyMs;
                    sinceGoalCheck = 0;

                    string? predictedBefore = screen;
                    string? recognizedId = recognized.RecognizedScreenId;
                    bool trusted = recognizedId is not null && recognizedId != FlowControl.UnknownScreen
                        && (recognized.ScreenConfidence ?? 0) >= _o.ChoiceThreshold && book.Get(recognizedId) is not null;
                    if (trusted)
                    {
                        if (screen is not null && recognizedId != screen && pending is not null) mispredictions++;
                        screen = recognizedId!; source = "jev";
                        if (frame.Fingerprint is { } f1) book.AddFingerprint(screen, f1);
                    }
                    else if (recognizedId is null || recognizedId == FlowControl.UnknownScreen)
                    {
                        string newId = book.NewScreenId();
                        string desc = JevActionBook.AutoDescriptionPrefix + (pending is null ? "unmapped screen" : $"reached via '{pending.Action.Description}' from {pending.FromScreen}");
                        book.EnsureScreen(newId, desc);
                        learnedScreens++;
                        screen = newId; source = "new";
                        if (frame.Fingerprint is { } f2) book.AddFingerprint(screen, f2);
                    }
                    else
                    {
                        // Low-confidence recognition of a known screen: do not guess.
                        steps.Add(new FlowStepRecord(step, "image", screen, "jev", "uncertain_screen", null, null, recognized.ScreenConfidence, recognized.LatencyMs, stepClock.ElapsedMilliseconds, "screen recognition below threshold"));
                        stopReason = "uncertain_screen";
                        break;
                    }
                    unconfirmed = 0;
                    ApplyPending(ref pending, screen!);
                    ApplySeed(screen!, seed, ref seedPending);

                    // Map a screen with no known inputs using the optional local detector.
                    if (book.MenuFor(screen!).Count == 0 && discoverer is not null)
                    {
                        foreach (var found in await discoverer.DiscoverAsync(frame, ct).ConfigureAwait(false))
                            book.Upsert(screen!, found, out _);
                    }
                    book.DescribeFromActions(screen!);

                    if (recognized.GoalReachedProbability is { } gp && gp >= _o.GoalThreshold)
                    {
                        steps.Add(new FlowStepRecord(step, "image", screen, source, "goal_reached", null, null, gp, recognized.LatencyMs, stepClock.ElapsedMilliseconds, "Jev goal Noul"));
                        book.RecordGoalScreen(goal, screen!);
                        goalReached = true; stopReason = "goal_reached";
                        break;
                    }

                    // Recognition alone learned the fingerprint, so the action decision below is usually text-only.
                    // If this call already carried a menu for the same screen, use its action choice directly.
                    if (source == "jev" && predictedBefore == screen && recognized.Choice.Length > 0 && menuForPredicted.Count > 0)
                    {
                        var outcome = await ExecuteChoiceAsync(step, "image", goal, screen!, frame, recognized, menuForPredicted, source, stepClock, ct).ConfigureAwait(false);
                        steps.Add(outcome.Record);
                        Absorb(outcome);
                        if (Evaluate(outcome) is { } s1) { stopReason = s1; break; }
                        continue;
                    }
                }

                // ---- 3. Choose the action with the cheapest sufficient evidence. ----
                var textMenu = ExecutableMenu(screen!, withVision: false, used);
                var fullMenu = ExecutableMenu(screen!, withVision: true, used);
                if (fullMenu.Count == 0 && used.Count > 0 && book.MenuFor(screen!).Count > 0)
                {
                    steps.Add(new FlowStepRecord(step, "local", screen, source, "action_menu_exhausted", null, null, null, 0, stepClock.ElapsedMilliseconds, "every known input on this screen was already used in this run"));
                    stopReason = "action_menu_exhausted";
                    break;
                }
                if (fullMenu.Count == 0 && discoverer is not null)
                {
                    foreach (var found in await discoverer.DiscoverAsync(frame, ct).ConfigureAwait(false))
                        book.Upsert(screen!, found, out _);
                    book.DescribeFromActions(screen!);
                    textMenu = ExecutableMenu(screen!, withVision: false, used);
                    fullMenu = ExecutableMenu(screen!, withVision: true, used);
                }
                if (fullMenu.Count == 0)
                {
                    steps.Add(new FlowStepRecord(step, "local", screen, source, "unmapped_screen", null, null, null, 0, stepClock.ElapsedMilliseconds, "no known inputs for this screen"));
                    stopReason = "unmapped_screen";
                    break;
                }

                FlowDecision? decision = null;
                string tier = "text";
                bool goalCheckDue = goalScreenId is null && sinceGoalCheck >= _o.GoalCheckEvery;
                if (textMenu.Count > 0 && !goalCheckDue)
                {
                    var req = new FlowDecisionRequest(goal, frame.FrameId, null, screen, previous, lastActionId, recent.TakeLast(8).ToArray(),
                        [], RecognizeScreen: false, textMenu, [], AskGoalReached: false, AllowNeedVision: true, seed.SessionContext);
                    decision = await decider.DecideAsync(req, ct).ConfigureAwait(false);
                    jevCalls++; textCalls++; jevMs += decision.LatencyMs;
                    if (!IsConfident(decision, textMenu) || decision.Choice == FlowControl.NeedVision || decision.Choice == FlowControl.Done)
                    {
                        escalations++;
                        decision = null;
                    }
                }
                if (decision is null)
                {
                    tier = "image";
                    var req = new FlowDecisionRequest(goal, frame.FrameId, frame.Jpeg, screen, previous, lastActionId, recent.TakeLast(8).ToArray(),
                        [], RecognizeScreen: false, fullMenu, NeedBoxFor(fullMenu), AskGoalReached: true, AllowNeedVision: false, seed.SessionContext);
                    decision = await decider.DecideAsync(req, ct).ConfigureAwait(false);
                    jevCalls++; imageCalls++; imageBytes += frame.Jpeg.Length; jevMs += decision.LatencyMs;
                    sinceGoalCheck = 0;
                    if (decision.GoalReachedProbability is { } gp2 && gp2 >= _o.GoalThreshold)
                    {
                        steps.Add(new FlowStepRecord(step, tier, screen, source, "goal_reached", null, null, gp2, decision.LatencyMs, stepClock.ElapsedMilliseconds, "Jev goal Noul"));
                        book.RecordGoalScreen(goal, screen!);
                        goalReached = true; stopReason = "goal_reached";
                        break;
                    }
                }

                var menu = tier == "text" ? textMenu : fullMenu;
                var executed = await ExecuteChoiceAsync(step, tier, goal, screen!, frame, decision, menu, source, stepClock, ct).ConfigureAwait(false);
                steps.Add(executed.Record);
                Absorb(executed);
                if (Evaluate(executed) is { } stop) { stopReason = stop; break; }
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
        }
        catch (JevUnavailableException ex)
        {
            stopReason = "jev_unavailable"; error = ex.Message;   // includes a missing API key in the DCU process; no input was sent for this decision
        }
        catch (Exception ex) when (ex is JevDisabledException or JevValidationException or InvalidOperationException)
        {
            stopReason = "error"; error = ex.Message;
        }

        book.Save();
        var stats = new FlowStats(steps.Count, jevCalls, textCalls, imageCalls, escalations, captures, settleCaptures, dispatches,
            localConfirms, mispredictions, learnedBoxes, learnedScreens, learnedTransitions, imageBytes, jevMs);
        bool reobserve = stopReason is "stale_frame" or "uncertain" or "uncertain_screen" or "box_rejected" or "invalid_placement" or "unmapped_screen" or "error" or "stuck_loop";
        return new FlowRunResult(stopReason, goalReached, screen, steps, stats, runClock.ElapsedMilliseconds, error, reobserve);

        // ---- local helpers (closures over run state) ----
        string? Evaluate(StepOutcome o)
        {
            if (o.Stop is { } s) { error = o.Error; return s; }
            if (CheckStale(o)) return "stale_frame";
            if (repeats > _o.MaxRepeatsWithoutEffect) return "stuck_loop";
            if (dispatches >= _o.MaxActions) return "action_limit";
            return null;
        }

        void Absorb(StepOutcome o)
        {
            jevCalls += o.ExtraJevCalls; imageCalls += o.ExtraJevCalls; imageBytes += o.ExtraImageBytes; jevMs += o.ExtraLatencyMs;
            captures += o.ExtraCaptures;
            if (o.Stale)
            {
                captures += o.SettleCaptures; settleCaptures += o.SettleCaptures;
                reuse = o.SettledFrame;
                return;
            }
            if (!o.Dispatched) return;
            dispatches++;
            sinceGoalCheck++;
            used.Add(UsedKey(o.FromScreen!, o.Action!.Id));
            settleCaptures += o.SettleCaptures;
            captures += o.SettleCaptures;
            pending = new Pending(o.FromScreen!, o.Action!, o.UnverifiedBox, o.BoxConfidence);
            previous = o.FromScreen; lastActionId = o.Action!.Id;
            recent.Add($"{o.Action.Id} sent from {o.FromScreen}; outcome unverified");
            if (recent.Count > 12) recent.RemoveAt(0);
            screen = o.PredictedScreen;
            reuse = o.SettledFrame;
            if (!o.LearnedBox && lastDispatch is { } ld && ld.Screen == o.FromScreen && ld.Action == o.Action.Id) repeats++; else repeats = 1;
            lastDispatch = (o.FromScreen!, o.Action.Id);
        }

        bool CheckStale(StepOutcome o)
        {
            staleStreak = o.Stale ? staleStreak + 1 : 0;
            return staleStreak > _o.StaleRetries;
        }

        void ApplyPending(ref Pending? p, string confirmedScreen)
        {
            if (p is null) return;
            if (confirmedScreen != p.FromScreen)
            {
                LearnTransition(p.FromScreen, p.Action, confirmedScreen);
                // The click demonstrably did something, so the box it used is now trusted for text-only reuse.
                if (p.UnverifiedBox is { } clicked) { book.RecordVerifiedBox(p.FromScreen, p.Action.Id, clicked, p.BoxConfidence); learnedBoxes++; }
            }
            else if (p.Action.Kind == FlowActionKind.Click)
            {
                // A click that provably changed nothing may be retried (bounded by MaxActions and the repeat guard); a click that worked may not.
                book.RecordNoEffect(p.FromScreen, p.Action.Id);
                used.Remove(UsedKey(p.FromScreen, p.Action.Id));
            }
            p = null;
        }
    }

    private void ApplySeed(string screenId, FlowSeed seed, ref bool seedPending)
    {
        if (!seedPending || seed.Actions is null) return;
        foreach (var action in seed.Actions) book.Upsert(screenId, action, out _);
        seedPending = false;
    }

    private static string UsedKey(string screen, string actionId) => screen + "\u001f" + actionId;

    private IReadOnlyList<FlowAction> ExecutableMenu(string screenId, bool withVision, ISet<string> used)
        => book.MenuFor(screenId).Where(a => !used.Contains(UsedKey(screenId, a.Id)) && JevFlowGuard.Refusal(a) is null && (withVision || a.ExecutableWithoutVision)).ToArray();

    private IReadOnlyList<FlowAction> NeedBoxFor(IReadOnlyList<FlowAction> menu)
        => menu.Where(a => (a.Kind == FlowActionKind.Click && !a.BoxVerified) || a.Kind == FlowActionKind.Place).Take(_o.MaxNeedBoxQuestions).ToArray();

    private bool IsConfident(FlowDecision d, IReadOnlyList<FlowAction> menu)
    {
        if (FlowControl.IsControl(d.Choice)) return true;
        if (menu.All(a => a.Id != d.Choice)) return false;
        if (d.Confidence < _o.ChoiceThreshold) return false;
        if (d.Probabilities.GetValueOrDefault(d.Choice) < _o.ChoiceThreshold) return false;
        var top = d.Probabilities.Values.OrderByDescending(v => v).Take(2).ToArray();
        return top.Length < 2 || top[0] - top[1] >= _o.AmbiguityMargin;
    }

    private sealed class StepOutcome
    {
        public required FlowStepRecord Record { get; init; }
        public string? Stop { get; init; }
        public string? Error { get; init; }
        public bool Dispatched { get; init; }
        public bool LearnedBox { get; init; }
        public NormBox? UnverifiedBox { get; init; }
        public double BoxConfidence { get; init; }
        public string? FromScreen { get; init; }
        public FlowAction? Action { get; init; }
        public string? PredictedScreen { get; init; }
        public FlowFrame? SettledFrame { get; init; }
        public int SettleCaptures { get; init; }
        public bool Stale { get; init; }
        public int ExtraJevCalls { get; init; }
        public long ExtraImageBytes { get; init; }
        public long ExtraLatencyMs { get; init; }
        public int ExtraCaptures { get; init; }
    }

    private static bool RegionContains(NormBox region, NormBox box)
        => box.Left >= region.Left && box.Top >= region.Top && box.Right <= region.Right && box.Bottom <= region.Bottom;

    private async Task<StepOutcome> ExecuteChoiceAsync(int step, string tier, string goal, string screen, FlowFrame frame, FlowDecision decision,
        IReadOnlyList<FlowAction> menu, string screenSource, Stopwatch stepClock, CancellationToken ct)
    {
        int extraCalls = 0, extraCaptures = 0;
        long extraBytes = 0, extraMs = 0;

        StepOutcome Refuse(string outcome, string stop, string note, double? conf = null) => new()
        {
            Record = new FlowStepRecord(step, tier, screen, screenSource, outcome, null, null, conf ?? decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, note),
            Stop = stop, ExtraJevCalls = extraCalls, ExtraImageBytes = extraBytes, ExtraLatencyMs = extraMs, ExtraCaptures = extraCaptures,
        };

        switch (decision.Choice)
        {
            case FlowControl.Stop: return Refuse("jev_stop", "jev_stop", "Jev chose STOP");
            case FlowControl.AskUser: return Refuse("ask_user", "ask_user", "Jev asked the user");
            case FlowControl.Wait:
                await Task.Delay(_o.SettleMs, ct).ConfigureAwait(false);
                return new StepOutcome { Record = new FlowStepRecord(step, tier, screen, screenSource, "wait", null, null, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, "Jev chose WAIT") };
            case FlowControl.Done:
            case FlowControl.NeedVision:
                return Refuse("uncertain", "uncertain", $"Jev chose {decision.Choice} with no further evidence", decision.Confidence);
        }

        var action = menu.FirstOrDefault(a => a.Id == decision.Choice);
        if (action is null) return Refuse("invalid_choice", "uncertain", "Jev chose an id outside the offered menu");
        if (!IsConfident(decision, menu)) return Refuse("uncertain", "uncertain", "choice below confidence or ambiguity gate");

        NormBox? target = null;
        bool unverified = false;
        double boxConfidence = 0;
        if (action.Kind == FlowActionKind.Click)
        {
            if (action.Box is { } known && action.BoxVerified) { target = known; boxConfidence = action.BoxConfidence; }
            else if (decision.Boxes.TryGetValue(action.Id, out var jb) && BoxPasses(jb))
            {
                // A box that no click has confirmed yet is cross-checked against the independent local proposal when one exists.
                if (action.Box is { } proposal && !jb.Box.Agrees(proposal))
                    return Refuse("box_rejected", "box_rejected", "Jev Box disagrees with the local detector's box for this control; no input sent");
                target = jb.Box; unverified = true; boxConfidence = jb.Confidence;
            }
            else return Refuse("box_rejected", "box_rejected", "no verified box and Jev Box failed its visibility/confidence/size gates; no input sent");
        }
        else if (action.Kind == FlowActionKind.Place)
        {
            if (!decision.Boxes.TryGetValue(action.Id, out var pb) || !BoxPasses(pb))
                return Refuse("box_rejected", "box_rejected", "placement target failed the Jev Box visibility/confidence/size gates; no input sent");
            if (action.ApprovedRegion is not { } region || !RegionContains(region, pb.Box))
                return Refuse("box_rejected", "box_rejected", "placement target lies outside the approved region (for example the opponent's half); no input sent");
            target = pb.Box; boxConfidence = pb.Confidence;
        }

        bool dynamicScreen = book.IsDynamic(screen);
        // The model call took time. Re-check the live screen with a free local capture before any input, and dispatch against that fresh frame.
        var dispatchFrame = frame;
        if (frame.Fingerprint is not null && !dynamicScreen)
        {
            var fresh = await env.CaptureAsync(ct).ConfigureAwait(false);
            extraCaptures++;
            if (fresh.Fingerprint is { } freshPrint && book.Match(freshPrint, _o.FingerprintTolerance)?.ScreenId != screen)
                return new StepOutcome
                {
                    Record = new FlowStepRecord(step, tier, screen, screenSource, "stale_screen", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, "screen changed while Jev decided; no input sent"),
                    Stale = true, SettledFrame = fresh, ExtraJevCalls = extraCalls, ExtraImageBytes = extraBytes, ExtraLatencyMs = extraMs, ExtraCaptures = extraCaptures,
                };
            dispatchFrame = fresh;
        }

        StepOutcome? Failure(FlowDispatchResult r) => r.InputSent ? null
            : r.Reobserve && !r.DoNotRetry
                ? new StepOutcome
                {
                    Record = new FlowStepRecord(step, tier, screen, screenSource, "stale_frame", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, r.Error),
                    Stale = true, ExtraJevCalls = extraCalls, ExtraImageBytes = extraBytes, ExtraLatencyMs = extraMs, ExtraCaptures = extraCaptures,
                }
                : new StepOutcome
                {
                    Record = new FlowStepRecord(step, tier, screen, screenSource, "dispatch_failed", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, r.Error),
                    Stop = "dispatch_failed", Error = r.Error, ExtraJevCalls = extraCalls, ExtraImageBytes = extraBytes, ExtraLatencyMs = extraMs, ExtraCaptures = extraCaptures,
                };

        FlowDispatchResult sent;
        if (action.Kind == FlowActionKind.Place)
        {
            // Move first (the game draws the placement ghost under the cursor), judge the preview on a fresh frame, and click only if it is valid.
            var moved = await env.MoveAsync(dispatchFrame, target!.Value, ct).ConfigureAwait(false);
            if (Failure(moved) is { } moveFailure) return moveFailure;
            await Task.Delay(_o.SettleMs, ct).ConfigureAwait(false);
            var check = await env.CaptureAsync(ct).ConfigureAwait(false);
            extraCaptures++;
            var verdict = await decider.DecideAsync(new FlowDecisionRequest(goal, check.FrameId, check.Jpeg, screen, null, action.Id, [], [], false, [], [],
                false, false, null, action.PlacementCheck), ct).ConfigureAwait(false);
            extraCalls++; extraBytes += check.Jpeg.Length; extraMs += verdict.LatencyMs;
            if (verdict.PlacementValidProbability is not { } valid || valid < _o.BoxThreshold)
                return Refuse("invalid_placement", "invalid_placement", $"the placement preview was not confirmed valid (score {verdict.PlacementValidProbability?.ToString("0.00") ?? "none"}); the cursor was moved but no click was sent");
            dispatchFrame = check;
            sent = await env.ClickAsync(dispatchFrame, target.Value, ct).ConfigureAwait(false);
        }
        else
        {
            sent = action.Kind == FlowActionKind.Click
                ? await env.ClickAsync(dispatchFrame, target!.Value, ct).ConfigureAwait(false)
                : await env.TapKeyAsync(dispatchFrame, action.Key!, ct).ConfigureAwait(false);
        }
        if (Failure(sent) is { } failure) return failure;

        bool firstUse = action.Uses == 0;
        book.RecordUse(screen, action.Id);

        string? learnedNext = action.ExpectedNextScreen;
        // An action on an animated screen, or one used before that never produced a transition, is assumed to leave the screen as it is.
        string? predicted = learnedNext ?? (dynamicScreen || !firstUse ? screen : null);
        bool quick = dynamicScreen || action.Kind == FlowActionKind.Place || learnedNext is null && !firstUse;
        var (settled, settleCaptures) = await SettleAsync(frame, screen, learnedNext, quick, ct).ConfigureAwait(false);
        return new StepOutcome
        {
            Record = new FlowStepRecord(step, tier, screen, screenSource, "dispatched", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds,
                predicted is null ? "next screen unknown; will be recognised" : $"predicted next screen {predicted}"),
            Dispatched = true, LearnedBox = unverified, UnverifiedBox = unverified ? target : null, BoxConfidence = boxConfidence,
            FromScreen = screen, Action = action, PredictedScreen = predicted,
            SettledFrame = settled, SettleCaptures = settleCaptures,
            ExtraJevCalls = extraCalls, ExtraImageBytes = extraBytes, ExtraLatencyMs = extraMs, ExtraCaptures = extraCaptures,
        };
    }

    private bool BoxPasses(FlowBox b)
        => b.VisibleProbability >= _o.BoxThreshold && b.Confidence >= _o.BoxThreshold && b.Box.IsValid && b.Box.Area <= JevFlowGuard.MaxBoxArea;

    /// <summary>
    /// Wait for the UI to react without involving the model: poll cheap local captures until the frame fingerprint matches the
    /// predicted screen (or leaves the origin and holds still when the destination is unknown). Without fingerprints, a fixed delay.
    /// </summary>
    private async Task<(FlowFrame? Frame, int Captures)> SettleAsync(FlowFrame before, string from, string? predicted, bool quick, CancellationToken ct)
    {
        // Quick path: gameplay/placement and actions known to leave the screen unchanged do not wait for a transition that will not come.
        if (before.Fingerprint is null || quick)
        {
            await Task.Delay(_o.SettleMs, ct).ConfigureAwait(false);
            return (null, 0);
        }
        int limit = predicted is not null ? _o.MaxSettleMs : _o.FirstSettleMs;
        var deadline = Stopwatch.StartNew();
        FlowFrame? last = null;
        byte[]? stableRef = null;
        int captures = 0;
        await Task.Delay(Math.Min(_o.SettleMs, limit), ct).ConfigureAwait(false);
        while (deadline.ElapsedMilliseconds < limit)
        {
            ct.ThrowIfCancellationRequested();
            var frame = await env.CaptureAsync(ct).ConfigureAwait(false);
            captures++;
            last = frame;
            if (frame.Fingerprint is { } fp)
            {
                var match = book.Match(fp, _o.FingerprintTolerance);
                if (predicted is not null) { if (match?.ScreenId == predicted) break; }
                else if (match?.ScreenId != from)
                {
                    if (stableRef is not null && JevActionBook.Distance(stableRef, fp) < 0.02) break;
                    stableRef = fp;
                }
            }
            await Task.Delay(_o.PollMs, ct).ConfigureAwait(false);
        }
        return (last, captures);
    }
}
