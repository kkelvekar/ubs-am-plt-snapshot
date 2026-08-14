using System.Text.Json;
using UBS.Advantage.CommunicationModels.Snapshot;

namespace UBS.AM.PLT.Snapshot.Worker.LiveTesting;

public sealed record SnapshotTemplate(
    IReadOnlyList<string> AccountIds,
    IReadOnlyList<PayloadTemplate> Payloads);

public sealed record PayloadTemplate(
    string PayloadType,
    string PublishedBy,
    JsonElement Payload);

public sealed record SnapshotSimulationCaseResult(
    string CaseName,
    string SnapshotId,
    string ExpectedStatus,
    string ExpectedReasonCode);

public sealed record SnapshotSimulationResult(
    IReadOnlyList<SnapshotSimulationCaseResult> Cases,
    int MessageCount);

internal sealed record GeneratedSnapshotMessage(string AccountId, SnapshotRequest Request);

internal sealed class SnapshotSimulationValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
