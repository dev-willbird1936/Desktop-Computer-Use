// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ShadowUse.Config;

namespace ShadowUse.Tools;

/// <summary>
/// Client-independent discovery gateway over <see cref="ToolCatalog"/>: search the hidden catalogue,
/// load a schema (optionally exposing it through tools/list_changed), and call any tool by name.
/// </summary>
[McpServerToolType]
public sealed class DiscoveryTools(ShadowSettings settings)
{
    [McpServerTool(Name = "search_tools"), Description(
        "Find DCU capabilities beyond the core desktop loop (browser, workflows, subgoals, memory, files, HTTP, grids, voice, " +
        "review rooms, isolated environments, demonstrations, helpers, Jev). Returns ranked names, namespaces, summaries and " +
        "safety hints without schemas. Empty query lists namespaces. Next: describe_tool(name).")]
    public object SearchTools(
        McpServer server,
        [Description("What you need to do, in plain words (e.g. 'fill a web form', 'retry workflow node')")] string query = "",
        [Description("Optional namespace prefix filter, e.g. 'browser' or 'workflow.dag'")] string? @namespace = null,
        [Description("Maximum results")] int limit = 10)
    {
        var tools = AllTools(server);
        ToolCatalog.ApplyAnnotations(tools);
        var exposeAll = ExposeAll;
        var results = ToolCatalog.Search(tools, query, @namespace)
            .OrderByDescending(r => r.Score).ThenBy(r => r.Tool.ProtocolTool.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(r => new
            {
                name = r.Tool.ProtocolTool.Name,
                @namespace = ToolCatalog.Get(r.Tool.ProtocolTool.Name)?.Namespace ?? "other",
                summary = Summary(r.Tool.ProtocolTool.Description),
                read_only = r.Tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false,
                destructive = r.Tool.ProtocolTool.Annotations?.DestructiveHint ?? false,
                visible = ToolCatalog.IsVisible(server, r.Tool.ProtocolTool.Name, exposeAll),
                score = Math.Round(r.Score, 2),
            })
            .ToList();
        object? namespaces = string.IsNullOrWhiteSpace(query) || results.Count == 0
            ? tools.GroupBy(t => ToolCatalog.Get(t.ProtocolTool.Name)?.Namespace ?? "other")
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count())
            : null;
        return new
        {
            results,
            namespaces,
            hint = results.Count == 0
                ? "No match. Broaden the query or pick a namespace from 'namespaces'."
                : "describe_tool(name) returns the schema; call_tool(name, arguments) runs it, or describe_tool(name, load=true) adds it to your tool list.",
        };
    }

    [McpServerTool(Name = "describe_tool"), Description(
        "Full description, input schema, namespace, safety annotations, schema hash, the full-catalogue schema fingerprint and the runtime build/process identity for one DCU tool. " +
        "load=true adds it to this session's tools/list (sends tools/list_changed); load=false removes a loaded tool.")]
    public async Task<object> DescribeTool(
        McpServer server,
        [Description("Exact tool name from search_tools")] string name,
        [Description("true: expose in tools/list; false: stop exposing; omit: no change")] bool? load = null,
        CancellationToken ct = default)
    {
        if (Find(server, name) is not { } tool)
            return new { ok = false, error = "UNKNOWN_TOOL", detail = $"No DCU tool named '{name}'. Use search_tools." };
        ToolCatalog.ApplyAnnotations([tool]);
        var entry = ToolCatalog.Get(name);
        var changed = load is { } l && entry is not { Core: true } && ToolCatalog.SetLoaded(server, name, l);
        if (changed && !ExposeAll)
            await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct).ConfigureAwait(false);
        var p = tool.ProtocolTool;
        return new
        {
            ok = true,
            name,
            @namespace = entry?.Namespace ?? "other",
            description = p.Description,
            input_schema = p.InputSchema,
            annotations = p.Annotations,
            schema_hash = ToolCatalog.SchemaHash(p),
            catalog_schema_fingerprint = ToolCatalog.SchemaFingerprint(AllTools(server)),
            runtime = new
            {
                version = RuntimeIdentification.Current.Version,
                build_timestamp_utc = RuntimeIdentification.Current.BuildTimestampUtc,
                module_version_id = RuntimeIdentification.Current.ModuleVersionId.ToString(),
                process_id = RuntimeIdentification.Current.ProcessId,
            },
            visible = ToolCatalog.IsVisible(server, name, ExposeAll),
            list_changed_sent = changed && !ExposeAll,
        };
    }

    [McpServerTool(Name = "call_tool"), Description(
        "Run any DCU tool by name with its arguments object, including tools not in your tool list. " +
        "Same behavior, validation and result as calling the tool directly. Get the schema from describe_tool first.")]
    public async Task<CallToolResult> CallTool(
        RequestContext<CallToolRequestParams> context,
        [Description("Exact tool name")] string name,
        [Description("Arguments object matching the tool's input_schema")] JsonElement? arguments = null,
        CancellationToken ct = default)
    {
        if (name is "call_tool" || Find(context.Server, name) is not { } tool)
            return Fail(name is "call_tool" ? "call_tool cannot call itself." : $"UNKNOWN_TOOL: No DCU tool named '{name}'. Use search_tools.");
        if (arguments is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
            return Fail("arguments must be a JSON object.");
        var args = arguments is { ValueKind: JsonValueKind.Object } obj
            ? obj.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();
        var inner = new RequestContext<CallToolRequestParams>(context.Server, context.JsonRpcRequest,
            new CallToolRequestParams { Name = name, Arguments = args })
        {
            Services = context.Services,
        };
        return await tool.InvokeAsync(inner, ct).ConfigureAwait(false);
    }

    private bool ExposeAll => settings.ToolExposure == "all";

    private static List<McpServerTool> AllTools(McpServer server) => server.ServerOptions.ToolCollection?.ToList() ?? [];

    private static McpServerTool? Find(McpServer server, string name) =>
        server.ServerOptions.ToolCollection is { } c && c.TryGetPrimitive(name, out var t) ? t : null;

    private static string Summary(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var s = end > 0 ? description[..(end + 1)] : description;
        return s.Length > 200 ? s[..197] + "..." : s;
    }

    private static CallToolResult Fail(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
