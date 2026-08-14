using System.Globalization;
using System.Text.Json.Nodes;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

internal static class SnapshotGenerator
{
    public static IReadOnlyList<GeneratedSnapshotMessage> Generate(
        SnapshotTemplate template,
        SnapshotSimulationRequest request,
        DateTimeOffset startedAt)
    {
        Validate(request);
        var messages = new List<GeneratedSnapshotMessage>(request.SnapshotCount * template.Payloads.Count);
        var invocationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        for (var snapshotIndex = 0; snapshotIndex < request.SnapshotCount; snapshotIndex++)
        {
            var accountId = template.AccountIds[snapshotIndex % template.AccountIds.Count];
            var snapshotId = $"corr{startedAt:yyyyMMddHHmmssfff}-{invocationId}-{snapshotIndex + 1:0000}";
            var snapshotPublishedAt = startedAt
                .AddTicks(request.MessageDelay.Ticks * snapshotIndex * (template.Payloads.Count - 1))
                .AddMinutes(snapshotIndex);

            for (var payloadIndex = 0; payloadIndex < template.Payloads.Count; payloadIndex++)
            {
                var payload = template.Payloads[payloadIndex];
                var messagePublishedAt = snapshotPublishedAt.AddTicks(request.MessageDelay.Ticks * payloadIndex);
                var message = new SnapshotRequest
                {
                    SnapshotId = snapshotId,
                    AccountId = accountId,
                    SnapshotType = "portfolio",
                    PayloadType = payload.PayloadType,
                    PublishedAt = messagePublishedAt.ToString("O", CultureInfo.InvariantCulture),
                    PublishedBy = payload.PublishedBy,
                    Payload = BuildPayload(payload, snapshotIndex, messagePublishedAt),
                };

                messages.Add(new GeneratedSnapshotMessage(accountId, message));
            }
        }

        return messages;
    }

    private static void Validate(SnapshotSimulationRequest request)
    {
        if (request.SnapshotCount < 1)
        {
            throw new SnapshotSimulationValidationException("SnapshotCount must be at least 1.");
        }

        if (request.MessageDelay < TimeSpan.Zero
            || request.SnapshotDelayMin < TimeSpan.Zero
            || request.SnapshotDelayMax < TimeSpan.Zero)
        {
            throw new SnapshotSimulationValidationException("Simulation delays cannot be negative.");
        }

        if (request.SnapshotDelayMax < request.SnapshotDelayMin)
        {
            throw new SnapshotSimulationValidationException(
                "SnapshotDelayMax must be greater than or equal to SnapshotDelayMin.");
        }
    }

    private static string BuildPayload(PayloadTemplate template, int snapshotIndex, DateTimeOffset publishedAt)
    {
        var node = JsonNode.Parse(template.Payload.GetRawText())
            ?? throw new SnapshotSimulationValidationException(
                $"Payload template '{template.PayloadType}' is not valid JSON.");

        if (node is JsonObject payload)
        {
            if (template.PayloadType == "header")
            {
                payload["programId"] = (123456 + snapshotIndex).ToString(CultureInfo.InvariantCulture);
                payload["batchId"] = (15884 + snapshotIndex).ToString(CultureInfo.InvariantCulture);
                payload["numOrders"] = 4 + snapshotIndex % 7;
                payload["orderApprovedAt"] = publishedAt.AddMinutes(-4).UtcDateTime;
                payload["orderSentAt"] = publishedAt.UtcDateTime;
            }

            if (template.PayloadType == "calculations")
            {
                payload["calculatedAt"] = publishedAt.UtcDateTime;
            }
        }

        return node.ToJsonString();
    }
}
