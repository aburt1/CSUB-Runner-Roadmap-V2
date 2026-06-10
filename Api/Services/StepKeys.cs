using System.Text.RegularExpressions;
using Api.Data;

namespace Api.Services;

// Step-key slug + uniqueness logic, ported from server/utils/stepKeys.ts.
// Keys are slugged titles, made unique per term with -2/-3 suffixes.
public static class StepKeys
{
    // NOTE: deliberately different from StudentTags.Slugify — this one strips
    // apostrophes/quotes BEFORE slugging ("Don't" -> "dont", not "don-t"). Both
    // produce STORED values (step keys here, derived tags there), so they must not
    // be unified without migrating existing data.
    private static string Slugify(string? value)
    {
        var s = (value ?? "").Trim().ToLowerInvariant();
        s = Regex.Replace(s, "['\"]", "");
        s = Regex.Replace(s, "[^a-z0-9]+", "-");
        s = Regex.Replace(s, "^-+|-+$", "");
        return s;
    }

    public static string? Normalize(string? value)
    {
        var slug = Slugify(value);
        return string.IsNullOrEmpty(slug) ? null : slug;
    }

    public static string BuildBase(string? stepKey = null, string? title = null, string? fallback = null) =>
        Normalize(stepKey) ?? Normalize(title) ?? Normalize(fallback) ?? "step";

    public static string CreateUnique(string baseKey, HashSet<string> usedKeys)
    {
        var b = Normalize(baseKey) ?? "step";
        var candidate = b;
        var suffix = 2;
        while (usedKeys.Contains(candidate))
        {
            candidate = $"{b}-{suffix}";
            suffix++;
        }
        usedKeys.Add(candidate);
        return candidate;
    }

    public static async Task<string> GetUniqueForTermAsync(
        Db db, int termId, string? stepKey = null, string? title = null, string? fallback = null, int? excludeStepId = null)
    {
        var b = BuildBase(stepKey, title, fallback);
        var rows = excludeStepId is null
            ? await db.QueryAllAsync<string>(
                "SELECT step_key FROM steps WHERE term_id = @termId AND step_key IS NOT NULL",
                new { termId })
            : await db.QueryAllAsync<string>(
                "SELECT step_key FROM steps WHERE term_id = @termId AND id <> @excludeStepId AND step_key IS NOT NULL",
                new { termId, excludeStepId });
        var used = new HashSet<string>(rows.Where(r => !string.IsNullOrEmpty(r)));
        return CreateUnique(b, used);
    }

    // Backfill/repair step keys so every step has a unique key within its term.
    public static async Task EnsureAllAsync(Db db)
    {
        var steps = await db.QueryAllAsync<StepKeyRow>(
            "SELECT id, title, term_id, step_key FROM steps ORDER BY COALESCE(term_id, 0), sort_order, id");
        var usedByTerm = new Dictionary<object, HashSet<string>>();

        foreach (var step in steps)
        {
            object termKey = step.term_id ?? (object)"global";
            if (!usedByTerm.TryGetValue(termKey, out var used))
            {
                used = new HashSet<string>();
                usedByTerm[termKey] = used;
            }

            var next = CreateUnique(BuildBase(step.step_key, step.title, $"step-{step.id}"), used);
            if (step.step_key != next)
                await db.ExecuteAsync("UPDATE steps SET step_key = @next WHERE id = @id", new { next, step.id });
        }
    }

    private sealed class StepKeyRow
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public int? term_id { get; set; }
        public string? step_key { get; set; }
    }
}
