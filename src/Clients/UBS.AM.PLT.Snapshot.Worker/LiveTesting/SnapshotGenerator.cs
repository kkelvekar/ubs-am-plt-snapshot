using System.Globalization;
using System.Text.Json.Nodes;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

internal static class SnapshotGenerator
{
    public static IReadOnlyList<GeneratedSnapshotMessage> Generate(
        SnapshotTemplate template,
        DateTimeOffset startedAt)
    {
        var messages = new List<GeneratedSnapshotMessage>(template.Payloads.Count);
        var invocationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var accountId = template.AccountIds[0];
        var snapshotId = $"corr{startedAt:yyyyMMddHHmmssfff}-{invocationId}-0001";

        foreach (var payload in template.Payloads)
        {
            var message = new SnapshotRequest
            {
                SnapshotId = snapshotId,
                AccountId = accountId,
                SnapshotType = "portfolio",
                PayloadType = payload.PayloadType,
                PublishedAt = startedAt.ToString("O", CultureInfo.InvariantCulture),
                PublishedBy = payload.PublishedBy,
                Payload = BuildPayload(payload, snapshotIndex: 0, startedAt),
            };

            messages.Add(new GeneratedSnapshotMessage(accountId, message));
        }

        return messages;
    }

    private static string BuildPayload(PayloadTemplate template, int snapshotIndex, DateTimeOffset publishedAt)
    {
        var node = JsonNode.Parse(template.Payload.GetRawText())
            ?? throw new SnapshotSimulationValidationException(
                $"Payload template '{template.PayloadType}' is not valid JSON.");

        if (node is JsonObject payload)
        {
            if (template.PayloadType == "header" && payload["Payload"] is JsonObject headerPayload)
            {
                headerPayload["programId"] = (123456 + snapshotIndex).ToString(CultureInfo.InvariantCulture);
                headerPayload["batchId"] = (15884 + snapshotIndex).ToString(CultureInfo.InvariantCulture);
                headerPayload["numOrders"] = 4 + snapshotIndex % 7;
                headerPayload["orderApprovedAt"] = publishedAt.AddMinutes(-4).UtcDateTime;
                headerPayload["orderSentAt"] = publishedAt.UtcDateTime;
            }

            if (template.PayloadType == "portfolio")
            {
                payload["calculatedAt"] = publishedAt.UtcDateTime;
            }
        }

        return node.ToJsonString();
    }
}
