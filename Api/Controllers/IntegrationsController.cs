using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Api.Controllers;

// Integration push API.
// Every action sits behind [IntegrationAuth].
//
//   PUT  /api/integrations/v1/step-completions        single completion (idempotent)
//   POST /api/integrations/v1/step-completions/batch   up to 500 completions
//   GET  /api/integrations/v1/step-catalog             step keys per term
//
// Idempotency is enforced via the integration_events unique (integration_client_id,
// source_event_id): a repeated source_event_id replays the cached status + body.
[ApiController]
[Route("api/integrations/v1")]
[IntegrationAuth]
public sealed class IntegrationsController : ControllerBase
{
    private readonly Db _db;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(Db db, ILogger<IntegrationsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Per-errorCode HTTP status.
    private static int StatusForErrorCode(string? code) => code switch
    {
        "invalid_student_id_number" => 400,
        "invalid_step_key" => 400,
        "student_term_missing" => 409,
        "student_not_found" => 404,
        "step_not_found" => 404,
        "step_inactive" => 409,
        "duplicate_student_id_number" => 409,
        "invalid_emplid" => 400,
        "missing_required_field" => 400,
        "term_not_found" => 404,
        "no_active_term" => 409,
        _ => 400,
    };

    public sealed class CompletionItem
    {
        public string? student_id_number { get; set; }
        public string? step_key { get; set; }
        public string? status { get; set; }
        public string? source_event_id { get; set; }
        public string? note { get; set; }
        public string? completed_at { get; set; }
    }

    public sealed class BatchRequest
    {
        public List<CompletionItem>? items { get; set; }
    }

    // Student provisioning push: pre-stage / upsert a student into a cohort by emplid
    // before they ever sign in. emplid + display_name + source_event_id are required;
    // the rest are optional profile fields written only when present in the payload.
    public sealed class StudentItem
    {
        public string? emplid { get; set; }
        public string? display_name { get; set; }
        public string? source_event_id { get; set; }
        public int? term_id { get; set; }
        public string? email { get; set; }
        public string? tags { get; set; }
        public string? preferred_name { get; set; }
        public string? phone { get; set; }
        public string? applicant_type { get; set; }
        public string? major { get; set; }
        public string? residency { get; set; }
        public string? admit_term { get; set; }
    }

    public sealed class StudentBatchRequest
    {
        public List<StudentItem>? items { get; set; }
    }

    private sealed class Outcome
    {
        public int HttpStatus { get; set; }
        public object Body { get; set; } = new { };
    }

    // PUT /api/integrations/v1/step-completions
    [HttpPut("step-completions")]
    public async Task<IActionResult> StepCompletion([FromBody] CompletionItem? body)
    {
        var outcome = await ProcessCompletionItemAsync(body ?? new CompletionItem());
        return StatusCode(outcome.HttpStatus, outcome.Body);
    }

    // POST /api/integrations/v1/step-completions/batch
    [HttpPost("step-completions/batch")]
    public async Task<IActionResult> StepCompletionsBatch([FromBody] BatchRequest? body)
    {
        var items = body?.items;
        if (items is null || items.Count == 0)
            return BadRequest(new { error = "items must be a non-empty array" });
        if (items.Count > 500)
            return BadRequest(new { error = "Batch size must not exceed 500 items" });

        var results = new List<object>();
        foreach (var item in items)
        {
            var outcome = await ProcessCompletionItemAsync(item ?? new CompletionItem());
            results.Add(outcome.Body);
        }

        var succeeded = 0;
        foreach (var result in results)
        {
            if (IsSuccess(result))
                succeeded++;
        }
        var failed = results.Count - succeeded;

        return Ok(new
        {
            success = true,
            items = results,
            summary = new
            {
                total = results.Count,
                succeeded,
                failed,
            },
        });
    }

    // PUT /api/integrations/v1/students
    [HttpPut("students")]
    public async Task<IActionResult> StudentUpsert([FromBody] StudentItem? body)
    {
        var outcome = await ProcessStudentItemAsync(body ?? new StudentItem());
        return StatusCode(outcome.HttpStatus, outcome.Body);
    }

    // POST /api/integrations/v1/students/batch
    [HttpPost("students/batch")]
    public async Task<IActionResult> StudentsBatch([FromBody] StudentBatchRequest? body)
    {
        var items = body?.items;
        if (items is null || items.Count == 0)
            return BadRequest(new { error = "items must be a non-empty array" });
        if (items.Count > 500)
            return BadRequest(new { error = "Batch size must not exceed 500 items" });

        var results = new List<object>();
        foreach (var item in items)
        {
            var outcome = await ProcessStudentItemAsync(item ?? new StudentItem());
            results.Add(outcome.Body);
        }

        var succeeded = 0;
        foreach (var result in results)
        {
            if (IsSuccess(result))
                succeeded++;
        }
        var failed = results.Count - succeeded;

        return Ok(new
        {
            success = true,
            items = results,
            summary = new
            {
                total = results.Count,
                succeeded,
                failed,
            },
        });
    }

    // GET /api/integrations/v1/step-catalog
    [HttpGet("step-catalog")]
    public async Task<IActionResult> StepCatalog()
    {
        int? termId = null;
        var rawTermId = Request.Query["term_id"].ToString();
        if (!string.IsNullOrEmpty(rawTermId))
        {
            // Parse the leading integer, then treat 0/NaN as falsy so an unparseable
            // term_id returns 400.
            var parsed = JsParse.LeadingInt(rawTermId);
            if (parsed is null || parsed.Value == 0)
                return BadRequest(new { error = "term_id must be a valid number" });
            termId = parsed.Value;
        }

        IReadOnlyList<CatalogRow> rows;
        if (termId is not null)
        {
            rows = await _db.QueryAllAsync<CatalogRow>(
                @"SELECT s.term_id, t.name as term_name, s.step_key, s.title, COALESCE(s.is_active, 1) as is_active
                  FROM steps s
                  JOIN terms t ON t.id = s.term_id
                  WHERE s.term_id = @termId
                  ORDER BY s.sort_order, s.id",
                new { termId });
        }
        else
        {
            rows = await _db.QueryAllAsync<CatalogRow>(
                @"SELECT s.term_id, t.name as term_name, s.step_key, s.title, COALESCE(s.is_active, 1) as is_active
                  FROM steps s
                  JOIN terms t ON t.id = s.term_id
                  ORDER BY t.created_at DESC, s.sort_order, s.id");
        }

        return Ok(rows);
    }

    // Core single-item pipeline. The PUT and each batch item run through this.
    // Returns the HTTP status + body to send/collect.
    private async Task<Outcome> ProcessCompletionItemAsync(CompletionItem item)
    {
        var integrationClientId = (int)HttpContext.Items["integrationClientId"]!;

        var sourceEventId = (item.source_event_id ?? "").Trim();
        if (string.IsNullOrEmpty(sourceEventId))
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildFailure(item, "source_event_id is required", "invalid_source_event_id"),
            };
        }

        var storedEvent = await GetStoredIntegrationEventAsync(integrationClientId, sourceEventId);
        if (storedEvent is not null)
            return storedEvent;

        // Validation failures (bad status / bad completed_at) are deliberately NOT recorded in
        // integration_events — the caller can retry the same source_event_id with a corrected
        // payload; resolution failures ARE recorded so a replay returns the original outcome.
        if (item.status != "completed" && item.status != "waived" && item.status != "not_completed")
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildFailure(item, "status must be completed, waived, or not_completed", "invalid_status"),
            };
        }

        var studentResolution = await Progress.ResolveStudentByIdNumberAsync(_db, item.student_id_number);
        if (studentResolution.Error is not null)
        {
            return await FinalizeOutcomeAsync(integrationClientId, item, new Outcome
            {
                HttpStatus = StatusForErrorCode(studentResolution.ErrorCode),
                Body = BuildFailure(item, studentResolution.Error, studentResolution.ErrorCode),
            });
        }

        var student = studentResolution.Student!;
        var studentIdNumber = studentResolution.StudentIdNumber!;
        var stepResolution = await Progress.ResolveStepForStudentByKeyAsync(_db, student.term_id, item.step_key ?? "");
        if (stepResolution.Error is not null)
        {
            return await FinalizeOutcomeAsync(integrationClientId, item, new Outcome
            {
                HttpStatus = StatusForErrorCode(stepResolution.ErrorCode),
                Body = BuildFailureWithStudent(item, stepResolution.Error, stepResolution.ErrorCode, student.id),
            });
        }

        var step = stepResolution.Step!;
        var stepKey = stepResolution.StepKey!;
        var progressChange = await Progress.ApplyAsync(_db, new Progress.ProgressChangeInput
        {
            StudentId = student.id,
            StepId = step.id,
            Status = item.status,
            Note = item.note,
            CompletedAt = item.completed_at,
            CompletedBy = "integration",
        });

        if (progressChange.Error is not null)
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildFailureWithStudentStep(item, progressChange.Error, "invalid_completed_at", student.id, step.id),
            };
        }

        var responseBody = new
        {
            success = true,
            student_id_number = studentIdNumber,
            step_key = stepKey,
            student_id = student.id,
            step_id = step.id,
            status = progressChange.Status,
            result = progressChange.Result,
            completed_at = progressChange.CompletedAt,
            source_event_id = sourceEventId,
        };

        if (progressChange.Result != "noop")
        {
            var action = item.status == "waived"
                ? "integration_waive"
                : item.status == "not_completed"
                    ? "integration_uncomplete"
                    : "integration_complete";

            var actor = Audit.ResolveActor(HttpContext);
            await Audit.LogAsync(_db, actor, "student_progress", student.id, action, new
            {
                source_system = actor,
                source_event_id = sourceEventId,
                studentName = student.display_name,
                student_id_number = studentIdNumber,
                stepId = step.id,
                stepTitle = step.title,
                step_key = stepKey,
                result = progressChange.Result,
                note = string.IsNullOrEmpty(item.note) ? null : item.note,
            });
        }

        return await FinalizeOutcomeAsync(integrationClientId, item, new Outcome
        {
            HttpStatus = 200,
            Body = responseBody,
        });
    }

    // Core single-student pipeline. The PUT and each batch item run through this.
    // Upserts the student keyed on emplid; idempotent via source_event_id like completions.
    private async Task<Outcome> ProcessStudentItemAsync(StudentItem item)
    {
        var integrationClientId = (int)HttpContext.Items["integrationClientId"]!;

        var sourceEventId = (item.source_event_id ?? "").Trim();
        if (string.IsNullOrEmpty(sourceEventId))
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildStudentFailure(item, "source_event_id is required", "invalid_source_event_id"),
            };
        }

        var storedEvent = await GetStoredIntegrationEventAsync(integrationClientId, sourceEventId);
        if (storedEvent is not null)
            return storedEvent;

        // Input-validation failures (blank emplid / display_name, bad term) are deliberately
        // NOT recorded — the caller can retry the same source_event_id with a corrected payload.
        var emplid = Progress.NormalizeStudentIdNumber(item.emplid);
        if (string.IsNullOrEmpty(emplid))
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildStudentFailure(item, "emplid is required", "invalid_emplid"),
            };
        }

        if (string.IsNullOrWhiteSpace(item.display_name))
        {
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildStudentFailure(item, "display_name is required", "missing_required_field"),
            };
        }

        var termResolution = await ResolveTermIdAsync(item.term_id);
        if (termResolution.Error is not null)
        {
            return new Outcome
            {
                HttpStatus = StatusForErrorCode(termResolution.ErrorCode),
                Body = BuildStudentFailure(item, termResolution.Error, termResolution.ErrorCode),
            };
        }
        var termId = termResolution.TermId!.Value;

        // Resolve an existing row by the SAME emplid predicate the completion API uses.
        // Branch on ErrorCode (the stable code), not Error (the human-readable message).
        var resolution = await Progress.ResolveStudentByIdNumberAsync(_db, emplid);
        if (resolution.ErrorCode is "duplicate_student_id_number")
        {
            // >1 existing rows for this emplid: surface a real 409 (and store it).
            return await FinalizeStudentOutcomeAsync(integrationClientId, item, emplid, new Outcome
            {
                HttpStatus = StatusForErrorCode(resolution.ErrorCode),
                Body = BuildStudentFailure(item, resolution.Error!, resolution.ErrorCode),
            });
        }
        if (resolution.ErrorCode is "invalid_student_id_number")
        {
            // Should not happen (emplid already validated non-blank), but mirror its 400.
            return new Outcome
            {
                HttpStatus = 400,
                Body = BuildStudentFailure(item, resolution.Error!, resolution.ErrorCode),
            };
        }

        Outcome outcome;
        if (resolution.ErrorCode is "student_not_found")
        {
            outcome = await CreateStudentAsync(item, emplid, termId);
        }
        else
        {
            outcome = await UpdateStudentAsync(item, resolution.Student!, emplid, termId);
        }

        return await FinalizeStudentOutcomeAsync(integrationClientId, item, emplid, outcome);
    }

    // Build the present-only optional-column SET/INSERT fragments using the same
    // whitelist + CoerceTruthy semantics the admin profile update uses: only columns
    // PRESENT on the payload are written, and falsy values ('' / 0 / false) become null.
    private static readonly string[] OptionalStudentFields =
    {
        "email", "tags", "preferred_name", "phone",
        "applicant_type", "major", "residency", "admit_term",
    };

    private async Task<Outcome> CreateStudentAsync(StudentItem item, string emplid, int termId)
    {
        var studentId = Guid.NewGuid().ToString();

        var columns = new List<string> { "id", "emplid", "display_name", "term_id" };
        var values = new List<string> { "@id", "@emplid", "@display_name", "@termId" };
        var parameters = new Dictionary<string, object?>
        {
            ["id"] = studentId,
            ["emplid"] = emplid,
            ["display_name"] = item.display_name,
            ["termId"] = termId,
        };
        AddPresentOptionalFields(item, columns, values, parameters, forUpdate: false);

        try
        {
            await _db.ExecuteAsync(
                $"INSERT INTO students ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})",
                parameters);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            // A concurrent push of the same new emplid won the unique index — re-resolve
            // and fall through to the UPDATE branch (the upsert's own race guard, distinct
            // from the integration_events idempotency race).
            var reresolved = await Progress.ResolveStudentByIdNumberAsync(_db, emplid);
            if (reresolved.Student is not null)
                return await UpdateStudentAsync(item, reresolved.Student, emplid, termId);
            throw;
        }

        // New students start with the "accepted" step already completed — only on a genuine
        // create, mirroring the sign-in provisioning path.
        await AutoCompleteAcceptedStepAsync(studentId, termId);

        return new Outcome
        {
            HttpStatus = 200,
            Body = BuildStudentSuccess(emplid, studentId, termId, "created", item),
        };
    }

    private async Task<Outcome> UpdateStudentAsync(StudentItem item, Progress.ResolvedStudent existing, string emplid, int termId)
    {
        var updates = new List<string> { "display_name = @display_name" };
        var parameters = new Dictionary<string, object?>
        {
            ["id"] = existing.id,
            ["display_name"] = item.display_name,
        };

        // Only move a student between cohorts when term_id was EXPLICITLY provided, so a
        // re-push without term_id never silently reassigns them.
        if (item.term_id.HasValue)
        {
            updates.Add("term_id = @termId");
            parameters["termId"] = termId;
        }

        AddPresentOptionalFields(item, columns: null, values: null, parameters: parameters, forUpdate: true, updates: updates);

        await _db.ExecuteAsync(
            $"UPDATE students SET {string.Join(", ", updates)} WHERE id = @id",
            parameters);

        // display_name is always present, so an update always writes >=1 field. Match the
        // completion result vocabulary: any present field => "updated".
        return new Outcome
        {
            HttpStatus = 200,
            Body = BuildStudentSuccess(emplid, existing.id, termId, "updated", item),
        };
    }

    // Append the optional columns/params that are present (non-null) on the payload,
    // coercing falsy -> null. For INSERT, fills columns+values; for UPDATE, fills updates.
    private static void AddPresentOptionalFields(
        StudentItem item,
        List<string>? columns,
        List<string>? values,
        Dictionary<string, object?> parameters,
        bool forUpdate,
        List<string>? updates = null)
    {
        var index = 0;
        foreach (var field in OptionalStudentFields)
        {
            var raw = GetOptionalField(item, field);
            // "Present" = the JSON carried a value. A null property is treated as omitted
            // (so an omitted optional never null-outs an existing column on update).
            if (raw is null)
                continue;

            var paramName = "opt" + index;
            // CoerceTruthy semantics: '' / 0 / false -> null.
            parameters[paramName] = string.IsNullOrEmpty(raw) ? null : raw;

            if (forUpdate)
                updates!.Add($"{field} = @{paramName}");
            else
            {
                columns!.Add(field);
                values!.Add("@" + paramName);
            }
            index++;
        }
    }

    private static string? GetOptionalField(StudentItem item, string field) => field switch
    {
        "email" => item.email,
        "tags" => item.tags,
        "preferred_name" => item.preferred_name,
        "phone" => item.phone,
        "applicant_type" => item.applicant_type,
        "major" => item.major,
        "residency" => item.residency,
        "admit_term" => item.admit_term,
        _ => null,
    };

    // Resolve the target cohort. THE single swappable place for cohort resolution: an
    // explicit term_id is validated against dbo.terms; otherwise we fall back to the
    // current active term (same SQL AuthController uses on provisioning).
    private async Task<TermResolution> ResolveTermIdAsync(int? requested)
    {
        if (requested.HasValue)
        {
            var found = await _db.QueryOneAsync<int?>(
                "SELECT id FROM dbo.terms WHERE id = @id", new { id = requested.Value });
            if (found is null)
                return new TermResolution { ErrorCode = "term_not_found", Error = "term_id does not exist" };
            return new TermResolution { TermId = found.Value };
        }

        var active = await _db.QueryOneAsync<int?>(
            "SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC");
        if (active is null)
            return new TermResolution { ErrorCode = "no_active_term", Error = "No active term to assign the student to" };
        return new TermResolution { TermId = active.Value };
    }

    // New students start with the "accepted" step already completed (idempotent).
    // Duplicated from AuthController by design (boring-code charter: a small block, not a layer).
    private async Task AutoCompleteAcceptedStepAsync(string studentId, int termId)
    {
        var stepId = await _db.QueryOneAsync<int?>(
            "SELECT TOP 1 id FROM steps WHERE term_id = @termId AND step_key = 'accepted' ORDER BY id", new { termId });
        if (stepId is null) return;
        await _db.ExecuteAsync(
            @"IF NOT EXISTS (SELECT 1 FROM student_progress WHERE student_id = @studentId AND step_id = @stepId)
              INSERT INTO student_progress (student_id, step_id) VALUES (@studentId, @stepId)",
            new { studentId, stepId });
    }

    // Store the student outcome when a source_event_id is present, otherwise pass through.
    private async Task<Outcome> FinalizeStudentOutcomeAsync(int integrationClientId, StudentItem item, string emplid, Outcome outcome)
    {
        var sourceEventId = (item.source_event_id ?? "").Trim();
        if (string.IsNullOrEmpty(sourceEventId))
            return outcome;

        // step_key is null for student pushes; store the normalized emplid as student_id_number.
        return await StoreIntegrationEventAsync(
            integrationClientId,
            sourceEventId,
            Json.NullIfEmpty(emplid),
            null,
            JsonSerializer.Serialize(item, StoreOptions),
            outcome);
    }

    private static object BuildStudentSuccess(string emplid, string studentId, int termId, string result, StudentItem item) => new
    {
        success = true,
        student_id_number = emplid,
        student_id = studentId,
        term_id = termId,
        result,
        source_event_id = (item.source_event_id ?? "").Trim(),
    };

    // Fixed-shape student failure body, mirroring BuildFailure.
    private static object BuildStudentFailure(StudentItem item, string error, string? code) => new
    {
        success = false,
        student_id_number = Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.emplid)),
        source_event_id = Json.NullIfEmpty(item.source_event_id),
        result = "failed",
        error,
        code,
    };

    private sealed class TermResolution
    {
        public int? TermId { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
    }

    // Replay a previously stored event response, or null if none exists.
    private async Task<Outcome?> GetStoredIntegrationEventAsync(int integrationClientId, string sourceEventId)
    {
        var row = await _db.QueryOneAsync<StoredEventRow>(
            @"SELECT response_status, response_body
              FROM integration_events
              WHERE integration_client_id = @integrationClientId AND source_event_id = @sourceEventId",
            new { integrationClientId, sourceEventId });

        if (row is null)
            return null;

        return new Outcome
        {
            HttpStatus = row.response_status,
            Body = ParseStoredBody(row.response_body),
        };
    }

    // Persist the event row for idempotency, then return the outcome. If the insert
    // races/collides on the unique key, replay the now-stored event instead.
    // Serialize the stored body with the same UTC-'Z' timestamp format as the live
    // responses, so an idempotent replay is byte-identical to the original response.
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        Converters = { new Api.Serialization.UtcDateTimeConverter() },
    };

    private Task<Outcome> StoreIntegrationEventAsync(int integrationClientId, string sourceEventId, CompletionItem item, Outcome outcome) =>
        StoreIntegrationEventAsync(
            integrationClientId,
            sourceEventId,
            Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.student_id_number)),
            StepKeys.Normalize(item.step_key ?? ""),
            JsonSerializer.Serialize(item, StoreOptions),
            outcome);

    // Core idempotency persist: callers pass the already-computed student_id_number,
    // step_key (null for student pushes), and the serialized request body so both
    // CompletionItem and StudentItem flows share this one write path.
    private async Task<Outcome> StoreIntegrationEventAsync(int integrationClientId, string sourceEventId, string? studentIdNumber, string? stepKey, string requestBody, Outcome outcome)
    {
        try
        {
            await _db.ExecuteAsync(
                @"INSERT INTO integration_events (
                    integration_client_id,
                    source_event_id,
                    student_id_number,
                    step_key,
                    request_body,
                    response_status,
                    response_body
                  ) VALUES (@integrationClientId, @sourceEventId, @studentIdNumber, @stepKey, @requestBody, @responseStatus, @responseBody)",
                new
                {
                    integrationClientId,
                    sourceEventId,
                    studentIdNumber,
                    stepKey,
                    requestBody,
                    responseStatus = outcome.HttpStatus,
                    responseBody = JsonSerializer.Serialize(outcome.Body, StoreOptions),
                });

            return outcome;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601)
        {
            // Unique-key collision: the event was already stored by a concurrent/earlier
            // request — replay its persisted outcome (this is the intended idempotency race).
            var stored = await GetStoredIntegrationEventAsync(integrationClientId, sourceEventId);
            return stored ?? outcome;
        }
        catch (Exception ex)
        {
            // A genuine failure (write timeout, other constraint, overflow, retry
            // exhaustion). Previously swallowed silently, which could leave idempotency
            // unpersisted while the response looked successful — a later replay would
            // re-execute. Log it so the failure is visible; behavior otherwise unchanged
            // (re-read the row in case it was in fact stored, else surface the outcome).
            _logger.LogError(
                ex,
                "Failed to persist integration_events row for client {ClientId} source_event_id {SourceEventId}",
                integrationClientId, sourceEventId);
            var stored = await GetStoredIntegrationEventAsync(integrationClientId, sourceEventId);
            return stored ?? outcome;
        }
    }

    // Store the outcome when a source_event_id is present, otherwise pass it through.
    private async Task<Outcome> FinalizeOutcomeAsync(int integrationClientId, CompletionItem item, Outcome outcome)
    {
        // Trim to match the idempotency LOOKUP (which trims): storing the raw value
        // would make a padded id miss its own stored outcome and re-execute on replay.
        var sourceEventId = (item.source_event_id ?? "").Trim();
        if (string.IsNullOrEmpty(sourceEventId))
            return outcome;

        return await StoreIntegrationEventAsync(integrationClientId, sourceEventId, item, outcome);
    }

    // Fixed-shape failure body (no student/step ids).
    private static object BuildFailure(CompletionItem item, string error, string? code) => new
    {
        success = false,
        student_id_number = Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.student_id_number)),
        step_key = StepKeys.Normalize(item.step_key ?? ""),
        status = Json.NullIfEmpty(item.status),
        source_event_id = Json.NullIfEmpty(item.source_event_id),
        result = "failed",
        error,
        code,
    };

    // buildFailure with the { student_id } extra.
    private static object BuildFailureWithStudent(CompletionItem item, string error, string? code, string studentId) => new
    {
        success = false,
        student_id_number = Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.student_id_number)),
        step_key = StepKeys.Normalize(item.step_key ?? ""),
        status = Json.NullIfEmpty(item.status),
        source_event_id = Json.NullIfEmpty(item.source_event_id),
        result = "failed",
        error,
        code,
        student_id = studentId,
    };

    // buildFailure with the { student_id, step_id } extras.
    private static object BuildFailureWithStudentStep(CompletionItem item, string error, string? code, string studentId, int stepId) => new
    {
        success = false,
        student_id_number = Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.student_id_number)),
        step_key = StepKeys.Normalize(item.step_key ?? ""),
        status = Json.NullIfEmpty(item.status),
        source_event_id = Json.NullIfEmpty(item.source_event_id),
        result = "failed",
        error,
        code,
        student_id = studentId,
        step_id = stepId,
    };

    // Parse a stored response_body back into an object to replay verbatim, matching
    // safeJsonParse's fallback when the stored text is missing/invalid.
    private static object ParseStoredBody(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return new { success = false, error = "Stored response unavailable" };
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.Clone();
        }
        catch
        {
            return new { success = false, error = "Stored response unavailable" };
        }
    }

    // Whether an outcome body carries success: true (used for the batch summary).
    private static bool IsSuccess(object body)
    {
        if (body is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("success", out var prop)
                && prop.ValueKind == JsonValueKind.True;
        }

        var successProp = body.GetType().GetProperty("success");
        if (successProp is null)
            return false;
        var value = successProp.GetValue(body);
        return value is bool b && b;
    }

    private sealed class StoredEventRow
    {
        public int response_status { get; set; }
        public string? response_body { get; set; }
    }

    private sealed class CatalogRow
    {
        public int term_id { get; set; }
        public string term_name { get; set; } = "";
        public string? step_key { get; set; }
        public string title { get; set; } = "";
        public int is_active { get; set; }
    }
}
