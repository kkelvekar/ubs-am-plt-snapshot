using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Api.Models;

namespace UBS.AM.PLT.Snapshot.Api.Controllers;

/// <summary>
/// Read endpoints for both audit screens. Screen 1, the "Load snapshots" grid (solution design
/// section 7): a flat JSON array, one row per snapshot. Screen 2, View-a-snapshot-detail
/// (section 10): the stored payload blobs of one snapshot, either one at a time or as a single
/// object keyed by payloadType. Thin edge - it maps the request to Application terms and calls
/// the single Application-layer entry point for each screen
/// (IPortfolioSnapshotGridQueryHandler, IPortfolioSnapshotDetailQueryHandler), which own
/// resolution, the read queries and any response shaping. Payload JSON is returned as raw
/// content, never routed through a serialiser, so the stored bytes reach the caller verbatim.
/// No error handling here: validation, not-found and unexpected faults propagate to the global
/// exception pipeline (see Program.cs / ErrorHandling), which returns a ProblemDetails and
/// logs server faults.
/// </summary>
[ApiController]
[Route("api/portfolio-snapshots")]
public sealed class PortfolioSnapshotController : ControllerBase
{
    private readonly IPortfolioSnapshotGridQueryHandler _gridQueryHandler;
    private readonly IPortfolioSnapshotDetailQueryHandler _detailQueryHandler;
    private readonly ILogger<PortfolioSnapshotController> _logger;

    public PortfolioSnapshotController(
        IPortfolioSnapshotGridQueryHandler gridQueryHandler,
        IPortfolioSnapshotDetailQueryHandler detailQueryHandler,
        ILogger<PortfolioSnapshotController> logger)
    {
        _gridQueryHandler = gridQueryHandler;
        _detailQueryHandler = detailQueryHandler;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
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

    /// <summary>
    /// Screen 2, single file: the stored blob JSON for one payload type of one snapshot,
    /// returned verbatim as application/json content.
    /// </summary>
    [HttpGet("{snapshotId}/payloads/{payloadType}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshotPayload(string snapshotId, string payloadType, CancellationToken ct)
    {
        var json = await _detailQueryHandler.GetPayloadAsync(snapshotId, payloadType, ct);

        // Logged only after the read succeeds, so both route values are already resolved and
        // known-safe rather than raw caller input.
        _logger.LogInformation(
            "Served payload {PayloadType} for snapshot {SnapshotId} ({Length} chars).",
            payloadType,
            snapshotId,
            json.Length);

        return Content(json, "application/json");
    }

    /// <summary>
    /// Screen 2, all files: one JSON object keyed by payloadType, each value the snapshot's
    /// stored blob text embedded verbatim.
    /// </summary>
    [HttpGet("{snapshotId}/payloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshotPayloads(string snapshotId, CancellationToken ct)
    {
        var json = await _detailQueryHandler.GetAllPayloadsAsync(snapshotId, ct);

        _logger.LogInformation(
            "Served all payloads for snapshot {SnapshotId} ({Length} chars).",
            snapshotId,
            json.Length);

        return Content(json, "application/json");
    }
}
