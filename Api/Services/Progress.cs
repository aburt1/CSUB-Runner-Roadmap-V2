using Api.Data;

namespace Api.Services;

// Student-progress writes and student/step resolution, ported from
// server/utils/progress.ts. Used by the student, admin, and integration endpoints.
public static class Progress
{
    public static string NormalizeStudentIdNumber(object? value) => (value?.ToString() ?? "").Trim();

    // Parses a caller-supplied completed_at. Returns null for null/blank ("not provided"),
    // and (null, invalid:true) when a non-blank value can't be parsed.
    public static (DateTime? Value, bool Invalid) NormalizeCompletedAt(string? value)
    {
        if (string.IsNullOrEmpty(value)) return (null, false);
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? (dt, false)
            : (null, true);
    }

    public static async Task<StudentResolution> ResolveStudentByIdNumberAsync(Db db, object? studentIdNumber)
    {
        var normalized = NormalizeStudentIdNumber(studentIdNumber);
        if (string.IsNullOrEmpty(normalized))
            return new StudentResolution { ErrorCode = "invalid_student_id_number", Error = "student_id_number is required" };

        var rows = await db.QueryAllAsync<ResolvedStudent>(
            @"SELECT id, display_name, email, emplid, term_id
              FROM students
              WHERE LTRIM(RTRIM(COALESCE(emplid, ''))) = @normalized",
            new { normalized });

        if (rows.Count == 0)
            return new StudentResolution { ErrorCode = "student_not_found", Error = "Student not found" };
        if (rows.Count > 1)
            return new StudentResolution { ErrorCode = "duplicate_student_id_number", Error = "Student ID # is not unique" };

        return new StudentResolution { Student = rows[0], StudentIdNumber = normalized };
    }

    public static async Task<StepResolution> ResolveStepForStudentByKeyAsync(Db db, int? studentTermId, string stepKey)
    {
        var normalizedStepKey = StepKeys.Normalize(stepKey);
        if (normalizedStepKey is null)
            return new StepResolution { ErrorCode = "invalid_step_key", Error = "step_key is required" };
        if (studentTermId is null)
            return new StepResolution { ErrorCode = "student_term_missing", Error = "Student does not have an assigned term" };

        var step = await db.QueryOneAsync<ResolvedStep>(
            @"SELECT id, title, term_id, step_key, is_active
              FROM steps
              WHERE term_id = @termId AND step_key = @key",
            new { termId = studentTermId, key = normalizedStepKey });

        if (step is null)
            return new StepResolution { ErrorCode = "step_not_found", Error = "Step not found in the student term" };
        if (step.is_active == 0)
            return new StepResolution { ErrorCode = "step_inactive", Error = "Step is inactive" };

        return new StepResolution { Step = step, StepKey = normalizedStepKey };
    }

    // Insert/update/delete a student_progress row to reach the requested status.
    // Wrap the call in db.TransactionAsync when several changes must be atomic.
    public static async Task<ProgressChangeResult> ApplyAsync(Db db, ProgressChangeInput input)
    {
        var nextStatus = input.Status == "waived" ? "waived"
            : input.Status == "not_completed" ? "not_completed"
            : "completed";
        var normalizedNote = string.IsNullOrEmpty(input.Note) ? null : input.Note;
        var normalizedCompletedBy = string.IsNullOrEmpty(input.CompletedBy) ? "manual" : input.CompletedBy;
        var (explicitCompletedAt, invalid) = NormalizeCompletedAt(input.CompletedAt);
        if (invalid)
            return new ProgressChangeResult { Error = "completed_at must be a valid ISO timestamp" };

        // Read-modify-write in ONE transaction. UPDLOCK serializes writers on an existing
        // row; HOLDLOCK additionally takes a key-range lock when the row is ABSENT, so two
        // concurrent first-completions can't both pass the existence check and race to a
        // duplicate-key 500 — the second blocks (or deadlocks as victim, which Db retries)
        // and then sees the committed row and takes the UPDATE path.
        return await db.TransactionAsync(async tx =>
        {
            var current = await tx.QueryOneAsync<CurrentProgress>(
                @"SELECT student_id, step_id, completed_at, status, note, completed_by
                  FROM student_progress WITH (UPDLOCK, HOLDLOCK)
                  WHERE student_id = @studentId AND step_id = @stepId",
                new { studentId = input.StudentId, stepId = input.StepId });

            if (nextStatus == "not_completed")
            {
                if (current is null)
                    return new ProgressChangeResult { Result = "noop", Status = "not_completed", CompletedAt = null, CompletedBy = normalizedCompletedBy };

                await tx.ExecuteAsync(
                    "DELETE FROM student_progress WHERE student_id = @studentId AND step_id = @stepId",
                    new { studentId = input.StudentId, stepId = input.StepId });
                return new ProgressChangeResult { Result = "updated", Status = "not_completed", CompletedAt = null, CompletedBy = normalizedCompletedBy };
            }

            if (current is not null)
            {
                var nextCompletedAt = explicitCompletedAt ?? current.completed_at ?? DateTime.UtcNow;
                var sameCompletedAt = input.CompletedAt is null || current.completed_at == explicitCompletedAt;

                if (current.status == nextStatus
                    && (current.note ?? null) == normalizedNote
                    && sameCompletedAt
                    && (string.IsNullOrEmpty(current.completed_by) ? "manual" : current.completed_by) == normalizedCompletedBy)
                {
                    return new ProgressChangeResult
                    {
                        Result = "noop",
                        Status = nextStatus,
                        CompletedAt = current.completed_at,
                        CompletedBy = string.IsNullOrEmpty(current.completed_by) ? "manual" : current.completed_by,
                    };
                }

                await tx.ExecuteAsync(
                    @"UPDATE student_progress
                      SET status = @status, note = @note, completed_at = @completedAt, completed_by = @completedBy
                      WHERE student_id = @studentId AND step_id = @stepId",
                    new { status = nextStatus, note = normalizedNote, completedAt = nextCompletedAt, completedBy = normalizedCompletedBy, studentId = input.StudentId, stepId = input.StepId });
                return new ProgressChangeResult { Result = "updated", Status = nextStatus, CompletedAt = nextCompletedAt, CompletedBy = normalizedCompletedBy };
            }

            var insertCompletedAt = explicitCompletedAt ?? DateTime.UtcNow;
            await tx.ExecuteAsync(
                @"INSERT INTO student_progress (student_id, step_id, completed_at, status, note, completed_by)
                  VALUES (@studentId, @stepId, @completedAt, @status, @note, @completedBy)",
                new { studentId = input.StudentId, stepId = input.StepId, completedAt = insertCompletedAt, status = nextStatus, note = normalizedNote, completedBy = normalizedCompletedBy });
            return new ProgressChangeResult { Result = "created", Status = nextStatus, CompletedAt = insertCompletedAt, CompletedBy = normalizedCompletedBy };
        });
    }

    public sealed class ResolvedStudent
    {
        public string id { get; set; } = "";
        public string? display_name { get; set; }
        public string? email { get; set; }
        public string? emplid { get; set; }
        public int? term_id { get; set; }
    }

    public sealed class ResolvedStep
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public int term_id { get; set; }
        public string step_key { get; set; } = "";
        public int is_active { get; set; }
    }

    public sealed class StudentResolution
    {
        public ResolvedStudent? Student { get; set; }
        public string? StudentIdNumber { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
    }

    public sealed class StepResolution
    {
        public ResolvedStep? Step { get; set; }
        public string? StepKey { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
    }

    public sealed class ProgressChangeInput
    {
        public string StudentId { get; set; } = "";
        public int StepId { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        public string? CompletedAt { get; set; }
        public string? CompletedBy { get; set; }
    }

    public sealed class ProgressChangeResult
    {
        public string? Result { get; set; }
        public string? Status { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? CompletedBy { get; set; }
        public string? Error { get; set; }
    }

    private sealed class CurrentProgress
    {
        public string student_id { get; set; } = "";
        public int step_id { get; set; }
        public DateTime? completed_at { get; set; }
        public string status { get; set; } = "";
        public string? note { get; set; }
        public string? completed_by { get; set; }
    }
}
