using System.Security.Cryptography;
using System.Text.Json;
using Api.Auth;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers.Admin;

// Admin user management, ported from server/routes/admin/users.ts.
// sysadmin-only. Mounted at /api/admin in the old app (router.use(usersRouter)),
// so the routes here live under /api/admin/users.
[ApiController]
[Route("api/admin/users")]
[AdminAuth("sysadmin")]
public sealed class UsersController : ControllerBase
{
    private readonly Db _db;

    public UsersController(Db db)
    {
        _db = db;
    }

    // GET /api/admin/users
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var users = await _db.QueryAllAsync<UserListRow>(
            "SELECT id, email, display_name, role, is_active, created_at FROM admin_users ORDER BY created_at");
        return Ok(users);
    }

    // POST /api/admin/users
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest? body)
    {
        var email = body?.Email;
        var role = body?.Role;
        var displayName = body?.DisplayName;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(displayName))
            return BadRequest(new { error = "email and displayName required" });

        var validRoles = new[] { "viewer", "admissions", "admissions_editor", "sysadmin" };
        if (!string.IsNullOrEmpty(role) && !validRoles.Contains(role))
            return BadRequest(new { error = $"role must be one of: {string.Join(", ", validRoles)}" });

        var normalizedEmail = email.ToLowerInvariant().Trim();
        var existing = await _db.QueryOneAsync<IdRow>(
            "SELECT id FROM admin_users WHERE email = @email", new { email = normalizedEmail });
        if (existing is not null)
            return Conflict(new { error = "Email already exists" });

        // Generate an unusable random hash — admin users authenticate via SSO or break-glass only
        var hashSource = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Passwords.Hash(hashSource);
        var effectiveRole = string.IsNullOrEmpty(role) ? "viewer" : role;

        var newId = await _db.QueryOneAsync<int>(
            @"INSERT INTO admin_users (email, password_hash, role, display_name)
              VALUES (@email, @hash, @role, @displayName);
              SELECT CAST(SCOPE_IDENTITY() AS int);",
            new { email = normalizedEmail, hash, role = effectiveRole, displayName });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "admin_user", newId, "admin_create",
            new { email, role = effectiveRole, displayName });

        return Ok(new { success = true, id = newId });
    }

    // PUT /api/admin/users/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] JsonElement body)
    {
        var user = await _db.QueryOneAsync<AdminUser>(
            "SELECT id, email, role, display_name, is_active, azure_id, created_at FROM admin_users WHERE id = @id",
            new { id });
        if (user is null)
            return NotFound(new { error = "Admin user not found" });

        // Read the raw body fields, mirroring the old destructure of req.body.
        var hasRole = TryGetProperty(body, "role", out var roleEl);
        var hasDisplayName = TryGetProperty(body, "displayName", out var displayNameEl);
        var hasIsActive = TryGetProperty(body, "is_active", out var isActiveEl);

        var updates = new List<string>();
        var parameters = new DynamicSqlParams();

        if (hasRole)
        {
            var role = roleEl.ValueKind == JsonValueKind.String ? roleEl.GetString() : null;
            var validRoles = new[] { "viewer", "admissions", "admissions_editor", "sysadmin" };
            if (role is null || !validRoles.Contains(role))
                return BadRequest(new { error = $"role must be one of: {string.Join(", ", validRoles)}" });

            // Prevent demoting the last active sysadmin
            if (role != "sysadmin" && user.role == "sysadmin")
            {
                var sysadminCount = await _db.QueryOneAsync<int>(
                    "SELECT COUNT(*) as count FROM admin_users WHERE role = @role AND is_active = 1 AND id != @id",
                    new { role = "sysadmin", id });
                if (sysadminCount == 0)
                    return Conflict(new { error = "Cannot demote the last active sysadmin" });
            }
            updates.Add($"role = {parameters.Next(role)}");
        }

        if (hasDisplayName)
        {
            var displayName = displayNameEl.ValueKind == JsonValueKind.String ? displayNameEl.GetString() : null;
            updates.Add($"display_name = {parameters.Next(displayName)}");
        }

        if (hasIsActive)
        {
            var isActive = isActiveEl.ValueKind == JsonValueKind.True
                || (isActiveEl.ValueKind == JsonValueKind.Number && isActiveEl.GetDouble() != 0)
                || (isActiveEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(isActiveEl.GetString()));

            // Prevent self-deactivation
            var ctx = HttpContext.AdminUser()!;
            if (!isActive && int.TryParse(ctx.Id, out var ctxId) && id == ctxId)
                return Conflict(new { error = "Cannot deactivate your own account" });

            // Prevent deactivating the last active sysadmin
            if (!isActive && user.role == "sysadmin")
            {
                var sysadminCount = await _db.QueryOneAsync<int>(
                    "SELECT COUNT(*) as count FROM admin_users WHERE role = @role AND is_active = 1 AND id != @id",
                    new { role = "sysadmin", id });
                if (sysadminCount == 0)
                    return Conflict(new { error = "Cannot deactivate the last active sysadmin" });
            }
            updates.Add($"is_active = {parameters.Next(isActive ? 1 : 0)}");
        }

        if (updates.Count == 0)
            return BadRequest(new { error = "No fields to update" });

        var idParam = parameters.Next(id);
        await _db.ExecuteAsync(
            $"UPDATE admin_users SET {string.Join(", ", updates)} WHERE id = {idParam}",
            parameters.ToObject());

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "admin_user", id, "admin_update",
            new { email = user.email, fields = BodyKeys(body) });

        return Ok(new { success = true });
    }

    private static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    // Mirrors Object.keys(req.body) for the audit "fields" detail.
    private static List<string> BodyKeys(JsonElement body)
    {
        var keys = new List<string>();
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in body.EnumerateObject())
                keys.Add(prop.Name);
        }
        return keys;
    }

    // Builds @p0, @p1, ... placeholders + a matching parameter bag, mirroring the
    // old paramBuilder() positional approach for the partial UPDATE.
    private sealed class DynamicSqlParams
    {
        private readonly Dictionary<string, object?> _values = new();
        private int _index;

        public string Next(object? value)
        {
            var name = "p" + _index;
            _values[name] = value;
            _index++;
            return "@" + name;
        }

        public Dictionary<string, object?> ToObject() => _values;
    }

    public sealed record CreateUserRequest(string? Email, string? Role, string? DisplayName);

    private sealed class IdRow
    {
        public int id { get; set; }
    }

    private sealed class UserListRow
    {
        public int id { get; set; }
        public string email { get; set; } = "";
        public string display_name { get; set; } = "";
        public string role { get; set; } = "";
        public int is_active { get; set; }
        public DateTime created_at { get; set; }
    }
}
