using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration push API: PUT /step-completions, POST /step-completions/batch,
// GET /step-catalog. Gated by [IntegrationAuth] (X-Integration-Key: dev-integration-key).
// Endpoints: Api/Controllers/IntegrationsController.cs.
//
// Shared-DB rules: every write uses a UNIQUE source_event_id (Guid) and targets the
// seeded student emplid 001000000 (seed-student-001) + step_key "submit-final-documents"
// in term 1. To avoid cross-test interference, the final state pushed for that step is
// "not_completed" where it matters, and presence/shape — not global counts — is asserted.
[Collection("api")]
public class IntegrationsTests
{
    private readonly WebAppFixture _fx;

    public IntegrationsTests(WebAppFixture fx) => _fx = fx;

    private const string Emplid = "001000000";
    private const string StepKey = "submit-final-documents";

    // ---- auth gate ----

    [Fact]
    public async Task No_key_returns_401()
    {
        // Anonymous client has no X-Integration-Key and no Bearer credential.
        var res = await _fx.Anonymous().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Integration authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Catalog_without_key_returns_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/integrations/v1/step-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Integration authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_key_returns_401_invalid_credentials()
    {
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Add("X-Integration-Key", "not-the-real-key");

        var res = await client.GetAsync("/api/integrations/v1/step-catalog");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid integration credentials", body.GetProperty("error").GetString());
    }

    // ---- PUT /step-completions: happy path ----

    [Fact]
    public async Task Put_step_completion_succeeds()
    {
        var sourceEventId = Guid.NewGuid().ToString();
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = sourceEventId });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(Emplid, body.GetProperty("student_id_number").GetString());
        Assert.Equal(StepKey, body.GetProperty("step_key").GetString());
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal(sourceEventId, body.GetProperty("source_event_id").GetString());
        // result is created or updated depending on prior state; never failed/noop here.
        var result = body.GetProperty("result").GetString();
        // created/updated normally; noop if another test already completed this step
        // for the shared seeded student (all are valid successful outcomes).
        Assert.Contains(result, new[] { "created", "updated", "noop" });
        Assert.False(string.IsNullOrEmpty(body.GetProperty("student_id").GetString()));
        Assert.True(body.GetProperty("step_id").GetInt32() > 0);

        // completed_at is ISO-8601 UTC ending in Z.
        var completedAt = body.GetProperty("completed_at").GetString();
        Assert.NotNull(completedAt);
        Assert.EndsWith("Z", completedAt);

        // Reset to a clean state so concurrent/later tests on this seeded step are unaffected.
        await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
    }

    // ---- PUT /step-completions: idempotent replay ----

    [Fact]
    public async Task Replay_same_source_event_id_is_idempotent()
    {
        var sourceEventId = Guid.NewGuid().ToString();
        // Explicit completed_at makes the replayed body byte-for-byte comparable.
        var completedAt = "2026-03-15T10:30:00.000Z";

        var first = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = sourceEventId, completed_at = completedAt });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(firstBody.GetProperty("success").GetBoolean());
        var firstResult = firstBody.GetProperty("result").GetString();
        Assert.Equal(completedAt, firstBody.GetProperty("completed_at").GetString());

        // Replay the SAME source_event_id. Even with a different status payload, the
        // stored event response is replayed verbatim (the new payload is ignored).
        var second = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = sourceEventId, completed_at = completedAt });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(secondBody.GetProperty("success").GetBoolean());
        Assert.Equal(sourceEventId, secondBody.GetProperty("source_event_id").GetString());
        Assert.Equal("completed", secondBody.GetProperty("status").GetString());
        Assert.Equal(completedAt, secondBody.GetProperty("completed_at").GetString());
        Assert.Equal(firstResult, secondBody.GetProperty("result").GetString());
        Assert.Equal(
            firstBody.GetProperty("step_id").GetInt32(),
            secondBody.GetProperty("step_id").GetInt32());

        // Cleanup: leave the seeded step uncompleted.
        await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
    }

    [Fact]
    public async Task Reapplying_completed_step_is_noop()
    {
        // First completion creates/updates.
        var firstRes = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = Guid.NewGuid().ToString(), completed_at = "2026-04-01T08:00:00.000Z" });
        Assert.Equal(HttpStatusCode.OK, firstRes.StatusCode);

        // Same status + same completed_at + same completed_by, NEW source_event_id ->
        // the progress change is a noop (state already matches).
        var noopRes = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = Guid.NewGuid().ToString(), completed_at = "2026-04-01T08:00:00.000Z" });
        Assert.Equal(HttpStatusCode.OK, noopRes.StatusCode);
        var body = await noopRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("noop", body.GetProperty("result").GetString());
        Assert.Equal("completed", body.GetProperty("status").GetString());

        // Cleanup.
        await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
    }

    // ---- PUT /step-completions: validation ----

    [Fact]
    public async Task Missing_source_event_id_returns_400()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("source_event_id is required", body.GetProperty("error").GetString());
        Assert.Equal("invalid_source_event_id", body.GetProperty("code").GetString());
        Assert.Equal("failed", body.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Invalid_status_returns_400()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "bogus", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("status must be completed, waived, or not_completed", body.GetProperty("error").GetString());
        Assert.Equal("invalid_status", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_student_returns_404_student_not_found()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = "999999999", step_key = StepKey, status = "completed", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Student not found", body.GetProperty("error").GetString());
        Assert.Equal("student_not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_step_returns_404_step_not_found()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = "no-such-step-key-ever", status = "completed", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Step not found in the student term", body.GetProperty("error").GetString());
        Assert.Equal("step_not_found", body.GetProperty("code").GetString());
        // Failure body includes the resolved student_id for step-resolution failures.
        Assert.False(string.IsNullOrEmpty(body.GetProperty("student_id").GetString()));
    }

    [Fact]
    public async Task Invalid_completed_at_returns_400()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = Guid.NewGuid().ToString(), completed_at = "not-a-date" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("completed_at must be a valid ISO timestamp", body.GetProperty("error").GetString());
        Assert.Equal("invalid_completed_at", body.GetProperty("code").GetString());
    }

    // ---- POST /step-completions/batch ----

    [Fact]
    public async Task Batch_processes_items_and_returns_summary()
    {
        var goodEventId = Guid.NewGuid().ToString();
        var badEventId = Guid.NewGuid().ToString();
        var payload = new
        {
            items = new object[]
            {
                new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = goodEventId, completed_at = "2026-05-01T12:00:00.000Z" },
                new { student_id_number = "999999999", step_key = StepKey, status = "completed", source_event_id = badEventId },
            },
        };

        var res = await _fx.Integration().PostAsJsonAsync("/api/integrations/v1/step-completions/batch", payload);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("success").GetBoolean());
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());

        var summary = body.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());

        // First item succeeded.
        var ok = items[0];
        Assert.True(ok.GetProperty("success").GetBoolean());
        Assert.Equal(goodEventId, ok.GetProperty("source_event_id").GetString());
        // Second item failed with student_not_found.
        var bad = items[1];
        Assert.False(bad.GetProperty("success").GetBoolean());
        Assert.Equal("student_not_found", bad.GetProperty("code").GetString());

        // Cleanup the seeded step.
        await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
    }

    [Fact]
    public async Task Batch_empty_items_returns_400()
    {
        var res = await _fx.Integration().PostAsJsonAsync(
            "/api/integrations/v1/step-completions/batch",
            new { items = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("items must be a non-empty array", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_missing_items_returns_400()
    {
        // No "items" property at all -> treated as null -> non-empty-array error.
        var res = await _fx.Integration().PostAsJsonAsync(
            "/api/integrations/v1/step-completions/batch",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("items must be a non-empty array", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Batch_replays_stored_events_idempotently()
    {
        var eventId = Guid.NewGuid().ToString();
        var completedAt = "2026-06-02T09:15:00.000Z";

        // Seed the event via a single PUT.
        var seed = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "completed", source_event_id = eventId, completed_at = completedAt });
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var seedBody = await seed.Content.ReadFromJsonAsync<JsonElement>();
        var seedResult = seedBody.GetProperty("result").GetString();

        // Replay the same source_event_id inside a batch -> stored response replayed verbatim.
        var res = await _fx.Integration().PostAsJsonAsync(
            "/api/integrations/v1/step-completions/batch",
            new { items = new object[] { new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = eventId } } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var item = body.GetProperty("items")[0];
        Assert.True(item.GetProperty("success").GetBoolean());
        Assert.Equal("completed", item.GetProperty("status").GetString());
        Assert.Equal(completedAt, item.GetProperty("completed_at").GetString());
        Assert.Equal(seedResult, item.GetProperty("result").GetString());
        Assert.Equal(1, body.GetProperty("summary").GetProperty("succeeded").GetInt32());

        // Cleanup.
        await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/step-completions",
            new { student_id_number = Emplid, step_key = StepKey, status = "not_completed", source_event_id = Guid.NewGuid().ToString() });
    }

    // ---- GET /step-catalog ----

    [Fact]
    public async Task Catalog_returns_steps_for_all_terms()
    {
        var res = await _fx.Integration().GetAsync("/api/integrations/v1/step-catalog");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.True(rows.GetArrayLength() >= 22);

        // The seeded submit-final-documents step is present in Fall 2026.
        var match = rows.EnumerateArray().FirstOrDefault(r =>
            r.GetProperty("step_key").GetString() == StepKey
            && r.GetProperty("term_id").GetInt32() == 1);
        Assert.Equal(JsonValueKind.Object, match.ValueKind);
        Assert.Equal("Submit final documents", match.GetProperty("title").GetString());
        Assert.Equal("Fall 2026", match.GetProperty("term_name").GetString());
        Assert.Equal(1, match.GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Catalog_filtered_by_term_returns_only_that_term()
    {
        var res = await _fx.Integration().GetAsync("/api/integrations/v1/step-catalog?term_id=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.True(rows.GetArrayLength() >= 22);

        // Every row is term 1 only.
        foreach (var row in rows.EnumerateArray())
            Assert.Equal(1, row.GetProperty("term_id").GetInt32());

        // accepted is the seeded public step (sort 1) and should appear.
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("step_key").GetString() == "accepted");
    }

    [Fact]
    public async Task Catalog_invalid_term_id_returns_400()
    {
        // Non-numeric term_id -> parseInt is NaN -> 400 with exact message.
        var res = await _fx.Integration().GetAsync("/api/integrations/v1/step-catalog?term_id=abc");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("term_id must be a valid number", body.GetProperty("error").GetString());
    }

    // ---- PUT /students: provisioning upsert ----

    // Fresh, never-seeded emplid per test so creates don't collide with the seeded roster.
    private static string FreshEmplid() => "9" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Put_student_create_succeeds()
    {
        var emplid = FreshEmplid();
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Pre Staged", email = $"{emplid}@t.edu", major = "Biology", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal("created", body.GetProperty("result").GetString());
        Assert.Equal(emplid, body.GetProperty("student_id_number").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("student_id").GetString()));

        // Defaulted to the active term.
        var activeTerm = Convert.ToInt32(await _fx.ScalarAsync("SELECT TOP 1 id FROM terms WHERE is_active = 1 ORDER BY id DESC"));
        Assert.Equal(activeTerm, body.GetProperty("term_id").GetInt32());

        // The "accepted" step was auto-completed on create — exactly one row, not just
        // "at least one" (a duplicate insert would still pass a >= 1 check).
        var studentId = body.GetProperty("student_id").GetString();
        var acceptedCount = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM student_progress sp JOIN steps s ON s.id = sp.step_id WHERE sp.student_id = '{studentId}' AND s.step_key = 'accepted'"));
        Assert.Equal(1, acceptedCount);
    }

    [Fact]
    public async Task Put_student_provisioned_accepted_step_renders_completed_via_student_endpoint()
    {
        // TESTS-07 parity: a PUSH-provisioned student must display identically to a
        // sign-in-provisioned one. The auto-complete writes a student_progress row with a
        // NULL status, and the read layer treats NULL as "completed" — so we prove the
        // completed state through the student endpoint (mirroring StepsTests:110-118), not
        // by reading the DB row's status.
        var emplid = FreshEmplid();
        var create = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Push Parity", email = $"{emplid}@t.edu", source_event_id = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal("created", (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        // Dev-login with the SAME emplid links to the provisioned row (emplid_norm match)
        // instead of creating a new student.
        var client = _fx.Anonymous();
        var loginRes = await client.PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "Push Parity", email = $"{emplid}@t.edu", emplid });
        loginRes.EnsureSuccessStatusCode();
        var token = (await loginRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Resolve the seeded "accepted" step id from the public listing (ids aren't fixed).
        var steps = await (await _fx.Anonymous().GetAsync("/api/steps")).Content.ReadFromJsonAsync<JsonElement>();
        var acceptedId = steps.EnumerateArray()
            .Single(s => s.GetProperty("step_key").GetString() == "accepted")
            .GetProperty("id").GetInt32();

        var body = await (await client.GetAsync("/api/steps/progress")).Content.ReadFromJsonAsync<JsonElement>();
        var acceptedProgress = body.GetProperty("progress").EnumerateArray()
            .Single(p => p.GetProperty("step_id").GetInt32() == acceptedId);
        Assert.Equal("completed", acceptedProgress.GetProperty("status").GetString());
        var completedAt = acceptedProgress.GetProperty("completed_at").GetString();
        Assert.NotNull(completedAt);
        Assert.EndsWith("Z", completedAt!);
    }

    [Fact]
    public async Task Put_student_update_existing_only_writes_present_fields()
    {
        var emplid = FreshEmplid();
        // Create with tags + major.
        var create = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Original", tags = "[\"vip\"]", major = "History", source_event_id = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var createBody = await create.Content.ReadFromJsonAsync<JsonElement>();
        var studentId = createBody.GetProperty("student_id").GetString();

        // Update display_name + phone, OMIT tags/major (must be left intact).
        var update = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Renamed", phone = "555-0100", source_event_id = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updateBody = await update.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", updateBody.GetProperty("result").GetString());
        // Same row reused.
        Assert.Equal(studentId, updateBody.GetProperty("student_id").GetString());

        Assert.Equal("Renamed", (string?)await _fx.ScalarAsync($"SELECT display_name FROM students WHERE id = '{studentId}'"));
        Assert.Equal("555-0100", (string?)await _fx.ScalarAsync($"SELECT phone FROM students WHERE id = '{studentId}'"));
        // Omitted fields untouched.
        Assert.Equal("[\"vip\"]", (string?)await _fx.ScalarAsync($"SELECT tags FROM students WHERE id = '{studentId}'"));
        Assert.Equal("History", (string?)await _fx.ScalarAsync($"SELECT major FROM students WHERE id = '{studentId}'"));
    }

    [Fact]
    public async Task Put_student_replay_same_source_event_id_is_idempotent()
    {
        var emplid = FreshEmplid();
        var sourceEventId = Guid.NewGuid().ToString();

        var first = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "First", source_event_id = sourceEventId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("created", firstBody.GetProperty("result").GetString());
        var firstStudentId = firstBody.GetProperty("student_id").GetString();

        // Replay the SAME source_event_id with a different payload -> stored response replayed verbatim.
        var second = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Changed Name", source_event_id = sourceEventId });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("created", secondBody.GetProperty("result").GetString());
        Assert.Equal(firstStudentId, secondBody.GetProperty("student_id").GetString());

        // The replayed payload was ignored: the stored name persists.
        Assert.Equal("First", (string?)await _fx.ScalarAsync($"SELECT display_name FROM students WHERE id = '{firstStudentId}'"));
    }

    [Fact]
    public async Task Put_student_invalid_term_id_returns_404_term_not_found()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid = FreshEmplid(), display_name = "Bad Term", term_id = 999999, source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("term_not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_student_missing_emplid_returns_400()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { display_name = "No Emplid", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_emplid", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_student_missing_display_name_returns_400()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid = FreshEmplid(), display_name = "", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("missing_required_field", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_student_missing_source_event_id_returns_400_not_stored()
    {
        var res = await _fx.Integration().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid = FreshEmplid(), display_name = "No Event" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_source_event_id", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_students_batch_processes_all()
    {
        var goodEmplid = FreshEmplid();
        var payload = new
        {
            items = new object[]
            {
                new { emplid = goodEmplid, display_name = "Batch Good", source_event_id = Guid.NewGuid().ToString() },
                new { emplid = FreshEmplid(), display_name = "Batch Bad Term", term_id = 999999, source_event_id = Guid.NewGuid().ToString() },
            },
        };

        var res = await _fx.Integration().PostAsJsonAsync("/api/integrations/v1/students/batch", payload);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("success").GetBoolean());
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.True(items[0].GetProperty("success").GetBoolean());
        Assert.False(items[1].GetProperty("success").GetBoolean());
        Assert.Equal("term_not_found", items[1].GetProperty("code").GetString());

        var summary = body.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Post_students_batch_empty_items_returns_400()
    {
        var res = await _fx.Integration().PostAsJsonAsync(
            "/api/integrations/v1/students/batch",
            new { items = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("items must be a non-empty array", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_students_batch_over_500_returns_400()
    {
        var items = Enumerable.Range(0, 501)
            .Select(_ => (object)new { emplid = FreshEmplid(), display_name = "X", source_event_id = Guid.NewGuid().ToString() })
            .ToArray();

        var res = await _fx.Integration().PostAsJsonAsync(
            "/api/integrations/v1/students/batch",
            new { items });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Batch size must not exceed 500 items", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_students_without_key_returns_401()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid = FreshEmplid(), display_name = "X", source_event_id = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
