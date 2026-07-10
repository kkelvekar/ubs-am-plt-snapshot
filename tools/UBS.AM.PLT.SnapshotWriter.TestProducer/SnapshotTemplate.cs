using System.Text.Json;

namespace UBS.AM.PLT.SnapshotWriter.TestProducer;

public sealed record SnapshotTemplate(
    IReadOnlyList<string> AccountIds,
    IReadOnlyList<string> Stages,
    IReadOnlyList<PayloadTemplate> Payloads);

public sealed record PayloadTemplate(
    string PayloadType,
    string PublishedBy,
    JsonElement Payload);
