using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Api.Controllers;

// Integration push API, ported from server/routes/integrations.ts.
// Every action sits behind [IntegrationAuth] (mirrors router.use(integrationAuth)).
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

    // Per-errorCode HTTP status, mirroring ERROR_STATUS in the old route file.
    private static int StatusForErrorCode(string? code) => code switch
    {
        "invalid_student_id_number" => 400,
        "invalid_step_key" => 400,
        "student_term_missing" => 409,
        "student_not_found" => 404,
        "step_not_found" => 404,
        "step_inactive" => 409,
        "duplicate_student_id_number" => 409,
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

    // GET /api/integrations/v1/step-catalog
    [HttpGet("step-catalog")]
    public async Task<IActionResult> StepCatalog()
    {
        int? termId = null;
        var rawTermId = Request.Query["term_id"].ToString();
        if (!string.IsNullOrEmpty(rawTermId))
        {
            // Mirror JS `parseInt(x, 10)`: parse the leading integer, then treat 0/NaN as
            // falsy so the old `req.query.term_id && !termId` guard returns 400.
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

    // Core single-item pipeline, ported from processCompletionItem(). The PUT and each
    // batch item run through this. Returns the HTTP status + body to send/collect.
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

    private async Task<Outcome> StoreIntegrationEventAsync(int integrationClientId, string sourceEventId, CompletionItem item, Outcome outcome)
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
                    studentIdNumber = Json.NullIfEmpty(Progress.NormalizeStudentIdNumber(item.student_id_number)),
                    stepKey = StepKeys.Normalize(item.step_key ?? ""),
                    requestBody = JsonSerializer.Serialize(item, StoreOptions),
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

    // Mirrors buildFailure(): fixed-shape failure body (no student/step ids).
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
