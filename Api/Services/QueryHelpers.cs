using Api.Data;

namespace Api.Services;

// Small query helpers shared across admin endpoints, ported from
// server/utils/queryHelpers.ts.
public static class QueryHelpers
{
    // SQL fragment for "active, non-optional" steps.
    public const string ActiveStepFilter = "is_active = 1 AND COALESCE(is_optional, 0) = 0";

    public static int? ParseTermId(HttpRequest req) =>
        int.TryParse(req.Query["term_id"], out var v) ? v : null;

    public static (int Page, int PerPage, int Offset) ParsePagination(HttpRequest req, int defaultPerPage = 25)
    {
        var page = Math.Max(1, int.TryParse(req.Query["page"], out var p) ? p : 1);
        var perPage = Math.Min(100, Math.Max(1, int.TryParse(req.Query["per_page"], out var pp) ? pp : defaultPerPage));
        return (page, perPage, (page - 1) * perPage);
    }

    public static Task<int> CountActiveStepsAsync(Db db, int termId) =>
        db.QueryOneAsync<int>(
            $"SELECT COUNT(*) FROM steps WHERE {ActiveStepFilter} AND term_id = @termId",
            new { termId });
}
