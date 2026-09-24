using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShadowUse.Browser;

public sealed record BrowserWebMcpToolDescriptor(string? Name, string? Description, string? Origin, JsonNode? InputSchema);

public sealed record BrowserWebMcpPageSnapshot(bool Supported, string? Origin, IReadOnlyList<BrowserWebMcpToolDescriptor> Tools, string? Error = null);

public sealed record BrowserWebMcpCandidate(
    string CandidateId,
    string Name,
    string Description,
    string Origin,
    JsonNode? InputSchema,
    bool Valid,
    string? ValidationError,
    string PageId,
    long DocumentEpoch);

public sealed record BrowserWebMcpCatalog(bool Supported, string? Origin, IReadOnlyList<BrowserWebMcpCandidate> Tools, string? Error = null);

public enum BrowserWebMcpInvokeStatus
{
    Executed,
    RegistrationChanged,
    Unavailable,
    Failed,
}

public sealed record BrowserWebMcpInvokeResult(BrowserWebMcpInvokeStatus Status, JsonNode? Result, string? Error = null);

public sealed record BrowserRoutedAction(string Route, string Reason, JsonNode? Result, BrowserObservation Observation);

public static class BrowserWebMcp
{
    private static readonly HashSet<string> SchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "title", "description", "type", "properties", "required", "additionalProperties",
        "enum", "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "items",
    };

    private static readonly JsonSerializerOptions StableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? GetSecureOrigin(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeHttp && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address)))
            return uri.GetLeftPart(UriPartial.Authority);
        return null;
    }

    public static string CandidateId(BrowserWebMcpToolDescriptor tool, string pageId, long documentEpoch)
    {
        var material = string.Join("\n", pageId, documentEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), tool.Origin ?? "", tool.Name ?? "", tool.Description ?? "", Canonical(tool.InputSchema));
        return "webmcp-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..24];
    }

    public static string? ValidateSchema(JsonNode? schema)
    {
        if (schema is not JsonObject root)
            return "The WebMCP input schema must be an object schema.";
        if (Encoding.UTF8.GetByteCount(Canonical(schema)) > 32 * 1024 || CountNodes(schema, 0) > 1024)
            return "The WebMCP input schema exceeds the supported size or complexity.";
        var error = ValidateSchemaNode(root, 0);
        if (error is not null)
            return error;
        return TryGetString(root["type"], out var rootType) && rootType == "object"
            ? null
            : "The WebMCP input schema must declare type object.";
    }

    public static string? ValidateArguments(JsonNode schema, JsonObject arguments)
    {
        if (Encoding.UTF8.GetByteCount(arguments.ToJsonString()) > 32 * 1024 || CountNodes(arguments, 0) > 2048)
            return "WebMCP arguments exceed the supported size or complexity.";
        return ValidateValue(schema, arguments, "$", 0);
    }

    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => JsonSerializer.Serialize(item.Key) + ":" + Canonical(item.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Canonical)) + "]",
        _ => node.ToJsonString(StableJson),
    };

    private static string? ValidateSchemaNode(JsonNode schema, int depth)
    {
        if (depth > 16 || schema is not JsonObject obj)
            return "The WebMCP schema uses an unsupported nested schema.";
        foreach (var key in obj.Select(pair => pair.Key))
            if (!SchemaKeywords.Contains(key))
                return $"The WebMCP schema keyword '{key}' is unsupported.";

        if (obj["$schema"] is not null && (!TryGetString(obj["$schema"], out var schemaUri) || !Uri.TryCreate(schemaUri, UriKind.Absolute, out _)))
            return "The WebMCP schema has an invalid $schema value.";
        if (obj["title"] is not null && !TryGetString(obj["title"], out _)
            || obj["description"] is not null && !TryGetString(obj["description"], out _))
            return "The WebMCP schema has invalid title or description metadata.";
        if (!TryGetString(obj["type"], out var type)
            || type is not ("object" or "array" or "string" or "number" or "integer" or "boolean" or "null"))
            return "The WebMCP schema has a missing or unsupported type.";
        if (obj["properties"] is JsonNode propertiesNode)
        {
            if (type != "object" || propertiesNode is not JsonObject properties || properties.Count > 128)
                return "The WebMCP schema has invalid object properties.";
            foreach (var (name, propertySchema) in properties)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || propertySchema is null)
                    return "The WebMCP schema has an invalid property.";
                var error = ValidateSchemaNode(propertySchema, depth + 1);
                if (error is not null)
                    return error;
            }
        }
        if (obj["required"] is JsonNode requiredNode)
        {
            if (type != "object" || requiredNode is not JsonArray required || required.Count > 128
                || required.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
                return "The WebMCP schema has an invalid required list.";
            var names = required.Select(item => item!.GetValue<string>()).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                return "The WebMCP schema has duplicate required names.";
            if (obj["properties"] is not JsonObject declared || names.Any(name => !declared.ContainsKey(name)))
                return "The WebMCP schema requires a property that is not declared.";
        }
        if (obj["additionalProperties"] is JsonNode additional
            && (additional is not JsonValue additionalValue || !additionalValue.TryGetValue<bool>(out _)))
            return "The WebMCP schema uses an unsupported additionalProperties value.";
        if (obj["enum"] is JsonNode enumNode && (enumNode is not JsonArray enumValues || enumValues.Count == 0 || enumValues.Count > 128))
            return "The WebMCP schema has an invalid enum.";
        if (obj["minimum"] is JsonNode minimumNode && !TryNumber(minimumNode, out _))
            return "The WebMCP schema has an invalid minimum bound.";
        if (obj["maximum"] is JsonNode maximumNode && !TryNumber(maximumNode, out _))
            return "The WebMCP schema has an invalid maximum bound.";
        if (obj["minimum"] is JsonNode minimumValue && obj["maximum"] is JsonNode maximumValue
            && TryNumber(minimumValue, out var minValue) && TryNumber(maximumValue, out var maxValue) && minValue > maxValue)
            return "The WebMCP schema minimum exceeds its maximum.";
        if (obj["minLength"] is JsonNode minLength && !TryInteger(minLength, out _)
            || obj["maxLength"] is JsonNode maxLength && !TryInteger(maxLength, out _)
            || obj["minItems"] is JsonNode minItems && !TryInteger(minItems, out _)
            || obj["maxItems"] is JsonNode maxItems && !TryInteger(maxItems, out _))
            return "The WebMCP schema has an invalid size bound.";
        if (obj["items"] is JsonNode items)
        {
            if (type != "array")
                return "Only array schemas may declare items.";
            var error = ValidateSchemaNode(items, depth + 1);
            if (error is not null)
                return error;
        }
        foreach (var key in new[] { "minimum", "maximum" })
            if (obj[key] is JsonNode bound && (type is not ("number" or "integer") || !TryNumber(bound, out _)))
                return $"The WebMCP schema has an invalid {key} bound.";
        foreach (var key in new[] { "minLength", "maxLength", "minItems", "maxItems" })
            if (obj[key] is JsonNode count && (!TryInteger(count, out var number) || number < 0
                || key.EndsWith("Length", StringComparison.Ordinal) && type != "string"
                || key.EndsWith("Items", StringComparison.Ordinal) && type != "array"))
                return $"The WebMCP schema has an invalid {key} bound.";
        if (obj["minLength"] is JsonNode minLengthValue && obj["maxLength"] is JsonNode maxLengthValue
            && TryInteger(minLengthValue, out var minLengthNumber) && TryInteger(maxLengthValue, out var maxLengthNumber) && minLengthNumber > maxLengthNumber
            || obj["minItems"] is JsonNode minItemsValue && obj["maxItems"] is JsonNode maxItemsValue
            && TryInteger(minItemsValue, out var minItemsNumber) && TryInteger(maxItemsValue, out var maxItemsNumber) && minItemsNumber > maxItemsNumber)
            return "The WebMCP schema has inverted size bounds.";
        return null;
    }

    private static string? ValidateValue(JsonNode schema, JsonNode? value, string path, int depth)
    {
        if (depth > 16 || schema is not JsonObject rule)
            return $"The WebMCP schema at {path} is unsupported.";
        if (!TryGetString(rule["type"], out var type))
            return $"The WebMCP schema at {path} has an invalid type.";
        var matches = type switch
        {
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value is JsonValue v && v.TryGetValue<string>(out _),
            "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
            "number" => TryNumber(value, out _),
            "integer" => TryNumber(value, out var numeric) && decimal.Truncate(numeric) == numeric,
            "null" => value is null,
            _ => false,
        };
        if (!matches)
            return $"WebMCP argument {path} does not match type '{type}'.";
        if (rule["enum"] is JsonArray enumValues && !enumValues.Any(item => Canonical(item) == Canonical(value)))
            return $"WebMCP argument {path} is not an allowed enum value.";
        if (value is JsonObject obj)
        {
            var properties = rule["properties"] as JsonObject ?? new JsonObject();
            if (rule["required"] is JsonArray required)
                foreach (var item in required)
                {
                    if (item is not JsonValue requiredName || !requiredName.TryGetValue<string>(out var name))
                        return $"The WebMCP schema at {path} has an invalid required list.";
                    if (!obj.ContainsKey(name))
                        return $"Required WebMCP argument {path}.{name} is missing.";
                }
            if (rule["additionalProperties"]?.GetValue<bool>() == false)
                foreach (var key in obj.Select(pair => pair.Key))
                    if (!properties.ContainsKey(key))
                        return $"WebMCP argument {path}.{key} is not allowed.";
            foreach (var (key, child) in obj)
                if (properties.TryGetPropertyValue(key, out var childSchema) && childSchema is not null)
                {
                    var error = ValidateValue(childSchema, child, path + "." + key, depth + 1);
                    if (error is not null)
                        return error;
                }
        }
        if (value is JsonArray array && rule["items"] is JsonNode itemSchema)
            for (var index = 0; index < array.Count; index++)
            {
                var error = ValidateValue(itemSchema, array[index], $"{path}[{index}]", depth + 1);
                if (error is not null)
                    return error;
            }
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        {
            if (rule["minLength"] is JsonNode min && TryInteger(min, out var minimum) && text.Length < minimum)
                return $"WebMCP argument {path} is shorter than allowed.";
            if (rule["maxLength"] is JsonNode max && TryInteger(max, out var maximum) && text.Length > maximum)
                return $"WebMCP argument {path} is longer than allowed.";
        }
        if (value is JsonArray boundedArray)
        {
            if (rule["minItems"] is JsonNode min && TryInteger(min, out var minimum) && boundedArray.Count < minimum)
                return $"WebMCP argument {path} has too few items.";
            if (rule["maxItems"] is JsonNode max && TryInteger(max, out var maximum) && boundedArray.Count > maximum)
                return $"WebMCP argument {path} has too many items.";
        }
        if (TryNumber(value, out var number))
        {
            if (rule["minimum"] is JsonNode min && TryNumber(min, out var minimum) && number < minimum)
                return $"WebMCP argument {path} is below the minimum.";
            if (rule["maximum"] is JsonNode max && TryNumber(max, out var maximum) && number > maximum)
                return $"WebMCP argument {path} is above the maximum.";
        }
        return null;
    }

    private static int CountNodes(JsonNode? node, int count)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj)
                count = CountNodes(pair.Value, count + 1);
        else if (node is JsonArray array)
            foreach (var item in array)
                count = CountNodes(item, count + 1);
        return count;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = "";
        return node is JsonValue json && json.TryGetValue<string>(out value!);
    }

    private static bool TryNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        if (node is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetDecimal(out number);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryInteger(JsonNode node, out int number)
    {
        number = 0;
        return TryNumber(node, out var value) && decimal.Truncate(value) == value && value <= int.MaxValue
            && value >= int.MinValue && (number = (int)value) == value;
    }
}
