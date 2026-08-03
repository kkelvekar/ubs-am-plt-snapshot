using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace UBS.AM.PLT.Snapshot.Api.ErrorHandling;

/// <summary>
/// Catch-all for any exception not handled earlier in the chain - a SqlException from the
/// Infrastructure layer, an InvalidOperationException, a null reference, anything. Writes a
/// 500 ProblemDetails carrying only a fixed generic message (never the exception detail or
/// stack trace) plus the traceId for correlation, and logs the full exception with stack
/// trace at Error under that same traceId. This is the single central place server faults are
/// logged; lower layers let exceptions propagate rather than log-and-rethrow.
/// </summary>
internal sealed class UnhandledExceptionHandler : IExceptionHandler
{
    private const string GenericDetail = "An unexpected error occurred while processing your request.";

    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<UnhandledExceptionHandler> _logger;

    public UnhandledExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<UnhandledExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        _logger.LogError(
            exception,
            "Unhandled exception processing {Method} {Path} traceId={TraceId}",
            httpContext.Request.Method,
            httpContext.Request.Path,
            traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = GenericDetail,
            },
        });
    }
}
