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

    // Single source for the assignable roles so Create and Update validate against
    // the same set; adding or renaming a role here can't drift between the two paths.
    private static readonly string[] ValidRoles = { "viewer", "admissions", "admissions_editor", "sysadmin" };

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

        if (!string.IsNullOrEmpty(role) && !ValidRoles.Contains(role))
            return BadRequest(new { error = $"role must be one of: {string.Join(", ", ValidRoles)}" });

        var normalizedEmail = email.ToLowerInvariant().Trim();
        var existing = await _db.QueryOneAsync<IdRow>(
            "SELECT id FROM admin_users WHERE email = @email", new { email = normalizedEmail });
        if (existing is not null)
            return Conflict(new { error = "Email already exists" });

        // Generate an unusable random hash — admin users authenticate via SSO or break-glass only
        var hashSource = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = Passwords.Hash(hashSource);
        var effectiveRole = string.IsNullOrEmpty(role) ? "viewer" : role;

        var newId = await _db.InsertReturningAsync<int>(
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
        var hasRole = Json.TryGetProperty(body, "role", out var roleEl);
        var hasDisplayName = Json.TryGetProperty(body, "displayName", out var displayNameEl);
        var hasIsActive = Json.TryGetProperty(body, "is_active", out var isActiveEl);

        var updates = new List<string>();
        // Dapper accepts IDictionary<string, object?> directly — no wrapper needed.
        var parameters = new Dictionary<string, object?>();

        // The "last active sysadmin" guards run INSIDE the update transaction below —
        // a standalone COUNT-then-UPDATE is a check-then-act race (two concurrent
        // demotions could each see "one other sysadmin" and both proceed).
        var guardDemote = false;
        var guardDeactivate = false;

        if (hasRole)
        {
            var role = roleEl.ValueKind == JsonValueKind.String ? roleEl.GetString() : null;
            if (role is null || !ValidRoles.Contains(role))
                return BadRequest(new { error = $"role must be one of: {string.Join(", ", ValidRoles)}" });

            if (role != "sysadmin" && user.role == "sysadmin")
                guardDemote = true;
            updates.Add("role = @role");
            parameters["role"] = role;
        }

        if (hasDisplayName)
        {
            var displayName = displayNameEl.ValueKind == JsonValueKind.String ? displayNameEl.GetString() : null;
            // display_name is NOT NULL in the schema — a non-string value would reach
            // the UPDATE as NULL and surface as a constraint-violation 500.
            if (string.IsNullOrEmpty(displayName))
                return BadRequest(new { error = "displayName must be a non-empty string" });
            updates.Add("display_name = @display_name");
            parameters["display_name"] = displayName;
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

            if (!isActive && user.role == "sysadmin")
                guardDeactivate = true;
            updates.Add("is_active = @is_active");
            parameters["is_active"] = isActive ? 1 : 0;
        }

        if (updates.Count == 0)
            return BadRequest(new { error = "No fields to update" });

        parameters["id"] = id;
        var updateSql = $"UPDATE admin_users SET {string.Join(", ", updates)} WHERE id = @id";
        var paramObject = parameters;

        // Guard + UPDATE atomically: UPDLOCK/HOLDLOCK on the count serializes concurrent
        // demote/deactivate attempts so the last active sysadmin can never be removed.
        var guardFailed = await _db.TransactionAsync(async tx =>
        {
            if (guardDemote || guardDeactivate)
            {
                var sysadminCount = await tx.QueryOneAsync<int>(
                    @"SELECT COUNT(*) as count FROM admin_users WITH (UPDLOCK, HOLDLOCK)
                      WHERE role = @role AND is_active = 1 AND id != @id",
                    new { role = "sysadmin", id });
                if (sysadminCount == 0)
                    return true;
            }
            await tx.ExecuteAsync(updateSql, paramObject);
            return false;
        });

        if (guardFailed)
            return Conflict(new
            {
                error = guardDemote
                    ? "Cannot demote the last active sysadmin"
                    : "Cannot deactivate the last active sysadmin",
            });

        var actor = Audit.ResolveActor(HttpContext);
        await Audit.LogAsync(_db, actor, "admin_user", id, "admin_update",
            new { email = user.email, fields = BodyKeys(body) });

        return Ok(new { success = true });
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
