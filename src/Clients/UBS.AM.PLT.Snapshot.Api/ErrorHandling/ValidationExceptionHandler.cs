using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

namespace UBS.AM.PLT.Snapshot.Api.ErrorHandling;

/// <summary>
/// Maps the Application-layer SnapshotGridFilterValidationException to a 400 Bad Request
/// ProblemDetails. The exception message is a safe, caller-facing reason (for example
/// "At least one accountId is required."), so it is surfaced as the ProblemDetails detail.
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
        if (exception is not SnapshotGridFilterValidationException validation)
        {
            return false;
        }

        _logger.LogWarning("Rejected snapshot grid query: {Reason}", validation.Message);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = validation,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request.",
                Detail = validation.Message,
            },
        });
    }
}
