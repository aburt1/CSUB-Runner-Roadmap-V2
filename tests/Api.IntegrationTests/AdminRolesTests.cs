using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Auth;

namespace Api.IntegrationTests;

// Role-based admin authorization deny/allow paths. Every other admin test runs as the
// seeded sysadmin, so the 403 branch in AdminAuthAttribute (Insufficient permissions)
// and the per-request role re-check had zero coverage. Here we seed one admin per role
// with a real bcrypt hash so each can actually log in through the real endpoint, then
// exercise a sysadmin-only endpoint, an editor-gated endpoint, and a plain read.
//
// Roles: viewer / admissions / admissions_editor / sysadmin.
[Collection("api")]
public class AdminRolesTests
{
    private readonly WebAppFixture _fx;

    public AdminRolesTests(WebAppFixture fx) => _fx = fx;

    // Seeds an admin_users row with a valid bcrypt password hash (so real login works),
    // then logs in through /api/admin/auth/login and returns an authed client.
    private async Task<HttpClient> AdminWithRoleAsync(string role)
    {
        var email = $"role-{role}-{Guid.NewGuid():N}@csub.edu";
        const string password = "Role_Test_Password_2026!";
        // Hash exactly the way the app does, so Passwords.Verify accepts it at login.
        var hash = Passwords.Hash(password);
        await _fx.ExecSqlAsync(
            $"INSERT INTO admin_users (email, password_hash, role, display_name) " +
            $"VALUES ('{email}', '{hash}', '{role}', 'Role {role}')");

        var login = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- sysadmin-only endpoint: GET /api/admin/users ([AdminAuth("sysadmin")]) ----

    [Fact]
    public async Task Sysadmin_only_endpoint_forbids_viewer()
    {
        var client = await AdminWithRoleAsync("viewer");
        var res = await client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sysadmin_only_endpoint_forbids_admissions()
    {
        var client = await AdminWithRoleAsync("admissions");
        var res = await client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Sysadmin_only_endpoint_allows_sysadmin()
    {
        var client = await AdminWithRoleAsync("sysadmin");
        var res = await client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- editor-gated endpoint: POST /api/admin/steps ([AdminAuth("admissions_editor", "sysadmin")]) ----

    [Fact]
    public async Task Editor_gated_endpoint_forbids_viewer()
    {
        // The role gate runs before body validation, so a viewer is 403 regardless of payload.
        var client = await AdminWithRoleAsync("viewer");
        var res = await client.PostAsJsonAsync("/api/admin/steps", new { title = "x", term_id = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Editor_gated_endpoint_forbids_admissions()
    {
        // 'admissions' (read/act on students) is NOT an editor role for the checklist.
        var client = await AdminWithRoleAsync("admissions");
        var res = await client.PostAsJsonAsync("/api/admin/steps", new { title = "x", term_id = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- plain read: GET /api/admin/steps ([AdminAuth], any authenticated admin) ----

    [Fact]
    public async Task Plain_read_endpoint_allows_low_role()
    {
        var client = await AdminWithRoleAsync("viewer");
        var res = await client.GetAsync("/api/admin/steps");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- mid-session role demotion: the per-request DB re-check re-authorizes on the CURRENT role ----

    [Fact]
    public async Task Demoting_admin_mid_session_revokes_access_on_next_request()
    {
        var email = $"role-demote-{Guid.NewGuid():N}@csub.edu";
        const string password = "Role_Test_Password_2026!";
        var hash = Passwords.Hash(password);
        // Start as sysadmin so login succeeds and the token embeds role=sysadmin.
        await _fx.ExecSqlAsync(
            $"INSERT INTO admin_users (email, password_hash, role, display_name) " +
            $"VALUES ('{email}', '{hash}', 'sysadmin', 'Demote Me')");

        var login = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The sysadmin token works on the sysadmin-only endpoint.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users")).StatusCode);

        // Demote to viewer in the DB, keeping the SAME (still-unexpired, still-sysadmin) token.
        await _fx.ExecSqlAsync($"UPDATE admin_users SET role = 'viewer' WHERE email = '{email}'");

        // The per-request re-check authorizes on the CURRENT role, so the same token is now 403.
        var res = await client.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Insufficient permissions", body.GetProperty("error").GetString());
    }
}
