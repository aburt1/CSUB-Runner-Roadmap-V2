using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Api.Auth;
using Api.Data;

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
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 200, Window = TimeSpan.FromMinutes(15) }));
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(15) }));
    options.AddPolicy("breakGlass", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15) }));
});

var app = builder.Build();

// Ensure the database exists, create/upgrade the schema, and seed defaults on
// startup, mirroring the old server (no manual DB setup needed).
var db = app.Services.GetRequiredService<Db>();
await SchemaInitializer.EnsureDatabaseAsync(connectionString);
await SchemaInitializer.RunAsync(db, Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql"));
await Seeder.RunAsync(db, app.Configuration, app.Environment);

// Security headers — the Helmet-equivalent CSP and friends from the old server.
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
