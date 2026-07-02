using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Api.IntegrationTests;

// JWT-validation negative space: the existing "invalid token" tests only send a garbage
// string, which fails at PARSE before any validation parameter matters. These mint
// structurally-valid tokens that must be rejected by a specific guard — the signing-key
// check, lifetime validation, and the type-claim check — so relaxing any one guard turns
// a test red. Each case hits both the student (/api/auth/me) and admin (/api/admin/auth/me)
// gates.
[Collection("api")]
public class AuthTokenNegativeTests
{
    private readonly WebAppFixture _fx;

    public AuthTokenNegativeTests(WebAppFixture fx) => _fx = fx;

    // The secret WebAppFixture signs the running app's tokens with. A token signed with any
    // OTHER key must fail IssuerSigningKey validation.
    private const string RealSecret = "integration-test-secret-0123456789-abcdefghijklmnopqrstuvwxyz";
    private const string ForeignSecret = "a-totally-different-secret-0123456789-zyxwvutsrqponmlkjihgfedcba";

    // Mints an HS256 token the same way JwtService.Issue does, but with a caller-chosen
    // secret and expiry so each test can target one validation parameter.
    private static string MintToken(string secret, Claim[] claims, DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(claims: claims, expires: expires, signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Claim[] StudentClaims() =>
    [
        new Claim("type", "student"),
        new Claim("studentId", Guid.NewGuid().ToString()),
        new Claim("email", "student@t.edu"),
    ];

    private static Claim[] AdminClaims(string role = "sysadmin") =>
    [
        new Claim("type", "admin"),
        new Claim("adminId", "1"),
        new Claim("role", role),
        new Claim("email", "admin@csub.edu"),
        new Claim("displayName", "Admin"),
    ];

    private HttpClient WithBearer(string token)
    {
        var client = _fx.Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ---- (a) token signed with a DIFFERENT (foreign) key ----

    [Fact]
    public async Task Student_me_rejects_token_signed_with_foreign_key()
    {
        // Correct type + still valid lifetime, but signed with the wrong key.
        var token = MintToken(ForeignSecret, StudentClaims(), DateTime.UtcNow.AddHours(1));
        var res = await WithBearer(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Admin_me_rejects_token_signed_with_foreign_key()
    {
        var token = MintToken(ForeignSecret, AdminClaims(), DateTime.UtcNow.AddHours(1));
        var res = await WithBearer(token).GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- (b) expired but otherwise-valid token (correct secret + type) ----

    [Fact]
    public async Task Student_me_rejects_expired_token()
    {
        // Signed with the REAL secret and correct type — only the (backdated) expiry is wrong.
        var token = MintToken(RealSecret, StudentClaims(), DateTime.UtcNow.AddHours(-1));
        var res = await WithBearer(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Admin_me_rejects_expired_token()
    {
        var token = MintToken(RealSecret, AdminClaims(), DateTime.UtcNow.AddHours(-1));
        var res = await WithBearer(token).GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- (c) valid signature + lifetime, but the wrong/missing type claim ----

    [Fact]
    public async Task Student_me_rejects_valid_token_with_wrong_type_claim()
    {
        // Correctly signed and unexpired, but type=admin instead of student.
        var token = MintToken(RealSecret, AdminClaims(), DateTime.UtcNow.AddHours(1));
        var res = await WithBearer(token).GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        // Distinguishes the type-claim gate from the earlier signature/lifetime gates.
        Assert.Equal("Invalid token type", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Admin_me_rejects_valid_token_with_missing_type_claim()
    {
        // Correctly signed and unexpired, but no type claim at all — the admin gate requires
        // type=admin, so this is rejected even though the signature and lifetime are valid.
        Claim[] noType =
        [
            new Claim("adminId", "1"),
            new Claim("role", "sysadmin"),
            new Claim("email", "admin@csub.edu"),
            new Claim("displayName", "Admin"),
        ];
        var token = MintToken(RealSecret, noType, DateTime.UtcNow.AddHours(1));
        var res = await WithBearer(token).GetAsync("/api/admin/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
