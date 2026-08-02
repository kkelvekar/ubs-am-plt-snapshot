using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Api.ErrorHandling;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The global exception pipeline turns exceptions into RFC 7807 ProblemDetails: validation
/// exceptions become a 400 carrying the safe reason; a snapshot-detail not-found becomes a 404
/// carrying its caller-safe message; everything else becomes a 500 that leaks no exception
/// detail or stack trace and is logged at Error. Each specific handler returns false for
/// anything that is not its own type, so the chain reaches the catch-all. The written JSON body
/// is inspected directly so a regression that surfaces internals would fail the test.
/// </summary>
public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task Validation_exception_writes_400_problem_details_with_safe_reason()
    {
        var (context, body) = CreateContext();
        var logger = new CapturingLogger<ValidationExceptionHandler>();
        var handler = new ValidationExceptionHandler(ProblemDetailsService(context), logger);

        var handled = await handler.TryHandleAsync(
            context,
            new SnapshotGridFilterValidationException("At least one accountId is required."),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        var problem = ReadJson(body);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Invalid request.", problem.GetProperty("title").GetString());
        Assert.Equal("At least one accountId is required.", problem.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Detail_validation_exception_writes_400_problem_details_with_safe_reason()
    {
        var (context, body) = CreateContext();
        var logger = new CapturingLogger<ValidationExceptionHandler>();
        var handler = new ValidationExceptionHandler(ProblemDetailsService(context), logger);

        // The one handler covers both read features' validation exceptions.
        var handled = await handler.TryHandleAsync(
            context,
            new SnapshotDetailValidationException("payloadType may contain only letters, digits, '_' and '-'."),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        var problem = ReadJson(body);
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Invalid request.", problem.GetProperty("title").GetString());
        Assert.Equal(
            "payloadType may contain only letters, digits, '_' and '-'.",
            problem.GetProperty("detail").GetString());

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Not_found_exception_writes_404_problem_details_with_safe_reason()
    {
        var (context, body) = CreateContext();
        var logger = new CapturingLogger<NotFoundExceptionHandler>();
        var handler = new NotFoundExceptionHandler(ProblemDetailsService(context), logger);

        var notFound = SnapshotNotFoundException.ForSnapshot("snap-1");
        var handled = await handler.TryHandleAsync(context, notFound, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);

        var problem = ReadJson(body);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("Not found.", problem.GetProperty("title").GetString());
        Assert.Equal(notFound.Message, problem.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Not_found_handler_ignores_other_exceptions()
    {
        var (context, _) = CreateContext();
        var handler = new NotFoundExceptionHandler(
            ProblemDetailsService(context), new CapturingLogger<NotFoundExceptionHandler>());

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("boom"), CancellationToken.None);

        // Returns false so the chain falls through to the catch-all handler.
        Assert.False(handled);
    }

    [Fact]
    public async Task Validation_handler_ignores_other_exceptions()
    {
        var (context, _) = CreateContext();
        var handler = new ValidationExceptionHandler(
            ProblemDetailsService(context), new CapturingLogger<ValidationExceptionHandler>());

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("boom"), CancellationToken.None);

        // Returns false so the chain falls through to the catch-all handler.
        Assert.False(handled);
    }

    [Fact]
    public async Task Unhandled_exception_writes_generic_500_without_leaking_detail_and_logs_error()
    {
        var (context, body) = CreateContext();
        var logger = new CapturingLogger<UnhandledExceptionHandler>();
        var handler = new UnhandledExceptionHandler(ProblemDetailsService(context), logger);

        const string secret = "Login failed for user 'sa' - connection string leaked detail";
        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException(secret), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);

        var raw = BodyText(body);
        // The internal exception message must never reach the client body.
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain("InvalidOperationException", raw);

        var problem = ReadJson(body);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("An unexpected error occurred.", problem.GetProperty("title").GetString());
        Assert.Equal(
            "An unexpected error occurred while processing your request.",
            problem.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));

        // Full exception logged once at Error for server-side diagnosis.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    private static IProblemDetailsService ProblemDetailsService(HttpContext context) =>
        context.RequestServices.GetRequiredService<IProblemDetailsService>();

    private static (HttpContext Context, MemoryStream Body) CreateContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = ctx =>
                ctx.ProblemDetails.Extensions["traceId"] =
                    System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
        var provider = services.BuildServiceProvider();

        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
        };
        context.Request.Method = "GET";
        context.Request.Path = "/api/portfolio-snapshots";
        context.Response.Body = body;

        return (context, body);
    }

    private static string BodyText(MemoryStream body)
    {
        body.Position = 0;
        return new StreamReader(body).ReadToEnd();
    }

    private static JsonElement ReadJson(MemoryStream body)
    {
        body.Position = 0;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
