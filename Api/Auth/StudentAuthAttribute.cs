using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Auth;

// Student JWT gate, ported from server/middleware/auth.ts (authMiddleware).
// Put [StudentAuth] on a controller or action. On success it stashes the
// student id/email on HttpContext.Items for the action to read.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class StudentAuthAttribute : Attribute, IAsyncActionFilter
{
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
        if (principal.FindFirst("type")?.Value != "student")
        {
            context.Result = Error(401, "Invalid token type");
            return;
        }

        http.Items["studentId"] = principal.FindFirst("studentId")?.Value;
        http.Items["studentEmail"] = principal.FindFirst("email")?.Value;
        await next();
    }

    private static ObjectResult Error(int status, string message) =>
        new(new { error = message }) { StatusCode = status };
}
