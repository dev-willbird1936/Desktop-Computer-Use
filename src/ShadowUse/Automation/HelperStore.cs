// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShadowUse.Automation;

/// <summary>F18: a registered reusable helper (a create_workflow-shaped node template with
/// {{param}} placeholders) plus its admission state. A helper is never admitted by registering
/// it -- admit_helper is a separate, caller-asserted step (same "never infer, always assert"
/// convention as ExperienceStore's outcome field), and admitted_hash is pinned to the exact
/// source that was reviewed. Any later Register call for the same id recomputes a fresh
/// source_hash and always resets admission, so an edited helper cannot silently keep running on
/// its old approval (F18-T01).</summary>
public sealed class HelperRecord
{
    public required string HelperId { get; init; }
    public required string NodesJson { get; init; }
    public required string ParameterSchemaJson { get; init; }
    public required IReadOnlyList<string> DeclaredApps { get; init; }
    public required string DependencyLock { get; init; }
    public required string SourceHash { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
    public bool Admitted { get; set; }
    public string? AdmittedHash { get; set; }
    public string? TestEvidence { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
}

/// <summary>F18-R01/R02: a process-lifetime workspace of generated/reusable helpers, separate
/// from DCU's own built-in tools. A helper is only ever executed by expanding it into ordinary
/// WorkflowNodeSpec entries dispatched through the SAME WorkflowRunner every create_workflow call
/// uses -- there is no separate "run arbitrary helper code" path, and no code-generation/eval of
/// any kind happens here.</summary>
public sealed class HelperStore
{
    private readonly ConcurrentDictionary<string, HelperRecord> _helpers = new();

    public HelperRecord Register(string helperId, string nodesJson, string parameterSchemaJson, IReadOnlyList<string> declaredApps, string dependencyLock)
    {
        if (string.IsNullOrWhiteSpace(helperId))
            throw new ArgumentException("helper_id is required.");
        if (string.IsNullOrWhiteSpace(nodesJson))
            throw new ArgumentException("nodes_json is required.");
        // Reject up front rather than deferring to invoke_helper: a helper workspace entry
        // that cannot even parse as JSON is not a reviewable artifact.
        using (JsonDocument.Parse(nodesJson)) { }
        var apps = declaredApps.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var record = new HelperRecord
        {
            HelperId = helperId,
            NodesJson = nodesJson,
            ParameterSchemaJson = parameterSchemaJson,
            DeclaredApps = apps,
            DependencyLock = dependencyLock ?? "",
            SourceHash = ComputeHash(nodesJson, parameterSchemaJson, apps, dependencyLock ?? ""),
            RegisteredAt = DateTimeOffset.UtcNow,
        };
        // F18-T01: registering (including re-registering an existing id with edited content)
        // always lands un-admitted. There is no path where an edit inherits a prior approval.
        _helpers[helperId] = record;
        return record;
    }

    public HelperRecord Admit(string helperId, string testEvidence)
    {
        if (string.IsNullOrWhiteSpace(testEvidence))
            throw new ArgumentException("admit_helper requires non-empty test_evidence describing what was actually verified.");
        var record = _helpers.GetValueOrDefault(helperId) ?? throw new ArgumentException($"Helper '{helperId}' was not registered.");
        record.Admitted = true;
        record.AdmittedHash = record.SourceHash;
        record.TestEvidence = testEvidence;
        record.AdmittedAt = DateTimeOffset.UtcNow;
        return record;
    }

    public HelperRecord? Find(string helperId) => _helpers.GetValueOrDefault(helperId);

    /// <summary>F18-T01: the only gate before a helper may be expanded into dispatchable nodes.
    /// Admitted is not enough on its own -- admitted_hash must still equal the CURRENT
    /// source_hash, so an edit that happened to leave `Admitted` true (it never does, Register
    /// always clears it, but this is checked independently rather than trusted) can never
    /// execute on a stale approval either.</summary>
    public HelperRecord RequireAdmitted(string helperId)
    {
        var record = _helpers.GetValueOrDefault(helperId) ?? throw new ArgumentException($"Helper '{helperId}' was not registered.");
        if (!record.Admitted || record.AdmittedHash != record.SourceHash)
            throw new ArgumentException($"Helper '{helperId}' is not admitted for its current source. An edit since the last admit_helper call invalidates the prior approval -- call admit_helper again after review.");
        return record;
    }

    public static string ComputeHash(string nodesJson, string parameterSchemaJson, IReadOnlyList<string> declaredApps, string dependencyLock)
    {
        var canonical = string.Join('\u0001',
            nodesJson,
            parameterSchemaJson ?? "",
            string.Join(',', declaredApps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)),
            dependencyLock ?? "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Recursively replaces every "{{name}}" token inside string values of the parsed
    /// nodes_json with the caller-supplied parameter value, leaving non-string JSON untouched.
    /// Runs BEFORE the substituted JSON is handed to the normal node/loop parser, so a helper's
    /// template may itself contain a bounded_loop node with no special-casing here.</summary>
    public static string SubstituteParams(string nodesJson, IReadOnlyDictionary<string, string> parameters)
    {
        using var doc = JsonDocument.Parse(nodesJson);
        return SubstituteNode(doc.RootElement, parameters)?.ToJsonString() ?? "null";
    }

    private static JsonNode? SubstituteNode(JsonElement element, IReadOnlyDictionary<string, string> parameters) => element.ValueKind switch
    {
        JsonValueKind.Object => BuildObject(element, parameters),
        JsonValueKind.Array => BuildArray(element, parameters),
        JsonValueKind.String => JsonValue.Create(Substitute(element.GetString() ?? "", parameters)),
        _ => JsonNode.Parse(element.GetRawText()),
    };

    private static JsonObject BuildObject(JsonElement element, IReadOnlyDictionary<string, string> parameters)
    {
        var obj = new JsonObject();
        foreach (var prop in element.EnumerateObject())
            obj[prop.Name] = SubstituteNode(prop.Value, parameters);
        return obj;
    }

    private static JsonArray BuildArray(JsonElement element, IReadOnlyDictionary<string, string> parameters)
    {
        var arr = new JsonArray();
        foreach (var el in element.EnumerateArray())
            arr.Add(SubstituteNode(el, parameters));
        return arr;
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var (name, value) in parameters)
            text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
        return text;
    }
}
