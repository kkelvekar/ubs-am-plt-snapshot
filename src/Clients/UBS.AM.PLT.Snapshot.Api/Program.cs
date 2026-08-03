using System.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
using UBS.AM.PLT.Snapshot.Api.ErrorHandling;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPortfolioSnapshotReadFeatures();
builder.Services.AddSqlReadInfrastructure(builder.Configuration["Database:ConnectionString"] ?? string.Empty);
builder.Services.AddAdlsReadInfrastructure(
    builder.Configuration["BlobStorage:ServiceUri"] ?? string.Empty,
    builder.Configuration["BlobStorage:ContainerName"] ?? string.Empty,
    builder.Configuration["BlobStorage:ConnectionString"]);

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
});

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??=
            $"{context.HttpContext.Request.PathBase}{context.HttpContext.Request.Path}";
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
    });

builder.Services.AddExceptionHandler<ClientDisconnectExceptionHandler>();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();
builder.Services.AddExceptionHandler<NotFoundExceptionHandler>();
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

var app = builder.Build();

app.UsePathBase("/snapshots");

app.UseResponseCompression();

app.UseExceptionHandler();

app.UseStatusCodePages();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();

app.MapHealthChecks("/api/health/live");

app.Run();
