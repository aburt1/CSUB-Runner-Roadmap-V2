using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

// Issues and validates the app's own HS256 session tokens. The claim names and
// 8-hour lifetime match the old server exactly so existing clients keep working:
//   student: { type: "student", studentId, email }
//   admin:   { type: "admin", adminId, role, email, displayName }
public sealed class JwtService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly SymmetricSecurityKey _key;

    public JwtService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Missing Jwt:Secret configuration. Server cannot start.");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

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
