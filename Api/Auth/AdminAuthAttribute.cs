using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Auth;

// Admin JWT gate + role check. Usage:
//   [AdminAuth]                              -> any authenticated admin
//   [AdminAuth("admissions", "sysadmin")]    -> only those roles
// On success it stashes the AdminContext on HttpContext.Items.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AdminAuthAttribute : Attribute, IAsyncActionFilter
{
    private readonly string[] _allowedRoles;

    public AdminAuthAttribute(params string[] allowedRoles) => _allowedRoles = allowedRoles;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            context.Result = AuthError.Result(401, "Authentication required");
            return;
        }

        var principal = http.RequestServices.GetRequiredService<JwtService>().Validate(header["Bearer ".Length..]);
        if (principal is null)
        {
            context.Result = AuthError.Result(401, "Invalid or expired token");
            return;
        }
        if (principal.FindFirst("type")?.Value != "admin")
        {
            context.Result = AuthError.Result(401, "Authentication required");
            return;
        }

        var admin = new AdminContext(
            Id: principal.FindFirst("adminId")?.Value ?? "",
            Role: principal.FindFirst("role")?.Value ?? "viewer",
            Email: principal.FindFirst("email")?.Value ?? "",
            DisplayName: principal.FindFirst("displayName")?.Value ?? "");

        // Re-check the admin against the DB every request so a deactivated or demoted
        // admin loses access immediately, instead of trusting the role/active flag baked
        // into the (8h) token. Break-glass has a non-numeric id and no DB row, so skip it.
        if (int.TryParse(admin.Id, out var adminId))
        {
            var db = http.RequestServices.GetRequiredService<Api.Data.Db>();
            var row = await db.QueryOneAsync<AdminRow>(
                "SELECT role, is_active FROM admin_users WHERE id = @adminId", new { adminId });
            if (row is null || row.is_active == 0)
            {
                context.Result = AuthError.Result(403, "Account is inactive");
                return;
            }
            admin = admin with { Role = row.role }; // authorize on the CURRENT role, not the token's
        }

        if (_allowedRoles.Length > 0 && !_allowedRoles.Contains(admin.Role))
        {
            context.Result = AuthError.Result(403, "Insufficient permissions");
            return;
        }

        http.Items["adminUser"] = admin;
        await next();
    }

    private sealed class AdminRow
    {
        public string role { get; set; } = "";
        public int is_active { get; set; }
    }
}
