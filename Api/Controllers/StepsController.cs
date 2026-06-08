using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

// Student roadmap steps, ported from server/routes/steps.ts.
// Mirrors that route file 1:1 so the two diff cleanly.
[ApiController]
[Route("api/steps")]
public sealed class StepsController : ControllerBase
{
    private readonly Db _db;
    private readonly JwtService _jwt;

    public StepsController(Db db, JwtService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    public sealed record StatusRequest(string? Status);

    // OPTIONAL student auth: returns the student id from a valid Bearer student
    // token, or null otherwise. Mirrors getOptionalStudentId in steps.ts.
    private string? GetOptionalStudentId()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
            return null;

        var principal = _jwt.Validate(header["Bearer ".Length..]);
        if (principal is null)
            return null;
        if (principal.FindFirst("type")?.Value != "student")
            return null;

        var studentId = principal.FindFirst("studentId")?.Value;
        return string.IsNullOrEmpty(studentId) ? null : studentId;
    }

    // Mirrors stepAppliesToStudent in steps.ts.
    private static bool StepAppliesToStudent(Step step, List<string> studentTags)
    {
        var requiredTags = Json.SafeParse<List<string>>(step.required_tags, []);
        var excludedTags = Json.SafeParse<List<string>>(step.excluded_tags, []);
        var requiredTagMode = step.required_tag_mode == "all" ? "all" : "any";

        if (excludedTags.Any(tag => studentTags.Contains(tag)))
            return false;
        if (requiredTags.Count == 0)
            return true;

        return requiredTagMode == "all"
            ? requiredTags.All(tag => studentTags.Contains(tag))
            : requiredTags.Any(tag => studentTags.Contains(tag));
    }

    // GET /api/steps - Get all active admissions steps.
    // When called with auth, filters by student's term.
    [HttpGet("")]
    public async Task<IActionResult> GetSteps()
    {
        var studentId = GetOptionalStudentId();
        IReadOnlyList<Step> steps;

        if (studentId is not null)
        {
            var student = await _db.QueryOneAsync<StudentTermRow>(
                "SELECT term_id FROM students WHERE id = @studentId",
                new { studentId });

            if (student?.term_id is not null)
            {
                steps = await _db.QueryAllAsync<Step>(
                    "SELECT * FROM steps WHERE (is_active = 1 OR is_active IS NULL) AND term_id = @termId ORDER BY sort_order",
                    new { termId = student.term_id });
            }
            else
            {
                var activeTerm = await _db.QueryOneAsync<int?>(
                    "SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");
                steps = activeTerm is not null
                    ? await _db.QueryAllAsync<Step>(
                        "SELECT * FROM steps WHERE (is_active = 1 OR is_active IS NULL) AND term_id = @termId ORDER BY sort_order",
                        new { termId = activeTerm })
                    : await _db.QueryAllAsync<Step>(
                        "SELECT * FROM steps WHERE (is_active = 1 OR is_active IS NULL) ORDER BY sort_order");
            }
        }
        else
        {
            var activeTerm = await _db.QueryOneAsync<int?>(
                "SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");
            steps = activeTerm is not null
                ? await _db.QueryAllAsync<Step>(
                    "SELECT * FROM steps WHERE (is_active = 1 OR is_active IS NULL) AND term_id = @termId ORDER BY sort_order",
                    new { termId = activeTerm })
                : await _db.QueryAllAsync<Step>(
                    "SELECT * FROM steps WHERE (is_active = 1 OR is_active IS NULL) ORDER BY sort_order");
        }

        return Ok(steps);
    }

    // GET /api/steps/progress - Get current student's progress + tags + term info.
    [HttpGet("progress")]
    [StudentAuth]
    public async Task<IActionResult> GetProgress()
    {
        var studentId = HttpContext.StudentId();

        var progress = await _db.QueryAllAsync<ProgressRow>(
            @"SELECT sp.step_id, sp.completed_at, sp.status
              FROM student_progress sp
              WHERE sp.student_id = @studentId",
            new { studentId });

        var student = await _db.QueryOneAsync<StudentTagsRow>(
            "SELECT tags, applicant_type, major, residency, term_id FROM students WHERE id = @studentId",
            new { studentId });

        TermRow? term = null;
        if (student?.term_id is not null)
        {
            term = await _db.QueryOneAsync<TermRow>(
                "SELECT id, name, start_date, end_date FROM terms WHERE id = @termId",
                new { termId = student.term_id });
        }

        var tags = StudentTags.Merged(student?.tags, student?.applicant_type, student?.residency, student?.major);

        return Ok(new
        {
            progress,
            tags,
            term,
        });
    }

    // PUT /api/steps/:stepId/status - Student self-service updates for optional steps.
    [HttpPut("{stepId}/status")]
    [StudentAuth]
    public async Task<IActionResult> UpdateStatus(int stepId, [FromBody] StatusRequest? body)
    {
        var studentId = HttpContext.StudentId();
        var status = body?.Status;

        if (status != "completed" && status != "not_completed")
            return BadRequest(new { error = "status must be completed or not_completed" });

        var student = await _db.QueryOneAsync<Student>(
            @"SELECT id, display_name, email, tags, applicant_type, major, residency, term_id, emplid
              FROM students WHERE id = @studentId",
            new { studentId });

        if (student is null)
            return NotFound(new { error = "Student not found" });

        if (student.term_id is null)
            return Conflict(new { error = "Student does not have an assigned term" });

        var step = await _db.QueryOneAsync<Step>(
            "SELECT * FROM steps WHERE id = @stepId AND term_id = @termId",
            new { stepId, termId = student.term_id });

        if (step is null)
            return NotFound(new { error = "Step not found in the student term" });

        if (step.is_active == 0)
            return Conflict(new { error = "Step is inactive" });

        if (step.is_optional != 1)
            return StatusCode(403, new { error = "Students may only update optional steps" });

        var studentTags = StudentTags.Merged(student.tags, student.applicant_type, student.residency, student.major);
        if (!StepAppliesToStudent(step, studentTags))
            return StatusCode(403, new { error = "Step does not apply to this student" });

        var progressChange = await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
        {
            StudentId = student.id,
            StepId = stepId,
            Status = status,
        });

        if (progressChange.Error is not null)
            return BadRequest(new { error = progressChange.Error });

        // The old code sets req.studentUser here so the audit actor resolves to the
        // student. Our StudentAuth filter already stashed studentEmail on HttpContext,
        // which Audit.ResolveActor uses.
        if (progressChange.Result != "noop")
        {
            await Audit.LogAsync(
                _db,
                // Match the old app: the actor for a student self-update is the
                // student's display name (falling back to email).
                !string.IsNullOrEmpty(student.display_name) ? student.display_name! : (student.email ?? "system"),
                "student_progress",
                student.id,
                status == "completed" ? "student_optional_complete" : "student_optional_uncomplete",
                new
                {
                    studentName = student.display_name,
                    student_id_number = string.IsNullOrEmpty(student.emplid) ? null : student.emplid,
                    stepId = step.id,
                    stepTitle = step.title,
                    step_key = string.IsNullOrEmpty(step.step_key) ? null : step.step_key,
                    result = progressChange.Result,
                });
        }

        return Ok(new
        {
            success = true,
            stepId,
            status = progressChange.Status,
            result = progressChange.Result,
            completedAt = progressChange.CompletedAt,
        });
    }

    private sealed class StudentTermRow
    {
        public int? term_id { get; set; }
    }

    private sealed class ProgressRow
    {
        public int step_id { get; set; }
        public DateTime? completed_at { get; set; }
        public string? status { get; set; }
    }

    private sealed class StudentTagsRow
    {
        public string? tags { get; set; }
        public string? applicant_type { get; set; }
        public string? major { get; set; }
        public string? residency { get; set; }
        public int? term_id { get; set; }
    }

    private sealed class TermRow
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public string? start_date { get; set; }
        public string? end_date { get; set; }
    }
}
