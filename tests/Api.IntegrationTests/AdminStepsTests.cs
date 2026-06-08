using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the AdminSteps area:
//   GET    /api/admin/steps
//   POST   /api/admin/steps
//   PUT    /api/admin/steps/{id}
//   PUT    /api/admin/steps/reorder
//   PUT    /api/admin/steps/bulk-status
//
// Ported contract: server/routes/admin/steps.ts -> Controllers/Admin/StepsController.cs.
// Shared seeded DB: never assert exact global counts; create our own uniquely-named
// steps for write tests and assert on those.
[Collection("api")]
public class AdminStepsTests
{
    private readonly WebAppFixture _fx;

    public AdminStepsTests(WebAppFixture fx) => _fx = fx;

    // Helper: create a step in term 1 with a unique title and return its id.
    private async Task<int> CreateStepAsync(HttpClient client, string title, object? extra = null)
    {
        object body = extra is null
            ? new { title, term_id = 1 }
            : MergeWithTitle(title, extra);

        var res = await client.PostAsJsonAsync("/api/admin/steps", body);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetInt32();
    }

    private static object MergeWithTitle(string title, object extra)
    {
        // Build a dictionary so callers can supply arbitrary fields alongside title/term_id.
        var dict = new Dictionary<string, object?> { ["title"] = title, ["term_id"] = 1 };
        foreach (var prop in extra.GetType().GetProperties())
            dict[prop.Name] = prop.GetValue(extra);
        return dict;
    }

    private static string UniqueTitle() => $"T-{Guid.NewGuid():N}";

    // ---------------- GET list ----------------

    [Fact]
    public async Task Get_list_returns_seeded_steps_with_known_fields()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/steps?term_id=1");
        res.EnsureSuccessStatusCode();

        var steps = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, steps.ValueKind);
        // Other tests add steps to term 1; the seed alone has 22, so >= 22.
        Assert.True(steps.GetArrayLength() >= 22);

        // The seeded "accepted" step is public with sort_order 1.
        var accepted = steps.EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("step_key", out var k) && k.GetString() == "accepted");
        Assert.Equal(JsonValueKind.Object, accepted.ValueKind);
        Assert.Equal(1, accepted.GetProperty("sort_order").GetInt32());
        Assert.Equal(1, accepted.GetProperty("is_public").GetInt32());
        Assert.Equal(1, accepted.GetProperty("term_id").GetInt32());
    }

    [Fact]
    public async Task Get_list_includes_inactive_steps_after_bulk_deactivate()
    {
        var admin = _fx.Admin();
        var id = await CreateStepAsync(admin, UniqueTitle());

        // Deactivate it, then confirm the admin list (unlike the public feed) still shows it.
        var bulk = await admin.PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { stepIds = new[] { id }, is_active = 0 });
        bulk.EnsureSuccessStatusCode();

        var steps = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var mine = steps.EnumerateArray().FirstOrDefault(s => s.GetProperty("id").GetInt32() == id);
        Assert.Equal(JsonValueKind.Object, mine.ValueKind);
        Assert.Equal(0, mine.GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Get_list_requires_auth()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/steps");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ---------------- POST create ----------------

    [Fact]
    public async Task Post_create_with_unique_title_returns_success_and_id()
    {
        var title = UniqueTitle();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/steps", new { title, term_id = 1 });
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("success").GetBoolean());
        var id = json.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        // The created step is retrievable and active by default.
        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var created = steps.EnumerateArray().FirstOrDefault(s => s.GetProperty("id").GetInt32() == id);
        Assert.Equal(JsonValueKind.Object, created.ValueKind);
        Assert.Equal(title, created.GetProperty("title").GetString());
        Assert.Equal(1, created.GetProperty("is_active").GetInt32());
        // step_key was auto-derived from the title (non-empty).
        Assert.False(string.IsNullOrEmpty(created.GetProperty("step_key").GetString()));
    }

    [Fact]
    public async Task Post_create_derives_unique_step_keys_for_duplicate_titles()
    {
        var admin = _fx.Admin();
        var title = UniqueTitle();
        var id1 = await CreateStepAsync(admin, title);
        var id2 = await CreateStepAsync(admin, title);
        Assert.NotEqual(id1, id2);

        var steps = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var k1 = steps.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == id1)
            .GetProperty("step_key").GetString();
        var k2 = steps.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == id2)
            .GetProperty("step_key").GetString();
        Assert.False(string.IsNullOrEmpty(k1));
        Assert.False(string.IsNullOrEmpty(k2));
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public async Task Post_create_missing_title_is_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/steps", new { term_id = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Title is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_create_missing_term_id_is_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/steps", new { title = UniqueTitle() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("term_id is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_create_invalid_term_id_is_400()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/steps",
            new { title = UniqueTitle(), term_id = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid term_id", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_create_requires_auth()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/steps",
            new { title = UniqueTitle(), term_id = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ---------------- PUT {id} update ----------------

    [Fact]
    public async Task Put_update_changes_fields_and_returns_success()
    {
        var admin = _fx.Admin();
        var id = await CreateStepAsync(admin, UniqueTitle());

        var newTitle = UniqueTitle();
        var res = await admin.PutAsJsonAsync($"/api/admin/steps/{id}",
            new { title = newTitle, description = "updated desc", is_optional = 1, sort_order = 7 });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        var steps = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var updated = steps.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == id);
        Assert.Equal(newTitle, updated.GetProperty("title").GetString());
        Assert.Equal("updated desc", updated.GetProperty("description").GetString());
        Assert.Equal(1, updated.GetProperty("is_optional").GetInt32());
        Assert.Equal(7, updated.GetProperty("sort_order").GetInt32());
    }

    [Fact]
    public async Task Put_update_nonexistent_step_is_404()
    {
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/steps/999999",
            new { title = UniqueTitle() });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Step not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_update_with_no_fields_is_400()
    {
        var admin = _fx.Admin();
        // Created step has a non-empty step_key, so an empty body does NOT trigger
        // step_key regeneration -> there are genuinely no fields to update.
        var id = await CreateStepAsync(admin, UniqueTitle());

        var res = await admin.PutAsJsonAsync($"/api/admin/steps/{id}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_update_invalid_term_id_is_400()
    {
        var admin = _fx.Admin();
        var id = await CreateStepAsync(admin, UniqueTitle());

        var res = await admin.PutAsJsonAsync($"/api/admin/steps/{id}",
            new { title = UniqueTitle(), term_id = 999999 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid term_id", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_update_requires_auth()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync("/api/admin/steps/1",
            new { title = UniqueTitle() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ---------------- PUT reorder ----------------

    [Fact]
    public async Task Put_reorder_updates_sort_order_of_our_steps()
    {
        var admin = _fx.Admin();
        var idA = await CreateStepAsync(admin, UniqueTitle());
        var idB = await CreateStepAsync(admin, UniqueTitle());

        // Assign large, unlikely-to-collide sort orders we can read back as a delta we control.
        var res = await admin.PutAsJsonAsync("/api/admin/steps/reorder", new
        {
            order = new[]
            {
                new { id = idA, sort_order = 9001 },
                new { id = idB, sort_order = 9002 },
            },
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        var steps = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var a = steps.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == idA);
        var b = steps.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == idB);
        Assert.Equal(9001, a.GetProperty("sort_order").GetInt32());
        Assert.Equal(9002, b.GetProperty("sort_order").GetInt32());
    }

    [Fact]
    public async Task Put_reorder_missing_array_is_400()
    {
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/steps/reorder", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("order must be an array of {id, sort_order}", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_reorder_requires_auth()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync("/api/admin/steps/reorder",
            new { order = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ---------------- PUT bulk-status ----------------

    [Fact]
    public async Task Put_bulk_status_deactivates_and_reactivates_our_steps()
    {
        var admin = _fx.Admin();
        var id = await CreateStepAsync(admin, UniqueTitle());

        // Deactivate
        var off = await admin.PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { stepIds = new[] { id }, is_active = 0 });
        off.EnsureSuccessStatusCode();
        Assert.True((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean());

        var afterOff = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterOff.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == id)
            .GetProperty("is_active").GetInt32());

        // Reactivate
        var on = await admin.PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { stepIds = new[] { id }, is_active = 1 });
        on.EnsureSuccessStatusCode();

        var afterOn = await (await admin.GetAsync("/api/admin/steps?term_id=1"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, afterOn.EnumerateArray().First(s => s.GetProperty("id").GetInt32() == id)
            .GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Put_bulk_status_missing_fields_is_400()
    {
        // stepIds absent.
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { is_active = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stepIds (array) and is_active (0|1) required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_bulk_status_invalid_is_active_value_is_400()
    {
        // is_active must be exactly 0 or 1; 2 is rejected.
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { stepIds = new[] { 1 }, is_active = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stepIds (array) and is_active (0|1) required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_bulk_status_requires_auth()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync("/api/admin/steps/bulk-status",
            new { stepIds = new[] { 1 }, is_active = 0 });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }
}
