using System.Text.Json;

namespace Testy.Cli.Mcp;

/// <summary>
/// Checks a tool's structuredContent against its outputSchema, for the JSON Schema subset the tool schemas use: type (including nullable type
/// arrays), required, properties, items, enum, minimum, maximum, minItems and maxItems. The unit checks and verify-mcp share it, so every
/// result an agent can receive is held to the schema it was promised.
/// </summary>
internal static class McpSchemaValidator
{
    /// <summary>The JSON path and description of the first violation, or null when the value conforms.</summary>
    public static string? FirstViolation(JsonElement schema, JsonElement value, string path = "$", bool declaredTopLevelOnly = false)
    {
        if (declaredTopLevelOnly && value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var declared))
            foreach (var property in value.EnumerateObject())
                if (!declared.TryGetProperty(property.Name, out _)) return $"{path}.{property.Name}: the outputSchema does not declare this property.";
        return Check(schema, value, path);
    }

    private static string? Check(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (schema.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()!).ToArray() : [type.GetString()!];
            var actual = Kind(value);
            if (!allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number"))) return $"{path}: expected {string.Join("|", allowed)} but found {actual}.";
        }
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(candidate => Same(candidate, value)))
            return $"{path}: {value.GetRawText()} is not one of {values.GetRawText()}.";
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble()) return $"{path}: {number} is below the minimum {minimum.GetDouble()}.";
            if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble()) return $"{path}: {number} is above the maximum {maximum.GetDouble()}.";
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
                    if (!value.TryGetProperty(name, out _)) return $"{path}: required property '{name}' is missing.";
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (value.TryGetProperty(property.Name, out var child) && Check(property.Value, child, path + "." + property.Name) is { } problem) return problem;
            if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False && schema.TryGetProperty("properties", out var known))
                foreach (var property in value.EnumerateObject())
                    if (!known.TryGetProperty(property.Name, out _)) return $"{path}.{property.Name}: additional properties are not allowed.";
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var least) && count < least.GetInt32()) return $"{path}: {count} items, fewer than minItems {least.GetInt32()}.";
            if (schema.TryGetProperty("maxItems", out var most) && count > most.GetInt32()) return $"{path}: {count} items, more than maxItems {most.GetInt32()}.";
            if (schema.TryGetProperty("items", out var items))
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    if (Check(items, item, $"{path}[{index++}]") is { } problem) return problem;
            }
        }
        return null;
    }

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Null => "null",
        JsonValueKind.Object => "object", JsonValueKind.Array => "array",
        JsonValueKind.Number => value.TryGetInt64(out _) || (value.TryGetDouble(out var real) && Math.Floor(real) == real) ? "integer" : "number",
        _ => "unknown"
    };
    private static bool Same(JsonElement left, JsonElement right) => left.ValueKind == right.ValueKind && left.ValueKind switch
    {
        JsonValueKind.String => left.GetString() == right.GetString(),
        JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
        _ => true
    };
}
