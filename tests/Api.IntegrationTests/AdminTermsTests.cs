using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for /api/admin/terms (Controllers/Admin/TermsController.cs).
//
// Shared-DB rules: term 1 ("Fall 2026") and the 50 seeded students are relied on
// by other tests, so we never rename/delete them. Write tests create their own
// terms with unique names and assert on those.
[Collection("api")]
public class AdminTermsTests
{
    private readonly WebAppFixture _fx;

    public AdminTermsTests(WebAppFixture fx) => _fx = fx;

    private static string UniqueName() => $"T-{Guid.NewGuid():N}";

    // Creates a term via the API and returns its id. Used to set up PUT/clone/delete cases.
    private async Task<(int Id, string Name)> CreateTermAsync()
    {
        var name = UniqueName();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms", new { name });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetInt32(), name);
    }

    // ---- GET /api/admin/terms ----

    [Fact]
    public async Task List_returns_terms_with_step_and_student_counts()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/terms");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.True(arr.GetArrayLength() >= 1);

        // Find the seeded Fall 2026 term (id 1) and verify the shape + counts.
        JsonElement? fall = null;
        foreach (var t in arr.EnumerateArray())
        {
            if (t.GetProperty("id").GetInt32() == 1) { fall = t; break; }
        }
        Assert.True(fall.HasValue, "Seeded term id 1 should be present in the list");
        var term = fall.Value;

        Assert.Equal("Fall 2026", term.GetProperty("name").GetString());
        Assert.Equal(1, term.GetProperty("is_active").GetInt32());

        // step_count and student_count keys must be present and numeric.
        Assert.Equal(JsonValueKind.Number, term.GetProperty("step_count").ValueKind);
        Assert.Equal(JsonValueKind.Number, term.GetProperty("student_count").ValueKind);
        // 22 Fall-2026 steps and 50 seeded students are the floor (>= to tolerate adds).
        Assert.True(term.GetProperty("step_count").GetInt32() >= 22);
        Assert.True(term.GetProperty("student_count").GetInt32() >= 50);

        // created_at is serialized as ISO-8601 UTC ending in 'Z'.
        var createdAt = term.GetProperty("created_at").GetString();
        Assert.NotNull(createdAt);
        Assert.EndsWith("Z", createdAt);
    }

    [Fact]
    public async Task List_requires_authentication()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/terms");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- POST /api/admin/terms ----

    [Fact]
    public async Task Create_returns_id_and_creates_inactive_term()
    {
        var name = UniqueName();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms",
            new { name, start_date = "2027-01-01", end_date = "2027-05-31" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        var newId = body.GetProperty("id").GetInt32();
        Assert.True(newId > 0);

        // New terms are created with is_active 0 — confirm via the list.
        var list = await (await _fx.Admin().GetAsync("/api/admin/terms")).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement? created = null;
        foreach (var t in list.EnumerateArray())
        {
            if (t.GetProperty("id").GetInt32() == newId) { created = t; break; }
        }
        Assert.True(created.HasValue, "Created term should appear in the list");
        Assert.Equal(name, created.Value.GetProperty("name").GetString());
        Assert.Equal(0, created.Value.GetProperty("is_active").GetInt32());
        // No students/steps on a brand-new empty term.
        Assert.Equal(0, created.Value.GetProperty("student_count").GetInt32());
        Assert.Equal(0, created.Value.GetProperty("step_count").GetInt32());
    }

    [Fact]
    public async Task Create_requires_name_returns_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms", new { start_date = "2027-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_requires_authentication()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/terms", new { name = UniqueName() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- PUT /api/admin/terms/{id} ----

    [Fact]
    public async Task Update_renames_term_and_returns_success()
    {
        var (id, _) = await CreateTermAsync();
        var newName = UniqueName();

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/terms/{id}", new { name = newName });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        // Verify the rename took effect (and is_active stayed 0 — no activation requested).
        var list = await (await _fx.Admin().GetAsync("/api/admin/terms")).Content.ReadFromJsonAsync<JsonElement>();
        var found = list.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
        Assert.Equal(newName, found.GetProperty("name").GetString());
        Assert.Equal(0, found.GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Update_activate_sets_term_active_and_deactivates_others()
    {
        // Create our own term and activate it. Activation flips every other term's
        // is_active to 0, so afterward exactly one term in the list is active: ours.
        var (id, _) = await CreateTermAsync();

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/terms/{id}", new { is_active = 1 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        var list = await (await _fx.Admin().GetAsync("/api/admin/terms")).Content.ReadFromJsonAsync<JsonElement>();
        var active = list.EnumerateArray().Where(t => t.GetProperty("is_active").GetInt32() == 1).ToList();
        Assert.Single(active);
        Assert.Equal(id, active[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Update_missing_term_returns_404()
    {
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/terms/999999", new { name = UniqueName() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Term not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_with_no_fields_returns_400()
    {
        var (id, _) = await CreateTermAsync();
        // Empty body object -> no fields provided -> "No fields to update".
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/terms/{id}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_requires_authentication()
    {
        var (id, _) = await CreateTermAsync();
        var res = await _fx.Anonymous().PutAsJsonAsync($"/api/admin/terms/{id}", new { name = UniqueName() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- POST /api/admin/terms/{id}/clone ----

    [Fact]
    public async Task Clone_copies_selected_steps_into_new_term()
    {
        var name = UniqueName();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms/1/clone",
            new { name, step_ids = new[] { 1, 2, 3 } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Response shape: { term, steps }.
        var term = body.GetProperty("term");
        var newTermId = term.GetProperty("id").GetInt32();
        Assert.True(newTermId > 0);
        Assert.NotEqual(1, newTermId);
        Assert.Equal(name, term.GetProperty("name").GetString());
        Assert.Equal(0, term.GetProperty("is_active").GetInt32());
        // Cloned term timestamp is ISO-8601 UTC.
        Assert.EndsWith("Z", term.GetProperty("created_at").GetString());

        var steps = body.GetProperty("steps");
        Assert.Equal(JsonValueKind.Array, steps.ValueKind);
        Assert.Equal(3, steps.GetArrayLength());
        foreach (var s in steps.EnumerateArray())
        {
            // Each cloned step is a fresh row attached to the new term.
            Assert.Equal(newTermId, s.GetProperty("term_id").GetInt32());
            Assert.True(s.GetProperty("id").GetInt32() > 0);
        }
    }

    [Fact]
    public async Task Clone_requires_name_returns_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms/1/clone",
            new { step_ids = new[] { 1, 2, 3 } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("name is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clone_requires_non_empty_step_ids_returns_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms/1/clone",
            new { name = UniqueName(), step_ids = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("step_ids must be a non-empty array", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clone_missing_source_term_returns_404()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms/999999/clone",
            new { name = UniqueName(), step_ids = new[] { 1, 2, 3 } });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Source term not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clone_with_no_matching_steps_returns_400()
    {
        // step_ids that don't belong to the source term -> no matching steps.
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/terms/1/clone",
            new { name = UniqueName(), step_ids = new[] { 9999998, 9999999 } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No matching steps found for source term", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Clone_requires_authentication()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/terms/1/clone",
            new { name = UniqueName(), step_ids = new[] { 1, 2, 3 } });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- DELETE /api/admin/terms/{id} ----

    [Fact]
    public async Task Delete_term_with_students_returns_409()
    {
        // Seeded term 1 has 50 students assigned -> cannot be deleted.
        var res = await _fx.Admin().DeleteAsync("/api/admin/terms/1");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cannot delete a term that still has students assigned", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_empty_term_succeeds()
    {
        // Create our own student-free term, then delete it.
        var (id, _) = await CreateTermAsync();

        var res = await _fx.Admin().DeleteAsync($"/api/admin/terms/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        // It should no longer appear in the list.
        var list = await (await _fx.Admin().GetAsync("/api/admin/terms")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Delete_missing_term_returns_404()
    {
        var res = await _fx.Admin().DeleteAsync("/api/admin/terms/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Term not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Delete_requires_authentication()
    {
        var (id, _) = await CreateTermAsync();
        var res = await _fx.Anonymous().DeleteAsync($"/api/admin/terms/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
