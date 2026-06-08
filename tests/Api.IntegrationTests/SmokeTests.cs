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
    public async Task Seed_created_50_students_and_22_steps()
    {
        var students = await (await _fx.Admin().GetAsync("/api/admin/students?per_page=5")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, students.GetProperty("total").GetInt32());

        var steps = await (await _fx.Admin().GetAsync("/api/admin/steps")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(22, steps.GetArrayLength());
    }

    [Fact]
    public async Task Bad_admin_password_is_401()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/admin/auth/login", new { email = "admin@csub.edu", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
