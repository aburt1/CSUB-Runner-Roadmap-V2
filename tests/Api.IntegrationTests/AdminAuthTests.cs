using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Integration tests for AdminAuthController (POST login, GET me, POST change-password,
// POST sso, POST local-login). Ported contract from server/routes/adminAuth.ts.
[Collection("api")]
public class AdminAuthTests
{
    private readonly WebAppFixture _fx;

    public AdminAuthTests(WebAppFixture fx) => _fx = fx;

    // ---------------------------------------------------------------- login

    [Fact]
    public async Task Login_with_good_credentials_returns_token_and_sysadmin_user()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email = "admin@csub.edu", password = "admin123" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        var user = body.GetProperty("user");
        Assert.Equal("admin@csub.edu", user.GetProperty("email").GetString());
        Assert.Equal("sysadmin", user.GetProperty("role").GetString());
        Assert.False(string.IsNullOrEmpty(user.GetProperty("displayName").GetString()));
        Assert.True(user.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_email()
    {
        // Controller lowercases+trims the email before lookup.
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email = "  ADMIN@CSUB.EDU  ", password = "admin123" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Login_with_bad_password_is_401_invalid_credentials()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email = "admin@csub.edu", password = "definitely-wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid credentials", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_with_unknown_email_is_401_invalid_credentials()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email = $"nobody-{Guid.NewGuid():N}@csub.edu", password = "admin123" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid credentials", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_missing_password_is_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { email = "admin@csub.edu" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Email and password required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Login_missing_email_is_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/login", new { password = "admin123" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Email and password required", body.GetProperty("error").GetString());
    }

    // ------------------------------------------------------------------- me

    [Fact]
    public async Task Me_with_admin_token_returns_current_user_with_iso8601_z_created_at()
    {
        var res = await _fx.Admin().GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var user = body.GetProperty("user");
        Assert.Equal("admin@csub.edu", user.GetProperty("email").GetString());
        Assert.Equal("sysadmin", user.GetProperty("role").GetString());

        // Corrected contract: timestamps are ISO-8601 UTC ending in 'Z'.
        var createdAt = user.GetProperty("createdAt").GetString();
        Assert.False(string.IsNullOrEmpty(createdAt));
        Assert.EndsWith("Z", createdAt);
        Assert.Contains("T", createdAt);
        Assert.True(DateTime.TryParse(
            createdAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out _));
    }

    [Fact]
    public async Task Me_without_token_is_401_authentication_required()
    {
        var res = await _fx.Anonymous().GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Me_with_garbage_bearer_token_is_401_invalid_or_expired()
    {
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");

        var res = await client.GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid or expired token", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Me_with_student_token_is_401_admin_gate()
    {
        // A student JWT lacks type=admin, so the admin gate rejects it.
        var (client, _) = await _fx.StudentAsync("Me Gate", $"u{Guid.NewGuid():N}@t.edu");

        var res = await client.GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    // ------------------------------------------------------- change-password

    [Fact]
    public async Task ChangePassword_without_token_is_401()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/change-password",
            new { currentPassword = "admin123", newPassword = "a-very-long-new-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Authentication required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChangePassword_missing_newPassword_is_400_with_exact_message()
    {
        var res = await _fx.Admin().PostAsJsonAsync(
            "/api/admin/auth/change-password", new { currentPassword = "admin123" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Current and new password required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChangePassword_missing_currentPassword_is_400_with_exact_message()
    {
        var res = await _fx.Admin().PostAsJsonAsync(
            "/api/admin/auth/change-password", new { newPassword = "this-is-long-enough" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Current and new password required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChangePassword_too_short_newPassword_is_400_min_12_chars()
    {
        // "tooshort123" is 11 chars -> below the 12-char minimum. Validation happens
        // before the current-password check, so this stays a 400 even with a valid
        // current password.
        var res = await _fx.Admin().PostAsJsonAsync(
            "/api/admin/auth/change-password",
            new { currentPassword = "admin123", newPassword = "tooshort123" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Password must be at least 12 characters", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ChangePassword_wrong_current_password_is_401()
    {
        // Long enough to pass length validation, but the current password is wrong.
        var res = await _fx.Admin().PostAsJsonAsync(
            "/api/admin/auth/change-password",
            new { currentPassword = "wrong-current-pw", newPassword = "a-brand-new-secure-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Current password is incorrect", body.GetProperty("error").GetString());
    }

    // ------------------------------------------------------------------ sso

    [Fact]
    public async Task Sso_returns_501_when_azure_not_configured()
    {
        // The integration fixture sets no Azure AD config, so SSO is disabled.
        // The not-configured check runs before body validation, so even a valid
        // body still gets 501.
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/sso", new { idToken = "any-token-value" });

        Assert.Equal((HttpStatusCode)501, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azure AD is not configured", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Sso_with_empty_body_is_still_501_when_not_configured()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/sso", new { });

        Assert.Equal((HttpStatusCode)501, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Azure AD is not configured", body.GetProperty("error").GetString());
    }

    // ----------------------------------------------------------- local-login

    [Fact]
    public async Task LocalLogin_with_good_credentials_returns_break_glass_sysadmin_token()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/local-login",
            new { username = "localadmin", password = "Local_Admin_2026!" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(body.GetProperty("token").GetString()));
        var user = body.GetProperty("user");
        Assert.Equal("break-glass", user.GetProperty("id").GetString());
        Assert.Equal("break-glass", user.GetProperty("email").GetString());
        Assert.Equal("Break Glass Admin", user.GetProperty("displayName").GetString());
        Assert.Equal("sysadmin", user.GetProperty("role").GetString());
    }

    [Fact]
    public async Task LocalLogin_token_works_as_admin_on_me()
    {
        // The break-glass token is a real admin token: it should authenticate /me,
        // which has no DB row and falls back to the context identity.
        var login = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/local-login",
            new { username = "localadmin", password = "Local_Admin_2026!" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await client.GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var user = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");
        Assert.Equal("break-glass", user.GetProperty("id").GetString());
        Assert.Equal("sysadmin", user.GetProperty("role").GetString());
    }

    [Fact]
    public async Task LocalLogin_with_bad_password_is_401_invalid_credentials()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/local-login",
            new { username = "localadmin", password = "wrong-break-glass-pw" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid credentials", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LocalLogin_with_bad_username_is_401_invalid_credentials()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/local-login",
            new { username = "not-localadmin", password = "Local_Admin_2026!" });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid credentials", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LocalLogin_missing_fields_is_400_with_exact_message()
    {
        var res = await _fx.Anonymous().PostAsJsonAsync(
            "/api/admin/auth/local-login", new { username = "localadmin" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Username and password required", body.GetProperty("error").GetString());
    }
}
