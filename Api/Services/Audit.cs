using System.Text.Json;
using Api.Auth;
using Api.Data;

namespace Api.Services;

// Audit-trail writes, ported from server/utils/audit.ts.
public static class Audit
{
    // Who performed the action, from request context (mirrors getAuditActor's order).
    // Endpoints may also pass an explicit actor (e.g. an integration client name).
    public static string ResolveActor(HttpContext http)
    {
        if (http.Items["integrationClientName"] is string clientName && !string.IsNullOrEmpty(clientName))
            return clientName;

        var admin = http.AdminUser();
        if (admin is not null)
            return !string.IsNullOrEmpty(admin.DisplayName) ? admin.DisplayName : admin.Email;

        var studentEmail = http.StudentEmail();
        if (!string.IsNullOrEmpty(studentEmail))
            return studentEmail;

        return "system";
    }

    public static Task LogAsync(Db db, string actor, string entityType, object entityId, string action, object? details = null)
    {
        return db.ExecuteAsync(
            @"INSERT INTO audit_log (entity_type, entity_id, action, changed_by, details)
              VALUES (@entityType, @entityId, @action, @changedBy, @details)",
            new
            {
                entityType,
                entityId = entityId.ToString(),
                action,
                changedBy = actor,
                details = details is null ? null : JsonSerializer.Serialize(details),
            });
    }
}
