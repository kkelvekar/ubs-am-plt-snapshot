using System.Text.Json;

namespace UBS.AM.PLT.Snapshot.TestProducer;

public sealed record SnapshotTemplate(
    IReadOnlyList<string> AccountIds,
    IReadOnlyList<PayloadTemplate> Payloads);

public sealed record PayloadTemplate(
    string PayloadType,
    string PublishedBy,
    JsonElement Payload);
