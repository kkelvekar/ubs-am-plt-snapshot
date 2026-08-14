using System.Text.Json;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

public sealed record SnapshotSimulationRequest
{
    public int SnapshotCount { get; init; } = 1;

    public TimeSpan MessageDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan SnapshotDelayMin { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan SnapshotDelayMax { get; init; } = TimeSpan.FromMinutes(2);

    public string TemplateFileName { get; init; } = "snapshot-simulation-data.json";
}

public sealed record SnapshotTemplate(
    IReadOnlyList<string> AccountIds,
    IReadOnlyList<PayloadTemplate> Payloads);

public sealed record PayloadTemplate(
    string PayloadType,
    string PublishedBy,
    JsonElement Payload);

public sealed record SnapshotSimulationDelivery(
    string SnapshotId,
    string AccountId,
    string PayloadType,
    string Topic,
    int Partition,
    long Offset);

public sealed record SnapshotSimulationResult(
    IReadOnlyList<string> SnapshotIds,
    int MessageCount,
    IReadOnlyList<SnapshotSimulationDelivery> Deliveries);

internal sealed record GeneratedSnapshotMessage(string AccountId, SnapshotRequest Request);

internal sealed class SnapshotSimulationValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
