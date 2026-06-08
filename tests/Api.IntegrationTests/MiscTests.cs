using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Api.IntegrationTests;

// Cross-cutting "misc" coverage: the health check, the global security-header
// middleware (Helmet-equivalent), and the 404 behavior for unknown /api routes.
// Reference: NEW Api/Program.cs + Controllers/HealthController.cs, mirroring the
// old Express server/index.ts (helmet config + /api/health route).
[Collection("api")]
public class MiscTests
{
    private readonly WebAppFixture _fx;

    public MiscTests(WebAppFixture fx) => _fx = fx;

    // ---- GET /api/health ----

    [Fact]
    public async Task Health_returns_ok_and_connected_with_iso_utc_timestamp()
    {
        var res = await _fx.Anonymous().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal("connected", body.GetProperty("db").GetString());

        // Corrected contract: timestamps are ISO-8601 UTC ending in "Z".
        var ts = body.GetProperty("timestamp").GetString();
        Assert.False(string.IsNullOrEmpty(ts));
        Assert.EndsWith("Z", ts);
        // It must parse as a real UTC instant.
        var parsed = DateTimeOffset.Parse(
            ts!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public async Task Health_is_publicly_accessible_without_auth()
    {
        // No bearer token / no integration key: the health endpoint is open.
        var res = await _fx.Anonymous().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- Security headers (Helmet-equivalent, applied to every response) ----

    [Fact]
    public async Task Security_headers_present_on_response()
    {
        var res = await _fx.Anonymous().GetAsync("/api/health");

        var headers = res.Headers;

        // Content-Security-Policy
        Assert.True(headers.Contains("Content-Security-Policy"));
        var csp = string.Join(" ", headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-src 'none'", csp);

        // X-Content-Type-Options: nosniff
        Assert.True(headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());

        // Strict-Transport-Security
        Assert.True(headers.Contains("Strict-Transport-Security"));
        Assert.Contains("max-age=", headers.GetValues("Strict-Transport-Security").Single());

        // X-Frame-Options: DENY
        Assert.True(headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", headers.GetValues("X-Frame-Options").Single());

        // Referrer-Policy: no-referrer
        Assert.True(headers.Contains("Referrer-Policy"));
        Assert.Equal("no-referrer", headers.GetValues("Referrer-Policy").Single());

        // Cross-Origin-Opener-Policy: same-origin
        Assert.True(headers.Contains("Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", headers.GetValues("Cross-Origin-Opener-Policy").Single());
    }

    [Fact]
    public async Task Security_headers_present_even_on_404_responses()
    {
        // The header middleware runs ahead of routing, so even a miss carries them.
        var res = await _fx.Anonymous().GetAsync($"/api/{Guid.NewGuid():N}-unknown");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True(res.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(res.Headers.Contains("Content-Security-Policy"));
    }

    // ---- Unknown /api/* routes ----

    [Fact]
    public async Task Unknown_api_route_returns_404()
    {
        // MapFallbackToFile only catches non-/api routes, so an unmatched /api/*
        // path is a genuine 404 (it does NOT fall through to the SPA index.html).
        var res = await _fx.Anonymous().GetAsync($"/api/does-not-exist-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_nested_api_route_returns_404()
    {
        var res = await _fx.Anonymous().GetAsync($"/api/admin/no-such-endpoint-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
