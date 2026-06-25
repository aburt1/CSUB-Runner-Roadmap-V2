using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the student Auth area (Controllers/AuthController.cs).
// Endpoints: POST /api/auth/dev-login, POST /api/auth/sso, GET /api/auth/me.
[Collection("api")]
public class AuthTests
{
    private readonly WebAppFixture _fx;

    public AuthTests(WebAppFixture fx) => _fx = fx;

    // ---- POST /api/auth/dev-login ----

    [Fact]
    public async Task DevLogin_creates_student_and_returns_token_and_student()
    {
        var email = $"u{Guid.NewGuid():N}@t.edu";
        var name = $"Dev User {Guid.NewGuid():N}";

        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login", new { name, email });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var token = body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var student = body.GetProperty("student");
        Assert.False(string.IsNullOrEmpty(student.GetProperty("id").GetString()));
        Assert.Equal(name, student.GetProperty("displayName").GetString());
        Assert.Equal(email, student.GetProperty("email").GetString());
    }

    [Fact]
    public async Task DevLogin_is_idempotent_returns_same_student_for_same_email()
    {
        var email = $"u{Guid.NewGuid():N}@t.edu";

        var first = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login", new { name = "First Name", email });
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = firstBody.GetProperty("student").GetProperty("id").GetString();

        // Second call with the same email reuses the existing student row (no new student created).
        var second = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login", new { name = "Second Name", email });
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondBody.GetProperty("student").GetProperty("id").GetString();

        Assert.Equal(firstId, secondId);
        // Existing student is returned with its stored display name, not the newly supplied one.
        Assert.Equal("First Name", secondBody.GetProperty("student").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task DevLogin_missing_name_returns_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login",
            new { email = $"u{Guid.NewGuid():N}@t.edu" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name and email are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DevLogin_missing_email_returns_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login",
            new { name = "No Email" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name and email are required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DevLogin_empty_strings_return_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/dev-login",
            new { name = "", email = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Name and email are required", body.GetProperty("error").GetString());
    }

    // ---- POST /api/auth/sso ----

    [Fact]
    public async Task Sso_returns_501_when_azure_not_configured()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/sso", new { idToken = "anything" });

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azure AD SSO is not configured", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sso_not_configured_check_precedes_input_validation()
    {
        // Even with no idToken, the "not configured" gate runs first -> 501, not 400.
        var res = await _fx.Anonymous().PostAsJsonAsync("/api/auth/sso", new { });

        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azure AD SSO is not configured", body.GetProperty("error").GetString());
    }

    // ---- GET /api/auth/me ----

    [Fact]
    public async Task Me_without_token_returns_401_with_exact_message()
    {
        var res = await _fx.Anonymous().GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Me_with_invalid_token_returns_401_with_exact_message()
    {
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        var res = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid or expired token", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Me_with_student_token_returns_session_with_iso_utc_createdAt()
    {
        var email = $"u{Guid.NewGuid():N}@t.edu";
        var name = $"Me User {Guid.NewGuid():N}";
        var (client, _) = await _fx.StudentAsync(name, email);

        var res = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(body.GetProperty("id").GetString()));
        Assert.Equal(name, body.GetProperty("displayName").GetString());
        Assert.Equal(email, body.GetProperty("email").GetString());

        // Corrected contract: timestamps are ISO-8601 UTC ending in 'Z'.
        var createdAt = body.GetProperty("createdAt").GetString();
        Assert.False(string.IsNullOrEmpty(createdAt));
        Assert.EndsWith("Z", createdAt);
        // Must parse as a real instant.
        Assert.True(DateTimeOffset.TryParse(createdAt, out _));
    }

    [Fact]
    public async Task Me_with_admin_token_is_rejected_as_wrong_token_type()
    {
        // The admin JWT is not a "student" token, so [StudentAuth] rejects it.
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fx.AdminToken);

        var res = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid token type", body.GetProperty("error").GetString());
    }

    // ---- sign-in links a pre-staged (SIS-pushed) student instead of duplicating ----

    [Fact]
    public async Task Signin_links_pre_staged_student_does_not_duplicate()
    {
        // Pre-stage a student via the integration provisioning push: emplid + email, NO azure_id.
        var emplid = "9" + Guid.NewGuid().ToString("N")[..8];
        var email = $"prestaged-{Guid.NewGuid():N}@t.edu";

        var integration = _fx.Anonymous();
        integration.DefaultRequestHeaders.Add("X-Integration-Key", "dev-integration-key");
        var push = await integration.PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Pre Staged Student", email, source_event_id = Guid.NewGuid().ToString() });
        push.EnsureSuccessStatusCode();
        var pushBody = await push.Content.ReadFromJsonAsync<JsonElement>();
        var preStagedId = pushBody.GetProperty("student_id").GetString();
        var preStagedTerm = pushBody.GetProperty("term_id").GetInt32();

        // First sign-in by the same email. dev-login matches by email (the same equality the
        // SSO email-link branch uses), so it MUST reuse the pre-staged row, not insert a new one.
        var signIn = await _fx.Anonymous().PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "Signed In Name", email });
        signIn.EnsureSuccessStatusCode();
        var signInBody = await signIn.Content.ReadFromJsonAsync<JsonElement>();
        var signedInId = signInBody.GetProperty("student").GetProperty("id").GetString();

        // Same row reused (no new GUID, no duplicate).
        Assert.Equal(preStagedId, signedInId);
        var rowCount = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE email = '{email}'"));
        Assert.Equal(1, rowCount);

        // Provisioned cohort preserved.
        var termAfter = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT term_id FROM students WHERE id = '{preStagedId}'"));
        Assert.Equal(preStagedTerm, termAfter);
    }

    [Fact]
    public async Task Signin_links_pre_staged_student_by_emplid_not_email()
    {
        // Pre-stage a student via the provisioning push: emplid + one email, NO azure_id.
        var emplid = "9" + Guid.NewGuid().ToString("N")[..8];
        var provisionedEmail = $"prestaged-{Guid.NewGuid():N}@t.edu";

        var integration = _fx.Anonymous();
        integration.DefaultRequestHeaders.Add("X-Integration-Key", "dev-integration-key");
        var push = await integration.PutAsJsonAsync(
            "/api/integrations/v1/students",
            new { emplid, display_name = "Pre Staged Student", email = provisionedEmail, source_event_id = Guid.NewGuid().ToString() });
        push.EnsureSuccessStatusCode();
        var preStagedId = (await push.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("student_id").GetString();

        // Sign in with the SAME emplid but a DIFFERENT email — so ONLY emplid can match.
        // emplid is our primary identifier, so the pre-staged row MUST be reused (no duplicate).
        var differentEmail = $"signin-{Guid.NewGuid():N}@t.edu";
        var signIn = await _fx.Anonymous().PostAsJsonAsync(
            "/api/auth/dev-login", new { name = "Signed In Name", email = differentEmail, emplid });
        signIn.EnsureSuccessStatusCode();
        var signedInId = (await signIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("student").GetProperty("id").GetString();

        Assert.Equal(preStagedId, signedInId);
        var rowCount = Convert.ToInt32(await _fx.ScalarAsync(
            $"SELECT COUNT(*) FROM students WHERE emplid_norm = '{emplid.ToLowerInvariant()}'"));
        Assert.Equal(1, rowCount);
    }
}
