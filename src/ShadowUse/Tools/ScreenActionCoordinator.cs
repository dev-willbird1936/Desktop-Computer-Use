// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using ShadowUse.Automation;
using ShadowUse.Jev;
using ShadowUse.Native;
using ShadowUse.Vision;

namespace ShadowUse.Tools;

/// <summary>A locally prepared route for one Jev-listed action. It is not an execution authorization.</summary>
public sealed record ScreenInputPlan(
    string ElementId,
    string ActionId,
    string InputKind,
    IReadOnlyList<string> KeySequence,
    string ObservationId,
    string? ActionCatalogRevision,
    int? X,
    int? Y,
    int? Width,
    int? Height,
    string Note);

/// <summary>One adapter-supplied option plus its current-frame, adapter-specific fallback input.</summary>
public sealed record ScreenActionOption(
    JevElement Element,
    string ActionKey,
    IReadOnlyList<string> Aliases,
    ScreenInputPlan FallbackInput);

public sealed record ScreenActionPlan(
    string AppId,
    string AppVersion,
    string Profile,
    string KeyboardLayout,
    string ScreenId,
    string ObservationId,
    GameScreenshotFrameBinding FrameBinding,
    string? ActionCatalogRevision,
    JevDecideRequest Request,
    IReadOnlyDictionary<string, ScreenInputPlan> Inputs);

public sealed record ScreenInputResolution(bool Prepared, ScreenInputPlan? Input, string? Error);

/// <summary>Combines an adapter's fresh options with app-sourced keybinds into a single advisory Jev Choice.</summary>
public static class ScreenActionCoordinator
{
    public static ScreenActionPlan Build(
        string appId,
        string userIntention,
        string? believedState,
        string appVersion,
        string profile,
        string keyboardLayout,
        string screenId,
        string? screenHint,
        string screenMemoryNote,
        string observationId,
        GameScreenshotFrameBinding frameBinding,
        string scope,
        bool adapterClaimsComplete,
        string? candidateSetIssue,
        IReadOnlyList<string> observedText,
        IReadOnlyList<ScreenActionOption> visibleOptions,
        AppActionCatalogLookup catalog,
        IReadOnlyList<string>? recentActions = null,
        IReadOnlyList<string>? storedRecentActions = null,
        IReadOnlyList<ScreenTransitionRecord>? observedTransitions = null,
        int screenObservationCount = 1,
        bool screenStateStable = false)
    {
        if (string.IsNullOrWhiteSpace(appId)) throw new ArgumentException("app_id is required.", nameof(appId));
        if (string.IsNullOrWhiteSpace(userIntention)) throw new ArgumentException("user_intention is required.", nameof(userIntention));
        if (string.IsNullOrWhiteSpace(observationId)) throw new ArgumentException("observation_id is required.", nameof(observationId));
        ArgumentNullException.ThrowIfNull(frameBinding);
        if (frameBinding.ObservationId != observationId || !frameBinding.IsFullFrame)
            throw new ArgumentException("A matching full-frame binding is required.", nameof(frameBinding));
        ArgumentNullException.ThrowIfNull(visibleOptions);
        if (visibleOptions.Count > 255) throw new ArgumentException("The visible option inventory exceeds Jev's 255-choice limit.", nameof(visibleOptions));

        var snapshot = catalog.Found ? catalog.Snapshot : null;
        var scopedActions = snapshot is null ? [] : AppActionCatalog.ApplicableActions(snapshot, screenHint);
        var conflicts = snapshot is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : AppActionCatalog.GetConflicts(snapshot.Actions, screenHint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool catalogCurrent = snapshot is not null && !catalog.Advisory;
        var ambiguousActions = catalogCurrent
            ? scopedActions.Where(entry => entry.KeySequence.Count > 0 && !conflicts.Contains(entry.ActionId)
                    && entry.EvidenceStatus != "unverified" && entry.TargetAliases.Count > 0
                    && visibleOptions.Count(option => entry.TargetAliases.Any(alias => option.Aliases.Any(candidateAlias => Matches(alias, candidateAlias)))) > 1)
                .Select(entry => entry.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elements = new List<JevElement>(Math.Min(255, visibleOptions.Count + 16));
        var inputs = new Dictionary<string, ScreenInputPlan>(StringComparer.Ordinal);
        var keyBindings = new Dictionary<string, JevControlOption>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in visibleOptions)
        {
            var matches = catalogCurrent
                ? scopedActions.Where(entry => entry.KeySequence.Count > 0 && !conflicts.Contains(entry.ActionId)
                    && entry.EvidenceStatus != "unverified" && !ambiguousActions.Contains(entry.ActionId)
                    && entry.TargetAliases.Any(alias => option.Aliases.Any(candidateAlias => Matches(alias, candidateAlias))))
                    .ToArray()
                : [];
            var distinct = matches
                .GroupBy(entry => entry.ActionId + "\u001e" + SequenceKey(entry.KeySequence), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
            if (distinct.Length == 1)
            {
                var binding = distinct[0];
                var keyText = string.Join(" then ", binding.KeySequence);
                var metadata = CopyMetadata(option.Element.Extra);
                metadata["recommended_input_kind"] = "keybinding";
                metadata["app_action_id"] = binding.ActionId;
                metadata["key_sequence"] = binding.KeySequence.ToArray();
                metadata["binding_source_kind"] = binding.SourceKind;
                metadata["binding_source_reference"] = binding.SourceReference;
                metadata["binding_evidence_status"] = binding.EvidenceStatus;
                metadata["binding_preconditions"] = binding.Preconditions;
                metadata["action_catalog_revision"] = snapshot!.Revision;
                metadata["dispatch_enabled"] = false;
                elements.Add(option.Element with { Name = $"{option.Element.Name} (key: {keyText})", Extra = metadata });
                inputs[option.Element.Id] = new(option.Element.Id, binding.ActionId, "key", binding.KeySequence.ToArray(),
                    observationId, snapshot.Revision, null, null, null, null,
                    "Sourced binding; recommendation only, no input dispatched.");
                AddBinding(keyBindings, binding);
            }
            else
            {
                var metadata = CopyMetadata(option.Element.Extra);
                metadata["recommended_input_kind"] = option.FallbackInput.InputKind;
                metadata["source_frame_id"] = observationId;
                metadata["dispatch_enabled"] = false;
                if (distinct.Length > 1) metadata["binding_conflict"] = true;
                if (catalogCurrent && scopedActions.Any(entry => ambiguousActions.Contains(entry.ActionId)
                        && entry.TargetAliases.Any(alias => option.Aliases.Any(candidateAlias => Matches(alias, candidateAlias)))))
                    metadata["binding_ambiguous"] = true;
                elements.Add(option.Element with { Extra = metadata });
                inputs[option.Element.Id] = option.FallbackInput with
                {
                    ObservationId = observationId,
                    ActionCatalogRevision = snapshot?.Revision,
                    Note = distinct.Length > 1
                        ? "Conflicting bindings; fallback is current adapter evidence only."
                        : option.FallbackInput.Note,
                };
            }
        }

        bool catalogActionsOmitted = false;
        if (catalogCurrent && snapshot is not null)
        {
            foreach (var entry in scopedActions.Where(entry => entry.KeySequence.Count > 0
                         && entry.TargetAliases.Count == 0 && !conflicts.Contains(entry.ActionId)
                         && entry.EvidenceStatus != "unverified")
                         .GroupBy(entry => entry.ActionId, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                if (elements.Count >= 255) { catalogActionsOmitted = true; break; }
                string id = $"{observationId}:action:{entry.ActionId}";
                if (inputs.ContainsKey(id)) continue;
                string keyText = string.Join(" then ", entry.KeySequence);
                var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "app_action_catalog",
                    ["app_action_id"] = entry.ActionId,
                    ["key_sequence"] = entry.KeySequence.ToArray(),
                    ["screen_id"] = screenId,
                    ["binding_source_kind"] = entry.SourceKind,
                    ["binding_source_reference"] = entry.SourceReference,
                    ["binding_evidence_status"] = entry.EvidenceStatus,
                    ["binding_preconditions"] = entry.Preconditions,
                    ["action_catalog_revision"] = snapshot.Revision,
                    ["candidate_set_complete"] = false,
                    ["actionability"] = "sourced_app_action_unverified",
                    ["dispatch_enabled"] = false,
                };
                elements.Add(new JevElement(id, "known app action", $"{entry.Label} (key: {keyText})", null, null,
                    [JevRecommendationRequestBuilder.RecommendTargetOperation], metadata));
                inputs[id] = new(id, entry.ActionId, "key", entry.KeySequence.ToArray(), observationId,
                    snapshot.Revision, null, null, null, null,
                    "Sourced app action; applicability is caller-reported; recommendation only.");
                AddBinding(keyBindings, entry);
            }
        }

        var history = (storedRecentActions ?? []).Concat(recentActions ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .TakeLast(ScreenStateStore.MaxRecentActions).ToArray();
        string issue = candidateSetIssue
            ?? "Visual and app-action discovery does not establish exhaustive controls or all valid actions.";
        string catalogNote = snapshot is null
            ? "No sourced action catalog is available."
            : $"Action catalog revision {snapshot.Revision}; app version/profile/layout match={(catalogCurrent ? "yes" : "no; advisory only")}; conflicting action bindings={conflicts.Count}.";
        var transitionNotes = (screenStateStable ? observedTransitions ?? [] : []).Take(8).Select(transition =>
            $"{transition.FromScreenId} --{transition.ActionId}/{transition.InputKind}--> {transition.ToScreenId}; effect={transition.Effect ?? "unknown"}; outcome={transition.Outcome ?? "unverified"}; confidence={transition.Confidence?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; success_reports={transition.SuccessCount}; failure_reports={transition.FailureCount}; {transition.EvidenceStatus}.").ToArray();
        string transitionContext = transitionNotes.Length == 0 ? "No prior screen transitions are recorded."
            : "Prior screen transitions and outcomes are caller-reported evidence, not authorization: " + string.Join(" ", transitionNotes);
        string context = string.Join(" ", new[]
        {
            scope,
            $"App identity: {appId}; version: {appVersion}; profile: {profile}; keyboard layout: {keyboardLayout}.",
            $"User intention: {userIntention}.",
            $"Caller-believed state (unverified): {believedState ?? "unknown"}.",
            $"Observed semantic screen id: {screenId}; hint: {screenHint ?? "unknown"}; observation_count={Math.Max(1, screenObservationCount)}; state_stable={screenStateStable}. {screenMemoryNote}",
            catalogNote,
            transitionContext,
            "Select only a listed target/action. Route preference is sourced keybind, deterministic adapter action, hash-verified static anchor, then fresh visual candidate. Dynamic or spatial actions require fresh visual localization. A key sequence is sourced knowledge, not permission or a guarantee of effect. Visual boxes are bound to this frame. No input will be dispatched by this decision tool.",
        });
        bool complete = false; // The merged visual/action universe is never promoted to complete here.
        string issueCombined = adapterClaimsComplete
            ? issue + " The app action catalog and full interaction universe remain unverified."
            : issue;
        if (catalogActionsOmitted) issueCombined += " The app action catalog exceeded Jev's 255-choice limit; some actions were omitted.";
        var request = JevRecommendationRequestBuilder.Build(
            userIntention, observationId, elements, context, string.Join("\n", observedText),
            DateTimeOffset.UtcNow.AddSeconds(20), complete, issueCombined, history,
            allowIncompleteCandidateSetForRecommendationOnly: true,
            allowScoreDisambiguation: false,
            maxRetriesOverride: 0,
            keyBindings: keyBindings.Values.OrderBy(item => item.Code, StringComparer.Ordinal).Take(255).ToArray());
        return new ScreenActionPlan(appId, appVersion, profile, keyboardLayout, screenId,
            observationId, frameBinding, snapshot?.Revision, request, inputs);
    }

    public static ScreenInputResolution Resolve(JevDecision decision, ScreenActionPlan plan,
        GameScreenshotFrameBinding currentFrameBinding, string? currentCatalogRevision)
    {
        if (decision.ObservationId != plan.ObservationId)
            return new(false, null, "Jev decision frame does not match the action plan.");
        if (!SameFrame(plan.FrameBinding, currentFrameBinding))
            return new(false, null, "The captured frame, target lifetime, geometry, DPI, or masks changed; rediscover and decide again.");
        if (plan.FrameBinding.MaskedImageBoxes is { Count: > 0 })
            return new(false, null, "Masked screenshots cannot produce a dispatchable input plan.");
        if (plan.Request.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return new(false, null, "The frame-bound action plan expired; capture and decide again.");
        if (!string.Equals(plan.ActionCatalogRevision, currentCatalogRevision, StringComparison.Ordinal))
            return new(false, null, "The app action catalog changed after the decision; rebuild the choice set.");
        if (!string.Equals(decision.Operation, JevRecommendationRequestBuilder.RecommendTargetOperation, StringComparison.Ordinal)
            || decision.TargetElementId is null)
            return new(false, null, "Jev selected a no-action control option; no input plan exists.");
        if (!plan.Inputs.TryGetValue(decision.TargetElementId, out var input))
            return new(false, null, "Jev selected an option absent from this action plan.");
        return new(true, input, null);
    }

    private static Dictionary<string, object?> CopyMetadata(IReadOnlyDictionary<string, object?>? metadata)
        => metadata is null ? new(StringComparer.Ordinal)
            : metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static bool Matches(string alias, string candidate)
        => Normalize(alias).Length > 0 && Normalize(alias) == Normalize(candidate);

    private static string Normalize(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static bool SameFrame(GameScreenshotFrameBinding expected, GameScreenshotFrameBinding current)
        => expected.ObservationId == current.ObservationId && expected.CapturedAtUtc == current.CapturedAtUtc
            && expected.TargetLifetime == current.TargetLifetime && expected.FrameWidth == current.FrameWidth
            && expected.FrameHeight == current.FrameHeight && expected.ImageWidth == current.ImageWidth
            && expected.ImageHeight == current.ImageHeight && expected.ImageScale == current.ImageScale
            && expected.DpiAtCapture == current.DpiAtCapture && expected.IsFullFrame == current.IsFullFrame
            && expected.WindowRect.Left == current.WindowRect.Left && expected.WindowRect.Top == current.WindowRect.Top
            && expected.WindowRect.Right == current.WindowRect.Right && expected.WindowRect.Bottom == current.WindowRect.Bottom
            && (expected.MaskedImageBoxes ?? []).SequenceEqual(current.MaskedImageBoxes ?? []);

    private static string SequenceKey(IReadOnlyList<string> sequence)
        => string.Join("\u001f", sequence.Select(chord => chord.Trim().ToLowerInvariant()));

    private static void AddBinding(Dictionary<string, JevControlOption> bindings, AppActionCatalogEntry entry)
    {
        string description = $"{entry.Label}: {string.Join(" then ", entry.KeySequence)}; evidence={entry.EvidenceStatus}; source={entry.SourceKind}; preconditions={entry.Preconditions ?? "unspecified"}.";
        if (!bindings.TryGetValue(entry.ActionId, out var existing))
            bindings[entry.ActionId] = new JevControlOption(entry.ActionId, description);
        else if (!string.Equals(existing.Description, description, StringComparison.Ordinal))
            bindings[entry.ActionId] = new JevControlOption(entry.ActionId, "Conflicting sourced bindings; do not press automatically.");
    }
}
