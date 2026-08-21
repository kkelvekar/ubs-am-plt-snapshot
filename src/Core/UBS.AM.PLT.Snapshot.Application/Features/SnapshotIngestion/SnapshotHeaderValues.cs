namespace UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;

/// <summary>
/// The two values resolved from a completed snapshot's header in a single parse:
/// <c>Payload.Event</c> and the raw text of the <c>Payload</c> object itself.
/// </summary>
public readonly record struct SnapshotHeaderValues(string EventType, string DisplayData);
