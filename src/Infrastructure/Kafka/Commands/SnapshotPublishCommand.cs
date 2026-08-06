using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Configuration;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;

/// <summary>
/// The outbound counterpart of <see cref="SnapshotRequestCommand"/>: the command that puts one
/// snapshot status response on the response topic. Every message this service publishes goes
/// through here, so the publish path reports its outcome the same way the consume path does —
/// a <see cref="CommandResult"/>, nothing else.
/// </summary>
/// <remarks>
/// One long-lived producer for the process lifetime (Confluent guidance: producers are
/// thread-safe and batching only works when reused), flushed on dispose so a graceful shutdown
/// never strands a queued response.
///
/// <see cref="CommandResult.Success"/> here means "accepted for delivery", not "delivered":
/// <c>Produce</c> queues the message and returns immediately, matching the org publisher's
/// fire-and-forget contract, and the delivery report is logged asynchronously. Only a refusal to
/// queue at all — a full queue, a fatal producer state, an unserialisable response — is a
/// <see cref="CommandResult.Fail"/>.
/// </remarks>
public sealed class SnapshotPublishCommand : ACommand<IMessage<string, SnapshotResponse>>, IDisposable
{
    /// <summary>
    /// PascalCase on the wire, matching the inbound request contract and the org DTO's own
    /// property names. Deliberately NOT <see cref="JsonSerializerDefaults.Web"/>, which the
    /// consumer uses for its case-insensitive binding but which would emit camelCase here.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<SnapshotPublishCommand> _logger;

    public SnapshotPublishCommand(
        IKafkaProducerFactory producerFactory,
        IOptions<KafkaProducerOptions> options,
        ILogger<SnapshotPublishCommand> logger)
    {
        _producer = producerFactory.Create();
        _topic = options.Value.ResponseTopic;
        _logger = logger;
    }

    public override Task<CommandResult> ExecuteAsync(IMessage<string, SnapshotResponse> message)
    {
        var response = message.Value;

        if (response is null)
        {
            _logger.LogWarning("Received null snapshot response messageKey={MessageKey}", message.Key);
            return Task.FromResult(CommandResult.Fail("Snapshot response is null"));
        }

        try
        {
            var value = JsonSerializer.Serialize(response, SerializerOptions);

            // Keyed by accountId, the same basis the request topic is partitioned on, so a
            // snapshot's responses stay ordered relative to each other.
            _producer.Produce(
                _topic,
                new Message<string, string> { Key = message.Key, Value = value },
                deliveryReport => LogDeliveryResult(response, deliveryReport));

            return Task.FromResult(CommandResult.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error publishing snapshot response snapshotId={SnapshotId} accountId={AccountId} status={Status} topic={Topic}",
                response.SnapshotId,
                response.AccountId,
                response.Status,
                _topic);

            return Task.FromResult(CommandResult.Fail(ex.Message));
        }
    }

    private void LogDeliveryResult(SnapshotResponse response, DeliveryReport<string, string> deliveryReport)
    {
        if (deliveryReport.Error.IsError)
        {
            _logger.LogError(
                "Failed to publish snapshot response snapshotId={SnapshotId} accountId={AccountId} status={Status} topic={Topic} reason={Reason}",
                response.SnapshotId,
                response.AccountId,
                response.Status,
                deliveryReport.Topic,
                deliveryReport.Error.Reason);
            return;
        }

        _logger.LogInformation(
            "Published snapshot response snapshotId={SnapshotId} accountId={AccountId} status={Status} topic={Topic} partition={Partition} offset={Offset}",
            response.SnapshotId,
            response.AccountId,
            response.Status,
            deliveryReport.Topic,
            deliveryReport.Partition.Value,
            deliveryReport.Offset.Value);
    }

    public void Dispose()
    {
        // Bounded so a broken broker cannot hang shutdown indefinitely; anything still
        // unflushed is covered by the offset never having been committed.
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
