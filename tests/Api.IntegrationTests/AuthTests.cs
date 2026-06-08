using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for the student Auth area (server/routes/auth.ts -> Controllers/AuthController.cs).
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
}
