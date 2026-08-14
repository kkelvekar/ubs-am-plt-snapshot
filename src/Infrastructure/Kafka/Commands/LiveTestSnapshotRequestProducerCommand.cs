using Microsoft.Extensions.Logging;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// Logs the broker delivery result for a Development live-test request. It is registered only
/// when <see cref="KafkaRegistration"/> enables Development live testing.
/// </summary>
public sealed class LiveTestSnapshotRequestProducerCommand(
    ILogger<LiveTestSnapshotRequestProducerCommand> logger)
    : ACommand<CommandStatusParameter<IMessage<string, SnapshotRequest>, bool>>
{
    public override Task<CommandResult> ExecuteAsync(
        CommandStatusParameter<IMessage<string, SnapshotRequest>, bool> parameter)
    {
        var request = parameter.Value.Value;

        if (request is null)
        {
            logger.LogWarning("Live-test request producer reported a null request messageKey={MessageKey}", parameter.Value.Key);
            return Task.FromResult(CommandResult.Fail("Live-test request was null."));
        }

        if (parameter.Status)
        {
            logger.LogInformation(
                "Live-test request delivered; snapshotId={SnapshotId}, accountId={AccountId}, payloadType={PayloadType}",
                request.SnapshotId,
                request.AccountId,
                request.PayloadType);

            return Task.FromResult(CommandResult.Success);
        }

        logger.LogError(
            "Live-test request delivery failed; snapshotId={SnapshotId}, accountId={AccountId}, payloadType={PayloadType}",
            request.SnapshotId,
            request.AccountId,
            request.PayloadType);

        return Task.FromResult(CommandResult.Fail("Live-test request delivery failed."));
    }
}
