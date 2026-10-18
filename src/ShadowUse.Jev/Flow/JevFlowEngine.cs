// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Diagnostics;

namespace ShadowUse.Jev.Flow;

public sealed class JevFlowOptions
{
    public int MaxSteps { get; init; } = 12;
    public TimeSpan MaxRuntime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Steps that may rely on a predicted (not locally verified) screen before an image sync is forced.</summary>
    public int SyncEvery { get; init; } = 4;

    public double ChoiceThreshold { get; init; } = 0.85;
    public double BoxThreshold { get; init; } = 0.90;
    public double AmbiguityMargin { get; init; } = 0.10;
    public double GoalThreshold { get; init; } = 0.85;

    /// <summary>Mean luminance difference (0-1) below which a frame counts as a saved screen.</summary>
    public double FingerprintTolerance { get; init; } = 0.08;

    public int SettleMs { get; init; } = 150;
    public int MaxSettleMs { get; init; } = 1_500;
    public int PollMs { get; init; } = 60;
    public int MaxNeedBoxQuestions { get; init; } = 8;
    public int MaxRepeatsWithoutEffect { get; init; } = 3;
}

public sealed record FlowSeed(
    string? ScreenHint = null,
    string? GoalScreenId = null,
    IReadOnlyList<FlowAction>? Actions = null,
    IReadOnlyList<string>? RecentActions = null,
    string? SessionContext = null);

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
    string? Error);

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

    private sealed record Pending(string FromScreen, FlowAction Action);

    public async Task<FlowRunResult> RunAsync(string goal, FlowSeed? seed = null, CancellationToken ct = default)
    {
        seed ??= new FlowSeed();
        var runClock = Stopwatch.StartNew();
        var steps = new List<FlowStepRecord>();
        var recent = new List<string>(seed.RecentActions ?? []);
        string? screen = seed.ScreenHint;
        bool seedPending = seed.Actions is { Count: > 0 };
        if (screen is not null)
        {
            book.EnsureScreen(screen, seed.ScreenHint!);
            ApplySeed(screen, seed, ref seedPending);
        }
        string? goalScreenId = seed.GoalScreenId ?? book.GoalScreenFor(goal);
        Pending? pending = null;
        string? previous = null, lastActionId = null, stopReason = "max_steps", error = null;
        bool goalReached = false;
        int unconfirmed = 0, repeats = 0;
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
                else if (frame.Fingerprint is null && screen is not null)
                {
                    // The environment cannot fingerprint: trust the prediction, but sync with an image periodically.
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
                    var menuForPredicted = screen is null ? [] : ExecutableMenu(screen, withVision: true);
                    var recognizeRequest = new FlowDecisionRequest(goal, frame.FrameId, frame.Jpeg, screen, previous, lastActionId,
                        recent.TakeLast(8).ToArray(), known, RecognizeScreen: true, menuForPredicted, NeedBoxFor(menuForPredicted),
                        AskGoalReached: true, AllowNeedVision: false, seed.SessionContext);
                    FlowDecision recognized;
                    recognized = await decider.DecideAsync(recognizeRequest, ct).ConfigureAwait(false);
                    jevCalls++; imageCalls++; imageBytes += frame.Jpeg.Length; jevMs += recognized.LatencyMs;

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
                        var outcome = await ExecuteChoiceAsync(step, "image", screen!, frame, recognized, menuForPredicted, source, stepClock, ct).ConfigureAwait(false);
                        steps.Add(outcome.Record);
                        Absorb(outcome);
                        if (outcome.Stop is { } s1) { stopReason = s1; error = outcome.Error; break; }
                        if (repeats > _o.MaxRepeatsWithoutEffect) { stopReason = "stuck_loop"; break; }
                        continue;
                    }
                }

                // ---- 3. Choose the action with the cheapest sufficient evidence. ----
                var textMenu = ExecutableMenu(screen!, withVision: false);
                var fullMenu = ExecutableMenu(screen!, withVision: true);
                if (fullMenu.Count == 0 && discoverer is not null)
                {
                    foreach (var found in await discoverer.DiscoverAsync(frame, ct).ConfigureAwait(false))
                        book.Upsert(screen!, found, out _);
                    book.DescribeFromActions(screen!);
                    textMenu = ExecutableMenu(screen!, withVision: false);
                    fullMenu = ExecutableMenu(screen!, withVision: true);
                }
                if (fullMenu.Count == 0)
                {
                    steps.Add(new FlowStepRecord(step, "local", screen, source, "unmapped_screen", null, null, null, 0, stepClock.ElapsedMilliseconds, "no known inputs for this screen"));
                    stopReason = "unmapped_screen";
                    break;
                }

                FlowDecision? decision = null;
                string tier = "text";
                if (textMenu.Count > 0)
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
                    if (decision.GoalReachedProbability is { } gp2 && gp2 >= _o.GoalThreshold)
                    {
                        steps.Add(new FlowStepRecord(step, tier, screen, source, "goal_reached", null, null, gp2, decision.LatencyMs, stepClock.ElapsedMilliseconds, "Jev goal Noul"));
                        book.RecordGoalScreen(goal, screen!);
                        goalReached = true; stopReason = "goal_reached";
                        break;
                    }
                }

                var menu = tier == "text" ? textMenu : fullMenu;
                var executed = await ExecuteChoiceAsync(step, tier, screen!, frame, decision, menu, source, stepClock, ct).ConfigureAwait(false);
                steps.Add(executed.Record);
                Absorb(executed);
                if (executed.Stop is { } stop) { stopReason = stop; error = executed.Error; break; }
                if (repeats > _o.MaxRepeatsWithoutEffect) { stopReason = "stuck_loop"; break; }
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "cancelled";
        }
        catch (Exception ex) when (ex is JevDisabledException or JevUnavailableException or JevValidationException or InvalidOperationException)
        {
            stopReason = "error"; error = ex.Message;
        }

        book.Save();
        var stats = new FlowStats(steps.Count, jevCalls, textCalls, imageCalls, escalations, captures, settleCaptures, dispatches,
            localConfirms, mispredictions, learnedBoxes, learnedScreens, learnedTransitions, imageBytes, jevMs);
        return new FlowRunResult(stopReason, goalReached, screen, steps, stats, runClock.ElapsedMilliseconds, error);

        // ---- local helpers (closures over run state) ----
        void Absorb(StepOutcome o)
        {
            if (!o.Dispatched) return;
            dispatches++;
            if (o.LearnedBox) learnedBoxes++;
            settleCaptures += o.SettleCaptures;
            captures += o.SettleCaptures;
            pending = new Pending(o.FromScreen!, o.Action!);
            previous = o.FromScreen; lastActionId = o.Action!.Id;
            recent.Add($"{o.Action.Id} sent from {o.FromScreen}; outcome unverified");
            if (recent.Count > 12) recent.RemoveAt(0);
            screen = o.PredictedScreen;
            reuse = o.SettledFrame;
            if (!o.LearnedBox && lastDispatch is { } ld && ld.Screen == o.FromScreen && ld.Action == o.Action.Id) repeats++; else repeats = 1;
            lastDispatch = (o.FromScreen!, o.Action.Id);
        }

        void ApplyPending(ref Pending? p, string confirmedScreen)
        {
            if (p is null) return;
            if (confirmedScreen != p.FromScreen) LearnTransition(p.FromScreen, p.Action, confirmedScreen);
            else if (p.Action.Kind == FlowActionKind.Click) book.RecordNoEffect(p.FromScreen, p.Action.Id);
            p = null;
        }
    }

    private void ApplySeed(string screenId, FlowSeed seed, ref bool seedPending)
    {
        if (!seedPending || seed.Actions is null) return;
        foreach (var action in seed.Actions) book.Upsert(screenId, action, out _);
        seedPending = false;
    }

    private IReadOnlyList<FlowAction> ExecutableMenu(string screenId, bool withVision)
        => book.MenuFor(screenId).Where(a => JevFlowGuard.Refusal(a) is null && (withVision || a.ExecutableWithoutVision)).ToArray();

    private IReadOnlyList<FlowAction> NeedBoxFor(IReadOnlyList<FlowAction> menu)
        => menu.Where(a => a.Kind == FlowActionKind.Click && a.Box is null).Take(_o.MaxNeedBoxQuestions).ToArray();

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
        public string? FromScreen { get; init; }
        public FlowAction? Action { get; init; }
        public string? PredictedScreen { get; init; }
        public FlowFrame? SettledFrame { get; init; }
        public int SettleCaptures { get; init; }
    }

    private async Task<StepOutcome> ExecuteChoiceAsync(int step, string tier, string screen, FlowFrame frame, FlowDecision decision,
        IReadOnlyList<FlowAction> menu, string screenSource, Stopwatch stepClock, CancellationToken ct)
    {
        StepOutcome Refuse(string outcome, string stop, string note, double? conf = null) => new()
        {
            Record = new FlowStepRecord(step, tier, screen, screenSource, outcome, null, null, conf ?? decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, note),
            Stop = stop,
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
        bool learnedBox = false;
        if (action.Kind == FlowActionKind.Click)
        {
            if (action.Box is { } known) target = known;
            else if (decision.Boxes.TryGetValue(action.Id, out var jb)
                     && jb.VisibleProbability >= _o.BoxThreshold && jb.Confidence >= _o.BoxThreshold
                     && jb.Box.IsValid && jb.Box.Area <= JevFlowGuard.MaxBoxArea)
            {
                target = jb.Box; learnedBox = true;
            }
            else return Refuse("box_rejected", "box_rejected", "no valid learned box and Jev Box failed its visibility/confidence/size gates");
        }

        FlowDispatchResult sent = action.Kind == FlowActionKind.Click
            ? await env.ClickAsync(frame, target!.Value, ct).ConfigureAwait(false)
            : await env.TapKeyAsync(frame, action.Key!, ct).ConfigureAwait(false);
        if (!sent.InputSent)
            return new StepOutcome
            {
                Record = new FlowStepRecord(step, tier, screen, screenSource, "dispatch_failed", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds, sent.Error),
                Stop = "dispatch_failed", Error = sent.Error,
            };

        if (learnedBox) book.RecordBox(screen, action.Id, target!.Value, decision.Boxes[action.Id].Confidence);
        book.RecordUse(screen, action.Id);

        string? predicted = action.ExpectedNextScreen;
        var (settled, settleCaptures) = await SettleAsync(frame, screen, predicted, ct).ConfigureAwait(false);
        return new StepOutcome
        {
            Record = new FlowStepRecord(step, tier, screen, screenSource, "dispatched", action.Id, action.Kind, decision.Confidence, decision.LatencyMs, stepClock.ElapsedMilliseconds,
                predicted is null ? "next screen unknown; will be recognised" : $"predicted next screen {predicted}"),
            Dispatched = true, LearnedBox = learnedBox, FromScreen = screen, Action = action, PredictedScreen = predicted,
            SettledFrame = settled, SettleCaptures = settleCaptures,
        };
    }

    /// <summary>
    /// Wait for the UI to react without involving the model: poll cheap local captures until the frame fingerprint matches the
    /// predicted screen (or leaves the origin and holds still when the destination is unknown). Without fingerprints, a fixed delay.
    /// </summary>
    private async Task<(FlowFrame? Frame, int Captures)> SettleAsync(FlowFrame before, string from, string? predicted, CancellationToken ct)
    {
        if (before.Fingerprint is null)
        {
            await Task.Delay(_o.SettleMs, ct).ConfigureAwait(false);
            return (null, 0);
        }
        var deadline = Stopwatch.StartNew();
        FlowFrame? last = null;
        byte[]? stableRef = null;
        int captures = 0;
        await Task.Delay(Math.Min(_o.SettleMs, _o.MaxSettleMs), ct).ConfigureAwait(false);
        while (deadline.ElapsedMilliseconds < _o.MaxSettleMs)
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
