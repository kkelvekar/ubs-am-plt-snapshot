using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Api.Models;

namespace UBS.AM.PLT.Snapshot.Api.Controllers;

/// <summary>
/// Read endpoint for the Audit "Load snapshots" grid (solution design section 7): a flat JSON
/// array, one row per snapshot. Thin edge - maps the request DTO to a SnapshotGridFilter
/// and calls the single Application-layer entry point (IPortfolioSnapshotGridQueryHandler),
/// which owns filter resolution, the read query and the display-JSON flattening. No error
/// handling here: validation and unexpected faults propagate to the global exception pipeline
/// (see Program.cs / ErrorHandling), which returns a ProblemDetails and logs server faults.
/// </summary>
[ApiController]
[Route("api/portfolio-snapshots")]
public sealed class PortfolioSnapshotController : ControllerBase
{
    private readonly IPortfolioSnapshotGridQueryHandler _gridQueryHandler;
    private readonly ILogger<PortfolioSnapshotController> _logger;

    public PortfolioSnapshotController(
        IPortfolioSnapshotGridQueryHandler gridQueryHandler,
        ILogger<PortfolioSnapshotController> logger)
    {
        _gridQueryHandler = gridQueryHandler;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPortfolioSnapshotIndex(
        [FromQuery] PortfolioSnapshotQuery query,
        CancellationToken ct)
    {
        var requestedFilter = new SnapshotGridFilter
        {
            AccountIds = query.AccountIds ?? [],
            FromDate = query.From,
            ToDate = query.To,
            EventType = query.Event,
        };

        var flattened = await _gridQueryHandler.HandleAsync(requestedFilter, ct);

        _logger.LogInformation(
            "Served snapshot grid query for {AccountCount} account(s): {RowCount} row(s).",
            requestedFilter.AccountIds.Count,
            flattened.Count);

        return Ok(flattened);
    }
}
