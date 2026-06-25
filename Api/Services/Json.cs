using System.Text.Json;

namespace Api.Services;

// Forgiving JSON helpers.
// Several columns store JSON as text (tags, links, required_tags, contact_info...).
public static class Json
{
    public static T SafeParse<T>(string? value, T fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return JsonSerializer.Deserialize<T>(value) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    // JavaScript Boolean() semantics for a JSON value: false/null/undefined/0/"" are
    // falsy; everything else (including objects and arrays) is truthy.
    public static bool IsTruthy(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            case JsonValueKind.Number:
                return el.TryGetDouble(out var d) && d != 0;
            case JsonValueKind.String:
                return el.GetString()!.Length > 0;
            default:
                return true;
        }
    }

    // TryGetProperty that also tolerates a non-object body (returns false instead of throwing).
    public static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    // JS-style falsy-empty-string normalization for request fields: "" and null both -> null.
    public static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
