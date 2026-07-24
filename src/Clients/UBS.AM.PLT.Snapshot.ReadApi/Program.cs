using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

// Read-side composition root for the Load-snapshots grid (solution design §7). The grid
// filter resolver is a pure static rule, so the only service it needs is TimeProvider; the
// SQL read port comes from AddSqlReadInfrastructure (no write-path stores are registered).
// Database:ConnectionString binds from configuration with the standard env-var override
// (Database__ConnectionString) — no environment-specific code, no secrets in the repo.
builder.Services.AddControllers();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSqlReadInfrastructure(builder.Configuration["Database:ConnectionString"] ?? string.Empty);

var app = builder.Build();

app.MapControllers();

app.Run();
