using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Sanity checks that the test harness (real app + fresh test DB + seed) is wired up.
[Collection("api")]
public class SmokeTests
{
    private readonly WebAppFixture _fx;

    public SmokeTests(WebAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Health_reports_connected()
    {
        var res = await _fx.Anonymous().GetAsync("/api/health");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("connected", body.GetProperty("db").GetString());
    }

    [Fact]
    public async Task Admin_login_succeeds_and_caches_token()
    {
        Assert.False(string.IsNullOrEmpty(_fx.AdminToken));
    }

    [Fact]
    public async Task Seed_created_students_and_steps()
    {
        // At-least, not exact: other tests add students (dev-login) and steps. The
        // deterministic seed creates 50 students and 22 Fall 2026 steps.
        var students = await (await _fx.Admin().GetAsync("/api/admin/students?per_page=5")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(students.GetProperty("total").GetInt32() >= 50);

        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps?term_id=1")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(steps.GetArrayLength() >= 22);
    }

    [Fact]
    public async Task Bad_admin_password_is_401()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { email = "admin@csub.edu", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Schema_version_is_recorded_on_startup()
    {
        var version = Api.Data.SchemaInitializer.CurrentSchemaVersion;
        var count = Convert.ToInt32(await _fx.ScalarAsync($"SELECT COUNT(*) FROM schema_version WHERE version = '{version}'"));
        Assert.Equal(1, count);
    }
}
