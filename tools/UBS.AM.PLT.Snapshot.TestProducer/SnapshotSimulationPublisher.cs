using System.Text.Json;
using Confluent.Kafka;

namespace UBS.AM.PLT.Snapshot.TestProducer;

public static class SnapshotSimulationPublisher
{
    // Default naming policy (PascalCase) — NOT JsonSerializerDefaults.Web — so the produced
    // JSON matches the org-approved wire schema (docs/snapshot-request.schema.json) exactly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    public static async Task PublishAsync(
        IReadOnlyList<SnapshotEnvelope> messages,
        SimulationOptions options,
        CancellationToken cancellationToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            Acks = Acks.All,
        };

        Console.WriteLine(
            $"Publishing {options.SnapshotCount} snapshots ({messages.Count} messages) to '{options.Topic}' via {options.BootstrapServers}.");
        Console.WriteLine(
            $"Delays: {options.MessageDelay} between payloads, {options.SnapshotDelayMin}-{options.SnapshotDelayMax} between snapshots.");

        using var producer = new ProducerBuilder<string, string>(config).Build();

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var json = JsonSerializer.Serialize(message, JsonOptions);
            var result = await producer.ProduceAsync(
                options.Topic,
                new Message<string, string> { Key = message.AccountId, Value = json },
                cancellationToken);

            Console.WriteLine(
                $"  snapshot={message.SnapshotId} account={message.AccountId} payload={message.PayloadType,-13} -> {result.TopicPartitionOffset}");

            var isLastMessage = i == messages.Count - 1;
            if (isLastMessage)
            {
                continue;
            }

            var nextMessage = messages[i + 1];
            var delay = nextMessage.SnapshotId == message.SnapshotId
                ? options.MessageDelay
                : NextSnapshotDelay(options);

            if (delay > TimeSpan.Zero)
            {
                Console.WriteLine($"  waiting {delay} before next message...");
                await Task.Delay(delay, cancellationToken);
            }
        }

        producer.Flush(TimeSpan.FromSeconds(10));
        Console.WriteLine("Done.");
    }

    private static TimeSpan NextSnapshotDelay(SimulationOptions options)
    {
        if (options.SnapshotDelayMin == options.SnapshotDelayMax)
        {
            return options.SnapshotDelayMin;
        }

        var minMs = (int)options.SnapshotDelayMin.TotalMilliseconds;
        var maxMs = (int)options.SnapshotDelayMax.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs + 1));
    }
}
