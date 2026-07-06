using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Api.Auth;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin analytics, stats, and CSV export. Mounted under /api/admin, gated by
// adminAuth (any authenticated admin, no requireRole).
//
// Boring on purpose: hand-written T-SQL inline, snake_case anonymous response
// objects whose keys are the public JSON contract, manual validation.
//
// 'Done' deliberately differs per metric:
//   - Completion/cohort/trend metrics count status IN ('completed','waived') —
//     waiving a step counts as finishing it for progress tracking purposes.
//   - Velocity and stalled count status = 'completed' only — waiving is not
//     activity so it should not mask that a student is stalled or inflate speed.
//   - Stats counts any student_progress row because NOT-completed rows are
//     deleted by Progress.ApplyAsync; a row existing means it was completed.
// See the one-line pointer comments at each divergent subquery.
[ApiController]
[Route("api/admin")]
[AdminAuth]
public sealed class AnalyticsController : ControllerBase
{
    private readonly Db _db;

    // Number of worst-performing steps the bottlenecks endpoint returns.
    private const int BottleneckStepCount = 5;

    // Ordered velocity buckets — the single source for bucketOrder, the zeroed
    // tally dictionary, and the output rows. The day cutoffs that classify a
    // student into a bucket (and BuildVelocityBucketFilter's SQL min/max bounds)
    // are a deliberately separate concern and are NOT derived from this.
    private static readonly string[] VelocityBuckets =
        { "1-3 days", "4-7 days", "1-2 weeks", "2-4 weeks", "4+ weeks" };

    public AnalyticsController(Db db)
    {
        _db = db;
    }

    // ─── Stats ───────────────────────────────────────────────

    // GET /api/admin/stats — optional ?term_id=
    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var stepFilter = termId.HasValue
            ? $"WHERE {QueryHelpers.ActiveStepFilter} AND term_id = @termId"
            : $"WHERE {QueryHelpers.ActiveStepFilter}";
        var studentFilter = termId.HasValue ? "WHERE term_id = @termId" : "";

        var totalStudents = await _db.QueryOneAsync<int>(
            $"SELECT COUNT(*) as count FROM students {studentFilter}", new { termId });
        var totalActiveSteps = await _db.QueryOneAsync<int>(
            $"SELECT COUNT(*) as count FROM steps {stepFilter}", new { termId });

        // SQL Server AVG over int truncates, so cast the count to float to keep the
        // fractional average.
        // Stats counts any student_progress row (see class header comment on the three 'done' tiers).
        string avgQuery = termId.HasValue
            ? $@"SELECT COALESCE(AVG(CAST(pc.completed AS float)), 0) as avg_completed
                 FROM students st
                 LEFT JOIN (
                   SELECT student_id, COUNT(*) as completed
                   FROM student_progress sp
                   JOIN steps s ON s.id = sp.step_id AND s.{QueryHelpers.ActiveStepFilter} AND s.term_id = @termId
                   GROUP BY student_id
                 ) pc ON pc.student_id = st.id
                 WHERE st.term_id = @termId"
            : $@"SELECT COALESCE(AVG(CAST(pc.completed AS float)), 0) as avg_completed
                 FROM students st
                 LEFT JOIN (
                   SELECT student_id, COUNT(*) as completed
                   FROM student_progress sp
                   JOIN steps s ON s.id = sp.step_id AND s.{QueryHelpers.ActiveStepFilter}
                   GROUP BY student_id
                 ) pc ON pc.student_id = st.id";

        var avgCompleted = await _db.QueryOneAsync<double>(avgQuery, new { termId });

        var avgPercent = totalActiveSteps > 0
            ? (int)Math.Round((avgCompleted / totalActiveSteps) * 100, MidpointRounding.AwayFromZero)
            : 0;

        return Ok(new
        {
            totalStudents,
            totalActiveSteps,
            avgCompletionPercent = avgPercent,
        });
    }

    // ─── Export ──────────────────────────────────────────────

    // GET /api/admin/export/progress?term_id=&format=csv
    [HttpGet("export/progress")]
    public async Task<IActionResult> ExportProgress()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var studentFilter = termId.HasValue ? "WHERE term_id = @termId" : "";
        var stepFilter = termId.HasValue
            ? $"WHERE {QueryHelpers.ActiveStepFilter} AND term_id = @termId"
            : $"WHERE {QueryHelpers.ActiveStepFilter}";

        var steps = await _db.QueryAllAsync<ExportStep>(
            $"SELECT id, title FROM steps {stepFilter} ORDER BY sort_order", new { termId });
        var students = await _db.QueryAllAsync<ExportStudent>(
            $"SELECT id, display_name, email FROM students {studentFilter} ORDER BY display_name", new { termId });

        // Get progress scoped to the relevant students and steps. Scoping is done with
        // JOINs, not IN-lists: Dapper expands an IN-list to one SQL parameter per element
        // and SQL Server caps a request at 2100 parameters, so a ~2k-student cohort would
        // make this endpoint 500. Joins keep it at a single @termId parameter.
        var allProgress = new List<ExportProgressRow>();
        if (students.Count > 0 && steps.Count > 0)
        {
            var progressScope = termId.HasValue ? "AND s.term_id = @termId AND st.term_id = @termId" : "";
            allProgress = (await _db.QueryAllAsync<ExportProgressRow>(
                $@"SELECT sp.student_id, sp.step_id, sp.status
                   FROM student_progress sp
                   JOIN students s ON s.id = sp.student_id
                   JOIN steps st ON st.id = sp.step_id AND st.{QueryHelpers.ActiveStepFilter}
                   WHERE 1 = 1 {progressScope}",
                new { termId })).ToList();
        }

        var progressMap = new Dictionary<string, string>();
        foreach (var p in allProgress)
        {
            var key = $"{p.student_id}:{p.step_id}";
            // NULL/empty status is legacy data and means completed (schema default — see schema.sql student_progress.status).
            progressMap[key] = string.IsNullOrEmpty(p.status) ? "completed" : p.status;
        }

        // Build CSV.
        var headers = new List<string> { "Student Name", "Email" };
        foreach (var step in steps) headers.Add(step.title);
        headers.Add("Total Complete");
        headers.Add("Percentage");

        var rows = new List<List<string>>();
        foreach (var student in students)
        {
            var doneCount = 0;
            var stepCells = new List<string>();
            foreach (var step in steps)
            {
                progressMap.TryGetValue($"{student.id}:{step.id}", out var status);
                if (status == "completed") { doneCount++; stepCells.Add("Completed"); }
                else if (status == "waived") { doneCount++; stepCells.Add("Waived"); }
                else stepCells.Add("");
            }
            var pct = steps.Count > 0
                ? (int)Math.Round(((double)doneCount / steps.Count) * 100, MidpointRounding.AwayFromZero)
                : 0;

            var row = new List<string> { student.display_name ?? "", student.email ?? "" };
            row.AddRange(stepCells);
            row.Add(doneCount.ToString(CultureInfo.InvariantCulture));
            row.Add($"{pct}%");
            rows.Add(row);
        }

        var lines = new List<string>();
        lines.Add(string.Join(",", headers.ConvertAll(SanitizeCell)));
        foreach (var row in rows)
            lines.Add(string.Join(",", row.ConvertAll(SanitizeCell)));
        var csvContent = string.Join("\n", lines);

        string termName = "all";
        if (termId.HasValue)
        {
            var name = await _db.QueryOneAsync<string>(
                "SELECT name FROM terms WHERE id = @termId", new { termId });
            termName = string.IsNullOrEmpty(name) ? "unknown" : name;
        }

        var datePart = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"progress-{termName}-{datePart}.csv";

        // termName is admin-controlled (a term's name), so it can contain a double quote
        // (which would break the Content-Disposition token) or non-ASCII characters like
        // "Otoño 2026" (which throw under Kestrel's ASCII-only header encoding). Build the
        // header via ContentDispositionHeaderValue.SetHttpFileName, which emits a safe ASCII
        // `filename` fallback plus an RFC 5987 `filename*` (percent-encoded UTF-8).
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();
        return Content(csvContent, "text/csv");
    }

    // Prevent spreadsheet formula injection; quotes doubled, leading risky chars escaped.
    private static string SanitizeCell(string value)
    {
        var str = (value ?? "").Replace("\"", "\"\"");
        if (Regex.IsMatch(str, "^[=+\\-@\t\r]"))
            str = "'" + str;
        return "\"" + str + "\"";
    }

    // ─── Analytics ───────────────────────────────────────────

    // GET /api/admin/analytics/step-completion?term_id=
    [HttpGet("analytics/step-completion")]
    public async Task<IActionResult> StepCompletion()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var termFilter = termId.HasValue ? "AND s.term_id = @termId" : "";

        var studentCountSql = termId.HasValue
            ? "SELECT COUNT(*) as count FROM students WHERE term_id = @termId"
            : "SELECT COUNT(*) as count FROM students";
        var totalStudents = await _db.QueryOneAsync<int>(studentCountSql, new { termId });

        var steps = await _db.QueryAllAsync<StepCompletionRow>(
            $@"SELECT s.id, s.title, s.sort_order,
                COUNT(DISTINCT sp.student_id) as completed_count
               FROM steps s
               LEFT JOIN student_progress sp ON sp.step_id = s.id AND sp.status IN ('completed', 'waived')
               WHERE s.{QueryHelpers.ActiveStepFilter} {termFilter}
               GROUP BY s.id, s.title, s.sort_order
               ORDER BY s.sort_order",
            new { termId });

        var stepsOut = new List<object>();
        foreach (var s in steps)
            stepsOut.Add(new
            {
                id = s.id,
                title = s.title,
                sort_order = s.sort_order,
                completed_count = s.completed_count,
                total_students = totalStudents,
            });

        return Ok(new { steps = stepsOut, totalStudents });
    }

    // GET /api/admin/analytics/completion-trend?term_id=&days=30
    [HttpGet("analytics/completion-trend")]
    public async Task<IActionResult> CompletionTrend()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var days = ParseDaysDefault(Request.Query["days"], 30);

        // deliberately no is_active filter: completions on since-deactivated steps remain part of the
        // historical trend (BuildTrendDateFilter must stay in sync).
        var termFilter = termId.HasValue
            ? "JOIN steps st ON st.id = sp.step_id AND st.term_id = @termId AND COALESCE(st.is_optional, 0) = 0"
            : "JOIN steps st ON st.id = sp.step_id AND COALESCE(st.is_optional, 0) = 0";

        var rows = await _db.QueryAllAsync<CompletionTrendRow>(
            $@"SELECT CONVERT(varchar(10), CAST(sp.completed_at AS date), 23) as date, COUNT(*) as completions
               FROM student_progress sp
               {termFilter}
               WHERE sp.status IN ('completed', 'waived')
                 AND sp.completed_at >= DATEADD(day, -@days, CAST(SYSUTCDATETIME() AS date))
               GROUP BY CAST(sp.completed_at AS date)
               ORDER BY CAST(sp.completed_at AS date)",
            new { termId, days });

        var outRows = new List<object>();
        foreach (var r in rows)
            outRows.Add(new { date = r.date, completions = r.completions });

        return Ok(outRows);
    }

    // GET /api/admin/analytics/bottlenecks?term_id=
    [HttpGet("analytics/bottlenecks")]
    public async Task<IActionResult> Bottlenecks()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var termFilter = termId.HasValue ? "AND s.term_id = @termId" : "";

        var totalStudents = await _db.QueryOneAsync<int>(
            termId.HasValue
                ? "SELECT COUNT(*) as count FROM students WHERE term_id = @termId"
                : "SELECT COUNT(*) as count FROM students",
            new { termId });

        var steps = await _db.QueryAllAsync<StepCompletionRow>(
            $@"SELECT s.id, s.title, s.sort_order,
                COUNT(DISTINCT sp.student_id) as completed_count
               FROM steps s
               LEFT JOIN student_progress sp ON sp.step_id = s.id AND sp.status IN ('completed', 'waived')
               WHERE s.{QueryHelpers.ActiveStepFilter} {termFilter}
               GROUP BY s.id, s.title, s.sort_order
               ORDER BY completed_count ASC
               OFFSET 0 ROWS FETCH NEXT {BottleneckStepCount} ROWS ONLY",
            new { termId });

        var stepsOut = new List<object>();
        foreach (var s in steps)
            stepsOut.Add(new
            {
                id = s.id,
                title = s.title,
                sort_order = s.sort_order,
                completed_count = s.completed_count,
                total_students = totalStudents,
                completion_pct = totalStudents > 0
                    ? (int)Math.Round(((double)s.completed_count / totalStudents) * 100, MidpointRounding.AwayFromZero)
                    : 0,
            });

        return Ok(new { steps = stepsOut, totalStudents });
    }

    // GET /api/admin/analytics/cohort-summary?term_id=
    [HttpGet("analytics/cohort-summary")]
    public async Task<IActionResult> CohortSummary()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var studentFilter = termId.HasValue ? "WHERE st.term_id = @termId" : "";
        var stepFilter = termId.HasValue ? "AND s.term_id = @termId" : "";

        int totalActiveSteps;
        if (termId.HasValue)
        {
            totalActiveSteps = await QueryHelpers.CountActiveStepsAsync(_db, termId.Value);
        }
        else
        {
            totalActiveSteps = await _db.QueryOneAsync<int>(
                $"SELECT COUNT(*) as count FROM steps WHERE {QueryHelpers.ActiveStepFilter}");
        }

        var divisor = totalActiveSteps > 0 ? totalActiveSteps : 1;

        // the bucket labels happen to sort alphabetically in range order — keep that property if renaming buckets.
        var rows = await _db.QueryAllAsync<CohortBucketRow>(
            $@"SELECT
                CASE
                  WHEN COALESCE(pc.done, 0) = 0 THEN '0%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.25 THEN '1-25%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.50 THEN '26-50%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.75 THEN '51-75%'
                  ELSE '76-100%'
                END as bucket,
                COUNT(*) as student_count
               FROM students st
               LEFT JOIN (
                 SELECT student_id, COUNT(*) as done
                 FROM student_progress sp
                 JOIN steps s ON s.id = sp.step_id AND s.{QueryHelpers.ActiveStepFilter} {stepFilter}
                 WHERE sp.status IN ('completed', 'waived')
                 GROUP BY student_id
               ) pc ON pc.student_id = st.id
               {studentFilter}
               GROUP BY
                CASE
                  WHEN COALESCE(pc.done, 0) = 0 THEN '0%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.25 THEN '1-25%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.50 THEN '26-50%'
                  WHEN CAST(COALESCE(pc.done, 0) AS float) / @divisor <= 0.75 THEN '51-75%'
                  ELSE '76-100%'
                END
               ORDER BY bucket",
            new { termId, divisor });

        var outRows = new List<object>();
        foreach (var r in rows)
            outRows.Add(new { bucket = r.bucket, student_count = r.student_count });

        return Ok(outRows);
    }

    // GET /api/admin/analytics/deadline-risk?term_id=&days=14
    [HttpGet("analytics/deadline-risk")]
    public async Task<IActionResult> DeadlineRisk()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var days = ParseDaysDefault(Request.Query["days"], 14);
        var termFilter = termId.HasValue ? "AND s.term_id = @termId" : "";

        // deadline_date stores 'YYYY-MM-DD' text; TRY_CAST tolerates legacy free-text values.
        var steps = await _db.QueryAllAsync<DeadlineRiskStep>(
            $@"SELECT s.id, s.title, s.deadline_date,
                COUNT(DISTINCT st.id) as total_students,
                COUNT(DISTINCT CASE WHEN sp.status IS NULL OR sp.status NOT IN ('completed', 'waived') THEN st.id END) as at_risk_count
               FROM steps s
               JOIN students st ON st.term_id = s.term_id
               LEFT JOIN student_progress sp ON sp.step_id = s.id AND sp.student_id = st.id
               WHERE s.is_active = 1 AND s.deadline_date IS NOT NULL
                 AND TRY_CAST(s.deadline_date AS date) <= CAST(DATEADD(day, @days, SYSUTCDATETIME()) AS date)
                 AND TRY_CAST(s.deadline_date AS date) > CAST(SYSUTCDATETIME() AS date) {termFilter}
               GROUP BY s.id, s.title, s.deadline_date
               ORDER BY s.deadline_date ASC",
            new { termId, days });

        var result = new List<object>();
        foreach (var step in steps)
        {
            IReadOnlyList<DeadlineRiskStudent> students;
            if (termId.HasValue)
            {
                students = await _db.QueryAllAsync<DeadlineRiskStudent>(
                    @"SELECT st.id, st.display_name, st.email
                      FROM students st
                      LEFT JOIN student_progress sp ON sp.step_id = @stepId AND sp.student_id = st.id AND sp.status IN ('completed', 'waived')
                      WHERE st.term_id = @termId AND sp.student_id IS NULL",
                    new { stepId = step.id, termId });
            }
            else
            {
                // scoped to the step's own term so the student list matches the aggregate count above.
                students = await _db.QueryAllAsync<DeadlineRiskStudent>(
                    @"SELECT st.id, st.display_name, st.email
                      FROM students st
                      LEFT JOIN student_progress sp ON sp.step_id = @stepId AND sp.student_id = st.id AND sp.status IN ('completed', 'waived')
                      WHERE st.term_id = (SELECT term_id FROM steps WHERE id = @stepId) AND sp.student_id IS NULL",
                    new { stepId = step.id });
            }

            var studentsOut = new List<object>();
            foreach (var s in students)
                studentsOut.Add(new { id = s.id, display_name = s.display_name, email = s.email });

            result.Add(new
            {
                id = step.id,
                title = step.title,
                deadline_date = step.deadline_date,
                total_students = step.total_students,
                at_risk_count = step.at_risk_count,
                students = studentsOut,
            });
        }

        return Ok(result);
    }

    // GET /api/admin/analytics/stalled-students?term_id=&days=7
    [HttpGet("analytics/stalled-students")]
    public async Task<IActionResult> StalledStudents()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var days = ParseDaysDefault(Request.Query["days"], 7);
        var termFilter = termId.HasValue ? "WHERE st.term_id = @termId" : "";

        // status = 'completed' only (see class header comment on the three 'done' tiers).
        var students = await _db.QueryAllAsync<StalledStudentRow>(
            $@"SELECT st.id, st.display_name, st.email,
                MAX(sp.completed_at) as last_completion_date,
                COUNT(CASE WHEN sp.status = 'completed' THEN 1 END) as completed_count
               FROM students st
               LEFT JOIN student_progress sp ON sp.student_id = st.id
               {termFilter}
               GROUP BY st.id, st.display_name, st.email, st.created_at
               HAVING COUNT(CASE WHEN sp.status = 'completed' THEN 1 END) = 0
                 OR MAX(sp.completed_at) < DATEADD(day, -@days, SYSUTCDATETIME())
               ORDER BY COALESCE(MAX(sp.completed_at), st.created_at) ASC",
            new { termId, days });

        int totalSteps;
        if (termId.HasValue)
            totalSteps = await QueryHelpers.CountActiveStepsAsync(_db, termId.Value);
        else
            totalSteps = await _db.QueryOneAsync<int>(
                $"SELECT COUNT(*) as count FROM steps WHERE {QueryHelpers.ActiveStepFilter}");

        var outRows = new List<object>();
        foreach (var s in students)
            outRows.Add(new
            {
                id = s.id,
                display_name = s.display_name,
                email = s.email,
                last_completion_date = s.last_completion_date,
                completed_count = s.completed_count,
                total_steps = totalSteps,
            });

        return Ok(outRows);
    }

    // Ordered stalled buckets — the single source for the bucket labels and their day
    // bounds, shared by the bucketed endpoint below and mirrored by BuildStalledFilter.
    // A student is "stalled" only past minDays 7; anything more recent is not emitted.
    private static readonly (string Bucket, int MinDays, int MaxDays)[] StalledBuckets =
        { ("7-14 days", 7, 14), ("2-4 weeks", 15, 28), ("1-3 months", 29, 90), ("3+ months", 91, 99999) };

    // GET /api/admin/analytics/stalled-students/buckets?term_id=
    // Server-side bucketing so the chart and its drilldown agree. Buckets by days inactive
    // using the SAME rule as BuildStalledFilter: for a student with completions, days since
    // MAX(completed_at) FILTERED TO status='completed' (a waive is not activity); for a
    // zero-completion student, days since created_at.
    [HttpGet("analytics/stalled-students/buckets")]
    public async Task<IActionResult> StalledStudentBuckets()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        var termFilter = termId.HasValue ? "WHERE st.term_id = @termId" : "";

        // Per-student days inactive, then classify into the ordered buckets. seconds/86400,
        // not DATEDIFF(day), to match BuildStalledFilter (see CompletionVelocity comment).
        var rows = await _db.QueryAllAsync<StalledBucketCountRow>(
            $@"WITH per_student AS (
                 SELECT
                   CASE
                     WHEN COUNT(CASE WHEN sp.status = 'completed' THEN 1 END) = 0
                       THEN DATEDIFF(second, st.created_at, SYSUTCDATETIME()) / 86400
                     ELSE DATEDIFF(second, MAX(CASE WHEN sp.status = 'completed' THEN sp.completed_at END), SYSUTCDATETIME()) / 86400
                   END AS days_inactive
                 FROM students st
                 LEFT JOIN student_progress sp ON sp.student_id = st.id
                 {termFilter}
                 GROUP BY st.id, st.created_at
               )
               SELECT
                 CASE
                   WHEN days_inactive BETWEEN 7 AND 14 THEN '7-14 days'
                   WHEN days_inactive BETWEEN 15 AND 28 THEN '2-4 weeks'
                   WHEN days_inactive BETWEEN 29 AND 90 THEN '1-3 months'
                   WHEN days_inactive >= 91 THEN '3+ months'
                 END AS bucket,
                 COUNT(*) AS student_count
               FROM per_student
               WHERE days_inactive >= 7
               GROUP BY
                 CASE
                   WHEN days_inactive BETWEEN 7 AND 14 THEN '7-14 days'
                   WHEN days_inactive BETWEEN 15 AND 28 THEN '2-4 weeks'
                   WHEN days_inactive BETWEEN 29 AND 90 THEN '1-3 months'
                   WHEN days_inactive >= 91 THEN '3+ months'
                 END",
            new { termId });

        var counts = new Dictionary<string, int>();
        foreach (var r in rows)
            if (r.bucket is not null)
                counts[r.bucket] = r.student_count;

        // Emit every bucket in order, zero-filled, so the chart's x-axis is stable.
        var outRows = new List<object>();
        foreach (var (bucket, _, _) in StalledBuckets)
            outRows.Add(new { bucket, student_count = counts.TryGetValue(bucket, out var c) ? c : 0 });

        return Ok(outRows);
    }

    // ─── Analytics Students Drilldown ────────────────────────

    // GET /api/admin/analytics/students?term_id=&filter_type=&filter_value=&page=1&per_page=50
    [HttpGet("analytics/students")]
    public async Task<IActionResult> AnalyticsStudents()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        string? filterType = Request.Query["filter_type"];
        string? filterValue = Request.Query["filter_value"];
        var (page, perPage, offset) = QueryHelpers.ParsePagination(Request, 50);

        if (!termId.HasValue || string.IsNullOrEmpty(filterType))
            return BadRequest(new { error = "term_id and filter_type are required" });

        var totalActiveSteps = await QueryHelpers.CountActiveStepsAsync(_db, termId.Value);

        FilterQuerySet filterSet;
        try
        {
            filterSet = await BuildFilterAsync(filterType, termId.Value, filterValue, perPage, offset, totalActiveSteps);
        }
        catch (InvalidFilterException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var studentsResult = await _db.QueryAllAsync<DrilldownStudent>(filterSet.StudentQuery, filterSet.Params);
        var total = await _db.QueryOneAsync<int>(filterSet.CountQuery, filterSet.CountParams);

        // Enrich with completion counts.
        var studentIds = new List<string>();
        foreach (var s in studentsResult) studentIds.Add(s.id);

        var completionMap = new Dictionary<string, int>();
        if (studentIds.Count > 0)
        {
            // IN-list is safe here: studentIds is one page, capped at per_page <= 100 by ParsePagination — far below the 2100-parameter limit (cf. the export comment above).
            // Canonical completed scope (shared with the students list): only active,
            // non-optional steps IN THIS TERM count, so done can never exceed totalActiveSteps.
            var completions = await _db.QueryAllAsync<CompletionCountRow>(
                $@"SELECT sp.student_id, COUNT(*) as done
                  FROM student_progress sp
                  JOIN steps s ON s.id = sp.step_id
                    AND s.{QueryHelpers.ActiveStepFilter}
                    AND s.term_id = @termId
                  WHERE sp.student_id IN @studentIds AND sp.status IN ('completed', 'waived')
                  GROUP BY sp.student_id",
                new { studentIds, termId = termId.Value });
            foreach (var c in completions)
                completionMap[c.student_id] = c.done;
        }

        var studentsOut = new List<object>();
        foreach (var s in studentsResult)
        {
            completionMap.TryGetValue(s.id, out var done);
            studentsOut.Add(new
            {
                id = s.id,
                display_name = s.display_name,
                email = s.email,
                emplid = s.emplid,
                completed_count = done,
                total_steps = totalActiveSteps,
                completion_pct = totalActiveSteps > 0
                    ? (int)Math.Round(((double)done / totalActiveSteps) * 100, MidpointRounding.AwayFromZero)
                    : 0,
            });
        }

        return Ok(new
        {
            title = filterSet.Title,
            students = studentsOut,
            total,
            page,
            per_page = perPage,
        });
    }

    // GET /api/admin/analytics/cohort-comparison?term_id=
    [HttpGet("analytics/cohort-comparison")]
    public async Task<IActionResult> CohortComparison()
    {
        var termId = QueryHelpers.ParseTermId(Request);

        int totalSteps;
        if (termId.HasValue)
            totalSteps = await QueryHelpers.CountActiveStepsAsync(_db, termId.Value);
        else
            totalSteps = await _db.QueryOneAsync<int>(
                $"SELECT COUNT(*) as count FROM steps WHERE {QueryHelpers.ActiveStepFilter}");

        var divisor = totalSteps > 0 ? totalSteps : 1;

        // fixed well-known cohort tags; matched as substrings of the JSON tags text
        // — exact-token matching is deliberately not attempted here, so keep tag names
        // non-overlapping. The order here is the source of truth for the tag{N} columns below.
        var tags = new[] { "freshman", "transfer", "first-gen", "honors", "athlete", "eop", "veteran", "out-of-state" };

        var termFilter = termId.HasValue ? "AND s.term_id = @termId" : "";
        var studentFilter = termId.HasValue ? "WHERE st.term_id = @termId" : "";

        // One pass: compute each student's completion pct once (pc is term-scoped like Stats,
        // so cohort numbers are term-scoped), then fan out into per-tag conditional aggregates
        // — one COUNT of members and one AVG of their completion pct per tag. This matches the
        // old per-tag COUNT(DISTINCT)/ROUND(AVG(...)*100). A student contributes to every
        // cohort whose tag it matches (tags are LIKE substrings).
        // status = 'completed' only (see class header comment on the three 'done' tiers).
        var cohort = await _db.QueryOneAsync<CohortComparisonRow>(
            $@"WITH student_pct AS (
                 SELECT st.tags, CAST(COALESCE(pc.done, 0) AS float) / @divisor * 100 as pct
                 FROM students st
                 LEFT JOIN (
                   SELECT student_id, COUNT(*) as done
                   FROM student_progress sp
                   JOIN steps s ON s.id = sp.step_id AND s.{QueryHelpers.ActiveStepFilter} {termFilter}
                   WHERE sp.status = 'completed'
                   GROUP BY student_id
                 ) pc ON pc.student_id = st.id
                 {studentFilter}
               )
               SELECT
                 COUNT(CASE WHEN tags LIKE @tag0 THEN 1 END) as count0, ROUND(AVG(CASE WHEN tags LIKE @tag0 THEN pct END), 0) as avg0,
                 COUNT(CASE WHEN tags LIKE @tag1 THEN 1 END) as count1, ROUND(AVG(CASE WHEN tags LIKE @tag1 THEN pct END), 0) as avg1,
                 COUNT(CASE WHEN tags LIKE @tag2 THEN 1 END) as count2, ROUND(AVG(CASE WHEN tags LIKE @tag2 THEN pct END), 0) as avg2,
                 COUNT(CASE WHEN tags LIKE @tag3 THEN 1 END) as count3, ROUND(AVG(CASE WHEN tags LIKE @tag3 THEN pct END), 0) as avg3,
                 COUNT(CASE WHEN tags LIKE @tag4 THEN 1 END) as count4, ROUND(AVG(CASE WHEN tags LIKE @tag4 THEN pct END), 0) as avg4,
                 COUNT(CASE WHEN tags LIKE @tag5 THEN 1 END) as count5, ROUND(AVG(CASE WHEN tags LIKE @tag5 THEN pct END), 0) as avg5,
                 COUNT(CASE WHEN tags LIKE @tag6 THEN 1 END) as count6, ROUND(AVG(CASE WHEN tags LIKE @tag6 THEN pct END), 0) as avg6,
                 COUNT(CASE WHEN tags LIKE @tag7 THEN 1 END) as count7, ROUND(AVG(CASE WHEN tags LIKE @tag7 THEN pct END), 0) as avg7
               FROM student_pct",
            new
            {
                termId,
                divisor,
                tag0 = $"%{tags[0]}%",
                tag1 = $"%{tags[1]}%",
                tag2 = $"%{tags[2]}%",
                tag3 = $"%{tags[3]}%",
                tag4 = $"%{tags[4]}%",
                tag5 = $"%{tags[5]}%",
                tag6 = $"%{tags[6]}%",
                tag7 = $"%{tags[7]}%",
            });

        var counts = cohort is null
            ? new int[tags.Length]
            : new[] { cohort.count0, cohort.count1, cohort.count2, cohort.count3, cohort.count4, cohort.count5, cohort.count6, cohort.count7 };
        var avgs = cohort is null
            ? new double?[tags.Length]
            : new[] { cohort.avg0, cohort.avg1, cohort.avg2, cohort.avg3, cohort.avg4, cohort.avg5, cohort.avg6, cohort.avg7 };

        var result = new List<CohortComparisonItem>();
        for (var i = 0; i < tags.Length; i++)
        {
            if (counts[i] <= 0)
                continue;
            var avg = avgs[i].HasValue ? (int)avgs[i]!.Value : 0;
            result.Add(new CohortComparisonItem
            {
                tag = tags[i],
                student_count = counts[i],
                avg_completion_pct = avg,
            });
        }

        result.Sort((a, b) => b.student_count.CompareTo(a.student_count));

        var outRows = new List<object>();
        foreach (var r in result)
            outRows.Add(new { tag = r.tag, student_count = r.student_count, avg_completion_pct = r.avg_completion_pct });

        return Ok(outRows);
    }

    // GET /api/admin/analytics/completion-velocity?term_id=
    [HttpGet("analytics/completion-velocity")]
    public async Task<IActionResult> CompletionVelocity()
    {
        var termId = QueryHelpers.ParseTermId(Request);

        // seconds/86400, not DATEDIFF(day): day counts midnight boundaries, not elapsed 24h periods.
        // (see BuildStalledFilter and BuildVelocityBucketFilter for the same idiom)
        // status = 'completed' only (see class header comment on the three 'done' tiers).
        var students = await _db.QueryAllAsync<VelocityRow>(
            $@"SELECT st.id,
                DATEDIFF(second, MIN(sp.completed_at), MAX(sp.completed_at)) / 86400 as days_elapsed
               FROM students st
               JOIN student_progress sp ON sp.student_id = st.id AND sp.status = 'completed'
               {(termId.HasValue ? "WHERE st.term_id = @termId" : "")}
               GROUP BY st.id",
            new { termId });

        var buckets = VelocityBuckets.ToDictionary(label => label, _ => 0);

        foreach (var student in students)
        {
            var d = student.days_elapsed ?? 0;
            if (d <= 3) buckets["1-3 days"]++;
            else if (d <= 7) buckets["4-7 days"]++;
            else if (d <= 14) buckets["1-2 weeks"]++;
            else if (d <= 28) buckets["2-4 weeks"]++;
            else buckets["4+ weeks"]++;
        }

        var outRows = new List<object>();
        foreach (var bucket in VelocityBuckets)
            outRows.Add(new { bucket, student_count = buckets[bucket] });

        return Ok(outRows);
    }

    // ─── Filter builders (drilldown) ─────────────────────────

    // Returns the student/count SQL plus their parameters for a given filter type;
    // throws InvalidFilterException for bad filter_value.
    private async Task<FilterQuerySet> BuildFilterAsync(
        string filterType, int termId, string? filterValue, int perPage, int offset, int totalActiveSteps)
    {
        // Filters that bind filter_value against typed columns must validate it up
        // front: a non-numeric step id or a non-date would otherwise fail the SQL
        // conversion at execution time and surface as a 500 instead of a 400.
        switch (filterType)
        {
            case "step_completed" or "step_not_completed" or "deadline_risk"
                when !int.TryParse(filterValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                throw new InvalidFilterException("filter_value must be a step id");
            case "trend_date" when !DateTime.TryParse(filterValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                throw new InvalidFilterException("filter_value must be a date");
        }

        // Parse the already-validated step id once; builders bind the typed int (not the raw string) against INT columns.
        int stepId = 0;
        if (filterType is "step_completed" or "step_not_completed" or "deadline_risk")
            stepId = int.Parse(filterValue!, NumberStyles.Integer, CultureInfo.InvariantCulture);

        switch (filterType)
        {
            case "step_completed":
                return await BuildStepCompletedFilter(termId, stepId, perPage, offset);
            case "step_not_completed":
                return await BuildStepNotCompletedFilter(termId, stepId, perPage, offset);
            case "cohort_bucket":
                return BuildCohortBucketFilter(termId, filterValue, perPage, offset, totalActiveSteps);
            case "tag":
                return BuildTagFilter(termId, filterValue, perPage, offset);
            case "stalled":
                return BuildStalledFilter(termId, filterValue, perPage, offset);
            case "deadline_risk":
                return await BuildDeadlineRiskFilter(termId, stepId, perPage, offset);
            case "velocity_bucket":
                return BuildVelocityBucketFilter(termId, filterValue, perPage, offset);
            case "trend_date":
                return BuildTrendDateFilter(termId, filterValue, perPage, offset);
            default:
                throw new InvalidFilterException("Invalid filter_type");
        }
    }

    private async Task<FilterQuerySet> BuildStepCompletedFilter(int termId, int stepId, int perPage, int offset)
    {
        var title = await _db.QueryOneAsync<string>("SELECT title FROM steps WHERE id = @stepId", new { stepId });
        const string body = @"
                FROM students st
                JOIN student_progress sp ON sp.student_id = st.id AND sp.step_id = @stepId AND sp.status IN ('completed', 'waived')
                WHERE st.term_id = @termId";
        return new FilterQuerySet
        {
            Title = $"Students who completed {(string.IsNullOrEmpty(title) ? "this step" : title)}",
            Params = new { stepId, termId, perPage, offset },
            CountParams = new { stepId, termId },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count{body}",
        };
    }

    private async Task<FilterQuerySet> BuildStepNotCompletedFilter(int termId, int stepId, int perPage, int offset)
    {
        var title = await _db.QueryOneAsync<string>("SELECT title FROM steps WHERE id = @stepId", new { stepId });
        const string body = @"
                FROM students st
                LEFT JOIN student_progress sp ON sp.student_id = st.id AND sp.step_id = @stepId AND sp.status IN ('completed', 'waived')
                WHERE st.term_id = @termId AND sp.student_id IS NULL";
        return new FilterQuerySet
        {
            Title = $"Students who haven't completed {(string.IsNullOrEmpty(title) ? "this step" : title)}",
            Params = new { stepId, termId, perPage, offset },
            CountParams = new { stepId, termId },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count{body}",
        };
    }

    private FilterQuerySet BuildCohortBucketFilter(int termId, string? filterValue, int perPage, int offset, int totalActiveSteps)
    {
        // '0%' uses a dedicated HAVING = 0; handle it first so the lo/hi switch below
        // never assigns dead values for the '0%' case.
        if (filterValue == "0%")
        {
            const string bucketCondition = "HAVING COALESCE(SUM(CASE WHEN sp.status IN ('completed', 'waived') THEN 1 ELSE 0 END), 0) = 0";
            // Grouping on the display columns as well as st.id yields the same groups as
            // GROUP BY st.id alone (they are functionally dependent on the PK), so the shared
            // body drives both the page rows and the count of matching students.
            var body = $@"
                    FROM students st
                    LEFT JOIN student_progress sp ON sp.student_id = st.id
                      AND sp.step_id IN (SELECT id FROM steps WHERE {QueryHelpers.ActiveStepFilter} AND term_id = @termId)
                    WHERE st.term_id = @termId
                    GROUP BY st.id, st.display_name, st.email, st.emplid
                    {bucketCondition}";
            return new FilterQuerySet
            {
                Title = $"Students at {filterValue} completion",
                Params = new { termId, perPage, offset },
                CountParams = new { termId },
                StudentQuery = $@"
                    SELECT st.id, st.display_name, st.email, st.emplid{body}
                    ORDER BY st.display_name
                    OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
                CountQuery = $"SELECT COUNT(*) as count FROM (SELECT st.id{body}) sub",
            };
        }

        // lo is the strict lower bound (> @lo); hi is the inclusive upper bound (<= @hi).
        // These match the CohortSummary CASE upper bounds so each drilldown bucket covers
        // exactly the students counted in the corresponding summary row.
        double lo, hi;
        switch (filterValue)
        {
            case "1-25%": lo = 0; hi = 0.25; break;
            case "26-50%": lo = 0.25; hi = 0.50; break;
            case "51-75%": lo = 0.50; hi = 0.75; break;
            case "76-100%": lo = 0.75; hi = 1.0; break;
            default:
                throw new InvalidFilterException("Invalid cohort_bucket value");
        }

        var divisor = totalActiveSteps > 0 ? totalActiveSteps : 1;
        var rangeCondition = @"HAVING (CAST(SUM(CASE WHEN sp.status IN ('completed', 'waived') THEN 1 ELSE 0 END) AS float) / @divisor) > @lo
                 AND (CAST(SUM(CASE WHEN sp.status IN ('completed', 'waived') THEN 1 ELSE 0 END) AS float) / @divisor) <= @hi";
        // Grouping on the display columns as well as st.id yields the same groups as
        // GROUP BY st.id alone (functionally dependent on the PK), so one body drives both.
        var rangeBody = $@"
                FROM students st
                LEFT JOIN student_progress sp ON sp.student_id = st.id
                  AND sp.step_id IN (SELECT id FROM steps WHERE {QueryHelpers.ActiveStepFilter} AND term_id = @termId)
                WHERE st.term_id = @termId
                GROUP BY st.id, st.display_name, st.email, st.emplid
                {rangeCondition}";
        return new FilterQuerySet
        {
            Title = $"Students at {filterValue} completion",
            Params = new { termId, lo, hi, divisor, perPage, offset },
            CountParams = new { termId, lo, hi, divisor },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{rangeBody}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count FROM (SELECT st.id{rangeBody}) sub",
        };
    }

    private FilterQuerySet BuildTagFilter(int termId, string? filterValue, int perPage, int offset)
    {
        var tagPattern = $"%{filterValue}%";
        var fv = filterValue ?? "";
        var title = fv.Length > 0
            ? $"{char.ToUpperInvariant(fv[0])}{fv[1..]} students"
            : " students";
        const string body = @"
                FROM students st
                WHERE st.term_id = @termId AND st.tags LIKE @tagPattern";
        return new FilterQuerySet
        {
            Title = title,
            Params = new { termId, tagPattern, perPage, offset },
            CountParams = new { termId, tagPattern },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count{body}",
        };
    }

    private FilterQuerySet BuildStalledFilter(int termId, string? filterValue, int perPage, int offset)
    {
        int minDays, maxDays;
        switch (filterValue)
        {
            case "7-14 days": minDays = 7; maxDays = 14; break;
            case "2-4 weeks": minDays = 15; maxDays = 28; break;
            case "1-3 months": minDays = 29; maxDays = 90; break;
            case "3+ months": minDays = 91; maxDays = 99999; break;
            default:
                throw new InvalidFilterException("Invalid stalled value");
        }

        // seconds/86400, not DATEDIFF(day): see CompletionVelocity comment for why.
        // Grouping on the display columns as well as st.id/st.created_at yields the same
        // groups as GROUP BY st.id, st.created_at (functionally dependent on the PK), so
        // one body drives both the page rows and the count.
        const string body = @"
                FROM students st
                LEFT JOIN student_progress sp ON sp.student_id = st.id
                WHERE st.term_id = @termId
                GROUP BY st.id, st.display_name, st.email, st.emplid, st.created_at
                HAVING (
                  COUNT(CASE WHEN sp.status = 'completed' THEN 1 END) = 0
                  AND @minDays <= DATEDIFF(second, st.created_at, SYSUTCDATETIME()) / 86400
                  AND DATEDIFF(second, st.created_at, SYSUTCDATETIME()) / 86400 <= @maxDays
                ) OR (
                  MAX(CASE WHEN sp.status = 'completed' THEN sp.completed_at END) IS NOT NULL
                  AND @minDays <= DATEDIFF(second, MAX(CASE WHEN sp.status = 'completed' THEN sp.completed_at END), SYSUTCDATETIME()) / 86400
                  AND DATEDIFF(second, MAX(CASE WHEN sp.status = 'completed' THEN sp.completed_at END), SYSUTCDATETIME()) / 86400 <= @maxDays
                )";
        return new FilterQuerySet
        {
            Title = $"Students stalled {filterValue}",
            Params = new { termId, minDays, maxDays, perPage, offset },
            CountParams = new { termId, minDays, maxDays },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count FROM (SELECT st.id{body}) sub",
        };
    }

    private async Task<FilterQuerySet> BuildDeadlineRiskFilter(int termId, int stepId, int perPage, int offset)
    {
        var title = await _db.QueryOneAsync<string>("SELECT title FROM steps WHERE id = @stepId", new { stepId });
        const string body = @"
                FROM students st
                LEFT JOIN student_progress sp ON sp.step_id = @stepId AND sp.student_id = st.id AND sp.status IN ('completed', 'waived')
                WHERE st.term_id = @termId AND sp.student_id IS NULL";
        return new FilterQuerySet
        {
            Title = $"At-risk students for {(string.IsNullOrEmpty(title) ? "this step" : title)}",
            Params = new { stepId, termId, perPage, offset },
            CountParams = new { stepId, termId },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count{body}",
        };
    }

    private FilterQuerySet BuildVelocityBucketFilter(int termId, string? filterValue, int perPage, int offset)
    {
        int minD, maxD;
        switch (filterValue)
        {
            case "1-3 days": minD = 0; maxD = 3; break;
            case "4-7 days": minD = 4; maxD = 7; break;
            case "1-2 weeks": minD = 8; maxD = 14; break;
            case "2-4 weeks": minD = 15; maxD = 28; break;
            case "4+ weeks": minD = 29; maxD = 99999; break;
            default:
                throw new InvalidFilterException("Invalid velocity_bucket value");
        }

        // seconds/86400, not DATEDIFF(day): see CompletionVelocity comment for why.
        const string body = @"
                FROM students st
                JOIN (
                  SELECT sp.student_id,
                    DATEDIFF(second, MIN(sp.completed_at), MAX(sp.completed_at)) / 86400 as days_elapsed
                  FROM student_progress sp
                  WHERE sp.status = 'completed'
                  GROUP BY sp.student_id
                ) vel ON vel.student_id = st.id AND vel.days_elapsed >= @minD AND vel.days_elapsed <= @maxD
                WHERE st.term_id = @termId";
        return new FilterQuerySet
        {
            Title = $"Students completing in {filterValue}",
            Params = new { termId, minD, maxD, perPage, offset },
            CountParams = new { termId, minD, maxD },
            StudentQuery = $@"
                SELECT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(*) as count{body}",
        };
    }

    private FilterQuerySet BuildTrendDateFilter(int termId, string? filterValue, int perPage, int offset)
    {
        // Format the date as "MMM d, yyyy" (e.g. "Jan 5, 2026") to match the labels
        // the trend query groups on.
        var dateStr = filterValue ?? "";
        if (DateTime.TryParse(filterValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            dateStr = parsed.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        const string body = @"
                FROM students st
                JOIN student_progress sp ON sp.student_id = st.id AND CAST(sp.completed_at AS date) = CAST(@filterValue AS date) AND sp.status IN ('completed', 'waived')
                JOIN steps s ON s.id = sp.step_id AND COALESCE(s.is_optional, 0) = 0
                WHERE st.term_id = @termId";
        return new FilterQuerySet
        {
            Title = $"Completions on {dateStr}",
            Params = new { filterValue, termId, perPage, offset },
            CountParams = new { filterValue, termId },
            StudentQuery = $@"
                SELECT DISTINCT st.id, st.display_name, st.email, st.emplid{body}
                ORDER BY st.display_name
                OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            CountQuery = $"SELECT COUNT(DISTINCT st.id) as count{body}",
        };
    }

    // ─── Helpers / DTOs ──────────────────────────────────────

    // An unparseable value or 0 falls back to the default. Clamped to 1..3650 — a
    // negative or huge value would overflow DATEADD/int negation and 500.
    private static int ParseDaysDefault(string? raw, int fallback)
    {
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0)
            return Math.Min(v, 3650);
        return fallback;
    }

    private sealed class FilterQuerySet
    {
        public string Title { get; set; } = "";
        public string StudentQuery { get; set; } = "";
        public string CountQuery { get; set; } = "";
        public object Params { get; set; } = new { };
        public object CountParams { get; set; } = new { };
    }

    private sealed class InvalidFilterException : Exception
    {
        public InvalidFilterException(string message) : base(message) { }
    }

    private sealed class ExportStep
    {
        public int id { get; set; }
        public string title { get; set; } = "";
    }

    private sealed class ExportStudent
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
    }

    private sealed class ExportProgressRow
    {
        public string student_id { get; set; } = "";
        public int step_id { get; set; }
        public string? status { get; set; }
    }

    private sealed class StepCompletionRow
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public int sort_order { get; set; }
        public int completed_count { get; set; }
    }

    private sealed class CompletionTrendRow
    {
        public string? date { get; set; }
        public int completions { get; set; }
    }

    private sealed class CohortBucketRow
    {
        public string bucket { get; set; } = "";
        public int student_count { get; set; }
    }

    private sealed class DeadlineRiskStep
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public string? deadline_date { get; set; }
        public int total_students { get; set; }
        public int at_risk_count { get; set; }
    }

    private sealed class DeadlineRiskStudent
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
    }

    private sealed class StalledStudentRow
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public DateTime? last_completion_date { get; set; }
        public int completed_count { get; set; }
    }

    private sealed class StalledBucketCountRow
    {
        public string? bucket { get; set; }
        public int student_count { get; set; }
    }

    private sealed class DrilldownStudent
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public string? emplid { get; set; }
    }

    private sealed class CompletionCountRow
    {
        public string student_id { get; set; } = "";
        public int done { get; set; }
    }

    // One row of per-tag conditional aggregates from CohortComparison's single pass:
    // count{N}/avg{N} correspond positionally to the fixed tags array in that method.
    private sealed class CohortComparisonRow
    {
        public int count0 { get; set; }
        public int count1 { get; set; }
        public int count2 { get; set; }
        public int count3 { get; set; }
        public int count4 { get; set; }
        public int count5 { get; set; }
        public int count6 { get; set; }
        public int count7 { get; set; }
        public double? avg0 { get; set; }
        public double? avg1 { get; set; }
        public double? avg2 { get; set; }
        public double? avg3 { get; set; }
        public double? avg4 { get; set; }
        public double? avg5 { get; set; }
        public double? avg6 { get; set; }
        public double? avg7 { get; set; }
    }

    private sealed class CohortComparisonItem
    {
        public string tag { get; set; } = "";
        public int student_count { get; set; }
        public int avg_completion_pct { get; set; }
    }

    private sealed class VelocityRow
    {
        public string id { get; set; } = "";
        public int? days_elapsed { get; set; }
    }
}
