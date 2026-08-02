using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

namespace UBS.AM.PLT.Snapshot.Api.ErrorHandling;

/// <summary>
/// Maps the Application-layer SnapshotNotFoundException to a 404 Not Found ProblemDetails.
/// The exception is built only through its static factories, so the message echoes just the
/// caller-supplied identifiers and is safe to surface as the ProblemDetails detail. Logged at
/// Warning - an expected "no such snapshot / no such payload" condition, not a server fault,
/// so no stack trace noise. Returns false for any other exception type so the chain falls
/// through to the catch-all UnhandledExceptionHandler.
/// </summary>
internal sealed class NotFoundExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly ILogger<NotFoundExceptionHandler> _logger;

    public NotFoundExceptionHandler(
        IProblemDetailsService problemDetailsService,
        ILogger<NotFoundExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not SnapshotNotFoundException notFound)
        {
            return false;
        }

        _logger.LogWarning("Snapshot read found nothing: {Reason}", notFound.Message);

        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = notFound,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not found.",
                Detail = notFound.Message,
            },
        });
    }
}
