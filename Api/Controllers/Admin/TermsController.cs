using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin term management + clone, ported from server/routes/admin/terms.ts.
// The old admin router applies adminAuth to every route (authentication, any
// role), then the mutation routes additionally requireRole('admissions_editor',
// 'sysadmin'). So GET is [AdminAuth] (authenticated admin) and the mutations are
// [AdminAuth("admissions_editor", "sysadmin")].
[ApiController]
[Route("api/admin/terms")]
public sealed class TermsController : ControllerBase
{
    private readonly Db _db;

    public TermsController(Db db)
    {
        _db = db;
    }

    public sealed record CreateTermRequest(string? Name, string? Start_date, string? End_date);

    public sealed record CloneTermRequest(string? Name, string? Start_date, string? End_date, int[]? Step_ids);

    // GET /api/admin/terms
    [HttpGet]
    [AdminAuth]
    public async Task<IActionResult> List()
    {
        var terms = await _db.QueryAllAsync<TermWithCounts>(@"
            SELECT t.*,
              (SELECT COUNT(*) FROM steps s WHERE s.term_id = t.id AND s.is_active = 1) as step_count,
              (SELECT COUNT(*) FROM students st WHERE st.term_id = t.id) as student_count
            FROM terms t ORDER BY t.created_at DESC");

        var result = new List<object>();
        foreach (var t in terms)
        {
            result.Add(new
            {
                id = t.id,
                name = t.name,
                start_date = t.start_date,
                end_date = t.end_date,
                is_active = t.is_active,
                created_at = t.created_at,
                step_count = t.step_count,
                student_count = t.student_count,
            });
        }

        return Ok(result);
    }

    // POST /api/admin/terms (admissions_editor+)
    [HttpPost]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Create([FromBody] CreateTermRequest? body)
    {
        var name = body?.Name;
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Name is required" });

        // INSERT and SCOPE_IDENTITY() must run on the same connection, so they go in
        // one statement (Db opens a fresh connection per call outside a transaction).
        var newId = await _db.InsertReturningAsync<int>(
            @"INSERT INTO terms (name, start_date, end_date, is_active) VALUES (@name, @start_date, @end_date, 0);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { name, start_date = NullIfEmpty(body?.Start_date), end_date = NullIfEmpty(body?.End_date) });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "term", newId, "term_create", new { name });

        return Ok(new { success = true, id = newId });
    }

    // PUT /api/admin/terms/:id (admissions_editor+)
    [HttpPut("{id}")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Update(int id, [FromBody] JsonElement body)
    {
        var term = await _db.QueryOneAsync<Term>("SELECT * FROM terms WHERE id = @id", new { id });
        if (term is null)
            return NotFound(new { error = "Term not found" });

        // Mirror Express req.body semantics: a field is "provided" if the key is
        // present at all (even when its JSON value is null). Object.keys order is
        // preserved for the audit `fields` detail.
        var bodyKeys = CollectBodyKeys(body);

        var nameProvided = TryGetProperty(body, "name", out var nameEl);
        var startProvided = TryGetProperty(body, "start_date", out var startEl);
        var endProvided = TryGetProperty(body, "end_date", out var endEl);
        var isActiveProvided = TryGetProperty(body, "is_active", out var isActiveEl);

        string? name = nameProvided ? AsString(nameEl) : null;
        string? startDate = startProvided ? AsString(startEl) : null;
        string? endDate = endProvided ? AsString(endEl) : null;

        var actor = Audit.ResolveActor(HttpContext);
        var auditName = nameProvided ? name : term.name;

        if (isActiveProvided && IsActivating(isActiveEl))
        {
            await _db.TransactionAsync(async txDb =>
            {
                await txDb.ExecuteAsync("UPDATE terms SET is_active = 0");
                await txDb.ExecuteAsync(
                    @"UPDATE terms
                       SET name = COALESCE(@name, name),
                           start_date = @start_date,
                           end_date = @end_date,
                           is_active = 1
                       WHERE id = @id",
                    new
                    {
                        name = nameProvided ? name : null,
                        start_date = startProvided ? startDate : term.start_date,
                        end_date = endProvided ? endDate : term.end_date,
                        id,
                    });
            });

            await Audit.LogAsync(_db, actor, "term", id, "term_update",
                new { name = auditName, fields = bodyKeys });
            return Ok(new { success = true });
        }

        var updates = new List<string>();
        var parameters = new Dapper.DynamicParameters();
        if (nameProvided) { updates.Add("name = @name"); parameters.Add("name", name); }
        if (startProvided) { updates.Add("start_date = @start_date"); parameters.Add("start_date", startDate); }
        if (endProvided) { updates.Add("end_date = @end_date"); parameters.Add("end_date", endDate); }
        if (isActiveProvided) { updates.Add("is_active = @is_active"); parameters.Add("is_active", IsTruthy(isActiveEl) ? 1 : 0); }

        if (updates.Count == 0)
            return BadRequest(new { error = "No fields to update" });

        parameters.Add("id", id);
        await _db.ExecuteAsync($"UPDATE terms SET {string.Join(", ", updates)} WHERE id = @id", parameters);

        await Audit.LogAsync(_db, actor, "term", id, "term_update",
            new { name = auditName, fields = bodyKeys });
        return Ok(new { success = true });
    }

    // POST /api/admin/terms/:id/clone (admissions_editor+)
    [HttpPost("{id}/clone")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Clone(int id, [FromBody] CloneTermRequest? body)
    {
        var sourceTermId = id;
        var name = body?.Name;
        var startDate = body?.Start_date;
        var endDate = body?.End_date;
        var stepIds = body?.Step_ids;

        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "name is required" });
        if (stepIds is null || stepIds.Length == 0)
            return BadRequest(new { error = "step_ids must be a non-empty array" });

        var sourceTerm = await _db.QueryOneAsync<Term>("SELECT * FROM terms WHERE id = @sourceTermId", new { sourceTermId });
        if (sourceTerm is null)
            return NotFound(new { error = "Source term not found" });

        var sourceSteps = await _db.QueryAllAsync<Step>(
            "SELECT * FROM steps WHERE term_id = @sourceTermId AND id IN @stepIds ORDER BY sort_order",
            new { sourceTermId, stepIds });

        if (sourceSteps.Count == 0)
            return BadRequest(new { error = "No matching steps found for source term" });

        var actor = Audit.ResolveActor(HttpContext);

        var result = await _db.TransactionAsync(async txDb =>
        {
            // INSERT + SCOPE_IDENTITY() must be one batch: SCOPE_IDENTITY is scope/batch-scoped
            // and returns NULL in a separate command, even on the same transaction/connection.
            var newTermId = await txDb.InsertReturningAsync<int>(
                @"INSERT INTO terms (name, start_date, end_date, is_active) VALUES (@name, @start_date, @end_date, 0);
                  SELECT CAST(SCOPE_IDENTITY() AS int);",
                new { name, start_date = NullIfEmpty(startDate), end_date = NullIfEmpty(endDate) });

            var clonedSteps = new List<Step>();
            foreach (var step in sourceSteps)
            {
                var newStepId = await txDb.InsertReturningAsync<int>(
                    @"INSERT INTO steps (title, description, icon, sort_order, deadline, deadline_date, guide_content, links, required_tags, required_tag_mode, excluded_tags, contact_info, term_id, step_key, is_active, is_public, is_optional)
                       VALUES (@title, @description, @icon, @sort_order, @deadline, @deadline_date, @guide_content, @links, @required_tags, @required_tag_mode, @excluded_tags, @contact_info, @term_id, @step_key, @is_active, @is_public, @is_optional);
                      SELECT CAST(SCOPE_IDENTITY() AS int);",
                    new
                    {
                        step.title,
                        step.description,
                        step.icon,
                        step.sort_order,
                        step.deadline,
                        step.deadline_date,
                        step.guide_content,
                        step.links,
                        step.required_tags,
                        required_tag_mode = step.required_tag_mode ?? "any",
                        step.excluded_tags,
                        step.contact_info,
                        term_id = newTermId,
                        step.step_key,
                        is_active = step.is_active ?? 1,
                        is_public = step.is_public ?? 0,
                        is_optional = step.is_optional ?? 0,
                    });

                var clonedStep = await txDb.QueryOneAsync<Step>("SELECT * FROM steps WHERE id = @newStepId", new { newStepId });
                clonedSteps.Add(clonedStep!);
            }

            await Audit.LogAsync(txDb, actor, "term", newTermId, "term_create",
                new { name, clonedFrom = sourceTermId, stepCount = clonedSteps.Count });

            var newTerm = await txDb.QueryOneAsync<Term>("SELECT * FROM terms WHERE id = @newTermId", new { newTermId });
            return new { term = newTerm, steps = clonedSteps };
        });

        return Ok(result);
    }

    // DELETE /api/admin/terms/:id (admissions_editor+)
    [HttpDelete("{id}")]
    [AdminAuth("admissions_editor", "sysadmin")]
    public async Task<IActionResult> Delete(int id)
    {
        var term = await _db.QueryOneAsync<Term>("SELECT * FROM terms WHERE id = @id", new { id });
        if (term is null)
            return NotFound(new { error = "Term not found" });

        var studentCount = await _db.QueryOneAsync<int>("SELECT COUNT(*) as count FROM students WHERE term_id = @id", new { id });
        if (studentCount > 0)
            return Conflict(new { error = "Cannot delete a term that still has students assigned" });

        var actor = Audit.ResolveActor(HttpContext);

        await _db.TransactionAsync(async txDb =>
        {
            var steps = await txDb.QueryAllAsync<StepIdTitle>("SELECT id, title FROM steps WHERE term_id = @id", new { id });

            foreach (var step in steps)
            {
                await txDb.ExecuteAsync("DELETE FROM student_progress WHERE step_id = @stepId", new { stepId = step.id });
                await txDb.ExecuteAsync("DELETE FROM steps WHERE id = @stepId", new { stepId = step.id });
                await Audit.LogAsync(txDb, actor, "step", step.id, "step_delete",
                    new { title = step.title, deletedWithTerm = id });
            }

            await txDb.ExecuteAsync("DELETE FROM terms WHERE id = @id", new { id });
            await Audit.LogAsync(txDb, actor, "term", id, "term_delete",
                new { name = term.name, deletedStepCount = steps.Count });
        });

        return Ok(new { success = true });
    }

    // Mirrors the old `value || null` coalescing (JS treats "" as falsy).
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // True when the request body is a JSON object containing `key`. Mirrors
    // Express's `key in req.body` / `req.body.key !== undefined` (a key present
    // with a JSON null value still counts as provided).
    private static bool TryGetProperty(JsonElement body, string key, out JsonElement value)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out value))
            return true;
        value = default;
        return false;
    }

    // String value of a JSON property, treating JSON null as SQL/C# null.
    private static string? AsString(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }

    // Mirrors `is_active === 1 || is_active === true` (the activation branch).
    private static bool IsActivating(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n)) return n == 1;
        return false;
    }

    // Mirrors `is_active ? 1 : 0` JS truthiness for the dynamic-update branch.
    private static bool IsTruthy(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            case JsonValueKind.Number:
                return el.TryGetDouble(out var d) && d != 0;
            case JsonValueKind.String:
                return el.GetString()!.Length > 0;
            default:
                return true;
        }
    }

    // Mirrors `Object.keys(req.body)` for the audit `fields` detail: every key
    // present in the request body, in order.
    private static List<string> CollectBodyKeys(JsonElement body)
    {
        var keys = new List<string>();
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
                keys.Add(prop.Name);
        }
        return keys;
    }

    private sealed class TermWithCounts
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public string? start_date { get; set; }
        public string? end_date { get; set; }
        public int is_active { get; set; }
        public DateTime created_at { get; set; }
        public int step_count { get; set; }
        public int student_count { get; set; }
    }

    private sealed class StepIdTitle
    {
        public int id { get; set; }
        public string title { get; set; } = "";
    }
}
