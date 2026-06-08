using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Api.IntegrationTests;

// Integration tests for the AdminStudents area:
//   GET    /api/admin/students                                   (list: pagination/search/sort/overdue_only)
//   GET    /api/admin/students/{id}/progress
//   POST   /api/admin/students/{id}/steps/{stepId}/complete
//   DELETE /api/admin/students/{id}/steps/{stepId}/complete
//   PUT    /api/admin/students/{id}/profile
//   PUT    /api/admin/students/{id}/tags
//   GET    /api/admin/audit
//
// Shared-DB safe: reads target a seeded student (seed-student-001); writes target a
// freshly dev-logged-in student with a unique email so other tests are unaffected.
[Collection("api")]
public class AdminStudentsTests
{
    private readonly WebAppFixture _fx;

    public AdminStudentsTests(WebAppFixture fx) => _fx = fx;

    private static readonly Regex IsoUtcZ =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    // Resolve a real numeric step id for the active term (Fall 2026 / term 1).
    private async Task<int> FirstStepIdAsync()
    {
        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(steps.GetArrayLength() >= 1);
        return steps[0].GetProperty("id").GetInt32();
    }

    // Create a fresh student (via dev-login) and return its id, so writes never touch
    // rows other tests rely on. dev-login returns the new student's id in student.id.
    private async Task<string> NewStudentIdAsync()
    {
        var email = $"u{Guid.NewGuid():N}@t.edu";
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "Temp Student", email });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("student").GetProperty("id").GetString()!;
    }

    // ─── List ─────────────────────────────────────────────────

    [Fact]
    public async Task List_returns_pagination_envelope_and_seeded_total()
    {
        var body = await (await _fx.Admin().GetAsync("/api/admin/students?page=1&per_page=5"))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Envelope: students[], total, page, per_page.
        Assert.Equal(JsonValueKind.Array, body.GetProperty("students").ValueKind);
        Assert.True(body.GetProperty("total").GetInt32() >= 50); // 50 seeded, others may add more
        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(5, body.GetProperty("per_page").GetInt32());
        Assert.True(body.GetProperty("students").GetArrayLength() <= 5);

        // Row shape includes progress-count columns.
        var first = body.GetProperty("students")[0];
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("completed_steps", out _));
        Assert.True(first.TryGetProperty("overdue_step_count", out _));
    }

    [Fact]
    public async Task List_search_matches_seeded_emplid()
    {
        var body = await (await _fx.Admin().GetAsync("/api/admin/students?search=001000000"))
            .Content.ReadFromJsonAsync<JsonElement>();

        var students = body.GetProperty("students");
        Assert.True(students.GetArrayLength() >= 1);
        // seed-student-001 has emplid 001000000 — search must surface it.
        var match = students.EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == "seed-student-001");
        Assert.Equal("001000000", match.GetProperty("emplid").GetString());
    }

    [Fact]
    public async Task List_term_filter_restricts_to_term_one()
    {
        var body = await (await _fx.Admin().GetAsync("/api/admin/students?term_id=1&per_page=100"))
            .Content.ReadFromJsonAsync<JsonElement>();

        foreach (var s in body.GetProperty("students").EnumerateArray())
            Assert.Equal(1, s.GetProperty("term_id").GetInt32());
    }

    [Fact]
    public async Task List_sort_name_asc_and_desc_are_reverses()
    {
        // Robust against SQL collation specifics: name_desc must be the reverse
        // of name_asc over the same page size.
        var asc = (await (await _fx.Admin().GetAsync("/api/admin/students?sort=name_asc&per_page=100"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("students").EnumerateArray()
            .Select(s => s.GetProperty("display_name").GetString() ?? "")
            .ToList();
        var desc = (await (await _fx.Admin().GetAsync("/api/admin/students?sort=name_desc&per_page=100"))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("students").EnumerateArray()
            .Select(s => s.GetProperty("display_name").GetString() ?? "")
            .ToList();

        Assert.Equal(asc.Count, desc.Count);
        Assert.True(asc.Count >= 2);
        // First of asc should be the last of desc (boundary check, collation-agnostic).
        Assert.Equal(asc.First(), desc.Last());
        Assert.Equal(asc.Last(), desc.First());
    }

    [Fact]
    public async Task List_overdue_only_returns_only_students_with_overdue_steps()
    {
        var body = await (await _fx.Admin().GetAsync("/api/admin/students?overdue_only=1&per_page=100"))
            .Content.ReadFromJsonAsync<JsonElement>();

        foreach (var s in body.GetProperty("students").EnumerateArray())
            Assert.True(s.GetProperty("overdue_step_count").GetInt32() > 0);
    }

    [Fact]
    public async Task List_requires_auth()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/students");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── GET {id}/progress ─────────────────────────────────────

    [Fact]
    public async Task Progress_for_seed_student_returns_tags_and_progress()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/students/seed-student-001/progress");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var student = body.GetProperty("student");
        Assert.Equal("seed-student-001", student.GetProperty("id").GetString());
        Assert.Equal("001000000", student.GetProperty("emplid").GetString());

        // created_at must be ISO-8601 UTC ending in Z (corrected contract).
        Assert.Matches(IsoUtcZ, student.GetProperty("created_at").GetString()!);

        // Manual tag "first-gen"; derived "freshman" (First-Time Freshman),
        // "in-state" (In-State), "major:business-administration"; merged = union.
        var manual = body.GetProperty("manualTags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("first-gen", manual);

        var derived = body.GetProperty("derivedTags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("freshman", derived);
        Assert.Contains("in-state", derived);
        Assert.Contains("major:business-administration", derived);

        var merged = body.GetProperty("mergedTags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("first-gen", merged);
        Assert.Contains("freshman", merged);

        // progress is an array; each row exposes step_id/status/title.
        Assert.Equal(JsonValueKind.Array, body.GetProperty("progress").ValueKind);
    }

    [Fact]
    public async Task Progress_404_for_unknown_student()
    {
        var res = await _fx.Admin().GetAsync($"/api/admin/students/{Guid.NewGuid():N}/progress");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Student not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Progress_requires_auth()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/students/seed-student-001/progress");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── POST/DELETE complete ──────────────────────────────────

    [Fact]
    public async Task Complete_creates_then_repeat_is_noop()
    {
        var studentId = await NewStudentIdAsync();
        var stepId = await FirstStepIdAsync();

        var res1 = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        var b1 = await res1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(b1.GetProperty("success").GetBoolean());
        Assert.Equal(studentId, b1.GetProperty("studentId").GetString());
        Assert.Equal(stepId, b1.GetProperty("stepId").GetInt32());
        Assert.Equal("completed", b1.GetProperty("status").GetString());
        Assert.Equal("created", b1.GetProperty("result").GetString());
        // completedAt is ISO-8601 UTC ending in Z.
        Assert.Matches(IsoUtcZ, b1.GetProperty("completedAt").GetString()!);

        // Same request again with no changed fields -> noop.
        var res2 = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var b2 = await res2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("noop", b2.GetProperty("result").GetString());
        Assert.Equal("completed", b2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Complete_with_waived_status_records_waived()
    {
        var studentId = await NewStudentIdAsync();
        var stepId = await FirstStepIdAsync();

        var res = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete",
            new { status = "waived", note = "manual waive" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("waived", b.GetProperty("status").GetString());
        Assert.Equal("created", b.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Complete_404_for_unknown_student()
    {
        var stepId = await FirstStepIdAsync();
        var res = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{Guid.NewGuid():N}/steps/{stepId}/complete", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Student not found", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_404_for_unknown_step()
    {
        var studentId = await NewStudentIdAsync();
        var res = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/999999/complete", new { });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Step not found", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Complete_requires_auth()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/students/seed-student-001/steps/1/complete", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Uncomplete_removes_then_repeat_is_noop()
    {
        var studentId = await NewStudentIdAsync();
        var stepId = await FirstStepIdAsync();

        // Create the progress first.
        var create = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // DELETE removes it -> updated.
        var del = await _fx.Admin().DeleteAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var bd = await del.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(bd.GetProperty("success").GetBoolean());
        Assert.Equal("updated", bd.GetProperty("result").GetString());
        Assert.Equal("not_completed", bd.GetProperty("status").GetString());

        // Deleting again -> noop (nothing there).
        var del2 = await _fx.Admin().DeleteAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete");
        Assert.Equal(HttpStatusCode.OK, del2.StatusCode);
        var bd2 = await del2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("noop", bd2.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Uncomplete_requires_auth()
    {
        var res = await _fx.Anonymous().DeleteAsync(
            "/api/admin/students/seed-student-001/steps/1/complete");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── PUT {id}/profile ──────────────────────────────────────

    [Fact]
    public async Task Profile_update_succeeds_and_is_reflected()
    {
        var studentId = await NewStudentIdAsync();
        var newName = $"Renamed {Guid.NewGuid():N}";
        var newMajor = $"Major-{Guid.NewGuid():N}";

        var res = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{studentId}/profile",
            new { display_name = newName, major = newMajor });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(b.GetProperty("success").GetBoolean());

        // Verify via /progress (returns the full student row).
        var prog = await (await _fx.Admin().GetAsync($"/api/admin/students/{studentId}/progress"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var student = prog.GetProperty("student");
        Assert.Equal(newName, student.GetProperty("display_name").GetString());
        Assert.Equal(newMajor, student.GetProperty("major").GetString());
    }

    [Fact]
    public async Task Profile_update_with_no_fields_is_400()
    {
        var studentId = await NewStudentIdAsync();
        var res = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{studentId}/profile", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No profile fields to update", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Profile_update_404_for_unknown_student()
    {
        var res = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{Guid.NewGuid():N}/profile",
            new { display_name = "x" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Student not found", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Profile_update_requires_auth()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync(
            "/api/admin/students/seed-student-001/profile", new { display_name = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── PUT {id}/tags ─────────────────────────────────────────

    [Fact]
    public async Task Tags_update_replaces_manual_tags()
    {
        var studentId = await NewStudentIdAsync();
        var unique = $"tag-{Guid.NewGuid():N}";

        var res = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{studentId}/tags",
            new { tags = new[] { unique, "athlete" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(b.GetProperty("success").GetBoolean());

        var prog = await (await _fx.Admin().GetAsync($"/api/admin/students/{studentId}/progress"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var manual = prog.GetProperty("manualTags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains(unique, manual);
        Assert.Contains("athlete", manual);
    }

    [Fact]
    public async Task Tags_update_404_for_unknown_student()
    {
        var res = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{Guid.NewGuid():N}/tags",
            new { tags = new[] { "x" } });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var b = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Student not found", b.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Tags_update_requires_auth()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync(
            "/api/admin/students/seed-student-001/tags", new { tags = new[] { "x" } });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ─── GET /api/admin/audit ──────────────────────────────────

    [Fact]
    public async Task Audit_records_a_write_and_filters_by_student()
    {
        var studentId = await NewStudentIdAsync();
        var stepId = await FirstStepIdAsync();

        // Generate an audited write on this student.
        var complete = await _fx.Admin().PostAsJsonAsync(
            $"/api/admin/students/{studentId}/steps/{stepId}/complete",
            new { note = "audit-probe" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var res = await _fx.Admin().GetAsync($"/api/admin/audit?studentId={studentId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        var logs = body.GetProperty("logs");
        Assert.True(logs.GetArrayLength() >= 1);

        var entry = logs[0];
        // studentId filter restricts to student_* entity types tied to this id.
        Assert.Equal(studentId, entry.GetProperty("entity_id").GetString());
        Assert.Equal("complete", entry.GetProperty("action").GetString());
        Assert.Equal("student_progress", entry.GetProperty("entity_type").GetString());
        // The actor is the seeded sysadmin admin.
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("changed_by").GetString()));
        // created_at is ISO-8601 UTC ending in Z.
        Assert.Matches(IsoUtcZ, entry.GetProperty("created_at").GetString()!);
    }

    [Fact]
    public async Task Audit_action_filter_returns_only_matching_action()
    {
        var studentId = await NewStudentIdAsync();

        // Produce a tags_update audit entry.
        var put = await _fx.Admin().PutAsJsonAsync(
            $"/api/admin/students/{studentId}/tags",
            new { tags = new[] { $"af-{Guid.NewGuid():N}" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var res = await _fx.Admin().GetAsync($"/api/admin/audit?studentId={studentId}&action=tags_update");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        foreach (var entry in body.GetProperty("logs").EnumerateArray())
            Assert.Equal("tags_update", entry.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Audit_requires_auth()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
