using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Auth;

// Admin JWT gate + role check, combining the old server/middleware/adminAuth.ts
// and requireRole.ts. Usage:
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
            context.Result = Error(401, "Authentication required");
            return;
        }

        var principal = http.RequestServices.GetRequiredService<JwtService>().Validate(header["Bearer ".Length..]);
        if (principal is null)
        {
            context.Result = Error(401, "Invalid or expired token");
            return;
        }
        if (principal.FindFirst("type")?.Value != "admin")
        {
            context.Result = Error(401, "Authentication required");
            return;
        }

        var admin = new AdminContext(
            Id: principal.FindFirst("adminId")?.Value ?? "",
            Role: principal.FindFirst("role")?.Value ?? "viewer",
            Email: principal.FindFirst("email")?.Value ?? "",
            DisplayName: principal.FindFirst("displayName")?.Value ?? "");

        if (_allowedRoles.Length > 0 && !_allowedRoles.Contains(admin.Role))
        {
            context.Result = Error(403, "Insufficient permissions");
            return;
        }

        http.Items["adminUser"] = admin;
        await next();
    }

    private static ObjectResult Error(int status, string message) =>
        new(new { error = message }) { StatusCode = status };
}
