namespace UBS.AM.PLT.Snapshot.TestProducer;

public sealed record SnapshotEnvelope(
    string SnapshotId,
    string AccountId,
    string SnapshotType,
    string PayloadType,
    string PublishedAt,
    string PublishedBy,
    string Payload);
