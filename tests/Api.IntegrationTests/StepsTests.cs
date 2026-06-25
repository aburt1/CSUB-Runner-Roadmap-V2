using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the Steps area: GET /api/steps (anonymous + authed,
// term/tag filtered), GET /api/steps/progress, and PUT /api/steps/{id}/status.
// Endpoints: Api/Controllers/StepsController.cs.
[Collection("api")]
public class StepsTests
{
    private readonly WebAppFixture _fx;

    public StepsTests(WebAppFixture fx) => _fx = fx;

    // Resolve a seeded Fall-2026 step id by its stable step_key. Step ids are not
    // fixed across runs, so we look them up from the public steps listing.
    private async Task<int> StepIdByKeyAsync(string stepKey)
    {
        var steps = await (await _fx.Anonymous().GetAsync("/api/steps")).Content.ReadFromJsonAsync<JsonElement>();
        foreach (var s in steps.EnumerateArray())
        {
            if (s.TryGetProperty("step_key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == stepKey)
                return s.GetProperty("id").GetInt32();
        }
        throw new Xunit.Sdk.XunitException($"Seeded step_key '{stepKey}' not found in /api/steps");
    }

    // ---- GET /api/steps ----

    [Fact]
    public async Task Get_steps_anonymous_returns_array_of_active_steps()
    {
        var res = await _fx.Anonymous().GetAsync("/api/steps");
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        // The deterministic seed has 22 Fall-2026 steps; assert a lower bound, not exact.
        Assert.True(body.GetArrayLength() >= 22);

        // The public "accepted" step is present with its verbatim snake_case fields.
        var accepted = body.EnumerateArray().Single(s => s.GetProperty("step_key").GetString() == "accepted");
        Assert.Equal(1, accepted.GetProperty("sort_order").GetInt32());
        Assert.Equal(1, accepted.GetProperty("is_public").GetInt32());
    }

    [Fact]
    public async Task Get_steps_anonymous_is_sorted_by_sort_order()
    {
        var body = await (await _fx.Anonymous().GetAsync("/api/steps")).Content.ReadFromJsonAsync<JsonElement>();
        var orders = body.EnumerateArray().Select(s => s.GetProperty("sort_order").GetInt32()).ToList();
        var sorted = orders.OrderBy(x => x).ToList();
        Assert.Equal(sorted, orders);
    }

    [Fact]
    public async Task Get_steps_authed_student_is_filtered_to_their_term()
    {
        // A fresh dev-login student is assigned the active term (Fall 2026, id 1),
        // so the authed listing returns that term's steps including "apply-for-housing".
        var (client, _) = await _fx.StudentAsync("Steps Filter", $"u{Guid.NewGuid():N}@t.edu");

        var body = await (await client.GetAsync("/api/steps")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        var keys = body.EnumerateArray()
            .Where(s => s.TryGetProperty("step_key", out var k) && k.ValueKind == JsonValueKind.String)
            .Select(s => s.GetProperty("step_key").GetString())
            .ToHashSet();

        Assert.Contains("apply-for-housing", keys);
        Assert.Contains("accepted", keys);
        // Every returned step belongs to the student's term (id 1).
        foreach (var s in body.EnumerateArray())
            Assert.Equal(1, s.GetProperty("term_id").GetInt32());
    }

    // ---- GET /api/steps/progress ----

    [Fact]
    public async Task Get_progress_anonymous_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/steps/progress");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Get_progress_returns_progress_tags_and_term()
    {
        // A new student starts with the "accepted" step auto-completed.
        var (client, _) = await _fx.StudentAsync("Progress Shape", $"u{Guid.NewGuid():N}@t.edu");

        var res = await client.GetAsync("/api/steps/progress");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Array, body.GetProperty("progress").ValueKind);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("tags").ValueKind);

        // Term info reflects the student's assigned active term.
        var term = body.GetProperty("term");
        Assert.Equal(1, term.GetProperty("id").GetInt32());
        Assert.Equal("Fall 2026", term.GetProperty("name").GetString());

        // The auto-completed "accepted" step shows up in progress with an ISO-8601 UTC
        // timestamp ending in "Z".
        var acceptedId = await StepIdByKeyAsync("accepted");
        var acceptedProgress = body.GetProperty("progress").EnumerateArray()
            .Single(p => p.GetProperty("step_id").GetInt32() == acceptedId);
        Assert.Equal("completed", acceptedProgress.GetProperty("status").GetString());
        var completedAt = acceptedProgress.GetProperty("completed_at").GetString();
        Assert.NotNull(completedAt);
        Assert.EndsWith("Z", completedAt!);
    }

    // ---- PUT /api/steps/{id}/status ----

    [Fact]
    public async Task Put_status_anonymous_is_401()
    {
        var housingId = await StepIdByKeyAsync("apply-for-housing");
        var res = await _fx.Anonymous().PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "completed" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Put_status_invalid_status_is_400_with_exact_message()
    {
        var (client, _) = await _fx.StudentAsync("Bad Status", $"u{Guid.NewGuid():N}@t.edu");
        var housingId = await StepIdByKeyAsync("apply-for-housing");

        var res = await client.PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("status must be completed or not_completed", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_status_completes_optional_no_tag_step_then_unsets_and_is_idempotent()
    {
        var (client, _) = await _fx.StudentAsync("Housing Optional", $"u{Guid.NewGuid():N}@t.edu");
        var housingId = await StepIdByKeyAsync("apply-for-housing");

        // First completion creates the progress row.
        var res = await client.PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "completed" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(housingId, body.GetProperty("stepId").GetInt32());
        Assert.Equal("completed", body.GetProperty("status").GetString());
        Assert.Equal("created", body.GetProperty("result").GetString());
        var completedAt = body.GetProperty("completedAt").GetString();
        Assert.NotNull(completedAt);
        Assert.EndsWith("Z", completedAt!);

        // Completing again with no changes is a no-op.
        var again = await client.PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "completed" });
        again.EnsureSuccessStatusCode();
        var againBody = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("noop", againBody.GetProperty("result").GetString());
        Assert.Equal("completed", againBody.GetProperty("status").GetString());

        // It now appears completed in the student's progress.
        var progress = await (await client.GetAsync("/api/steps/progress")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(progress.GetProperty("progress").EnumerateArray(),
            p => p.GetProperty("step_id").GetInt32() == housingId
                 && p.GetProperty("status").GetString() == "completed");

        // not_completed removes the row -> updated, status not_completed, null completedAt.
        var clear = await client.PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "not_completed" });
        clear.EnsureSuccessStatusCode();
        var clearBody = await clear.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", clearBody.GetProperty("result").GetString());
        Assert.Equal("not_completed", clearBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, clearBody.GetProperty("completedAt").ValueKind);

        // Clearing an absent row is a no-op.
        var clearAgain = await client.PutAsJsonAsync($"/api/steps/{housingId}/status", new { status = "not_completed" });
        clearAgain.EnsureSuccessStatusCode();
        var clearAgainBody = await clearAgain.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("noop", clearAgainBody.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Put_status_on_required_non_optional_step_is_403()
    {
        // "accepted" is a required (is_optional = 0) step; students may only touch optional ones.
        var (client, _) = await _fx.StudentAsync("Required Step", $"u{Guid.NewGuid():N}@t.edu");
        var acceptedId = await StepIdByKeyAsync("accepted");

        var res = await client.PutAsJsonAsync($"/api/steps/{acceptedId}/status", new { status = "not_completed" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Students may only update optional steps", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_status_on_optional_step_requiring_a_missing_tag_is_403()
    {
        // "register-for-future-runner-day" is optional but requires tag "freshman".
        // A fresh dev-login student has no tags, so the step does not apply to them.
        var (client, _) = await _fx.StudentAsync("No Freshman Tag", $"u{Guid.NewGuid():N}@t.edu");
        var runnerDayId = await StepIdByKeyAsync("register-for-future-runner-day");

        var res = await client.PutAsJsonAsync($"/api/steps/{runnerDayId}/status", new { status = "completed" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Step does not apply to this student", body.GetProperty("error").GetString());
    }
}
