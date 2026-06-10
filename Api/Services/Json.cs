using System.Text.Json;

namespace Api.Services;

// Forgiving JSON helpers, ported from server/utils/json.ts (safeJsonParse).
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
}
