using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the sysadmin-only admin-user management area.
// Contract sources:
//   OLD: CSUB-admissions/server/routes/admin/users.ts
//   NEW: Api/Controllers/Admin/UsersController.cs  (mounted at /api/admin/users)
//
// Shared-DB notes: the seed creates exactly one admin row (admin@csub.edu, sysadmin).
// Other test classes may add admin rows; we never assert exact global counts and we
// always create our own rows with unique emails for write assertions.
[Collection("api")]
public class AdminUsersTests
{
    private readonly WebAppFixture _fx;

    public AdminUsersTests(WebAppFixture fx) => _fx = fx;

    private static string UniqueEmail() => $"u{Guid.NewGuid():N}@t.edu";

    // ---- GET /api/admin/users ----

    [Fact]
    public async Task List_returns_users_with_expected_shape_and_no_password_hash()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() >= 1, "at least the seeded admin should be present");

        // The seeded sysadmin must be present and carry the SELECTed columns only.
        JsonElement? seeded = null;
        foreach (var u in body.EnumerateArray())
        {
            // No row may ever leak the password hash.
            Assert.False(u.TryGetProperty("password_hash", out _), "password_hash must never be returned");
            Assert.False(u.TryGetProperty("passwordHash", out _), "passwordHash must never be returned");

            if (u.GetProperty("email").GetString() == "admin@csub.edu")
                seeded = u;
        }

        Assert.True(seeded is not null, "seeded admin@csub.edu must appear in the list");
        var row = seeded!.Value;

        // PropertyNamingPolicy is null -> exact snake_case member names from the projection.
        Assert.Equal(JsonValueKind.Number, row.GetProperty("id").ValueKind);
        Assert.Equal("Admin", row.GetProperty("display_name").GetString());
        Assert.Equal("sysadmin", row.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Number, row.GetProperty("is_active").ValueKind);
        Assert.Equal(1, row.GetProperty("is_active").GetInt32());

        // Corrected contract: timestamps are ISO-8601 UTC ending in "Z".
        var createdAt = row.GetProperty("created_at").GetString();
        Assert.False(string.IsNullOrEmpty(createdAt));
        Assert.EndsWith("Z", createdAt);
        Assert.True(DateTimeOffset.TryParse(createdAt, out _), "created_at must parse as a timestamp");
    }

    [Fact]
    public async Task List_without_token_is_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_with_non_admin_student_token_is_401()
    {
        // A student JWT has type=student; the admin gate rejects it before role checks.
        var (client, _) = await _fx.StudentAsync("Gate Tester", UniqueEmail());
        var res = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task List_with_invalid_bearer_token_is_401()
    {
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        var res = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid or expired token", body.GetProperty("error").GetString());
    }

    // ---- POST /api/admin/users ----

    [Fact]
    public async Task Create_with_unique_email_returns_id_and_appears_in_list()
    {
        var email = UniqueEmail();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email, role = "admissions", displayName = "Created Admissions" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        var newId = body.GetProperty("id").GetInt32();
        Assert.True(newId > 0);

        // The created row should now be visible (email lowercased/trimmed by the API).
        var list = await (await _fx.Admin().GetAsync("/api/admin/users")).Content.ReadFromJsonAsync<JsonElement>();
        var found = list.EnumerateArray().FirstOrDefault(u => u.GetProperty("id").GetInt32() == newId);
        Assert.Equal(JsonValueKind.Object, found.ValueKind);
        Assert.Equal(email.ToLowerInvariant(), found.GetProperty("email").GetString());
        Assert.Equal("admissions", found.GetProperty("role").GetString());
        Assert.Equal("Created Admissions", found.GetProperty("display_name").GetString());
        Assert.Equal(1, found.GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Create_without_role_defaults_to_viewer()
    {
        var email = UniqueEmail();
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email, displayName = "Defaulted Role" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var list = await (await _fx.Admin().GetAsync("/api/admin/users")).Content.ReadFromJsonAsync<JsonElement>();
        var found = list.EnumerateArray().First(u => u.GetProperty("id").GetInt32() == newId);
        Assert.Equal("viewer", found.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Create_normalizes_email_to_lowercase_trimmed()
    {
        var local = $"Mixed{Guid.NewGuid():N}";
        var raw = $"  {local}@T.EDU  ";
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email = raw, displayName = "Normalize Me" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var list = await (await _fx.Admin().GetAsync("/api/admin/users")).Content.ReadFromJsonAsync<JsonElement>();
        var found = list.EnumerateArray().First(u => u.GetProperty("id").GetInt32() == newId);
        Assert.Equal($"{local.ToLowerInvariant()}@t.edu", found.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Create_missing_email_is_400_with_exact_message()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { displayName = "No Email" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("email and displayName required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_missing_display_name_is_400_with_exact_message()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email = UniqueEmail() });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("email and displayName required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_invalid_role_is_400_with_exact_message()
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email = UniqueEmail(), role = "superuser", displayName = "Bad Role" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role must be one of: viewer, admissions, admissions_editor, sysadmin",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_duplicate_email_is_409_with_exact_message()
    {
        var email = UniqueEmail();
        var first = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email, displayName = "First" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Same email (different case) must collide via the lowercase/trim normalization.
        var dup = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email = email.ToUpperInvariant(), displayName = "Second" });

        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var body = await dup.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Email already exists", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Create_without_token_is_401()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/users",
            new { email = UniqueEmail(), displayName = "No Auth" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ---- PUT /api/admin/users/{id} ----

    // Create a fresh admin row we fully own, returning its id.
    private async Task<int> CreateOwnedUserAsync(string role = "viewer", string displayName = "Owned")
    {
        var res = await _fx.Admin().PostAsJsonAsync("/api/admin/users",
            new { email = UniqueEmail(), role, displayName });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> FetchRowAsync(int id)
    {
        var list = await (await _fx.Admin().GetAsync("/api/admin/users")).Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().First(u => u.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Update_role_and_display_name_succeeds()
    {
        var id = await CreateOwnedUserAsync(role: "viewer", displayName: "Before");

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}",
            new { role = "admissions_editor", displayName = "After" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());

        var row = await FetchRowAsync(id);
        Assert.Equal("admissions_editor", row.GetProperty("role").GetString());
        Assert.Equal("After", row.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Update_is_active_false_deactivates_a_non_sysadmin()
    {
        var id = await CreateOwnedUserAsync(role: "viewer", displayName: "ToDeactivate");

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}",
            new { is_active = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean());

        var row = await FetchRowAsync(id);
        Assert.Equal(0, row.GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Update_nonexistent_id_is_404_with_exact_message()
    {
        var res = await _fx.Admin().PutAsJsonAsync("/api/admin/users/999999999",
            new { displayName = "Ghost" });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Admin user not found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_invalid_role_is_400_with_exact_message()
    {
        var id = await CreateOwnedUserAsync();

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}",
            new { role = "wizard" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("role must be one of: viewer, admissions, admissions_editor, sysadmin",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_with_no_recognized_fields_is_400_with_exact_message()
    {
        var id = await CreateOwnedUserAsync();

        // Body has only unrelated keys -> nothing to update.
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}",
            new { somethingElse = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Update_empty_body_is_400_with_exact_message()
    {
        var id = await CreateOwnedUserAsync();

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}", new { });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No fields to update", body.GetProperty("error").GetString());
    }

    // NOTE on the "last active sysadmin" 409 guards (demote + deactivate): exercising the
    // true 409 path requires the target to be the ONLY active sysadmin, which would mean
    // deactivating/demoting the seeded admin@csub.edu that every other test class depends on.
    // The shared-DB rules forbid that, so we cover the *complementary* behavior instead: the
    // guard correctly ALLOWS demoting/deactivating a sysadmin we own while another active
    // sysadmin (the seed) remains, proving the count-based guard does not over-fire.

    [Fact]
    public async Task Update_can_promote_owned_user_to_sysadmin_and_back()
    {
        // Round-trip role changes on a user we own; never touches the seeded sysadmin.
        var id = await CreateOwnedUserAsync(role: "viewer", displayName: "Promote Me");

        var up = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}", new { role = "sysadmin" });
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal("sysadmin", (await FetchRowAsync(id)).GetProperty("role").GetString());

        // Demoting is allowed because the seeded sysadmin keeps the count > 0.
        var down = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        Assert.Equal("viewer", (await FetchRowAsync(id)).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Update_deactivating_owned_sysadmin_is_allowed_while_others_active()
    {
        // The last-sysadmin guard only fires when no OTHER active sysadmin exists. Since the
        // seeded admin@csub.edu is always active, deactivating one we own must succeed.
        var id = await CreateOwnedUserAsync(role: "sysadmin", displayName: "Spare Sysadmin");

        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/users/{id}", new { is_active = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, (await FetchRowAsync(id)).GetProperty("is_active").GetInt32());
    }

    [Fact]
    public async Task Update_without_token_is_401()
    {
        var res = await _fx.Anonymous().PutAsJsonAsync("/api/admin/users/1",
            new { displayName = "No Auth" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }
}
