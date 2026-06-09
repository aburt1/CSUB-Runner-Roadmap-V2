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
        // Emit timestamps as ISO-8601 UTC with a trailing 'Z' (matches the old app).
        options.JsonSerializerOptions.Converters.Add(new Api.Serialization.UtcDateTimeConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        // The old Express API validates inputs by hand and returns { error: "..." }.
        // Turn off the automatic ProblemDetails 400 so controllers return those
        // exact error bodies themselves.
        options.SuppressModelStateInvalidFilter = true;
    });

if (builder.Environment.IsDevelopment())
    builder.Services.AddOpenApi();

// One shared Db, built from the configured connection string.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default in configuration.");
builder.Services.AddSingleton(new Db(connectionString));

builder.Services.AddSingleton<JwtService>();
builder.Services.AddSingleton<AzureAdTokenValidator>();
builder.Services.AddSingleton<Api.Services.Encryption>();
builder.Services.AddSingleton<Api.Services.ApiCheckRunner>();

// Honor X-Forwarded-* from the nginx reverse proxy so rate limiting and audit logging
// key on the real client IP, not the proxy's. Only the web container fronts the api.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust the forwarded headers regardless of source: the api is only reachable
    // through the web container's nginx proxy on the internal Docker network.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// CORS — same behavior as the old server (allow the SPA origin with credentials;
// in production CORS is effectively closed unless Cors:Origin is set).
var corsOrigin = builder.Configuration["Cors:Origin"]
    ?? (builder.Environment.IsProduction() ? null : "http://localhost:3000");
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (!string.IsNullOrEmpty(corsOrigin))
        policy.WithOrigins(corsOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// Rate limiting — global 200/15min per IP, plus stricter named policies the auth
// endpoints opt into (matches the express-rate-limit limits in the old server).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    // 200/15min per IP — scoped to /api only (like the old app), so SPA/static
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

// Create/upgrade the schema and seed defaults on startup.
//   - Production: the database is provisioned by a DBA and the app's login is NOT
//     expected to have CREATE DATABASE rights — the app only applies the schema.
//   - Dev/test: auto-create the database so it's zero-setup.
// Override explicitly with Database:AutoCreate (default: true off-Production).
var db = app.Services.GetRequiredService<Db>();
var autoCreateDatabase = app.Configuration.GetValue<bool?>("Database:AutoCreate") ?? !app.Environment.IsProduction();
if (autoCreateDatabase)
    await SchemaInitializer.EnsureDatabaseAsync(connectionString);

await SchemaInitializer.RunAsync(db, Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql"));

// Seed bootstrap data (default term, checklist, default admin, integration client) on an
// empty database. Idempotent. Disable with Database:Seed=false if a DBA seeds out-of-band.
if (app.Configuration.GetValue<bool?>("Database:Seed") ?? true)
    await Seeder.RunAsync(db, app.Configuration, app.Environment);

// Outermost: turn any unhandled error into the old { error: "Internal server error" }
// envelope (and never leak stack traces).
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled request error");
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

// Security headers — the Helmet-equivalent set from the old server.
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

// Serve the built Vue SPA in production (single process serves API + client,
// same as the old Express server). In dev these no-op because wwwroot is empty
// and Vite serves the client on :3000.
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
