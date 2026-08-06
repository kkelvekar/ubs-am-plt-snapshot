using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.Advantage.Messaging;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Configuration;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Publishing;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The outbound command reports the same two outcomes the inbound one does. Success here means
/// "queued for delivery" — Produce returns as soon as the message is accepted — so only a
/// refusal to queue is a failure.
/// </summary>
public class SnapshotPublishCommandTests
{
    private const string ResponseTopic = "ubs-advantage-snapshot-responses";

    [Fact]
    public async Task Response_is_produced_to_the_configured_topic_keyed_by_the_envelope_key()
    {
        var producer = new Mock<IProducer<string, string>>();
        Message<string, string>? produced = null;
        string? producedTopic = null;
        producer
            .Setup(p => p.Produce(
                It.IsAny<string>(),
                It.IsAny<Message<string, string>>(),
                It.IsAny<Action<DeliveryReport<string, string>>>()))
            .Callback<string, Message<string, string>, Action<DeliveryReport<string, string>>>(
                (topic, message, _) =>
                {
                    producedTopic = topic;
                    produced = message;
                });

        using var command = CreateCommand(producer.Object);

        var result = await command.ExecuteAsync(CreateMessage(CreateResponse()));

        Assert.True(result.IsSuccess);
        Assert.Equal(ResponseTopic, producedTopic);
        Assert.NotNull(produced);
        Assert.Equal("00675442A", produced.Key);
    }

    [Fact]
    public async Task Produced_body_is_PascalCase_matching_the_org_wire_contract()
    {
        var producer = new Mock<IProducer<string, string>>();
        Message<string, string>? produced = null;
        producer
            .Setup(p => p.Produce(
                It.IsAny<string>(),
                It.IsAny<Message<string, string>>(),
                It.IsAny<Action<DeliveryReport<string, string>>>()))
            .Callback<string, Message<string, string>, Action<DeliveryReport<string, string>>>(
                (_, message, _) => produced = message);

        using var command = CreateCommand(producer.Object);

        await command.ExecuteAsync(CreateMessage(CreateResponse()));

        Assert.NotNull(produced);
        using var document = JsonDocument.Parse(produced.Value);
        Assert.Equal("corr98765", document.RootElement.GetProperty("SnapshotId").GetString());
        Assert.Equal("Complete", document.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task Null_response_fails_without_producing_anything()
    {
        var producer = new Mock<IProducer<string, string>>();
        using var command = CreateCommand(producer.Object);

        var result = await command.ExecuteAsync(CreateMessage(response: null));

        Assert.False(result.IsSuccess);
        Assert.Equal("Snapshot response is null", result.Error);
        producer.Verify(
            p => p.Produce(
                It.IsAny<string>(),
                It.IsAny<Message<string, string>>(),
                It.IsAny<Action<DeliveryReport<string, string>>>()),
            Times.Never);
    }

    [Fact]
    public async Task A_producer_that_refuses_to_queue_fails_rather_than_throwing_at_the_caller()
    {
        var producer = new Mock<IProducer<string, string>>();
        producer
            .Setup(p => p.Produce(
                It.IsAny<string>(),
                It.IsAny<Message<string, string>>(),
                It.IsAny<Action<DeliveryReport<string, string>>>()))
            .Throws(new KafkaException(ErrorCode.Local_QueueFull));

        using var command = CreateCommand(producer.Object);

        var result = await command.ExecuteAsync(CreateMessage(CreateResponse()));

        Assert.False(result.IsSuccess);
        Assert.NotEqual(string.Empty, result.Error);
    }

    private static SnapshotPublishCommand CreateCommand(IProducer<string, string> producer)
    {
        var factory = new Mock<IKafkaProducerFactory>();
        factory.Setup(f => f.Create()).Returns(producer);

        return new SnapshotPublishCommand(
            factory.Object,
            Options.Create(new KafkaProducerOptions { ResponseTopic = ResponseTopic }),
            new CapturingLogger<SnapshotPublishCommand>());
    }

    private static IMessage<string, SnapshotResponse> CreateMessage(SnapshotResponse? response)
        => new MessageEnvelope<string, SnapshotResponse>("00675442A", response);

    private static SnapshotResponse CreateResponse()
        => new()
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            ReceivedFiles = ["header.json"],
            Status = "Complete",
            FirstReceivedAt = "2026-05-22T06:10:14.0000000Z",
            LastUpdatedAt = "2026-05-22T06:14:22.0000000Z",
        };
}
