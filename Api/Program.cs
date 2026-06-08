using Api.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    // Preserve exact JSON key names. The existing API is intentionally
    // inconsistent (snake_case for DB-row responses like step_id/completed_at,
    // camelCase for hand-built auth responses like displayName), so every
    // response object spells its keys verbatim and we apply NO naming policy.
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.DictionaryKeyPolicy = null;
});

if (builder.Environment.IsDevelopment())
    builder.Services.AddOpenApi();

// One shared Db, built from the configured connection string.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default in configuration.");
builder.Services.AddSingleton(new Db(connectionString));

var app = builder.Build();

// Create/upgrade the schema on startup (idempotent), mirroring the old server.
var schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
await SchemaInitializer.RunAsync(app.Services.GetRequiredService<Db>(), schemaPath);

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapControllers();

app.Run();
