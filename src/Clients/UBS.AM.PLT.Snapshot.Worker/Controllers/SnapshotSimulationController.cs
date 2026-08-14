using Confluent.Kafka;
using Microsoft.AspNetCore.Mvc;
using UBS.AM.PLT.Snapshot.Worker.LiveTesting;

namespace UBS.AM.PLT.Snapshot.Worker.Controllers;

[ApiController]
[Route("api/live-tests/snapshots")]
public sealed class SnapshotSimulationController(
    ISnapshotSimulationPublisher publisher,
    ILogger<SnapshotSimulationController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SnapshotSimulationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PublishAsync(
        [FromBody] SnapshotSimulationRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await publisher.PublishAsync(request ?? new SnapshotSimulationRequest(), cancellationToken);
            return Ok(result);
        }
        catch (SnapshotSimulationValidationException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid live-test request.",
                detail: ex.Message);
        }
        catch (ProduceException<string, string> ex)
        {
            logger.LogError(ex, "Kafka delivery failed while publishing live-test snapshots.");
            return ServiceUnavailable(ex.Error.Reason);
        }
        catch (KafkaException ex)
        {
            logger.LogError(ex, "Kafka was unavailable while publishing live-test snapshots.");
            return ServiceUnavailable(ex.Error.Reason);
        }
    }

    private ObjectResult ServiceUnavailable(string detail) => Problem(
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "Kafka delivery unavailable.",
        detail: detail);
}
