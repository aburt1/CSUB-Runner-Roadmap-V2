using System.Security.Cryptography;
using System.Text;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Api.Controllers;

// Admin authentication.
// The legacy X-API-Key path was dropped per the conversion plan. The break-glass
// local login is kept and gated by the LocalLogin:Username/Password config.
[ApiController]
[Route("api/admin/auth")]
public sealed class AdminAuthController : ControllerBase
{
    private readonly Db _db;
    private readonly JwtService _jwt;
    private readonly AzureAdTokenValidator _azure;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminAuthController> _logger;

    public AdminAuthController(Db db, JwtService jwt, AzureAdTokenValidator azure, IConfiguration config, ILogger<AdminAuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _azure = azure;
        _config = config;
        _logger = logger;
    }

    public sealed record LoginRequest(string? Email, string? Password);
    public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
    public sealed record SsoRequest(string? IdToken);
    public sealed record LocalLoginRequest(string? Username, string? Password);

    // POST /api/admin/auth/login — local email + password.
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? body)
    {
        if (string.IsNullOrEmpty(body?.Email) || string.IsNullOrEmpty(body.Password))
            return BadRequest(new { error = "Email and password required" });

        var user = await _db.QueryOneAsync<AdminUser>(
            "SELECT * FROM admin_users WHERE email = @email AND is_active = 1",
            new { email = body.Email.ToLowerInvariant().Trim() });

        if (user is null || !Passwords.Verify(body.Password, user.password_hash))
        {
            _logger.LogWarning(
                "Admin login failed for {Email} from {RemoteIp}",
                body.Email, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        var token = _jwt.IssueAdminToken(user.id.ToString(), user.role, user.email, user.display_name);
        return Ok(new { token, user = new { id = user.id, email = user.email, displayName = user.display_name, role = user.role } });
    }

    // GET /api/admin/auth/me — current admin session.
    [HttpGet("me")]
    [AdminAuth]
    public async Task<IActionResult> Me()
    {
        var ctx = HttpContext.AdminUser()!;
        if (int.TryParse(ctx.Id, out var adminId))
        {
            var user = await _db.QueryOneAsync<AdminMe>(
                "SELECT id, email, display_name, role, created_at FROM admin_users WHERE id = @adminId", new { adminId });
            if (user is not null)
                return Ok(new
                {
                    user = new { id = user.id, email = user.email, displayName = user.display_name, role = user.role, createdAt = user.created_at },
                });
        }

        // Break-glass admin has no DB row.
        return Ok(new { user = new { id = ctx.Id, email = ctx.Email, displayName = ctx.DisplayName, role = ctx.Role } });
    }

    // POST /api/admin/auth/change-password — current admin changes their password.
    [HttpPost("change-password")]
    [AdminAuth]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest? body)
    {
        if (string.IsNullOrEmpty(body?.CurrentPassword) || string.IsNullOrEmpty(body.NewPassword))
            return BadRequest(new { error = "Current and new password required" });
        if (body.NewPassword.Length < 12)
            return BadRequest(new { error = "Password must be at least 12 characters" });

        var ctx = HttpContext.AdminUser()!;
        if (!int.TryParse(ctx.Id, out var adminId))
            return NotFound(new { error = "User not found" });

        var user = await _db.QueryOneAsync<AdminUser>("SELECT * FROM admin_users WHERE id = @adminId", new { adminId });
        if (user is null)
            return NotFound(new { error = "User not found" });
        if (!Passwords.Verify(body.CurrentPassword, user.password_hash))
            return Unauthorized(new { error = "Current password is incorrect" });

        await _db.ExecuteAsync("UPDATE admin_users SET password_hash = @hash WHERE id = @id",
            new { hash = Passwords.Hash(body.NewPassword), id = user.id });
        return Ok(new { success = true });
    }

    // POST /api/admin/auth/sso — Azure AD SSO login for admins.
    [HttpPost("sso")]
    public async Task<IActionResult> Sso([FromBody] SsoRequest? body)
    {
        if (!_azure.IsConfigured)
            return StatusCode(501, new { error = "Azure AD is not configured" });
        if (string.IsNullOrEmpty(body?.IdToken))
            return BadRequest(new { error = "idToken is required" });

        string oid, email, name;
        try
        {
            var claims = await _azure.ValidateAsync(body.IdToken);
            oid = claims.oid;
            email = claims.email ?? "";
            name = claims.name ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Admin SSO token validation failed from {RemoteIp}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "Invalid or expired token" });
        }

        var admin = await _db.QueryOneAsync<AdminUser>("SELECT * FROM admin_users WHERE azure_id = @oid", new { oid });
        if (admin is null && !string.IsNullOrEmpty(email))
            admin = await _db.QueryOneAsync<AdminUser>("SELECT * FROM admin_users WHERE LOWER(email) = LOWER(@email)", new { email });

        if (admin is null)
            return StatusCode(403, new { error = "No admin account found. Contact your system administrator." });
        if (admin.is_active == 0)
            return StatusCode(403, new { error = "Account is inactive" });

        if (string.IsNullOrEmpty(admin.azure_id))
            await _db.ExecuteAsync("UPDATE admin_users SET azure_id = @oid WHERE id = @id", new { oid, id = admin.id });
        if (!string.IsNullOrEmpty(name) && name != admin.display_name)
            await _db.ExecuteAsync("UPDATE admin_users SET display_name = @name WHERE id = @id", new { name, id = admin.id });

        var displayName = string.IsNullOrEmpty(name) ? admin.display_name : name;
        var token = _jwt.IssueAdminToken(admin.id.ToString(), admin.role, admin.email, displayName);
        return Ok(new { token, user = new { id = admin.id, email = admin.email, displayName, role = admin.role } });
    }

    // POST /api/admin/auth/local-login — break-glass login gated by env config.
    [HttpPost("local-login")]
    [EnableRateLimiting("breakGlass")]
    public async Task<IActionResult> LocalLogin([FromBody] LocalLoginRequest? body)
    {
        var configUsername = _config["LocalLogin:Username"];
        var configPassword = _config["LocalLogin:Password"];
        if (string.IsNullOrEmpty(configUsername) || string.IsNullOrEmpty(configPassword))
            return NotFound(new { error = "Not found" });

        if (string.IsNullOrEmpty(body?.Username) || string.IsNullOrEmpty(body.Password))
            return BadRequest(new { error = "Username and password required" });

        var usernameMatch = ConstantTimeEquals(body.Username, configUsername);
        var passwordMatch = ConstantTimeEquals(body.Password, configPassword);

        try
        {
            await Audit.LogAsync(_db, "break-glass", "break-glass", 0,
                usernameMatch && passwordMatch ? "break_glass_login_success" : "break_glass_login_failure",
                new { timestamp = DateTime.UtcNow.ToString("o") });
        }
        catch
        {
            // Audit failure must not block break-glass auth.
        }

        if (!usernameMatch || !passwordMatch)
            return Unauthorized(new { error = "Invalid credentials" });

        var token = _jwt.IssueAdminToken("break-glass", "sysadmin", "break-glass", "Break Glass Admin");
        return Ok(new { token, user = new { id = "break-glass", email = "break-glass", displayName = "Break Glass Admin", role = "sysadmin" } });
    }

    // Length-independent comparison to avoid leaking timing information.
    private static bool ConstantTimeEquals(string a, string b)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var hmac = new HMACSHA256(key);
        var hashA = hmac.ComputeHash(Encoding.UTF8.GetBytes(a));
        var hashB = hmac.ComputeHash(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }

    private sealed class AdminMe
    {
        public int id { get; set; }
        public string email { get; set; } = "";
        public string display_name { get; set; } = "";
        public string role { get; set; } = "";
        public DateTime created_at { get; set; }
    }
}
