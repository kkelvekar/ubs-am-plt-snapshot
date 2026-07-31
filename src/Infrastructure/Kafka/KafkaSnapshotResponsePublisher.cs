using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Kafka adapter for <see cref="ISnapshotResponsePublisher"/>: maps the domain notification
/// onto the org response DTO and produces it to the response topic. As thin and disposable
/// as the consumer on the inbound side — at lift-and-shift the org publisher library
/// replaces this class and nothing outside Infrastructure moves.
///
/// One long-lived producer for the process lifetime (Confluent guidance: producers are
/// thread-safe and batching only works when reused), flushed on dispose so a graceful
/// shutdown never strands a queued response.
/// </summary>
public sealed class KafkaSnapshotResponsePublisher : ISnapshotResponsePublisher, IDisposable
{
    /// <summary>
    /// PascalCase on the wire, matching the inbound request contract and the org DTO's own
    /// property names. Deliberately NOT <see cref="JsonSerializerDefaults.Web"/>, which the
    /// consumer uses for its case-insensitive binding but which would emit camelCase here.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new();

    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<KafkaSnapshotResponsePublisher> _logger;

    public KafkaSnapshotResponsePublisher(
        IKafkaProducerFactory producerFactory,
        IOptions<KafkaProducerOptions> options,
        ILogger<KafkaSnapshotResponsePublisher> logger)
    {
        _producer = producerFactory.Create();
        _topic = options.Value.ResponseTopic;
        _logger = logger;
    }

    public void Publish(SnapshotStatusNotification notification)
    {
        var response = SnapshotResponseMapper.ToOrgResponse(notification);
        var value = JsonSerializer.Serialize(response, SerializerOptions);

        // Keyed by accountId, the same basis the request topic is partitioned on, so a
        // snapshot's responses stay ordered relative to each other. Produce() queues the
        // message and returns immediately; delivery is confirmed or failed asynchronously
        // via the handler below, matching the org publisher's fire-and-forget contract.
        _producer.Produce(
            _topic,
            new Message<string, string> { Key = notification.AccountId, Value = value },
            deliveryReport => LogDeliveryResult(notification, response.Status, deliveryReport));
    }

    private void LogDeliveryResult(
        SnapshotStatusNotification notification,
        string status,
        DeliveryReport<string, string> deliveryReport)
    {
        if (deliveryReport.Error.IsError)
        {
            _logger.LogError(
                "Failed to publish snapshot response snapshotId={SnapshotId} accountId={AccountId} status={Status} topic={Topic} reason={Reason}",
                notification.SnapshotId,
                notification.AccountId,
                status,
                deliveryReport.Topic,
                deliveryReport.Error.Reason);
            return;
        }

        _logger.LogInformation(
            "Published snapshot response snapshotId={SnapshotId} accountId={AccountId} status={Status} topic={Topic} partition={Partition} offset={Offset}",
            notification.SnapshotId,
            notification.AccountId,
            status,
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
