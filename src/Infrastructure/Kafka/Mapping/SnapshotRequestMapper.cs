using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Mapping;

/// <summary>
/// Adapts the org shared-library <see cref="SnapshotRequest"/> onto our domain envelope.
/// The seam that keeps the org type from leaking past the Infrastructure layer: at
/// lift-and-shift the org DTO becomes a NuGet type and only this mapper changes.
///
/// Straight 1:1 assignment. <see cref="SnapshotRequest.Payload"/> is carried across as the
/// same string reference — never parsed, never re-serialised, so the blob write stays
/// byte-identical to what was published. Empty optional fields are passed through as-is:
/// the org contract's defaults are the contract, and tightening validation is a separate
/// concern, not this adapter's.
/// </summary>
internal static class SnapshotRequestMapper
{
    public static SnapshotMessage ToDomain(SnapshotRequest request)
        => new()
        {
            SnapshotId = request.SnapshotId,
            AccountId = request.AccountId,
            SnapshotType = request.SnapshotType,
            PayloadType = request.PayloadType,
            PublishedAt = request.PublishedAt,
            PublishedBy = request.PublishedBy,
            Payload = request.Payload,
        };
}
