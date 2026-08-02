using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

namespace UBS.AM.PLT.Snapshot.Api.ErrorHandling;

/// <summary>
/// Maps the Application-layer request-validation exceptions - SnapshotGridFilterValidationException
/// from the grid and SnapshotDetailValidationException from the snapshot-detail reads - to a
/// 400 Bad Request ProblemDetails. Both messages are safe, caller-facing reasons (for example
/// "At least one accountId is required."), so they are surfaced as the ProblemDetails detail.
/// Logged at Warning - an expected client-input condition, not a server fault, so no stack
/// trace noise. Returns false for any other exception type so the chain falls through to the
/// catch-all UnhandledExceptionHandler.
/// </summary>
internal sealed class ValidationExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<ValidationExceptionHandler> _logger;

    public ValidationExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<ValidationExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (SnapshotGridFilterValidationException or SnapshotDetailValidationException))
        {
            return false;
        }

        _logger.LogWarning("Rejected snapshot read request: {Reason}", exception.Message);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request.",
                Detail = exception.Message,
            },
        });
    }
}
