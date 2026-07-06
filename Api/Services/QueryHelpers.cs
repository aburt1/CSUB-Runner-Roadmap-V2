using Api.Data;

namespace Api.Services;

// Small query helpers shared across admin endpoints.
public static class QueryHelpers
{
    // SQL fragment for "active, non-optional" steps.
    // Used by admin analytics, exports, and ApiCheckRunner (NULL is_active treated as
    // INACTIVE).
    public const string ActiveStepFilter = "is_active = 1 AND COALESCE(is_optional, 0) = 0";

    // SQL fragment for student-facing step queries.
    // Treats NULL is_active as ACTIVE — legacy rows that predate the is_active column
    // have no explicit value, and students must still see them.
    // Admin/analytics paths use ActiveStepFilter above (different convention).
    public const string StudentVisibleStepFilter = "(is_active = 1 OR is_active IS NULL)";

    public static int? ParseTermId(HttpRequest req) =>
        int.TryParse(req.Query["term_id"], out var v) ? v : null;

    public static (int Page, int PerPage, int Offset) ParsePagination(HttpRequest req, int defaultPerPage = 25)
    {
        var page = Math.Max(1, int.TryParse(req.Query["page"], out var p) ? p : 1);
        var perPage = Math.Min(100, Math.Max(1, int.TryParse(req.Query["per_page"], out var pp) ? pp : defaultPerPage));
        return (page, perPage, (page - 1) * perPage);
    }

    // The current active term: newest active term wins (ORDER BY id DESC).
    // Used when provisioning students and resolving an unspecified cohort.
    // NOTE: the seeder deliberately picks the OLDEST active term instead — do not fold that in.
    public static Task<int?> GetActiveTermIdAsync(Db db) =>
        db.QueryOneAsync<int?>("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");

    public static Task<int> CountActiveStepsAsync(Db db, int termId) =>
        db.QueryOneAsync<int>(
            $"SELECT COUNT(*) FROM steps WHERE {ActiveStepFilter} AND term_id = @termId",
            new { termId });
}
