using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

// Read-side composition root for the Load-snapshots grid (solution design §7). The grid
// filter resolver is a pure static rule, so the only service it needs is TimeProvider; the
// SQL read port comes from AddSqlReadInfrastructure (no write-path stores are registered).
// Database:ConnectionString binds from configuration with the standard env-var override
// (Database__ConnectionString) — no environment-specific code, no secrets in the repo.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSqlReadInfrastructure(builder.Configuration["Database:ConnectionString"] ?? string.Empty);

var app = builder.Build();

// Deployed in AKS under a base path — every endpoint (controllers, Swagger, health) is
// served beneath /snapshot. Controller route attributes stay path-base-relative, so the
// grid lands at /snapshot/api/portfolio-snapshots and Swagger UI at /snapshot/swagger.
app.UsePathBase("/snapshot");

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

// AKS liveness probe: 200 "Healthy" while the process is up. No checks registered on
// purpose, so a SQL/DB outage never fails liveness (that is a readiness concern).
app.MapHealthChecks("/api/health/live");

app.Run();
