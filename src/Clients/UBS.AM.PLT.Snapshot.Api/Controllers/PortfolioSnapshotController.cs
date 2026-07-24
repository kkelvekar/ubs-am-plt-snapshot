using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Models;
using UBS.AM.PLT.Snapshot.Api.Models;

namespace UBS.AM.PLT.Snapshot.Api.Controllers;

/// <summary>
/// Read endpoint for the Audit "Load snapshots" grid (solution design §7): a flat JSON array,
/// one row per snapshot. Thin edge — maps the request DTO to a <see cref="SnapshotGridFilter"/>,
/// runs the pure resolver (400 on an empty account list or inverted window), calls the read
/// port, and flattens the opaque display JSON into the response rows.
/// </summary>
[ApiController]
[Route("api/portfolio-snapshots")]
public sealed class PortfolioSnapshotController : ControllerBase
{
    private readonly ISnapshotIndexQuery _indexQuery;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PortfolioSnapshotController> _logger;

    public PortfolioSnapshotController(
        ISnapshotIndexQuery indexQuery,
        TimeProvider timeProvider,
        ILogger<PortfolioSnapshotController> logger)
    {
        _indexQuery = indexQuery;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPortfolioSnapshotIndex(
        [FromQuery] PortfolioSnapshotQuery query,
        CancellationToken ct)
    {
        SnapshotGridFilter filter;
        try
        {
            filter = SnapshotGridFilter.Resolve(
                new SnapshotGridFilter
                {
                    AccountIds = query.AccountIds ?? [],
                    FromDate = query.From,
                    ToDate = query.To,
                    EventType = query.Event,
                },
                _timeProvider);
        }
        catch (SnapshotGridFilterValidationException ex)
        {
            _logger.LogWarning("Rejected snapshot grid query: {Reason}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }

        var rows = await _indexQuery.QueryAsync(filter, ct);
        var flattened = SnapshotRowFlattener.Flatten(rows);

        _logger.LogInformation(
            "Served snapshot grid query for {AccountCount} account(s) over [{FromDate:o}, {ToDate:o}]: {RowCount} row(s).",
            filter.AccountIds.Count,
            filter.FromDate,
            filter.ToDate,
            flattened.Count);

        return Ok(flattened);
    }
}
