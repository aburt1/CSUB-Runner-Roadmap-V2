using Microsoft.AspNetCore.Mvc;

namespace Api.Auth;

// The { error: "..." } envelope every auth filter returns on rejection.
internal static class AuthError
{
    public static ObjectResult Result(int status, string message) =>
        new(new { error = message }) { StatusCode = status };
}
