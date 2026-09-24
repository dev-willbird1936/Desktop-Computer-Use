// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Globalization;

namespace ShadowUse.Automation;

/// <summary>F59-R01: a versioned catalogue entry -- typed props and the exact tool names this
/// component type is allowed to bind an action to. Action bindings are validated against BOTH this
/// per-component allowlist AND DCU's own global dispatchable-tool allowlist (the same
/// KnownWorkflowTools set F18's helpers use) -- never just one or the other.</summary>
public sealed record ComponentDefinition(string ComponentId, IReadOnlyDictionary<string, string> PropTypes, IReadOnlyList<string> AllowedActionBindings);

public static class ComponentCatalogue
{
    public const string Version = "v1";

    public static readonly IReadOnlyDictionary<string, ComponentDefinition> Components = new Dictionary<string, ComponentDefinition>(StringComparer.Ordinal)
    {
        ["button"] = new("button", new Dictionary<string, string> { ["label"] = "string" }, ["click", "browser_click"]),
        ["text_field"] = new("text_field", new Dictionary<string, string> { ["label"] = "string", ["placeholder"] = "string" }, ["set_value", "browser_fill"]),
        ["text"] = new("text", new Dictionary<string, string> { ["content"] = "string" }, []),
    };
}

/// <summary>F59-T02: an action binding is a typed reference to an existing dispatchable tool
/// (app/tool/args, the same shape as WorkflowNodeSpec), never inline code.</summary>
public sealed record CompositionActionBinding(string App, string Tool, string ArgsJson);

public sealed record CompositionNodeSpec(string NodeId, string ComponentId, IReadOnlyDictionary<string, string> Props, CompositionActionBinding? ActionBinding);

public sealed class CompositionRecord
{
    public required string CompositionId { get; init; }
    public required string CatalogueVersion { get; init; }
    public required IReadOnlyList<CompositionNodeSpec> Nodes { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>F59-T01: "an unknown component, executable string, or forbidden action binding fails
/// validation before rendering". Every node is checked; a single violation anywhere refuses the
/// WHOLE composition (nothing is partially stored/rendered).</summary>
public static class CompositionValidator
{
    // A heuristic denylist, not a sanitizer -- props are already typed/schema-constrained (F59-T01's
    // structural half), this is defense-in-depth against an obviously code-shaped string value.
    private static readonly string[] SuspiciousPropPatterns =
        ["<script", "javascript:", "eval(", "${", "`", "onerror=", "onclick=", "onload="];

    public static string? ValidateNode(CompositionNodeSpec node, IReadOnlySet<string> knownDispatchTools)
    {
        if (string.IsNullOrWhiteSpace(node.NodeId))
            return "Every composition node needs a non-empty id.";
        if (!ComponentCatalogue.Components.TryGetValue(node.ComponentId, out var definition))
            return $"Node '{node.NodeId}' references unknown component '{node.ComponentId}'.";
        foreach (var (propName, propValue) in node.Props)
        {
            if (!definition.PropTypes.TryGetValue(propName, out var expectedType))
                return $"Node '{node.NodeId}' sets undeclared prop '{propName}' for component '{node.ComponentId}'.";
            if (!MatchesType(propValue, expectedType))
                return $"Node '{node.NodeId}' prop '{propName}' does not match declared type '{expectedType}'.";
            foreach (var pattern in SuspiciousPropPatterns)
                if (propValue.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return $"Node '{node.NodeId}' prop '{propName}' contains a disallowed executable-looking pattern ('{pattern}').";
        }
        if (node.ActionBinding is { } binding)
        {
            if (!knownDispatchTools.Contains(binding.Tool))
                return $"Node '{node.NodeId}' action binding uses tool '{binding.Tool}', which is not one of DCU's own dispatchable workflow tools.";
            if (!definition.AllowedActionBindings.Contains(binding.Tool))
                return $"Node '{node.NodeId}' component '{node.ComponentId}' does not allow an action binding to tool '{binding.Tool}'.";
        }
        return null;
    }

    private static bool MatchesType(string value, string type) => type switch
    {
        "string" => true,
        "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        "bool" => bool.TryParse(value, out _),
        _ => false,
    };
}

/// <summary>F59-R01: a process-lifetime store of validated compositions.</summary>
public sealed class CompositionStore
{
    private readonly ConcurrentDictionary<string, CompositionRecord> _compositions = new();

    public CompositionRecord Save(string compositionId, IReadOnlyList<CompositionNodeSpec> nodes)
    {
        if (string.IsNullOrWhiteSpace(compositionId))
            throw new ArgumentException("composition_id is required.");
        if (nodes.Count == 0)
            throw new ArgumentException("A composition needs at least one node.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
            if (!seen.Add(node.NodeId))
                throw new ArgumentException($"Duplicate node id '{node.NodeId}'.");
        var record = new CompositionRecord
        {
            CompositionId = compositionId, CatalogueVersion = ComponentCatalogue.Version, Nodes = nodes, CreatedAt = DateTimeOffset.UtcNow,
        };
        _compositions[compositionId] = record;
        return record;
    }

    public CompositionRecord? Find(string compositionId) => _compositions.GetValueOrDefault(compositionId);
}
