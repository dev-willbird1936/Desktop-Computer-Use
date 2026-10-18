// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ShadowUse.Tools;

/// <summary>
/// Machine-readable capability registry. Keeps the full tool catalogue callable while tools/list
/// exposes only the core desktop loop, the discovery tools, and whatever a session has loaded.
/// Namespaces and annotations live here so discovery metadata evolves without touching tool code.
/// </summary>
public static class ToolCatalog
{
    public sealed record Entry(string Namespace, bool ReadOnly = false, bool Destructive = false, bool OpenWorld = false, bool Core = false, bool Game = false);

    // Every [McpServerTool] must appear here; ToolCatalogTests enforces full coverage.
    private static readonly (Entry Defaults, string[] Names)[] Groups =
    [
        (new("desktop.observe", ReadOnly: true, Core: true), ["list_apps", "get_app_state", "wait_for"]),
        (new("desktop.observe", ReadOnly: true), ["wait_for_visual_state"]),
        (new("desktop.input", Core: true), ["click", "type_text", "press_key", "scroll", "drag", "set_value"]),
        (new("desktop.input"), ["click_visual_candidate", "route_click", "route_set_text"]),
        (new("desktop.batch", Core: true), ["execute_sequence"]),
        (new("desktop.overlay", Core: true), ["hide_cursor"]),
        (new("diagnostics", ReadOnly: true, Core: true), ["health_check"]),
        (new("diagnostics", ReadOnly: true), ["get_run_timeline", "get_benchmark_report"]),
        (new("browser.session", OpenWorld: true), ["browser_open", "browser_close"]),
        (new("browser.observe", ReadOnly: true, OpenWorld: true), ["browser_observe", "browser_wait", "browser_diagnostics", "browser_capture_value", "browser_extract"]),
        (new("browser.input", OpenWorld: true), ["browser_click", "browser_point_click", "browser_point_fill", "browser_fill", "browser_dialog", "browser_upload", "browser_download"]),
        (new("browser.webmcp", ReadOnly: true, OpenWorld: true), ["browser_webmcp_discover"]),
        (new("browser.webmcp", OpenWorld: true), ["browser_webmcp_invoke"]),
        (new("browser.memory", ReadOnly: true), ["browser_resolve_cached_action", "browser_recall_task_experience"]),
        (new("browser.memory"), ["browser_remember_action_resolution", "browser_record_task_experience"]),
        (new("browser.jev", ReadOnly: true, OpenWorld: true), ["browser_jev_decide", "browser_jev_inspect"]),
        (new("browser.jev", OpenWorld: true), ["browser_jev_act"]),
        (new("desktop.jev", ReadOnly: true, OpenWorld: true), ["jev_decide", "jev_inspect"]),
        (new("desktop.jev", OpenWorld: true), ["jev_act"]),
        (new("memory.action_cache", ReadOnly: true), ["resolve_cached_action"]),
        (new("memory.action_cache"), ["remember_action_resolution"]),
        (new("memory.experience", ReadOnly: true), ["recall_task_experience"]),
        (new("memory.experience"), ["record_task_experience"]),
        (new("workflow.values"), ["set_workflow_value", "capture_workflow_value", "resolve_field_text"]),
        (new("workflow.dag", ReadOnly: true), ["get_workflow_status"]),
        (new("workflow.dag"), ["create_workflow", "run_workflow", "pause_for_takeover", "resume_workflow", "reconcile_workflow_node"]),
        (new("workflow.subgoal", ReadOnly: true), ["get_subgoal", "check_subgoal_readiness", "check_subgoal_postcondition"]),
        (new("workflow.subgoal"), ["define_subgoal", "complete_subgoal", "escalate_subgoal"]),
        (new("workflow.demonstration", ReadOnly: true), ["get_demonstration_status"]),
        (new("workflow.demonstration"), ["create_demonstration", "admit_demonstration", "replay_demonstration"]),
        (new("data.grid", ReadOnly: true), ["preview_grid_schema"]),
        (new("data.grid"), ["record_grid_cell_edit", "resolve_grid_cell", "keep_grid_batch"]),
        (new("data.grid", Destructive: true), ["undo_grid_batch"]),
        (new("data.transform", ReadOnly: true), ["transform_data"]),
        (new("files", ReadOnly: true), ["read_file", "search_local_files"]),
        (new("files", Destructive: true), ["write_file"]),
        (new("network.http", Destructive: true, OpenWorld: true), ["http_request"]),
        (new("integration.helper", ReadOnly: true), ["get_helper_status"]),
        (new("integration.helper"), ["register_helper", "admit_helper"]),
        (new("integration.helper", Destructive: true, OpenWorld: true), ["invoke_helper"]),
        (new("workspace", ReadOnly: true), ["get_workspace"]),
        (new("workspace"), ["save_workspace", "open_workspace"]),
        (new("model.provider"), ["configure_model_provider", "attach_compatibility_adapter", "record_provider_failure", "select_model_provider"]),
        (new("environment.isolated", ReadOnly: true), ["check_isolated_environment_lease"]),
        (new("environment.isolated"), ["create_isolated_environment", "attach_isolated_environment", "acquire_isolated_environment_lease"]),
        (new("environment.isolated", Destructive: true), ["reset_isolated_environment", "destroy_isolated_environment"]),
        (new("voice.session"), ["open_voice_session", "submit_voice_partial", "submit_voice_final", "check_voice_silence_timeout", "stop_voice_session", "report_voice_transcription_failure"]),
        (new("ui.composition"), ["render_composition", "dispatch_composition_action"]),
        (new("review.room", ReadOnly: true), ["list_annotations"]),
        (new("review.room"), ["join_review_room", "post_annotation", "undo_annotation"]),
        (new("review.execution"), ["acquire_execution_lease", "release_execution_lease", "submit_execution_command"]),
        (new("image.observe", ReadOnly: true), ["ui_candidates_detect_image"]),
        (new("game.observe", ReadOnly: true, Game: true), ["game_screenshot", "game_ocr", "game_bars", "game_wait", "game_input_ownership"]),
        (new("game.jev", ReadOnly: true, OpenWorld: true, Game: true), ["game_jev_decide", "game_jev_decide_candidates", "game_jev_decide_known_state"]),
        (new("game.jev", Destructive: true, OpenWorld: true, Game: true), ["game_jev_act", "game_jev_flow"]),
        (new("game.jev", Game: true), ["game_jev_flow_book"]),
        (new("memory.app_actions"), ["app_action_catalog_import", "screen_transition_report"]),
        (new("memory.app_actions", ReadOnly: true), ["app_action_catalog_get", "screen_state_get"]),
        (new("memory.app_actions", Destructive: true), ["app_action_catalog_invalidate", "screen_state_invalidate"]),
        (new("game.tracking", Game: true), ["game_track", "game_follow"]),
        (new("game.input", Game: true), ["game_click", "game_move", "game_drag", "game_scroll", "game_key", "game_release_keys", "game_release_input_ownership"]),
        (new("discovery", ReadOnly: true, Core: true), ["search_tools", "describe_tool"]),
        (new("discovery", Core: true), ["call_tool"]),
    ];

    public static readonly IReadOnlyDictionary<string, Entry> Entries =
        Groups.SelectMany(g => g.Names.Select(n => (n, g.Defaults))).ToDictionary(p => p.n, p => p.Defaults);

    public static Entry? Get(string name) => Entries.GetValueOrDefault(name);

    /// <summary>Stamps registry annotations onto the protocol tools once at startup.</summary>
    public static void ApplyAnnotations(IEnumerable<McpServerTool> tools)
    {
        foreach (var tool in tools)
        {
            if (Get(tool.ProtocolTool.Name) is not { } e) continue;
            var a = tool.ProtocolTool.Annotations ??= new ToolAnnotations();
            a.ReadOnlyHint = e.ReadOnly;
            a.DestructiveHint = !e.ReadOnly && e.Destructive;
            a.OpenWorldHint = e.OpenWorld;
        }
    }

    public static string SchemaHash(Tool tool) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tool.InputSchema.GetRawText())))[..16].ToLowerInvariant();

    /// <summary>SHA-256 over the full catalogue (every registered tool, listed or not), sorted by name with object keys sorted,
    /// so it is identical across collection order and changes when any name, description, schema or annotation changes.</summary>
    public static string SchemaFingerprint(IEnumerable<McpServerTool> tools)
    {
        var all = tools.ToList();
        ApplyAnnotations(all);
        return SchemaFingerprint(all.Select(t => t.ProtocolTool));
    }

    public static string SchemaFingerprint(IEnumerable<Tool> tools)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("description", tool.Description);
                writer.WritePropertyName("input_schema");
                WriteCanonical(writer, tool.InputSchema);
                if (tool.OutputSchema is { } output)
                {
                    writer.WritePropertyName("output_schema");
                    WriteCanonical(writer, output);
                }
                writer.WriteBoolean("read_only", tool.Annotations?.ReadOnlyHint ?? false);
                writer.WriteBoolean("destructive", tool.Annotations?.DestructiveHint ?? false);
                writer.WriteBoolean("open_world", tool.Annotations?.OpenWorldHint ?? false);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    // Tools a session has loaded through describe_tool(load: true), keyed by MCP session id so HTTP
    // clients never see each other's working set. Request-scoped McpServer wrappers differ per call,
    // so the id (null for stdio, which has one session) is the stable key.
    private static readonly ConcurrentDictionary<string, HashSet<string>> Loaded = new();

    private static string Key(McpServer server) => server.SessionId ?? "";

    /// <summary>Set once at startup from the GameMode setting; game_* tools are listed only while it is true.</summary>
    public static bool GameModeEnabled { get; set; }

    public static bool IsVisible(McpServer server, string name, bool exposeAll) =>
        exposeAll || Get(name) is { Core: true } || (GameModeEnabled && Get(name) is { Game: true }) || (Loaded.TryGetValue(Key(server), out var set) && Contains(set, name));

    /// <summary>Returns true when the session's visible set actually changed.</summary>
    public static bool SetLoaded(McpServer server, string name, bool loaded)
    {
        var set = Loaded.GetOrAdd(Key(server), _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set) return loaded ? set.Add(name) : set.Remove(name);
    }

    private static bool Contains(HashSet<string> set, string name) { lock (set) return set.Contains(name); }

    /// <summary>Lexical ranking over name, namespace and description. Deliberately simple and
    /// replaceable: callers depend only on the ranked name list, not on the scoring method.</summary>
    public static IEnumerable<(McpServerTool Tool, double Score)> Search(IEnumerable<McpServerTool> tools, string query, string? ns)
    {
        var terms = Tokenize(query).Distinct().ToArray();
        foreach (var tool in tools)
        {
            var name = tool.ProtocolTool.Name;
            var entryNs = Get(name)?.Namespace ?? "other";
            if (!string.IsNullOrEmpty(ns) && !entryNs.StartsWith(ns, StringComparison.OrdinalIgnoreCase)) continue;
            var nameTokens = Tokenize(name).ToHashSet();
            var nsTokens = Tokenize(entryNs).ToHashSet();
            var descTokens = Tokenize(tool.ProtocolTool.Description ?? "").ToHashSet();
            double score = terms.Length == 0 ? 1 : 0;
            foreach (var t in terms)
            {
                if (nameTokens.Contains(t)) score += 3;
                else if (nameTokens.Any(n => n.StartsWith(t, StringComparison.Ordinal))) score += 2;
                if (nsTokens.Contains(t)) score += 2;
                if (descTokens.Contains(t)) score += 1;
                else if (t.Length >= 4 && descTokens.Any(d => d.StartsWith(t, StringComparison.Ordinal))) score += 0.5;
            }
            if (score > 0) yield return (tool, score);
        }
    }

    private static IEnumerable<string> Tokenize(string text) =>
        text.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1);

    private static readonly char[] Separators = " \t\r\n_.,;:()[]{}\"'`/\\-|=<>!?*+".ToCharArray();
}
