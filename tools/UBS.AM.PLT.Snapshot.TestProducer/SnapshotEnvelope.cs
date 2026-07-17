using System.Text.Json;

namespace UBS.AM.PLT.Snapshot.TestProducer;

public sealed record SnapshotEnvelope(
    string SnapshotId,
    string AccountId,
    string SnapshotType,
    string PayloadType,
    string Stage,
    DateTimeOffset PublishedAt,
    string PublishedBy,
    string SchemaVersion,
    JsonElement Payload);
