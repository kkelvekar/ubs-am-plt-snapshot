using Microsoft.AspNetCore.Diagnostics;

namespace UBS.AM.PLT.Snapshot.Api.ErrorHandling;

/// <summary>
/// Swallows the cancellation raised when the caller goes away mid-request - browser tab
/// closed, client timeout, load balancer cutting the connection. Runs first in the chain so
/// an aborted request never reaches the catch-all: there is no socket left to write a
/// response to, and logging it at Error would fill the server-fault log (and any alerting
/// built on it) with what is really a client decision. Logged at Debug instead, and no
/// response is written. A cancellation that did NOT come from the client - an internal
/// timeout, for example - is left alone and still surfaces as a 500.
/// </summary>
internal sealed class ClientDisconnectExceptionHandler : IExceptionHandler
{
    private readonly ILogger<ClientDisconnectExceptionHandler> _logger;

    public ClientDisconnectExceptionHandler(ILogger<ClientDisconnectExceptionHandler> logger)
    {
        _logger = logger;
    }

    public ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // TaskCanceledException derives from OperationCanceledException, so both are covered.
        // RequestAborted is what distinguishes "the caller left" from any other cancellation.
        if (exception is not OperationCanceledException || !httpContext.RequestAborted.IsCancellationRequested)
        {
            return ValueTask.FromResult(false);
        }

        _logger.LogDebug(
            "Client disconnected before {Method} {Path} completed.",
            httpContext.Request.Method,
            httpContext.Request.Path);

        return ValueTask.FromResult(true);
    }
}
