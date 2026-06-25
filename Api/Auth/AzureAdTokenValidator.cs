using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Api.Auth;

// Validates an Azure AD ID token.
// Signing keys come from the tenant's OpenID Connect metadata; ConfigurationManager
// caches them and refreshes automatically (handling key rotation).
public sealed class AzureAdTokenValidator
{
    // Cap the OpenID metadata/JWKS fetch so an unreachable Microsoft endpoint fails fast
    // instead of blocking an admin login for the framework default (100s). Matches the 5s
    // deadline ApiCheckRunner.PerRequestTimeoutMs applies to every other outbound call.
    private const int MetadataFetchTimeoutMs = 5_000;

    private readonly IConfiguration _config;
    private ConfigurationManager<OpenIdConnectConfiguration>? _configManager;
    private readonly object _lock = new();

    public AzureAdTokenValidator(IConfiguration config) => _config = config;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config["AzureAd:ClientId"]) &&
        !string.IsNullOrWhiteSpace(_config["AzureAd:TenantId"]);

    public async Task<(string oid, string? email, string? name)> ValidateAsync(string idToken)
    {
        var tenantId = _config["AzureAd:TenantId"];
        var clientId = _config["AzureAd:ClientId"];
        var authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(MetadataFetchTimeoutMs));
        var oidcConfig = await GetConfigManager(authority).GetConfigurationAsync(cts.Token);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authority,
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            ValidAlgorithms = ["RS256"],
        };

        var principal = new JwtSecurityTokenHandler().ValidateToken(idToken, parameters, out _);

        var oid = principal.FindFirst("oid")?.Value
            ?? throw new SecurityTokenException("Azure AD token is missing the oid claim.");
        var email = principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst("email")?.Value;
        var name = principal.FindFirst("name")?.Value;
        return (oid, email, name);
    }

    private ConfigurationManager<OpenIdConnectConfiguration> GetConfigManager(string authority)
    {
        if (_configManager is not null) return _configManager;
        lock (_lock)
        {
            _configManager ??= new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{authority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
        }
        return _configManager;
    }
}
