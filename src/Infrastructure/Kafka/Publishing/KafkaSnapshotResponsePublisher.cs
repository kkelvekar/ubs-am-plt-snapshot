using Microsoft.Extensions.Logging;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Mapping;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;

/// <summary>
/// Kafka adapter for <see cref="ISnapshotResponsePublisher"/>. It maps the domain notification
/// onto the org response DTO, wraps it in a message envelope and hands it to the publish
/// command — the mirror image of the consume side, where the consumer deserialises and hands
/// the message to the request command. All the Kafka mechanics live in the command, so at
/// lift-and-shift this class keeps only its mapping and the command is registered with the org
/// publisher library.
/// </summary>
public sealed class KafkaSnapshotResponsePublisher : ISnapshotResponsePublisher
{
    private readonly ACommand<IMessage<string, SnapshotResponse>> _command;
    private readonly ILogger<KafkaSnapshotResponsePublisher> _logger;

    public KafkaSnapshotResponsePublisher(
        ACommand<IMessage<string, SnapshotResponse>> command,
        ILogger<KafkaSnapshotResponsePublisher> logger)
    {
        _command = command;
        _logger = logger;
    }

    public void Publish(SnapshotStatusNotification notification)
    {
        var response = SnapshotResponseMapper.ToOrgResponse(notification);

        // Keyed by accountId, the same basis the request topic is partitioned on.
        var message = new MessageEnvelope<string, SnapshotResponse>(notification.AccountId, response);

        // The port is fire-and-forget by contract (see ISnapshotResponsePublisher), and the
        // publish command completes synchronously — it queues the message and returns, leaving
        // delivery to the producer's own callback. So this never blocks on the broker.
        var result = _command.ExecuteAsync(message).GetAwaiter().GetResult();

        if (!result.IsSuccess)
        {
            // Logged, not thrown: a caller mid-write must not have its message failed by a
            // notification that could not be queued. The Failed/Receiving/Complete response is
            // republished when the message is redelivered.
            _logger.LogError(
                "Snapshot response was not queued for publishing snapshotId={SnapshotId} accountId={AccountId} status={Status} reason={Reason}",
                response.SnapshotId,
                response.AccountId,
                response.Status,
                result.Error);
        }
    }
}
