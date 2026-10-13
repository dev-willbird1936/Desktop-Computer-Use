// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using ModelContextProtocol.Server;
using ShadowUse.Automation;

namespace ShadowUse.Tools;

/// <summary>Local app action and screen-map memory. Imports and observations persist only to the configured user-local JSON stores.</summary>
[McpServerToolType]
public sealed class ScreenActionMemoryTools(AppActionCatalog catalog, ScreenStateStore screens)
{
    [McpServerTool(Name = "app_action_catalog_import"), Description(
        "Import a bounded, app-agnostic action/keybinding catalog researched from explicitly permitted app config, official documentation, background research, UIA accelerators, or user-confirmed sources. It stores source metadata in the configured user-local catalog; it does not read files, contact the web, press keys, or dispatch input. Unverified entries are kept as leads but are not offered as key choices. Use relative non-secret local_config source_reference values; never include credential values or file excerpts.")]
    public object ImportAppActionCatalog(
        [Description("JSON with app_id, app_version, profile, keyboard_layout, and actions[{action_id,label,key_sequence[],screen_id,target_aliases[],source_kind,source_reference,source_hash?,evidence_status,preconditions?}]")] string catalog_json)
    {
        var result = catalog.Import(catalog_json);
        if (!result.Imported) return new { ok = false, error = result.Error, no_input_sent = true };
        return new
        {
            ok = true,
            stored_in_process_memory = true,
            persisted = result.Persisted,
            persistence_error = result.Error,
            action_count = result.ActionCount,
            binding_conflict_count = result.ConflictCount,
            revision = result.Revision,
            evidence_is_caller_reported = true,
            no_input_sent = true,
        };
    }

    [McpServerTool(Name = "app_action_catalog_get"), Description(
        "Read an imported app action catalog for an exact app identity, profile, and keyboard layout. Version mismatches are returned as advisory; this tool never resolves conflicts by guessing and never dispatches input.")]
    public object GetAppActionCatalog(
        [Description("Stable app identity supplied by the caller or research process")] string app_id,
        [Description("Current app version, or unknown")] string app_version = "unknown",
        [Description("Active user/application profile")] string profile = "default",
        [Description("Current keyboard layout, or unknown")] string keyboard_layout = "unknown")
    {
        var lookup = catalog.Lookup(app_id, app_version, profile, keyboard_layout);
        if (!lookup.Found || lookup.Snapshot is null)
            return new { found = false, reason = lookup.Reason, no_input_sent = true };
        var snapshot = lookup.Snapshot;
        return new
        {
            found = true,
            app_id = snapshot.AppId,
            app_version = snapshot.AppVersion,
            profile = snapshot.Profile,
            keyboard_layout = snapshot.KeyboardLayout,
            revision = snapshot.Revision,
            advisory = lookup.Advisory,
            advisory_reason = lookup.Reason,
            binding_conflicts = AppActionCatalog.GetAllConflicts(snapshot.Actions),
            actions = snapshot.Actions,
            persistence_error = catalog.PersistenceError,
            no_input_sent = true,
        };
    }

    [McpServerTool(Name = "screen_state_get"), Description(
        "Read the persisted semantic screen map and observed action transitions for an app identity. Stable normalized anchors are reusable only after the current full screenshot matches every stored region hash and layout fingerprint; dynamic actions stay fresh-frame-only.")]
    public object GetScreenStateMap(
        [Description("Stable app identity used when storing screen observations")] string app_id,
        [Description("Optional screen_id to inspect its transitions; omit for recent screen summaries")] string? screen_id = null)
    {
        var recent = screens.ScreensFor(app_id);
        return new
        {
            app_id,
            screens = recent.Select(state => new
            {
                state.ScreenId,
                state.ScreenHint,
                state.Signature,
                state.AppVersion,
                state.Profile,
                state.KeyboardLayout,
                state.Scope,
                state.LayoutFingerprint,
                state.CandidateSetComplete,
                state.CandidateSetIssue,
                state.CandidateCount,
                state.ConsecutiveObservationCount,
                stable = state.ConsecutiveObservationCount >= 2,
                state.Actions,
                state.ContextRegions,
                static_anchor_count = state.Actions.Count(action => action.Anchor is { IsStatic: true }),
                state.FirstSeenUtc,
                state.LastSeenUtc,
                state.ObservationCount,
                transitions = screen_id is not null && screen_id == state.ScreenId
                    && state.ConsecutiveObservationCount >= 2
                    ? screens.TransitionsFor(app_id, state.ScreenId)
                    : [],
            }).ToArray(),
            recent_actions = screens.RecentActions(app_id),
            cached_anchor_count = recent.Sum(state => state.Actions.Count(action => action.Anchor is { IsStatic: true })),
            coordinates_reuse_requires_fresh_frame_hash = true,
            persistence_error = screens.PersistenceError,
            no_input_sent = true,
        };
    }

    [McpServerTool(Name = "screen_transition_report"), Description(
        "Attach a caller-reported success/failure outcome and confidence to an already observed app/action/screen transition. This updates local history only and does not send input.")]
    public object ReportScreenTransition(
        [Description("Stable app identity used in screen_state_get") ] string app_id,
        [Description("Previous screen id") ] string from_screen_id,
        [Description("Action id reported as sent") ] string action_id,
        [Description("Observed destination screen id") ] string to_screen_id,
        [Description("Whether the action achieved its requested transition") ] bool success,
        [Description("Caller confidence from 0 to 1") ] double confidence,
        [Description("Short evidence summary; no screenshot or raw OCR is stored") ] string? evidence = null)
    {
        bool recorded = screens.ReportTransitionOutcome(app_id, from_screen_id, action_id, to_screen_id,
            success, confidence, evidence);
        return new { recorded, outcome = success ? "success" : "failure", confidence, no_input_sent = true };
    }

    [McpServerTool(Name = "screen_state_invalidate"), Description(
        "Forget the persisted screen map, anchors, transitions, and recent actions for one app identity. This only changes local DCU knowledge; it does not interact with the app.")]
    public object InvalidateScreenState(
        [Description("Stable app identity used in screen_state_get") ] string app_id)
    {
        bool invalidated = screens.Invalidate(app_id);
        return new { invalidated, app_id, no_input_sent = true };
    }

    [McpServerTool(Name = "app_action_catalog_invalidate"), Description(
        "Remove a persisted action catalog for one app/profile/keyboard-layout tuple. This changes local DCU knowledge only; it does not send input.")]
    public object InvalidateAppActionCatalog(
        [Description("Stable app identity used in app_action_catalog_get") ] string app_id,
        [Description("User/application profile") ] string profile = "default",
        [Description("Keyboard layout") ] string keyboard_layout = "unknown")
    {
        bool invalidated = catalog.Invalidate(app_id, profile, keyboard_layout);
        return new { invalidated, app_id, profile, keyboard_layout, no_input_sent = true };
    }
}
