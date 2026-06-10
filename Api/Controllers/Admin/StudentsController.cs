using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin students + audit, ported from server/routes/admin/students.ts.
// Mounted under /api/admin (the old index.ts applies adminAuth to the whole group),
// so every action carries at least [AdminAuth]; the write endpoints add the same
// requireRole('admissions','admissions_editor','sysadmin') gate the old code used.
//
// Boring on purpose: hand-written T-SQL, explicit control flow, the shared
// services (Progress, Audit, StudentTags, QueryHelpers) are called, not recreated.
[ApiController]
[Route("api/admin")]
[AdminAuth]
public sealed class StudentsController : ControllerBase
{
    private readonly Db _db;

    public StudentsController(Db db)
    {
        _db = db;
    }

    // Request DTOs. Bodies bind case-insensitively; arbitrary-field bodies
    // (profile) bind as a dictionary so we can detect which keys were sent.
    public sealed record CompleteRequest(string? Note, string? Status);
    public sealed record UncompleteRequest(string? Note);
    public sealed record TagsRequest(List<string>? Tags);

    // ─── Student Progress ────────────────────────────────────

    // POST /api/admin/students/:studentId/steps/:stepId/complete (admissions+)
    [HttpPost("students/{studentId}/steps/{stepId}/complete")]
    [AdminAuth("admissions", "admissions_editor", "sysadmin")]
    public async Task<IActionResult> CompleteStep(string studentId, string stepId, [FromBody] CompleteRequest? body)
    {
        var step = ParseIntOrZero(stepId);
        var note = body?.Note;
        var status = body?.Status;
        var progressStatus = status == "waived" ? "waived" : "completed";

        var student = await _db.QueryOneAsync<StudentNameRow>(
            "SELECT id, display_name FROM students WHERE id = @studentId",
            new { studentId });
        if (student is null)
            return NotFound(new { error = "Student not found" });

        var stepRow = await _db.QueryOneAsync<StepInfoRow>(
            "SELECT id, title, step_key FROM steps WHERE id = @step",
            new { step });
        if (stepRow is null)
            return NotFound(new { error = "Step not found" });

        var progressChange = await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
        {
            StudentId = studentId,
            StepId = step,
            Status = progressStatus,
            Note = note,
        });

        if (progressChange.Error is not null)
            return BadRequest(new { error = progressChange.Error });

        if (progressChange.Result != "noop")
        {
            await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "student_progress", studentId,
                progressStatus == "waived" ? "waive" : "complete",
                new
                {
                    stepId = step,
                    stepKey = string.IsNullOrEmpty(stepRow.step_key) ? null : stepRow.step_key,
                    stepTitle = stepRow.title,
                    studentName = student.display_name,
                    note = string.IsNullOrEmpty(note) ? null : note,
                    result = progressChange.Result,
                });
        }

        return Ok(new
        {
            success = true,
            studentId,
            stepId = step,
            status = progressChange.Status,
            result = progressChange.Result,
            completedAt = progressChange.CompletedAt,
        });
    }

    // DELETE /api/admin/students/:studentId/steps/:stepId/complete (admissions+)
    [HttpDelete("students/{studentId}/steps/{stepId}/complete")]
    [AdminAuth("admissions", "admissions_editor", "sysadmin")]
    public async Task<IActionResult> UncompleteStep(string studentId, string stepId, [FromBody] UncompleteRequest? body)
    {
        var step = ParseIntOrZero(stepId);
        var note = body?.Note;

        var student = await _db.QueryOneAsync<StudentNameRow>(
            "SELECT display_name FROM students WHERE id = @studentId",
            new { studentId });
        var stepRow = await _db.QueryOneAsync<StepInfoRow>(
            "SELECT title, step_key FROM steps WHERE id = @step",
            new { step });

        var progressChange = await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
        {
            StudentId = studentId,
            StepId = step,
            Status = "not_completed",
            Note = note,
        });

        if (progressChange.Error is not null)
            return BadRequest(new { error = progressChange.Error });

        if (progressChange.Result != "noop")
        {
            await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "student_progress", studentId, "uncomplete",
                new
                {
                    stepId = step,
                    stepKey = string.IsNullOrEmpty(stepRow?.step_key) ? null : stepRow!.step_key,
                    stepTitle = stepRow?.title,
                    studentName = student?.display_name,
                    note = string.IsNullOrEmpty(note) ? null : note,
                    result = progressChange.Result,
                });
        }

        return Ok(new
        {
            success = true,
            studentId,
            stepId = step,
            result = progressChange.Result,
            status = progressChange.Status,
        });
    }

    // GET /api/admin/students/:studentId/progress
    [HttpGet("students/{studentId}/progress")]
    public async Task<IActionResult> GetStudentProgress(string studentId)
    {
        // Local row type: the shared Api.Models.Student lacks admit_term, but the
        // old endpoint returns this row verbatim, so admit_term must be in the JSON.
        var student = await _db.QueryOneAsync<StudentProgressDetailRow>(
            @"SELECT id, display_name, email, azure_id, tags, created_at, term_id,
                     emplid, preferred_name, phone, applicant_type, major, residency, admit_term, last_synced_at
              FROM students WHERE id = @studentId",
            new { studentId });
        if (student is null)
            return NotFound(new { error = "Student not found" });

        var progress = await _db.QueryAllAsync<ProgressRow>(
            @"SELECT sp.step_id, sp.completed_at, sp.status, sp.note, s.title
              FROM student_progress sp
              JOIN steps s ON s.id = sp.step_id
              WHERE sp.student_id = @studentId
              ORDER BY sp.step_id",
            new { studentId });

        return Ok(new
        {
            student,
            manualTags = StudentTags.Manual(student.tags),
            derivedTags = StudentTags.Derived(student.applicant_type, student.residency, student.major),
            mergedTags = StudentTags.Merged(student.tags, student.applicant_type, student.residency, student.major),
            progress,
        });
    }

    // PUT /api/admin/students/:studentId/profile (admissions+)
    [HttpPut("students/{studentId}/profile")]
    [AdminAuth("admissions", "admissions_editor", "sysadmin")]
    public async Task<IActionResult> UpdateProfile(string studentId, [FromBody] Dictionary<string, JsonElement>? body)
    {
        var requestBody = body ?? new Dictionary<string, JsonElement>();

        // Local row type so admit_term maps cleanly (not on the shared Student model);
        // only display_name and emplid are read for the audit entry below.
        var student = await _db.QueryOneAsync<StudentProfileRow>(
            @"SELECT id, display_name, email, emplid, preferred_name, phone,
                     applicant_type, major, residency, admit_term, last_synced_at
              FROM students WHERE id = @studentId",
            new { studentId });

        if (student is null)
            return NotFound(new { error = "Student not found" });

        var fields = new[]
        {
            "display_name", "email", "emplid", "preferred_name", "phone",
            "applicant_type", "major", "residency", "admit_term", "last_synced_at",
        };

        var updates = new List<string>();
        var parameters = new Dictionary<string, object?>();
        var index = 0;

        foreach (var field in fields)
        {
            // Match the old `req.body[field] !== undefined` presence check.
            if (TryGetField(requestBody, field, out var element))
            {
                var paramName = "p" + index;
                updates.Add($"{field} = @{paramName}");
                // `req.body[field] || null`: falsy (null, "", 0, false) -> null.
                parameters[paramName] = CoerceTruthy(element);
                index++;
            }
        }

        if (updates.Count == 0)
            return BadRequest(new { error = "No profile fields to update" });

        parameters["studentId"] = studentId;
        await _db.ExecuteAsync(
            $"UPDATE students SET {string.Join(", ", updates)} WHERE id = @studentId",
            parameters);

        // emplid in the audit: the sent value if present, else the existing one.
        object? auditEmplid = TryGetField(requestBody, "emplid", out var emplidEl)
            ? JsonToObject(emplidEl)
            : student.emplid;

        await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "student_profile", studentId, "student_profile_update",
            new
            {
                studentName = student.display_name,
                emplid = auditEmplid,
                fields = requestBody.Keys.ToList(),
            });

        return Ok(new { success = true });
    }

    // PUT /api/admin/students/:studentId/tags (admissions+)
    [HttpPut("students/{studentId}/tags")]
    [AdminAuth("admissions", "admissions_editor", "sysadmin")]
    public async Task<IActionResult> UpdateTags(string studentId, [FromBody] TagsRequest? body)
    {
        var tags = body?.Tags;

        var student = await _db.QueryOneAsync<StudentTagsRow>(
            "SELECT id, tags, display_name FROM students WHERE id = @studentId",
            new { studentId });
        if (student is null)
            return NotFound(new { error = "Student not found" });

        var oldTags = Json.SafeParse<List<string>>(student.tags, []);

        await _db.ExecuteAsync(
            "UPDATE students SET tags = @tags WHERE id = @studentId",
            new { tags = tags is not null ? JsonSerializer.Serialize(tags) : null, studentId });

        await Audit.LogAsync(_db, Audit.ResolveActor(HttpContext), "student_tags", studentId, "tags_update",
            new
            {
                oldTags,
                newTags = tags ?? new List<string>(),
                studentName = string.IsNullOrEmpty(student.display_name) ? null : student.display_name,
            });

        return Ok(new { success = true });
    }

    // GET /api/admin/students — paginated, with progress counts
    [HttpGet("students")]
    public async Task<IActionResult> ListStudents()
    {
        var search = Request.Query["search"].ToString();
        var sort = Request.Query["sort"].ToString();
        if (string.IsNullOrEmpty(sort)) sort = "date_desc";
        var overdueOnly = Request.Query["overdue_only"].ToString();
        var termIdRaw = Request.Query["term_id"].ToString();
        // Mirror `term_id ? parseInt(term_id) : null`: blank string is falsy -> null.
        var termId = ParseTruthyTermId(termIdRaw);

        var (page, perPage, offset) = QueryHelpers.ParsePagination(Request);

        const string baseQuery = @"
            SELECT s.id, s.display_name, s.email, s.azure_id, s.tags, s.created_at, s.term_id,
                   s.emplid, s.applicant_type, s.major, s.residency, s.admit_term,
                   COALESCE(pc.completed, 0) as completed_steps,
                   COALESCE(ov.overdue_count, 0) as overdue_step_count
            FROM students s
            LEFT JOIN (
              SELECT student_id, COUNT(*) as completed
              FROM student_progress sp
              JOIN steps st_req ON st_req.id = sp.step_id AND COALESCE(st_req.is_optional, 0) = 0
              GROUP BY student_id
            ) pc ON pc.student_id = s.id
            LEFT JOIN (
              SELECT s2.id as student_id, COUNT(st.id) as overdue_count
              FROM students s2
              JOIN steps st ON st.is_active = 1 AND COALESCE(st.is_optional, 0) = 0 AND st.deadline_date IS NOT NULL AND st.deadline_date < CONVERT(varchar(10), CAST(SYSUTCDATETIME() AS date), 23)
                AND (st.term_id = s2.term_id OR st.term_id IS NULL)
              LEFT JOIN student_progress sp ON sp.student_id = s2.id AND sp.step_id = st.id
              WHERE sp.student_id IS NULL
              GROUP BY s2.id
            ) ov ON ov.student_id = s.id
        ";

        var where = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(search))
        {
            where.Add("(s.display_name LIKE @search OR s.email LIKE @search OR COALESCE(s.emplid, '') LIKE @search OR COALESCE(s.major, '') LIKE @search)");
            parameters["search"] = $"%{search}%";
        }
        if (termId is not null)
        {
            where.Add("s.term_id = @termId");
            parameters["termId"] = termId;
        }
        if (overdueOnly == "1")
        {
            where.Add("COALESCE(ov.overdue_count, 0) > 0");
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";

        // Sort mapping (identical to the old sortMap).
        string orderBy;
        switch (sort)
        {
            case "date_asc": orderBy = "s.created_at ASC"; break;
            case "name_asc": orderBy = "s.display_name ASC"; break;
            case "name_desc": orderBy = "s.display_name DESC"; break;
            case "progress_asc": orderBy = "completed_steps ASC"; break;
            case "progress_desc": orderBy = "completed_steps DESC"; break;
            case "date_desc": orderBy = "s.created_at DESC"; break;
            default: orderBy = "s.created_at DESC"; break;
        }

        var total = await _db.QueryOneAsync<int>(
            $"SELECT COUNT(*) as count FROM ({baseQuery} {whereClause}) sub",
            parameters);

        parameters["perPage"] = perPage;
        parameters["offset"] = offset;
        var students = await _db.QueryAllAsync<StudentListRow>(
            $"{baseQuery} {whereClause} ORDER BY {orderBy} OFFSET @offset ROWS FETCH NEXT @perPage ROWS ONLY",
            parameters);

        return Ok(new { students, total, page, per_page = perPage });
    }

    // ─── Audit Log ───────────────────────────────────────────

    // GET /api/admin/audit
    [HttpGet("audit")]
    public async Task<IActionResult> Audit_()
    {
        var studentId = Request.Query["studentId"].ToString();
        var entityType = Request.Query["entityType"].ToString();
        var action = Request.Query["action"].ToString();
        var changedBy = Request.Query["changedBy"].ToString();
        var q = Request.Query["q"].ToString();
        var limitRaw = Request.Query["limit"].ToString();
        var offsetRaw = Request.Query["offset"].ToString();

        // Clamp to sane bounds: a negative limit/offset reaches OFFSET/FETCH and 500s.
        var lim = Math.Clamp((int.TryParse(limitRaw, out var l) && l > 0) ? l : 50, 1, 200);
        var off = Math.Max((int.TryParse(offsetRaw, out var o) && o > 0) ? o : 0, 0);

        var where = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(studentId))
        {
            where.Add("entity_id = @studentId AND entity_type IN ('student_progress', 'student_tags', 'student_profile')");
            parameters["studentId"] = studentId;
        }
        if (!string.IsNullOrEmpty(entityType))
        {
            where.Add("entity_type = @entityType");
            parameters["entityType"] = entityType;
        }
        if (!string.IsNullOrEmpty(action))
        {
            where.Add("action = @action");
            parameters["action"] = action;
        }
        if (!string.IsNullOrEmpty(changedBy))
        {
            where.Add("changed_by LIKE @changedBy");
            parameters["changedBy"] = $"%{changedBy}%";
        }
        if (!string.IsNullOrEmpty(q))
        {
            where.Add("(entity_type LIKE @q OR action LIKE @q OR changed_by LIKE @q OR COALESCE(details, '') LIKE @q)");
            parameters["q"] = $"%{q}%";
        }

        var whereClause = where.Count > 0 ? $"WHERE {string.Join(" AND ", where)}" : "";

        var total = await _db.QueryOneAsync<int>(
            $"SELECT COUNT(*) as count FROM audit_log {whereClause}",
            parameters);

        parameters["lim"] = lim;
        parameters["off"] = off;
        var logs = await _db.QueryAllAsync<AuditLogEntry>(
            $"SELECT * FROM audit_log {whereClause} ORDER BY created_at DESC OFFSET @off ROWS FETCH NEXT @lim ROWS ONLY",
            parameters);

        return Ok(new { logs, total });
    }

    // ─── Helpers ─────────────────────────────────────────────

    // parseInt(stepId, 10) — leading digits, else 0 (NaN-style behavior is moot
    // because the step lookup then misses and yields 404).
    private static int ParseIntOrZero(string? value) =>
        int.TryParse(value, out var v) ? v : 0;

    // `term_id ? parseInt(term_id) : null`: empty string is falsy -> null.
    // A present "0" parses to 0, which is itself falsy in the original ternary;
    // but the ternary tests the raw query string, not the parsed int, so "0"
    // (truthy string) yields parseInt("0") = 0.
    private static int? ParseTruthyTermId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        return int.TryParse(raw, out var v) ? v : 0;
    }

    private static bool TryGetField(Dictionary<string, JsonElement> body, string field, out JsonElement element)
    {
        return body.TryGetValue(field, out element);
    }

    // `req.body[field] || null`: JS falsy values become null.
    private static object? CoerceTruthy(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                var s = element.GetString();
                return string.IsNullOrEmpty(s) ? null : s;
            case JsonValueKind.Number:
                var d = element.GetDouble();
                return d == 0 ? null : (object)d;
            case JsonValueKind.False:
                return null;
            case JsonValueKind.True:
                return true;
            default:
                return element.ToString();
        }
    }

    private static object? JsonToObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return element.ToString();
        }
    }

    // ─── Row types ───────────────────────────────────────────

    private sealed class StudentNameRow
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
    }

    private sealed class StepInfoRow
    {
        public int id { get; set; }
        public string? title { get; set; }
        public string? step_key { get; set; }
    }

    // Mirrors the old /progress SELECT (and column order). The shared
    // Api.Models.Student lacks admit_term, so we keep a local shape here to
    // preserve the wire JSON exactly.
    private sealed class StudentProgressDetailRow
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public string? azure_id { get; set; }
        public string? tags { get; set; }
        public DateTime created_at { get; set; }
        public int? term_id { get; set; }
        public string? emplid { get; set; }
        public string? preferred_name { get; set; }
        public string? phone { get; set; }
        public string? applicant_type { get; set; }
        public string? major { get; set; }
        public string? residency { get; set; }
        public string? admit_term { get; set; }
        public DateTime? last_synced_at { get; set; }
    }

    // Mirrors the old /profile SELECT; only display_name + emplid are read.
    private sealed class StudentProfileRow
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public string? emplid { get; set; }
        public string? preferred_name { get; set; }
        public string? phone { get; set; }
        public string? applicant_type { get; set; }
        public string? major { get; set; }
        public string? residency { get; set; }
        public string? admit_term { get; set; }
        public DateTime? last_synced_at { get; set; }
    }

    private sealed class StudentTagsRow
    {
        public string id { get; set; } = "";
        public string? tags { get; set; }
        public string? display_name { get; set; }
    }

    private sealed class ProgressRow
    {
        public int step_id { get; set; }
        public DateTime? completed_at { get; set; }
        public string? status { get; set; }
        public string? note { get; set; }
        public string title { get; set; } = "";
    }

    private sealed class StudentListRow
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public string? azure_id { get; set; }
        public string? tags { get; set; }
        public DateTime created_at { get; set; }
        public int? term_id { get; set; }
        public string? emplid { get; set; }
        public string? applicant_type { get; set; }
        public string? major { get; set; }
        public string? residency { get; set; }
        public string? admit_term { get; set; }
        public int completed_steps { get; set; }
        public int overdue_step_count { get; set; }
    }

}
