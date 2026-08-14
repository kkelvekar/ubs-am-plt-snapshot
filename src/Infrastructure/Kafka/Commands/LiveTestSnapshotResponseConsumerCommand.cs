using Microsoft.Extensions.Logging;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// Logs Development live-test responses. Every response is handled successfully so observing a
/// test result can never block the response topic or stop the Worker.
/// </summary>
public sealed class LiveTestSnapshotResponseConsumerCommand(
    ILogger<LiveTestSnapshotResponseConsumerCommand> logger)
    : ACommand<IMessage<string, SnapshotResponse>>
{
    public override Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotResponse> message)
    {
        var response = message.Value;

        if (response is null)
        {
            logger.LogWarning("Received null live-test snapshot response messageKey={MessageKey}", message.Key);
            return Task.FromResult(CommandResult.Success);
        }

        logger.LogInformation(
            "Live-test response received; snapshotId={SnapshotId}, accountId={AccountId}, status={Status}, receivedFiles={ReceivedFiles}, missingFiles={MissingFiles}, reasonCode={ReasonCode}, reasonDetail={ReasonDetail}",
            response.SnapshotId,
            response.AccountId,
            response.Status,
            response.ReceivedFiles,
            response.MissingFiles,
            response.ReasonCode,
            response.ReasonDetail);

        return Task.FromResult(CommandResult.Success);
    }
}
