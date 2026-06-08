using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the per-step API-check configuration (admin, sysadmin-gated)
// and the student-triggered run endpoints.
//
// Contract sources:
//   OLD: server/routes/apiChecks.ts + server/routes/studentApiChecks.ts
//   NEW: Api/Controllers/Admin/ApiChecksController.cs + Api/Controllers/RoadmapApiChecksController.cs
//
// Shared-DB discipline: every write test creates its OWN step (unique title/key) and
// configures the check against that step, so it never disturbs the seeded steps that
// other test classes depend on, and never asserts a global count.
[Collection("api")]
public class ApiChecksTests
{
    private readonly WebAppFixture _fx;

    // The saved-credential mask is eight U+2022 BULLET characters (NOT ASCII "********").
    // This is the literal the controller emits and round-trips on "preserve if masked".
    private const string Masked = "••••••••";

    public ApiChecksTests(WebAppFixture fx) => _fx = fx;

    // Creates a fresh step in term 1 with a unique title and returns its integer id.
    private async Task<int> CreateStepAsync()
    {
        var admin = _fx.Admin();
        var res = await admin.PostAsJsonAsync("/api/admin/steps", new
        {
            term_id = 1,
            title = $"T-{Guid.NewGuid():N}",
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean());
        return body.GetProperty("id").GetInt32();
    }

    // ---- GET (unconfigured) ------------------------------------------------

    [Fact]
    public async Task Get_unconfigured_step_returns_configured_false()
    {
        var stepId = await CreateStepAsync();

        var res = await _fx.Admin().GetAsync($"/api/admin/steps/{stepId}/api-check");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("configured").GetBoolean());
        // Unconfigured response carries ONLY { configured: false }.
        Assert.False(body.TryGetProperty("url", out _));
        Assert.False(body.TryGetProperty("auth_credentials", out _));
    }

    // ---- PUT (configure) happy path + GET masking --------------------------

    [Fact]
    public async Task Put_configures_check_then_get_masks_credentials_and_parses_headers()
    {
        var admin = _fx.Admin();
        var stepId = await CreateStepAsync();

        var put = await admin.PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/api/{{studentId}}",
            response_field_path = "data.enrolled",
            http_method = "GET",
            auth_type = "bearer",
            auth_credentials = new { token = "super-secret-token" },
            headers = new[] { new { key = "X-Test", value = "yes" } },
            student_param_name = "studentId",
            student_param_source = "emplid",
            is_enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(putBody.GetProperty("success").GetBoolean());

        var get = await admin.GetAsync($"/api/admin/steps/{stepId}/api-check");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal(stepId, body.GetProperty("step_id").GetInt32());
        Assert.Equal("https://example.com/api/{{studentId}}", body.GetProperty("url").GetString());
        Assert.Equal("data.enrolled", body.GetProperty("response_field_path").GetString());
        Assert.Equal("GET", body.GetProperty("http_method").GetString());
        Assert.Equal("bearer", body.GetProperty("auth_type").GetString());
        Assert.True(body.GetProperty("is_enabled").GetBoolean());

        // Credentials are masked, never the plaintext token.
        Assert.Equal(Masked, body.GetProperty("auth_credentials").GetString());

        // Stored headers JSON is parsed back to a JSON array/object (not a raw string).
        var headers = body.GetProperty("headers");
        Assert.Equal(JsonValueKind.Array, headers.ValueKind);
        Assert.Equal("X-Test", headers[0].GetProperty("key").GetString());
        Assert.Equal("yes", headers[0].GetProperty("value").GetString());

        // Corrected contract: timestamps are ISO-8601 UTC ending in "Z".
        var updatedAt = body.GetProperty("updated_at").GetString();
        Assert.NotNull(updatedAt);
        Assert.EndsWith("Z", updatedAt!);
        // Round-trips as a real instant.
        Assert.True(DateTimeOffset.TryParse(updatedAt, out _));
    }

    [Fact]
    public async Task Put_preserves_credentials_when_resubmitted_masked()
    {
        var admin = _fx.Admin();
        var stepId = await CreateStepAsync();

        // First configure with real bearer credentials.
        var first = await admin.PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/api/{{studentId}}",
            response_field_path = "ok",
            auth_type = "bearer",
            auth_credentials = new { token = "original-token" },
            is_enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Re-PUT with the masked sentinel (what the UI sends back unchanged) and a new field.
        var second = await admin.PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/api/v2/{{studentId}}",
            response_field_path = "ok",
            auth_type = "bearer",
            auth_credentials = Masked,
            is_enabled = false,
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean());

        // GET still shows masked creds (preserved, not wiped) plus the updated url.
        var body = await (await admin.GetAsync($"/api/admin/steps/{stepId}/api-check")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://example.com/api/v2/{{studentId}}", body.GetProperty("url").GetString());
        Assert.Equal(Masked, body.GetProperty("auth_credentials").GetString());
        Assert.False(body.GetProperty("is_enabled").GetBoolean());

        // The preserved creds still decrypt and drive a test run (no "Failed to decrypt").
        var test = await admin.PostAsJsonAsync($"/api/admin/steps/{stepId}/api-check/test", new { testStudentId = "001000000" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var tBody = await test.Content.ReadFromJsonAsync<JsonElement>();
        if (tBody.TryGetProperty("error", out var err))
            Assert.DoesNotContain("Failed to decrypt", err.GetString());
    }

    [Fact]
    public async Task Put_with_auth_none_stores_no_credentials()
    {
        var admin = _fx.Admin();
        var stepId = await CreateStepAsync();

        var put = await admin.PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/api/{{studentId}}",
            response_field_path = "value",
            auth_type = "none",
            is_enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await (await admin.GetAsync($"/api/admin/steps/{stepId}/api-check")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("none", body.GetProperty("auth_type").GetString());
        // No credentials -> not masked (null/empty passes through unchanged).
        var creds = body.GetProperty("auth_credentials");
        Assert.True(creds.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(creds.GetString()));
    }

    // ---- PUT validation ----------------------------------------------------

    [Fact]
    public async Task Put_missing_url_returns_400_with_exact_message()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            response_field_path = "value",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("url and response_field_path are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_missing_response_field_path_returns_400()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/{{studentId}}",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("url and response_field_path are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_invalid_url_returns_400()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "not-a-valid-url",
            response_field_path = "value",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid URL format", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_invalid_http_method_returns_400()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/{{studentId}}",
            response_field_path = "value",
            http_method = "DELETE",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http_method must be GET or POST", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_invalid_auth_type_returns_400()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/{{studentId}}",
            response_field_path = "value",
            auth_type = "oauth",
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("auth_type must be none, basic, or bearer", body.GetProperty("error").GetString());
    }

    // ---- POST test ---------------------------------------------------------

    [Fact]
    public async Task Test_without_testStudentId_returns_400()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PostAsJsonAsync($"/api/admin/steps/{stepId}/api-check/test", new { });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("testStudentId is required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Test_unconfigured_step_returns_404()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Admin().PostAsJsonAsync($"/api/admin/steps/{stepId}/api-check/test", new { testStudentId = "001000000" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("No API check configured for this step", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Test_configured_step_returns_error_or_result_shape()
    {
        var admin = _fx.Admin();
        var stepId = await CreateStepAsync();

        // Point at a host that won't resolve so the run is deterministic (no live API).
        var unresolvable = $"https://{Guid.NewGuid():N}.invalid/api/{{{{studentId}}}}";
        var put = await admin.PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = unresolvable,
            response_field_path = "data.value",
            auth_type = "none",
            is_enabled = true,
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var res = await admin.PostAsJsonAsync($"/api/admin/steps/{stepId}/api-check/test", new { testStudentId = "001000000" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Either an error result { error } OR a success result
        // { statusCode, responseBody, extractedValue, wouldMarkComplete } — exactly one shape.
        if (body.TryGetProperty("error", out var err))
        {
            Assert.False(string.IsNullOrEmpty(err.GetString()));
            Assert.False(body.TryGetProperty("statusCode", out _));
        }
        else
        {
            Assert.True(body.TryGetProperty("statusCode", out _));
            Assert.True(body.TryGetProperty("wouldMarkComplete", out _));
        }
    }

    // ---- Admin auth gates --------------------------------------------------

    [Fact]
    public async Task Get_without_token_returns_401()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Anonymous().GetAsync($"/api/admin/steps/{stepId}/api-check");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Put_without_token_returns_401()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Anonymous().PutAsJsonAsync($"/api/admin/steps/{stepId}/api-check", new
        {
            url = "https://example.com/{{studentId}}",
            response_field_path = "value",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Test_without_token_returns_401()
    {
        var stepId = await CreateStepAsync();
        var res = await _fx.Anonymous().PostAsJsonAsync($"/api/admin/steps/{stepId}/api-check/test", new { testStudentId = "001000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoint_rejects_student_token_with_401()
    {
        var stepId = await CreateStepAsync();
        var (student, _) = await _fx.StudentAsync("ApiCheck Gate", $"u{Guid.NewGuid():N}@t.edu");

        // A student JWT is type=student, not type=admin -> 401 (not 403).
        var res = await student.GetAsync($"/api/admin/steps/{stepId}/api-check");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- Student run endpoints ([StudentAuth]) -----------------------------

    [Fact]
    public async Task Run_api_checks_started_then_check_status_shape()
    {
        var (student, _) = await _fx.StudentAsync("ApiCheck Runner", $"u{Guid.NewGuid():N}@t.edu");

        var run = await student.PostAsJsonAsync("/api/roadmap/run-api-checks", new { });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var runBody = await run.Content.ReadFromJsonAsync<JsonElement>();
        // Fresh student has never run -> not skipped; either started or already running.
        var status = runBody.GetProperty("status").GetString();
        Assert.True(status is "started" or "running", $"unexpected run status: {status}");

        var statusRes = await student.GetAsync("/api/roadmap/check-status");
        Assert.Equal(HttpStatusCode.OK, statusRes.StatusCode);
        var statusBody = await statusRes.Content.ReadFromJsonAsync<JsonElement>();
        var s = statusBody.GetProperty("status").GetString();
        Assert.True(s is "no_run" or "running" or "complete", $"unexpected check status: {s}");
        // checkedSteps is always present and is an array.
        Assert.Equal(JsonValueKind.Array, statusBody.GetProperty("checkedSteps").ValueKind);
    }

    [Fact]
    public async Task Run_api_checks_second_call_is_throttled_or_running()
    {
        var (student, _) = await _fx.StudentAsync("ApiCheck Throttle", $"u{Guid.NewGuid():N}@t.edu");

        var first = await student.PostAsJsonAsync("/api/roadmap/run-api-checks", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A second immediate call returns a non-error status — either still running,
        // or skipped once the throttle timestamp has been written by the prior run.
        var second = await student.PostAsJsonAsync("/api/roadmap/run-api-checks", new { });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        var status = body.GetProperty("status").GetString();
        Assert.True(status is "started" or "running" or "skipped", $"unexpected status: {status}");
    }

    [Fact]
    public async Task Run_api_checks_without_token_returns_401()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/roadmap/run-api-checks", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Check_status_without_token_returns_401()
    {
        var res = await _fx.Anonymous().GetAsync("/api/roadmap/check-status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Student_endpoint_rejects_admin_token_with_401()
    {
        // An admin JWT is type=admin, not type=student -> 401 on the student route.
        var client = _fx.Admin();
        var res = await client.GetAsync("/api/roadmap/check-status");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
