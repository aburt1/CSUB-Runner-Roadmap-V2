using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

// Issues and validates the app's own HS256 session tokens. The claim names and
// 8-hour lifetime are a fixed contract that issued clients depend on:
//   student: { type: "student", studentId, email }
//   admin:   { type: "admin", adminId, role, email, displayName }
public sealed class JwtService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly SymmetricSecurityKey _key;

    public JwtService(IConfiguration config, IHostEnvironment env)
    {
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException("Jwt:Secret is not configured. Server cannot start.");

        // Fail safe in production: reject short or known placeholder/default secrets so a
        // misconfigured deployment can never sign tokens with a guessable key.
        if (env.IsProduction())
        {
            if (secret.Length < 32)
                throw new InvalidOperationException("Jwt:Secret must be at least 32 characters in Production.");
            if (IsKnownWeakSecret(secret))
                throw new InvalidOperationException("Jwt:Secret is a known default/placeholder value; set a real secret in Production.");
        }

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    // Catches the committed dev defaults and the .env.example placeholders.
    public static bool IsKnownWeakSecret(string secret) =>
        secret.Contains("change-me", StringComparison.OrdinalIgnoreCase)
        || secret.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
        || secret.StartsWith("dev-only", StringComparison.OrdinalIgnoreCase);

    public string IssueStudentToken(string studentId, string email) => Issue(
    [
        new Claim("type", "student"),
        new Claim("studentId", studentId),
        new Claim("email", email ?? ""),
    ]);

    public string IssueAdminToken(string adminId, string role, string email, string displayName) => Issue(
    [
        new Claim("type", "admin"),
        new Claim("adminId", adminId),
        new Claim("role", role),
        new Claim("email", email ?? ""),
        new Claim("displayName", displayName ?? ""),
    ]);

    // Returns the validated principal, or null if the token is missing/invalid/expired.
    public ClaimsPrincipal? Validate(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }

    private string Issue(Claim[] claims)
    {
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.Add(Lifetime),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
