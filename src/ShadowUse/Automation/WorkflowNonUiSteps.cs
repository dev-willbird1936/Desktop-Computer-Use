// Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
// Licensed under MIT. See LICENSE. Keep this notice when redistributing.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShadowUse.Automation;

/// <summary>F32-R01/R02: typed non-UI workflow steps (file read/write, bounded HTTP, deterministic
/// data transform) so a DAG workflow does not have to fake every non-UI operation through fake UI
/// clicks. Every operation is deterministic and bounded by an explicit caller-supplied root/allowlist
/// -- there is no generated-code admission path here, matching F32-R02's "keep transformation code
/// deterministic by default; separate generated-code admission from ordinary data processing."</summary>
public static class WorkflowFilePaths
{
    /// <summary>Resolves a file path that must stay strictly inside an approved absolute root
    /// (F32-T02: path traversal must be blocked before the prohibited operation runs). Mirrors the
    /// same containment shape already proven by BrowserDownloadPaths/BrowserUploadPaths, kept as a
    /// separate small helper here so file-workflow-step validation does not depend on the browser
    /// project's exception type.</summary>
    public static string Resolve(string root, string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
            throw new ArgumentException("A workflow file step requires an absolute allowed_root.");
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A workflow file step requires a non-empty path.");
        var rootFull = Path.GetFullPath(root);
        var full = Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path escapes the approved allowed_root.");
        if (mustExist && !File.Exists(full))
            throw new ArgumentException("The file was not found inside the approved allowed_root.");
        return full;
    }
}

public static class WorkflowHttpSteps
{
    private const long MaxResponseBytes = 2 * 1024 * 1024; // bounded read, never an unbounded body
    private const int MaxRedirects = 5;

    /// <summary>F32-T02: an unapproved redirect is blocked before the prohibited operation --
    /// disables HttpClient's automatic redirect handling and manually validates EVERY hop's host
    /// against allowedHosts (not just the initial URL), following at most MaxRedirects hops.</summary>
    public static async Task<(int StatusCode, string Body, string FinalUrl)> ExecuteAsync(
        HttpClient client, string url, IReadOnlyCollection<string> allowedHosts, string method, string? body, int timeoutMs, CancellationToken ct)
    {
        if (allowedHosts.Count == 0)
            throw new ArgumentException("http_request requires at least one allowed_hosts entry.");
        var current = new Uri(url, UriKind.Absolute);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        for (var hop = 0; ; hop++)
        {
            if (!allowedHosts.Contains(current.Host, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Host '{current.Host}' is not in allowed_hosts.");
            if (hop > MaxRedirects)
                throw new ArgumentException("Too many redirects for a bounded http_request.");
            using var request = new HttpRequestMessage(new HttpMethod(method), current);
            if (body != null && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                request.Content = new StringContent(body);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
                or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new ArgumentException("Redirect response had no Location header.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            long total = 0;
            while ((read = await stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxResponseBytes)
                    throw new ArgumentException("http_request response exceeded the bounded size limit.");
                bounded.Write(buffer, 0, read);
            }
            return ((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(bounded.ToArray()), current.ToString());
        }
    }
}

public static class WorkflowDataTransform
{
    /// <summary>Deterministic, non-generated transforms only (F32-R02). "csv_cell:row,col" indexes
    /// a 0-based row/column out of simple comma-separated text (no quoting/escaping support -- a
    /// real CSV parser is out of scope for this slice, documented honestly).</summary>
    public static string Apply(string op, string input)
    {
        if (op == "trim") return input.Trim();
        if (op == "uppercase") return input.ToUpperInvariant();
        if (op == "lowercase") return input.ToLowerInvariant();
        if (op.StartsWith("csv_cell:", StringComparison.Ordinal))
        {
            var parts = op["csv_cell:".Length..].Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var row) || !int.TryParse(parts[1], out var col) || row < 0 || col < 0)
                throw new ArgumentException("csv_cell op requires 'csv_cell:row,col' with non-negative integers.");
            var lines = input.Replace("\r\n", "\n").Split('\n');
            if (row >= lines.Length)
                throw new ArgumentException($"csv_cell row {row} is out of range ({lines.Length} rows).");
            var cells = lines[row].Split(',');
            if (col >= cells.Length)
                throw new ArgumentException($"csv_cell col {col} is out of range ({cells.Length} columns).");
            return cells[col].Trim();
        }
        throw new ArgumentException($"Unknown transform_data op '{op}'. Supported: trim, uppercase, lowercase, csv_cell:row,col.");
    }
}

// F32-R01's fifth node kind: a bounded-loop node, expanded into ordinary WorkflowNodeSpec entries
// at CREATE time (before create_workflow ever calls WorkflowRunner.Create), not given real runtime
// loop machinery. An unbounded/oversized loop is structurally impossible to construct at all
// (F32-T02), not merely rejected after the fact. Every generated node runs through the exact same
// dispatch path as any other node -- no fifth execution mechanism, only a preprocessing expansion.
public static class WorkflowLoopExpansion
{
    public const int MaxIterations = 50;

    // Expands one bounded-loop node into 0..count ordinary WorkflowNodeSpec entries (id
    // "{loopId}#{index}"), substituting the literal tokens "{{loop_index}}" (always) and
    // "{{loop_item}}" (items-based loops only) inside every string value of the template args,
    // recursively. Every generated node shares the loop node's own prerequisites (parallel
    // siblings by default); a caller needing sequential iterations can chain
    // "{loopId}#{index-1}" as an explicit prerequisite of a later node instead.
    public static IReadOnlyList<WorkflowNodeSpec> Expand(string loopId, JsonElement loopNode)
    {
        if (!loopNode.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Loop '{loopId}' needs a 'template' object with tool/app/args.");
        var toolName = template.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException($"Loop '{loopId}' template needs a tool.");
        var appName = template.TryGetProperty("app", out var appEl) ? appEl.GetString() ?? "" : "";
        var hasTemplateArgs = template.TryGetProperty("args", out var templateArgs);
        var prereqs = loopNode.TryGetProperty("prerequisites", out var prereqEl) && prereqEl.ValueKind == JsonValueKind.Array
            ? prereqEl.EnumerateArray().Select(p => p.GetString() ?? "").ToArray()
            : [];

        var hasItems = loopNode.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array;
        var hasCount = loopNode.TryGetProperty("count", out var countEl);
        if (hasItems == hasCount)
            throw new ArgumentException($"Loop '{loopId}' needs exactly one of 'items' (array of strings) or 'count' (integer).");

        string[]? items = null;
        int count;
        if (hasItems)
        {
            items = itemsEl.EnumerateArray().Select(e => e.GetString() ?? throw new ArgumentException($"Loop '{loopId}' items must all be strings.")).ToArray();
            count = items.Length;
        }
        else
        {
            count = countEl.GetInt32();
            if (count < 0)
                throw new ArgumentException($"Loop '{loopId}' count must be non-negative.");
        }

        // F32-T02: the hard cap is checked BEFORE a single node is created -- an unbounded or
        // oversized loop cannot be constructed at all, not merely refused once discovered.
        if (count > MaxIterations)
            throw new ArgumentException($"Loop '{loopId}' requests {count} iterations, exceeding the hard cap of {MaxIterations}. An unbounded/oversized loop is refused before any node is created.");

        var expanded = new List<WorkflowNodeSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var argsJson = hasTemplateArgs ? SubstituteTokens(templateArgs, i, items?[i]) : "{}";
            expanded.Add(new WorkflowNodeSpec($"{loopId}#{i}", appName, toolName!, argsJson, prereqs));
        }
        return expanded;
    }

    private static string SubstituteTokens(JsonElement element, int index, string? item) => SubstituteNode(element, index, item)?.ToJsonString() ?? "null";

    private static JsonNode? SubstituteNode(JsonElement element, int index, string? item) => element.ValueKind switch
    {
        JsonValueKind.Object => BuildObject(element, index, item),
        JsonValueKind.Array => BuildArray(element, index, item),
        JsonValueKind.String => JsonValue.Create(Substitute(element.GetString() ?? "", index, item)),
        _ => JsonNode.Parse(element.GetRawText()),
    };

    private static JsonObject BuildObject(JsonElement element, int index, string? item)
    {
        var obj = new JsonObject();
        foreach (var prop in element.EnumerateObject())
            obj[prop.Name] = SubstituteNode(prop.Value, index, item);
        return obj;
    }

    private static JsonArray BuildArray(JsonElement element, int index, string? item)
    {
        var arr = new JsonArray();
        foreach (var el in element.EnumerateArray())
            arr.Add(SubstituteNode(el, index, item));
        return arr;
    }

    private static string Substitute(string text, int index, string? item)
    {
        text = text.Replace("{{loop_index}}", index.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (item is not null)
            text = text.Replace("{{loop_item}}", item, StringComparison.Ordinal);
        return text;
    }
}
