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
    public async Task Readiness_reports_connected()
    {
        var res = await _fx.Anonymous().GetAsync("/api/health/ready");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
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
    public async Task Liveness_and_readiness_probes_respond()
    {
        var live = await _fx.Anonymous().GetAsync("/api/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var ready = await _fx.Anonymous().GetAsync("/api/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode); // DB is up in tests
        var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Schema_version_is_recorded_on_startup()
    {
        var version = Api.Data.SchemaInitializer.CurrentSchemaVersion;
        var count = Convert.ToInt32(await _fx.ScalarAsync($"SELECT COUNT(*) FROM schema_version WHERE version = '{version}'"));
        Assert.Equal(1, count);
    }

    // Guards the structural seed invariants the analytics suite asserts against, so a
    // cross-class leak surfaces as ONE clear failure here rather than a confusing
    // count drift elsewhere. All classes share one seeded DB with no per-test reset:
    // IntegrationsTests toggles seed-student-001's submit-final-documents PROGRESS, and
    // the deadline-risk exclusion test waives a step — those mutate student_progress
    // rows, never the seed STRUCTURE. These invariants are exactly the structure:
    //   - 50 seeded students remain in term 1 (other tests only ADD students),
    //   - the submit-final-documents step still exists and is active.
    // (Progress rows are intentionally not asserted — they are legitimately mutated.)
    [Fact]
    public async Task Seed_structural_invariants_intact()
    {
        var term1Students = Convert.ToInt32(
            await _fx.ScalarAsync("SELECT COUNT(*) FROM students WHERE term_id = 1"));
        Assert.True(term1Students >= 50, $"expected >= 50 term-1 students, found {term1Students}");

        var submitFinalActive = Convert.ToInt32(
            await _fx.ScalarAsync(
                "SELECT COUNT(*) FROM steps WHERE step_key = 'submit-final-documents' AND term_id = 1 AND is_active = 1"));
        Assert.Equal(1, submitFinalActive);
    }
}
