using Microsoft.AspNetCore.Mvc;
using Ubs.Advantage.Core.Messaging.Kafka;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Worker.LiveTesting;

namespace UBS.AM.PLT.Snapshot.Worker.Controllers;

[ApiController]
[Route("api/live-tests/snapshots")]
public sealed class SnapshotSimulationController(
    IProducer<string, SnapshotRequest> producer,
    SnapshotTemplateLoader templateLoader,
    TimeProvider timeProvider,
    ILogger<SnapshotSimulationController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SnapshotSimulationResult>(StatusCodes.Status200OK)]
    public IActionResult Publish(CancellationToken cancellationToken)
    {
        var cases = new List<SnapshotSimulationCaseResult>(LiveTestManifest.Cases.Count);
        var messageCount = 0;

        foreach (var testCase in LiveTestManifest.Cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var template = templateLoader.Load(testCase.TemplateFileName);
            var messages = SnapshotGenerator.Generate(template, timeProvider.GetUtcNow());

            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                producer.Publish(new Message<string, SnapshotRequest>(message.AccountId, message.Request, []));

                logger.LogInformation(
                    "Queued live-test payload; snapshotId={SnapshotId}, accountId={AccountId}, payloadType={PayloadType}",
                    message.Request.SnapshotId,
                    message.Request.AccountId,
                    message.Request.PayloadType);
            }

            cases.Add(new SnapshotSimulationCaseResult(
                testCase.Name,
                messages[0].Request.SnapshotId,
                testCase.ExpectedStatus,
                testCase.ExpectedReasonCode));
            messageCount += messages.Count;
        }

        return Ok(new SnapshotSimulationResult(cases, messageCount));
    }
}
