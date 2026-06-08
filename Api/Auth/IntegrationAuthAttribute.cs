using Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Auth;

// Integration push-API gate, ported from server/middleware/integrationAuth.ts.
// Reads the credential from X-Integration-Key or a Bearer token, then bcrypt-compares
// it against integration_clients.key_hash for active clients. If X-Client-Name is
// supplied we look up that single client (avoids iterating + bcrypt DoS); otherwise we
// fall back to scanning up to 10 active clients.
//
// On success it stashes the integration client name (read by Audit.ResolveActor) and
// id on HttpContext.Items. Otherwise it returns 401 with the matching error body.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class IntegrationAuthAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var db = http.RequestServices.GetRequiredService<Db>();

        var credential = GetIntegrationCredential(http);
        if (string.IsNullOrEmpty(credential))
        {
            context.Result = Error(401, "Integration authentication required");
            return;
        }

        // If X-Client-Name header is provided, look up that single client (avoids bcrypt DoS).
        var clientName = http.Request.Headers["X-Client-Name"].ToString();
        if (!string.IsNullOrEmpty(clientName))
        {
            var client = await db.QueryOneAsync<IntegrationClientRow>(
                @"SELECT id, name, key_hash, is_active
                  FROM integration_clients
                  WHERE name = @clientName AND is_active = 1",
                new { clientName });

            if (client is not null && Passwords.Verify(credential, client.key_hash))
            {
                http.Items["integrationClientName"] = client.name;
                http.Items["integrationClientId"] = client.id;
                await next();
                return;
            }

            context.Result = Error(401, "Invalid integration credentials");
            return;
        }

        // Fallback: iterate all active clients (backward compat, limited to prevent DoS).
        var clients = await db.QueryAllAsync<IntegrationClientRow>(
            @"SELECT TOP 10 id, name, key_hash, is_active
              FROM integration_clients
              WHERE is_active = 1");

        foreach (var candidate in clients)
        {
            if (Passwords.Verify(credential, candidate.key_hash))
            {
                http.Items["integrationClientName"] = candidate.name;
                http.Items["integrationClientId"] = candidate.id;
                await next();
                return;
            }
        }

        context.Result = Error(401, "Invalid integration credentials");
    }

    private static string? GetIntegrationCredential(HttpContext http)
    {
        var headerKey = http.Request.Headers["X-Integration-Key"].ToString();
        if (!string.IsNullOrEmpty(headerKey))
            return headerKey.Trim();

        var authHeader = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
            return authHeader["Bearer ".Length..].Trim();

        return null;
    }

    private static ObjectResult Error(int status, string message) =>
        new(new { error = message }) { StatusCode = status };

    private sealed class IntegrationClientRow
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public string key_hash { get; set; } = "";
        public int is_active { get; set; }
    }
}
