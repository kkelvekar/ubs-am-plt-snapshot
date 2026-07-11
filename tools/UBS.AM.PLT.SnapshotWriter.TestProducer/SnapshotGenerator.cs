using System.Text.Json;
using System.Text.Json.Nodes;

namespace UBS.AM.PLT.SnapshotWriter.TestProducer;

public static class SnapshotGenerator
{
    public static IReadOnlyList<SnapshotEnvelope> GenerateSnapshots(
        SnapshotTemplate template,
        SimulationOptions options,
        DateTimeOffset startedAt)
    {
        var messages = new List<SnapshotEnvelope>(options.SnapshotCount * template.Payloads.Count);

        for (var snapshotIndex = 0; snapshotIndex < options.SnapshotCount; snapshotIndex++)
        {
            var accountId = template.AccountIds[snapshotIndex % template.AccountIds.Count];
            var stage = template.Stages[snapshotIndex % template.Stages.Count];
            var snapshotId = $"corr{startedAt:yyyyMMddHHmmss}-{snapshotIndex + 1:0000}";
            var snapshotPublishedAt = startedAt
                .AddTicks(options.MessageDelay.Ticks * snapshotIndex * (template.Payloads.Count - 1))
                .AddMinutes(snapshotIndex);

            for (var payloadIndex = 0; payloadIndex < template.Payloads.Count; payloadIndex++)
            {
                var payload = template.Payloads[payloadIndex];
                var messagePublishedAt = snapshotPublishedAt.AddTicks(options.MessageDelay.Ticks * payloadIndex);
                messages.Add(new SnapshotEnvelope(
                    snapshotId,
                    accountId,
                    "portfolio",
                    payload.PayloadType,
                    stage,
                    messagePublishedAt,
                    payload.PublishedBy,
                    "1.0",
                    BuildPayload(payload, snapshotIndex, messagePublishedAt)));
            }
        }

        return messages;
    }

    private static JsonElement BuildPayload(PayloadTemplate template, int snapshotIndex, DateTimeOffset publishedAt)
    {
        var node = JsonNode.Parse(template.Payload.GetRawText())
            ?? throw new InvalidOperationException($"Payload template '{template.PayloadType}' is not valid JSON.");

        if (node is JsonObject payload)
        {
            if (template.PayloadType == "header")
            {
                payload["programId"] = (123456 + snapshotIndex).ToString();
                payload["batchId"] = (15884 + snapshotIndex).ToString();
                payload["numOrders"] = 4 + snapshotIndex % 7;
                payload["orderApprovedAt"] = publishedAt.AddMinutes(-4).UtcDateTime;
                payload["orderSentAt"] = publishedAt.UtcDateTime;
            }

            if (template.PayloadType == "calculations")
            {
                payload["calculatedAt"] = publishedAt.UtcDateTime;
            }
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }
}
