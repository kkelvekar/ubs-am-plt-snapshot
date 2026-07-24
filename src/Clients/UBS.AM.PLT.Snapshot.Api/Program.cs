using System.Diagnostics;
using UBS.AM.PLT.Snapshot.Api.ErrorHandling;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

// Read-side composition root for the Load-snapshots grid (solution design section 7).
// AddPortfolioSnapshotGrid registers only the grid feature use case (never the write-side
// SnapshotMessageHandler); AddSqlReadInfrastructure supplies the SQL read port (no
// write-path stores are registered). Database:ConnectionString binds from configuration
// with the standard env-var override (Database__ConnectionString) - no
// environment-specific code, no secrets in the repo.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPortfolioSnapshotGrid();
builder.Services.AddSqlReadInfrastructure(builder.Configuration["Database:ConnectionString"] ?? string.Empty);

// RFC 7807 ProblemDetails is the single error contract for every failure (validation 400s,
// model-binding 400s, and unhandled 500s). Stamp a traceId on every problem so the generic
// client response correlates to the full exception logged server-side under the same id.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

// Registration order is execution order: the specific validation handler runs first and
// returns false for anything that is not a SnapshotGridFilterValidationException, so the
// catch-all runs last for everything else (including SqlException from Infrastructure).
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

var app = builder.Build();

// Deployed in AKS under a base path - every endpoint (controllers, Swagger, health) is
// served beneath /snapshot. Controller route attributes stay path-base-relative, so the
// grid lands at /snapshot/api/portfolio-snapshots and Swagger UI at /snapshot/swagger.
app.UsePathBase("/snapshot");

// Wraps the whole pipeline: any exception escaping an endpoint is turned into a
// ProblemDetails response by the registered IExceptionHandlers. Placed right after the path
// base so rewritten paths are in effect when handlers log the request path.
app.UseExceptionHandler();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

// AKS liveness probe: 200 "Healthy" while the process is up. No checks registered on
// purpose, so a SQL/DB outage never fails liveness (that is a readiness concern).
app.MapHealthChecks("/api/health/live");

app.Run();
