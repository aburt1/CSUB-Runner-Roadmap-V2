using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin steps CRUD.
//
// Class-level [AdminAuth] is the default-deny backstop: every action requires at least
// an authenticated admin even if a future action is added without its own attribute.
// The GET list needs only that; every mutation additionally requires the
// 'admissions_editor' or 'sysadmin' role via [AdminAuth("admissions_editor", "sysadmin")].
[ApiController]
[Route("api/admin/steps")]
[AdminAuth]
public sealed class StepsController : ControllerBase
{
    private readonly Db _db;

    public StepsController(Db db)
    {
        _db = db;
    }

    // Request DTOs. Bound case-insensitively and validated by hand. Mutable fields are
    // nullable so we can distinguish "absent" (null) from "present" on partial updates.
    public sealed class ReorderItem
    {
        public int id { get; set; }
        public int sort_order { get; set; }
    }

    public sealed class ReorderRequest
    {
        public List<ReorderItem>? order { get; set; }
    }

    public sealed class BulkStatusRequest
    {
        public List<int>? stepIds { get; set; }
        public int? is_active { get; set; }
    }

    // GET /api/admin/steps — list all steps (including inactive), optional ?term_id=
    // Covered by the class-level [AdminAuth] (any authenticated admin); no extra role gate.
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var termId = QueryHelpers.ParseTermId(Request);
        IReadOnlyList<Step> steps;
        if (termId is not null)
        {
            steps = await _db.QueryAllAsync<Step>(
                "SELECT * FROM steps WHERE term_id = @termId ORDER BY sort_order",
                new { termId });
        }
        else
        {
            steps = await _db.QueryAllAsync<Step>("SELECT * FROM steps ORDER BY sort_order");
        }
        return Ok(steps);
    }

    // POST /api/admin/steps — create a new step (admissions_editor+)
    [HttpPost]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Create([FromBody] JsonElement body)
    {
        var title = GetString(body, "title");
        var termIdRaw = GetElement(body, "term_id");

        if (string.IsNullOrEmpty(title))
            return BadRequest(new { error = "Title is required" });
        if (termIdRaw is null || termIdRaw.Value.ValueKind == JsonValueKind.Null)
            return BadRequest(new { error = "term_id is required" });

        // Accept a JSON number or a numeric string.
        var termId = ParseIntLike(termIdRaw.Value);
        if (termId is null)
            return BadRequest(new { error = "Invalid term_id" });

        var term = await _db.QueryOneAsync<IdRow>("SELECT id FROM terms WHERE id = @id", new { id = termId.Value });
        if (term is null)
            return BadRequest(new { error = "Invalid term_id" });

        var nextStepKey = await StepKeys.GetUniqueForTermAsync(
            _db, termId.Value,
            stepKey: GetString(body, "step_key"),
            title: title,
            fallback: "step");

        var maxOrder = await _db.QueryOneAsync<int?>(
            "SELECT MAX(sort_order) FROM steps WHERE term_id = @termId",
            new { termId = termId.Value });

        // order = sort_order ?? (max || 0) + 1
        var sortOrderInput = GetInt(body, "sort_order");
        var order = sortOrderInput ?? (maxOrder ?? 0) + 1;

        var description = GetStringOrNull(body, "description");
        var icon = GetStringOrNull(body, "icon");
        var deadline = GetStringOrNull(body, "deadline");
        var deadlineDate = GetStringOrNull(body, "deadline_date");
        var guideContent = GetStringOrNull(body, "guide_content");
        var links = SerializeOrNull(body, "links");
        var requiredTags = SerializeOrNull(body, "required_tags");
        var requiredTagMode = GetString(body, "required_tag_mode") == "all" ? "all" : "any";
        var excludedTags = SerializeOrNull(body, "excluded_tags");
        var contactInfo = SerializeOrNull(body, "contact_info");
        var isPublic = IsTruthy(body, "is_public") ? 1 : 0;
        var isOptional = IsTruthy(body, "is_optional") ? 1 : 0;

        var newId = await _db.InsertReturningAsync<int>(
            @"INSERT INTO steps (title, description, icon, sort_order, deadline, deadline_date, guide_content, links, required_tags, required_tag_mode, excluded_tags, contact_info, term_id, step_key, is_active, is_public, is_optional)
              VALUES (@title, @description, @icon, @sort_order, @deadline, @deadline_date, @guide_content, @links, @required_tags, @required_tag_mode, @excluded_tags, @contact_info, @term_id, @step_key, 1, @is_public, @is_optional);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new
            {
                title,
                description,
                icon,
                sort_order = order,
                deadline,
                deadline_date = deadlineDate,
                guide_content = guideContent,
                links,
                required_tags = requiredTags,
                required_tag_mode = requiredTagMode,
                excluded_tags = excludedTags,
                contact_info = contactInfo,
                term_id = termId.Value,
                step_key = nextStepKey,
                is_public = isPublic,
                is_optional = isOptional,
            });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "step", newId, "step_create",
            new { title, stepKey = nextStepKey });

        return Ok(new { success = true, id = newId });
    }

    // PUT /api/admin/steps/reorder — bulk update sort_order (admissions_editor+)
    [HttpPut("reorder")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest? body)
    {
        if (body?.order is null)
            return BadRequest(new { error = "order must be an array of {id, sort_order}" });

        await _db.TransactionAsync(async txDb =>
        {
            foreach (var item in body.order)
            {
                await txDb.ExecuteAsync(
                    "UPDATE steps SET sort_order = @sort_order WHERE id = @id",
                    new { sort_order = item.sort_order, id = item.id });
            }
        });

        return Ok(new { success = true });
    }

    // PUT /api/admin/steps/bulk-status — bulk activate/deactivate (admissions_editor+)
    [HttpPut("bulk-status")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> BulkStatus([FromBody] BulkStatusRequest? body)
    {
        if (body?.stepIds is null || (body.is_active != 0 && body.is_active != 1))
            return BadRequest(new { error = "stepIds (array) and is_active (0|1) required" });

        var isActive = body.is_active.Value;
        var stepIds = body.stepIds;
        var actor = Audit.ResolveActor(HttpContext);

        await _db.TransactionAsync(async txDb =>
        {
            foreach (var id in stepIds)
            {
                await txDb.ExecuteAsync(
                    "UPDATE steps SET is_active = @is_active WHERE id = @id",
                    new { is_active = isActive, id });
                await Audit.LogAsync(txDb, actor, "step", id,
                    isActive == 1 ? "step_restore" : "step_delete",
                    new { bulk = true });
            }
        });

        return Ok(new { success = true });
    }

    // PUT /api/admin/steps/:id — update a step (admissions_editor+)
    [HttpPut("{id}")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Update(int id, [FromBody] JsonElement body)
    {
        var step = await _db.QueryOneAsync<Step>("SELECT * FROM steps WHERE id = @id", new { id });
        if (step is null)
            return NotFound(new { error = "Step not found" });

        // When term_id is present on the body, parse it; otherwise keep the step's current term_id.
        int? requestedTermId;
        var termIdRaw = GetElement(body, "term_id");
        if (termIdRaw is not null)
            requestedTermId = ParseIntLike(termIdRaw.Value);
        else
            requestedTermId = step.term_id;

        // if (!requestedTermId) — guards null AND 0 (parseInt failure yields NaN -> falsy)
        if (requestedTermId is null || requestedTermId.Value == 0)
            return BadRequest(new { error = "term_id is required" });

        var term = await _db.QueryOneAsync<IdRow>("SELECT id FROM terms WHERE id = @id", new { id = requestedTermId.Value });
        if (term is null)
            return BadRequest(new { error = "Invalid term_id" });

        // Build the dynamic SET clause exactly over the same field list/order.
        string[] fields =
        {
            "title", "description", "icon", "sort_order", "deadline", "deadline_date",
            "guide_content", "links", "required_tags", "required_tag_mode", "excluded_tags",
            "contact_info", "term_id", "is_active", "is_public", "is_optional",
        };

        var setClauses = new List<string>();
        // Dapper accepts IDictionary<string, object?> directly — no wrapper needed.
        var parameters = new Dictionary<string, object?>();

        foreach (var field in fields)
        {
            var element = GetElement(body, field);
            if (element is null)
                continue; // field absent (undefined) — skip

            var paramName = "p_" + field;
            setClauses.Add($"{field} = @{paramName}");

            if (field == "links" || field == "required_tags" || field == "excluded_tags" || field == "contact_info")
            {
                // Serialize a truthy value to its JSON text; a falsy value stores NULL.
                parameters.Add(paramName, SerializeElementOrNull(element.Value));
            }
            else if (field == "required_tag_mode")
            {
                var modeStr = element.Value.ValueKind == JsonValueKind.String ? element.Value.GetString() : null;
                parameters.Add(paramName, modeStr == "all" ? "all" : "any");
            }
            else
            {
                parameters.Add(paramName, ElementToScalar(element.Value));
            }
        }

        // step_key regeneration: when step_key provided, or step has none, or term changed.
        var stepKeyElement = GetElement(body, "step_key");
        var bodyTitleElement = GetElement(body, "title");
        var termChanged = requestedTermId.Value != step.term_id;
        var shouldUpdateStepKey = stepKeyElement is not null || string.IsNullOrEmpty(step.step_key) || termChanged;

        if (shouldUpdateStepKey)
        {
            // stepKey: body.step_key ?? step.step_key ; title: body.title ?? step.title
            var stepKeyArg = stepKeyElement is not null
                ? (stepKeyElement.Value.ValueKind == JsonValueKind.String ? stepKeyElement.Value.GetString() : null)
                : step.step_key;
            var titleArg = bodyTitleElement is not null
                ? (bodyTitleElement.Value.ValueKind == JsonValueKind.String ? bodyTitleElement.Value.GetString() : null)
                : step.title;

            var newStepKey = await StepKeys.GetUniqueForTermAsync(
                _db, requestedTermId.Value,
                stepKey: stepKeyArg,
                title: titleArg,
                fallback: $"step-{id}",
                excludeStepId: id);

            setClauses.Add("step_key = @p_step_key");
            parameters.Add("p_step_key", newStepKey);
        }

        if (setClauses.Count == 0)
            return BadRequest(new { error = "No fields to update" });

        parameters["id"] = id;
        var sql = $"UPDATE steps SET {string.Join(", ", setClauses)} WHERE id = @id";
        await _db.ExecuteAsync(sql, parameters);

        // Detect restore vs regular update: body.is_active === 1 && step.is_active === 0.
        // The JS `=== 1` is a strict number compare, so a string "1" does NOT match.
        var bodyIsActiveEl = GetElement(body, "is_active");
        var bodyIsActiveIsOne =
            bodyIsActiveEl is not null
            && bodyIsActiveEl.Value.ValueKind == JsonValueKind.Number
            && bodyIsActiveEl.Value.TryGetInt32(out var ia)
            && ia == 1;
        var action = (bodyIsActiveIsOne && step.is_active == 0) ? "step_restore" : "step_update";

        // details.fields = shouldUpdateStepKey ? [...keys(body), 'step_key'] : keys(body)
        var bodyKeys = GetObjectKeys(body);
        var detailFields = new List<string>(bodyKeys);
        if (shouldUpdateStepKey)
            detailFields.Add("step_key");

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "step", id, action,
            new { title = step.title, fields = detailFields });

        return Ok(new { success = true });
    }

    // DELETE /api/admin/steps/:id — soft delete (admissions_editor+)
    [HttpDelete("{id}")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Delete(int id)
    {
        // No 404 on purpose: delete is idempotent; the title lookup is only for the
        // audit entry.
        var step = await _db.QueryOneAsync<TitleRow>("SELECT title FROM steps WHERE id = @id", new { id });
        await _db.ExecuteAsync("UPDATE steps SET is_active = 0 WHERE id = @id", new { id });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "step", id, "step_delete",
            new { title = step?.title });

        return Ok(new { success = true });
    }

    // POST /api/admin/steps/:id/duplicate — duplicate a step (admissions_editor+)
    [HttpPost("{id}/duplicate")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Duplicate(int id)
    {
        var step = await _db.QueryOneAsync<Step>("SELECT * FROM steps WHERE id = @id", new { id });
        if (step is null)
            return NotFound(new { error = "Step not found" });

        var maxOrder = await _db.QueryOneAsync<int?>(
            "SELECT MAX(sort_order) FROM steps WHERE term_id = @termId",
            new { termId = step.term_id });
        var newOrder = (maxOrder ?? 0) + 1;

        var duplicatedStepKey = await StepKeys.GetUniqueForTermAsync(
            _db, step.term_id ?? 0,
            stepKey: $"{(string.IsNullOrEmpty(step.step_key) ? step.title : step.step_key)}-copy",
            title: $"{step.title} Copy",
            fallback: $"step-{step.id}-copy");

        var newId = await _db.InsertReturningAsync<int>(
            @"INSERT INTO steps (title, description, icon, sort_order, deadline, deadline_date, guide_content, links, required_tags, required_tag_mode, excluded_tags, contact_info, term_id, step_key, is_active, is_public, is_optional)
              VALUES (@title, @description, @icon, @sort_order, @deadline, @deadline_date, @guide_content, @links, @required_tags, @required_tag_mode, @excluded_tags, @contact_info, @term_id, @step_key, 1, @is_public, @is_optional);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new
            {
                title = step.title + " (Copy)",
                description = step.description,
                icon = step.icon,
                sort_order = newOrder,
                deadline = step.deadline,
                deadline_date = step.deadline_date,
                guide_content = step.guide_content,
                links = step.links,
                required_tags = step.required_tags,
                required_tag_mode = step.required_tag_mode ?? "any",
                excluded_tags = step.excluded_tags,
                contact_info = step.contact_info,
                term_id = step.term_id,
                step_key = duplicatedStepKey,
                is_public = step.is_public ?? 0,
                is_optional = step.is_optional ?? 0,
            });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "step", newId, "step_create",
            new { title = step.title + " (Copy)", duplicatedFrom = id, stepKey = duplicatedStepKey });

        return Ok(new { success = true, id = newId });
    }

    // ---- JSON body helpers ----

    private static JsonElement? GetElement(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        return body.TryGetProperty(name, out var el) ? el : null;
    }

    // String for a present property; null if absent or JSON null/non-string.
    private static string? GetString(JsonElement body, string name)
    {
        var el = GetElement(body, name);
        if (el is null) return null;
        return el.Value.ValueKind == JsonValueKind.String ? el.Value.GetString() : null;
    }

    // For create's `x || null` fields (description/icon/deadline/...): a present
    // value is used, but an empty string (falsy in JS) collapses to SQL NULL.
    private static string? GetStringOrNull(JsonElement body, string name)
    {
        var s = GetString(body, name);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    // Int for a present numeric (or numeric-string) property; null otherwise.
    private static int? GetInt(JsonElement body, string name)
    {
        var el = GetElement(body, name);
        if (el is null) return null;
        return ParseIntLike(el.Value);
    }

    // Truthiness for is_public / is_optional (boolean or number, both accepted).
    private static bool IsTruthy(JsonElement body, string name)
    {
        var el = GetElement(body, name);
        return el is not null && Json.IsTruthy(el.Value);
    }

    // Serialize a named body field to its JSON text, or null when absent/falsy.
    private static string? SerializeOrNull(JsonElement body, string name)
    {
        var el = GetElement(body, name);
        if (el is null) return null;
        return SerializeElementOrNull(el.Value);
    }

    // Serialize a truthy value to its JSON text; JSON null / empty values map to SQL NULL.
    private static string? SerializeElementOrNull(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.False:
                return null;
            case JsonValueKind.String:
                return string.IsNullOrEmpty(el.GetString()) ? null : el.GetRawText();
            case JsonValueKind.Number:
                return el.TryGetDouble(out var d) && d == 0 ? null : el.GetRawText();
            default:
                return el.GetRawText();
        }
    }

    // Generic scalar binding for the plain string/number/bool columns in update.
    private static object? ElementToScalar(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return el.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return l;
                if (el.TryGetDouble(out var d)) return d;
                return el.GetRawText();
            default:
                return el.GetRawText();
        }
    }

    // Accept a JSON number or a leading-digits string, returning null when neither.
    private static int? ParseIntLike(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetInt32(out var n)) return n;
            if (el.TryGetDouble(out var d)) return (int)d;
            return null;
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.IsNullOrEmpty(s)) return null;
            return JsParse.LeadingInt(s);
        }
        return null;
    }

    // Keys present on the request body object, in document order.
    private static List<string> GetObjectKeys(JsonElement body)
    {
        var keys = new List<string>();
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
                keys.Add(prop.Name);
        }
        return keys;
    }

    private sealed class IdRow
    {
        public int id { get; set; }
    }

    private sealed class TitleRow
    {
        public string? title { get; set; }
    }
}
