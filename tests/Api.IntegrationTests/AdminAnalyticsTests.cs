using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the AdminAnalytics area:
//   GET /api/admin/stats
//   GET /api/admin/export/progress
//   GET /api/admin/analytics/{step-completion,completion-trend,bottlenecks,
//        cohort-summary,deadline-risk,stalled-students,cohort-comparison,
//        completion-velocity}
//   GET /api/admin/analytics/students  (drilldown)
//
// Endpoints: Api/Controllers/Admin/AnalyticsController.cs. All endpoints are gated by
// [AdminAuth] (any authenticated admin; no role restriction), so the auth gate
// here is the 401 path. Shared-DB rules: no exact global counts, only presence
// / >= bounds / documented-shape assertions on the deterministic seed.
[Collection("api")]
public class AdminAnalyticsTests
{
    private readonly WebAppFixture _fx;

    public AdminAnalyticsTests(WebAppFixture fx) => _fx = fx;

    // ─── Stats ───────────────────────────────────────────────

    [Fact]
    public async Task Stats_returns_documented_shape()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/stats?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // 50 seeded students in Fall 2026 + students other tests create via dev-login.
        Assert.True(body.GetProperty("totalStudents").GetInt32() >= 50);
        // 22 active Fall-2026 steps.
        Assert.True(body.GetProperty("totalActiveSteps").GetInt32() >= 22);

        var pct = body.GetProperty("avgCompletionPercent").GetInt32();
        Assert.InRange(pct, 0, 100);
    }

    [Fact]
    public async Task Stats_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ─── Export ──────────────────────────────────────────────

    [Fact]
    public async Task ExportProgress_returns_csv_with_header_row()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/export/progress?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);

        var csv = await res.Content.ReadAsStringAsync();
        var firstLine = csv.Split('\n', StringSplitOptions.None)[0];

        // Header row is quoted, formula-injection-sanitized cells.
        Assert.StartsWith("\"Student Name\",\"Email\"", firstLine);
        Assert.Contains("\"Total Complete\"", firstLine);
        Assert.Contains("\"Percentage\"", firstLine);

        // At least the header + the 50 seeded students.
        var dataLines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(dataLines.Length >= 51);
    }

    [Fact]
    public async Task ExportProgress_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/export/progress?term_id=1");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── analytics/step-completion ───────────────────────────

    [Fact]
    public async Task StepCompletion_returns_steps_and_totalStudents()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/step-completion?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var total = body.GetProperty("totalStudents").GetInt32();
        Assert.True(total >= 50);

        var steps = body.GetProperty("steps");
        Assert.True(steps.GetArrayLength() >= 22);

        var first = steps[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("title", out _));
        Assert.True(first.TryGetProperty("sort_order", out _));
        // completed_count <= total_students, and total_students mirrors the top-level total.
        Assert.True(first.GetProperty("completed_count").GetInt32() >= 0);
        Assert.Equal(total, first.GetProperty("total_students").GetInt32());
    }

    // ─── analytics/completion-trend ──────────────────────────

    [Fact]
    public async Task CompletionTrend_returns_array_of_date_and_completions()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/completion-trend?term_id=1&days=365");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        // Seed completions land within the last ~60 days, so a 365-day window has rows.
        Assert.True(body.GetArrayLength() >= 1);
        var row = body[0];
        // date is a CONVERT(varchar(10), ... , 23) yyyy-MM-dd string.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", row.GetProperty("date").GetString()!);
        Assert.True(row.GetProperty("completions").GetInt32() >= 1);
    }

    // ─── analytics/bottlenecks ───────────────────────────────

    [Fact]
    public async Task Bottlenecks_returns_at_most_five_steps_with_completion_pct()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/bottlenecks?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var total = body.GetProperty("totalStudents").GetInt32();
        Assert.True(total >= 50);

        var steps = body.GetProperty("steps");
        Assert.True(steps.GetArrayLength() <= 5);
        Assert.True(steps.GetArrayLength() >= 1);

        var first = steps[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("title", out _));
        Assert.True(first.TryGetProperty("completed_count", out _));
        Assert.Equal(total, first.GetProperty("total_students").GetInt32());
        Assert.InRange(first.GetProperty("completion_pct").GetInt32(), 0, 100);
    }

    // ─── analytics/cohort-summary ────────────────────────────

    [Fact]
    public async Task CohortSummary_returns_buckets_with_student_counts()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/cohort-summary?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() >= 1);

        var validBuckets = new[] { "0%", "1-25%", "26-50%", "51-75%", "76-100%" };
        var sum = 0;
        foreach (var row in body.EnumerateArray())
        {
            Assert.Contains(row.GetProperty("bucket").GetString(), validBuckets);
            var count = row.GetProperty("student_count").GetInt32();
            Assert.True(count >= 0);
            sum += count;
        }
        // Every Fall-2026 student lands in exactly one bucket.
        Assert.True(sum >= 50);
    }

    // ─── analytics/deadline-risk ─────────────────────────────

    [Fact]
    public async Task DeadlineRisk_returns_array_shape()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/deadline-risk?term_id=1&days=3650");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        // Whether or not any seeded step has a future deadline in-window, each row
        // (if present) carries the documented shape.
        foreach (var row in body.EnumerateArray())
        {
            Assert.True(row.TryGetProperty("id", out _));
            Assert.True(row.TryGetProperty("title", out _));
            Assert.True(row.TryGetProperty("deadline_date", out _));
            Assert.True(row.GetProperty("total_students").GetInt32() >= 0);
            Assert.True(row.GetProperty("at_risk_count").GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Array, row.GetProperty("students").ValueKind);
        }
    }

    // ─── analytics/stalled-students ──────────────────────────

    [Fact]
    public async Task StalledStudents_returns_rows_with_iso_utc_last_completion()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/stalled-students?term_id=1&days=7");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        // The seed gives some students 0 completions, so the stalled list is non-empty.
        Assert.True(body.GetArrayLength() >= 1);

        var sawNonNullTimestamp = false;
        foreach (var row in body.EnumerateArray())
        {
            Assert.True(row.TryGetProperty("id", out _));
            Assert.True(row.GetProperty("completed_count").GetInt32() >= 0);
            Assert.True(row.GetProperty("total_steps").GetInt32() >= 22);

            var lastCompletion = row.GetProperty("last_completion_date");
            if (lastCompletion.ValueKind == JsonValueKind.String)
            {
                sawNonNullTimestamp = true;
                var ts = lastCompletion.GetString()!;
                // Corrected contract: ISO-8601 UTC ending in 'Z'.
                Assert.EndsWith("Z", ts);
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", ts);
            }
        }
        // A stalled student who completed >7 days ago carries a non-null timestamp.
        Assert.True(sawNonNullTimestamp);
    }

    // ─── analytics/cohort-comparison ─────────────────────────

    [Fact]
    public async Task CohortComparison_returns_tagged_cohorts_sorted_desc()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/cohort-comparison?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        // Seed tags include first-gen, honors, eop, athlete, veteran.
        Assert.True(body.GetArrayLength() >= 1);

        var knownTags = new[] { "freshman", "transfer", "first-gen", "honors", "athlete", "eop", "veteran", "out-of-state" };
        var prevCount = int.MaxValue;
        var sawFirstGen = false;
        foreach (var row in body.EnumerateArray())
        {
            var tag = row.GetProperty("tag").GetString();
            Assert.Contains(tag, knownTags);
            if (tag == "first-gen") sawFirstGen = true;

            var count = row.GetProperty("student_count").GetInt32();
            Assert.True(count > 0); // only cohorts with members are emitted
            Assert.InRange(row.GetProperty("avg_completion_pct").GetInt32(), 0, 100);

            // Sorted by student_count descending.
            Assert.True(count <= prevCount);
            prevCount = count;
        }
        Assert.True(sawFirstGen);
    }

    // ─── analytics/completion-velocity ───────────────────────

    [Fact]
    public async Task CompletionVelocity_returns_all_five_buckets_in_order()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/completion-velocity?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        var expected = new[] { "1-3 days", "4-7 days", "1-2 weeks", "2-4 weeks", "4+ weeks" };
        Assert.Equal(expected.Length, body.GetArrayLength());
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], body[i].GetProperty("bucket").GetString());
            Assert.True(body[i].GetProperty("student_count").GetInt32() >= 0);
        }
    }

    // ─── analytics/students (drilldown) ──────────────────────

    [Fact]
    public async Task Drilldown_tag_first_gen_returns_those_students()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=tag&filter_value=first-gen");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("First-gen students", body.GetProperty("title").GetString());
        Assert.Equal(50, body.GetProperty("per_page").GetInt32());
        Assert.Equal(1, body.GetProperty("page").GetInt32());

        var total = body.GetProperty("total").GetInt32();
        Assert.True(total >= 1); // seed has first-gen students in Fall 2026

        var students = body.GetProperty("students");
        Assert.True(students.GetArrayLength() >= 1);

        // seed-student-001 has tag "first-gen" and emplid 001000000.
        var sawSeed001 = false;
        foreach (var s in students.EnumerateArray())
        {
            Assert.True(s.TryGetProperty("id", out _));
            Assert.True(s.TryGetProperty("display_name", out _));
            Assert.True(s.TryGetProperty("email", out _));
            Assert.True(s.TryGetProperty("emplid", out _));
            Assert.True(s.GetProperty("completed_count").GetInt32() >= 0);
            Assert.True(s.GetProperty("total_steps").GetInt32() >= 22);
            Assert.InRange(s.GetProperty("completion_pct").GetInt32(), 0, 100);

            if (s.GetProperty("id").GetString() == "seed-student-001")
            {
                sawSeed001 = true;
                Assert.Equal("001000000", s.GetProperty("emplid").GetString());
            }
        }
        Assert.True(sawSeed001);
    }

    [Fact]
    public async Task Drilldown_cohort_bucket_zero_pct_returns_students()
    {
        // Second filter_type: cohort_bucket "0%" -> students with no completions.
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=cohort_bucket&filter_value=0%25");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Students at 0% completion", body.GetProperty("title").GetString());

        // The seed gives some students 0 steps completed.
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        foreach (var s in body.GetProperty("students").EnumerateArray())
            Assert.Equal(0, s.GetProperty("completed_count").GetInt32());
    }

    [Fact]
    public async Task Drilldown_missing_term_id_is_400_with_exact_message()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?filter_type=tag&filter_value=first-gen");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("term_id and filter_type are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Drilldown_missing_filter_type_is_400_with_exact_message()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/analytics/students?term_id=1");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("term_id and filter_type are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Drilldown_unknown_filter_type_is_400_with_exact_message()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=not_a_real_filter");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid filter_type", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Drilldown_invalid_cohort_bucket_value_is_400_with_exact_message()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=cohort_bucket&filter_value=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid cohort_bucket value", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Drilldown_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=tag&filter_value=first-gen");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── Drilldown: the remaining filter_types ───────────────
    // Only `tag` and `cohort_bucket` were covered. Each builder below produces its own
    // hand-written SQL + Title; these assert the SQL executes (200), the documented
    // shape holds, and the per-builder Title string is exact. Student counts use >= 0 /
    // shape checks (shared-DB rules); the asserts that matter are that the query runs
    // and the Title is correct.

    private async Task<int> FirstActiveStepIdAsync()
    {
        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        return steps[0].GetProperty("id").GetInt32();
    }

    private static void AssertDrilldownShape(JsonElement body, string expectedTitle)
    {
        Assert.Equal(expectedTitle, body.GetProperty("title").GetString());
        Assert.True(body.GetProperty("total").GetInt32() >= 0);
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("per_page").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("students").ValueKind);
        foreach (var s in body.GetProperty("students").EnumerateArray())
        {
            Assert.True(s.TryGetProperty("id", out _));
            Assert.True(s.TryGetProperty("display_name", out _));
            Assert.True(s.GetProperty("completed_count").GetInt32() >= 0);
            Assert.True(s.GetProperty("total_steps").GetInt32() >= 22);
            Assert.InRange(s.GetProperty("completion_pct").GetInt32(), 0, 100);
        }
    }

    [Fact]
    public async Task Drilldown_step_completed_returns_students_with_step_title()
    {
        var stepId = await FirstActiveStepIdAsync();
        var res = await _fx.Admin().GetAsync(
            $"/api/admin/analytics/students?term_id=1&filter_type=step_completed&filter_value={stepId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Students who completed ", body.GetProperty("title").GetString());
        AssertDrilldownShape(body, body.GetProperty("title").GetString()!);
    }

    [Fact]
    public async Task Drilldown_step_not_completed_returns_students_with_step_title()
    {
        var stepId = await FirstActiveStepIdAsync();
        var res = await _fx.Admin().GetAsync(
            $"/api/admin/analytics/students?term_id=1&filter_type=step_not_completed&filter_value={stepId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Students who haven't completed ", body.GetProperty("title").GetString());
        AssertDrilldownShape(body, body.GetProperty("title").GetString()!);
    }

    [Fact]
    public async Task Drilldown_stalled_bucket_returns_students()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=stalled&filter_value=7-14%20days");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        AssertDrilldownShape(body, "Students stalled 7-14 days");
    }

    [Fact]
    public async Task Drilldown_velocity_bucket_returns_students()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=velocity_bucket&filter_value=1-3%20days");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        AssertDrilldownShape(body, "Students completing in 1-3 days");
    }

    [Fact]
    public async Task Drilldown_trend_date_returns_completions_with_formatted_title()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=trend_date&filter_value=2026-01-05");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // Label is the en-US short date format: abbreviated month, numeric day, numeric year.
        AssertDrilldownShape(body, "Completions on Jan 5, 2026");
    }

    [Fact]
    public async Task Drilldown_deadline_risk_returns_at_risk_students_with_step_title()
    {
        var stepId = await FirstActiveStepIdAsync();
        var res = await _fx.Admin().GetAsync(
            $"/api/admin/analytics/students?term_id=1&filter_type=deadline_risk&filter_value={stepId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("At-risk students for ", body.GetProperty("title").GetString());
        AssertDrilldownShape(body, body.GetProperty("title").GetString()!);
    }

    // The numeric/date pre-validation guards turn a SQL-conversion 500 into a 400.

    [Fact]
    public async Task Drilldown_step_completed_with_non_numeric_value_is_400()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=step_completed&filter_value=abc");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filter_value must be a step id", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Drilldown_trend_date_with_non_date_value_is_400()
    {
        var res = await _fx.Admin().GetAsync(
            "/api/admin/analytics/students?term_id=1&filter_type=trend_date&filter_value=notadate");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("filter_value must be a date", body.GetProperty("error").GetString());
    }

    // ─── deadline-risk: waived students are excluded ─────────
    // AnalyticsController.DeadlineRisk excludes status IN ('completed','waived') from
    // both at_risk_count and the per-step students[]. The shape-only test above can't
    // catch a regression that flips 'waived' back into the at-risk set. This waives an
    // active in-window step for a currently-at-risk student and asserts they drop out of
    // that step's students[] AND at_risk_count falls by exactly 1 (relative compare per
    // shared-DB rules — no reliance on an absolute count).

    [Fact]
    public async Task DeadlineRisk_excludes_a_waived_student_from_step()
    {
        // Default 14-day window has a seeded future-deadline step ("Register for
        // Orientation", 2026-07-01) for term 1.
        var before = await (await _fx.Admin().GetAsync("/api/admin/analytics/deadline-risk?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Pick any in-window step that has at least one at-risk student to waive.
        JsonElement targetStep = default;
        string? studentId = null;
        foreach (var step in before.EnumerateArray())
        {
            var students = step.GetProperty("students");
            if (students.GetArrayLength() > 0)
            {
                targetStep = step;
                studentId = students[0].GetProperty("id").GetString();
                break;
            }
        }

        Assert.NotNull(studentId); // seed must surface at least one at-risk student in-window
        var stepId = targetStep.GetProperty("id").GetInt32();
        var beforeAtRisk = targetStep.GetProperty("at_risk_count").GetInt32();

        // Waive that step for the at-risk student via the admin progress endpoint.
        var waive = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete",
            new { status = "waived", note = "deadline-risk exclusion test" });
        Assert.Equal(HttpStatusCode.OK, waive.StatusCode);

        // Re-fetch: the waived student must be gone from this step's students[] and the
        // at_risk_count must have dropped by exactly 1.
        var after = await (await _fx.Admin().GetAsync("/api/admin/analytics/deadline-risk?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var found = false;
        foreach (var step in after.EnumerateArray())
        {
            if (step.GetProperty("id").GetInt32() != stepId) continue;
            found = true;

            foreach (var s in step.GetProperty("students").EnumerateArray())
                Assert.NotEqual(studentId, s.GetProperty("id").GetString());

            Assert.Equal(beforeAtRisk - 1, step.GetProperty("at_risk_count").GetInt32());
        }
        Assert.True(found); // step is still in-window, just with one fewer at-risk student
    }
}
