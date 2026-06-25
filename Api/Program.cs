using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Api.Auth;
using Api.Data;
using Microsoft.AspNetCore.HttpOverrides;

// Keep JWT claim names verbatim ("type", "studentId", "role", ...) instead of
// remapping them to long SOAP URIs, so the auth filters can read them directly.
JwtSecurityTokenHandler.DefaultMapInboundClaims = false;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Preserve exact JSON key names. The existing API is intentionally
        // inconsistent (snake_case for DB-row responses like step_id/completed_at,
        // camelCase for hand-built auth responses like displayName), so every
        // response object spells its keys verbatim and we apply NO naming policy.
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.DictionaryKeyPolicy = null;
        // Emit timestamps as ISO-8601 UTC with a trailing 'Z'.
        options.JsonSerializerOptions.Converters.Add(new Api.Serialization.UtcDateTimeConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // Controllers validate inputs by hand and return { error: "..." }.
        // Turn off the automatic ProblemDetails 400 so they return those
        // exact error bodies themselves.
        options.SuppressModelStateInvalidFilter = true;
    });

if (builder.Environment.IsDevelopment())
    builder.Services.AddOpenApi();

// One shared Db, built from the configured connection string.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default in configuration.");
builder.Services.AddSingleton(sp => new Db(connectionString, sp.GetService<ILogger<Db>>()));

builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<AzureAdTokenValidator>();
builder.Services.AddSingleton<Api.Services.Encryption>();
builder.Services.AddSingleton<Api.Services.ApiCheckRunner>();

// Honor X-Forwarded-* from the nginx reverse proxy so rate limiting and audit logging
// key on the real client IP, not the proxy's. Only the web container fronts the api.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    // By default forwarded headers are trusted from ANY source, which is only safe
    // because the api's port is not reachable except through the trusted nginx
    // (compose binds it to 127.0.0.1; prod doesn't publish it at all). If the api
    // is ever exposed more widely, set ForwardedHeaders:KnownNetworks to the
    // proxy's CIDR(s) (e.g. "172.16.0.0/12;10.0.0.0/8") so a direct caller can't
    // spoof X-Forwarded-For to dodge per-IP rate limiting.
    var knownNetworks = builder.Configuration["ForwardedHeaders:KnownNetworks"];
    if (!string.IsNullOrEmpty(knownNetworks))
    {
        foreach (var cidr in knownNetworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    }
});

// CORS — allow the SPA origin with credentials; in production CORS is
// effectively closed unless Cors:Origin is set.
var corsOrigin = builder.Configuration["Cors:Origin"]
    ?? (builder.Environment.IsProduction() ? null : "http://localhost:3000");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (!string.IsNullOrEmpty(corsOrigin))
        policy.WithOrigins(corsOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Rate limiting — global 200/15min per IP, plus stricter named policies the auth
// endpoints opt into.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // 200/15min per IP — scoped to /api only, so SPA/static
    // asset requests don't consume the API budget.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        http.Request.Path.StartsWithSegments("/api")
            ? RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(15) })
            : RateLimitPartition.GetNoLimiter("static"));
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
    options.AddPolicy("breakGlass", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
});

var app = builder.Build();

// Resolve the fail-safe singletons eagerly so a misconfigured Jwt:Secret or
// ApiCheck:EncryptionKey stops the deployment at startup, not on the first request.
_ = app.Services.GetRequiredService<JwtService>();
_ = app.Services.GetRequiredService<Api.Services.Encryption>();

// Create/upgrade the schema and seed defaults on startup.
//   - Production: the database is provisioned by a DBA and the app's login is NOT
//     expected to have CREATE DATABASE rights — the app only applies the schema.
//   - Dev/test: auto-create the database so it's zero-setup.
// Override explicitly with Database:AutoCreate (default: true off-Production).
var db = app.Services.GetRequiredService<Db>();
var autoCreateDatabase = app.Configuration.GetValue<bool?>("Database:AutoCreate") ?? !app.Environment.IsProduction();
if (autoCreateDatabase)
    await SchemaInitializer.EnsureDatabaseAsync(connectionString, app.Logger);

await SchemaInitializer.RunAsync(db, Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql"));

// Seed bootstrap data (default term, checklist, default admin, integration client) on an
// empty database. Idempotent. Disable with Database:Seed=false if a DBA seeds out-of-band.
if (app.Configuration.GetValue<bool?>("Database:Seed") ?? true)
    await Seeder.RunAsync(db, app.Configuration, app.Environment, app.Logger);

// Outermost: turn any unhandled error into the { error: "Internal server error" }
// envelope (and never leak stack traces).
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "Unhandled request error for {Method} {Path} (trace {TraceId})",
            context.Request.Method, context.Request.Path, context.TraceIdentifier);
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"Internal server error\"}");
        }
    }
});

// Trust the reverse proxy's forwarded headers (must run before rate limiting/audit).
app.UseForwardedHeaders();

// Security headers applied to every response.
// A second, SPA-specific CSP lives in client/nginx.conf.template (it additionally
// allows connect-src login.microsoftonline.com for MSAL) — keep the two in sync.
// Consequently the single-process wwwroot fallback below works only without Azure SSO.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; img-src 'self' data: https:; " +
        "connect-src 'self'; frame-src 'none'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Strict-Transport-Security"] = "max-age=15552000; includeSubDomains";
    headers["X-DNS-Prefetch-Control"] = "off";
    headers["X-Download-Options"] = "noopen";
    headers["X-Permitted-Cross-Domain-Policies"] = "none";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    headers["Cross-Origin-Resource-Policy"] = "same-origin";
    headers["Origin-Agent-Cluster"] = "?1";
    await next();
});

app.UseCors();

// Rate limiting can be turned off (e.g. for the integration test suite, which
// would otherwise trip the per-IP login limit) via RateLimiting:Disabled=true.
if (!app.Configuration.GetValue<bool>("RateLimiting:Disabled"))
    app.UseRateLimiter();

// Static-file fallback: in the supported 3-container deployment the web/nginx
// container serves the SPA and wwwroot here stays empty (these middlewares no-op).
// Kept so a single-process deployment (SPA published into wwwroot) also works.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();

// Any non-/api route falls back to the SPA's index.html (client-side routing).
app.MapFallbackToFile("index.html");

app.Run();

// Exposed so the integration test project can host the app via WebApplicationFactory.
public partial class Program { }
