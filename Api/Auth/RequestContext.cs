namespace Api.Auth;

// The authenticated admin, as carried on the request after AdminAuth runs.
// Id is a string because the break-glass/local login uses a non-numeric id.
public sealed record AdminContext(string Id, string Role, string Email, string DisplayName);

// Typed accessors for values the auth filters stash on HttpContext.Items,
// replacing the old Express req.studentId / req.adminUser fields.
public static class RequestContext
{
    public static string? StudentId(this HttpContext http) => http.Items["studentId"] as string;

    public static string? StudentEmail(this HttpContext http) => http.Items["studentEmail"] as string;

    public static AdminContext? AdminUser(this HttpContext http) => http.Items["adminUser"] as AdminContext;
}
