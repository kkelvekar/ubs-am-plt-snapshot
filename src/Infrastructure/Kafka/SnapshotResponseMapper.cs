using System.Globalization;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Kafka;

/// <summary>
/// Maps the domain notification onto the org shared-library <see cref="SnapshotResponse"/>.
/// The outbound counterpart of <see cref="SnapshotRequestMapper"/> and the seam that keeps the
/// org type from leaking past Infrastructure.
/// </summary>
/// <remarks>
/// Every value the org contract carries as text is rendered here rather than in the Domain:
/// the status enum becomes "Receiving"/"Complete"/"Failed", timestamps become ISO-8601
/// round-trip ("O") UTC, and an absent timestamp becomes an empty string.
/// </remarks>
internal static class SnapshotResponseMapper
{
    public static SnapshotResponse ToOrgResponse(SnapshotStatusNotification notification)
        => new()
        {
            SnapshotId = notification.SnapshotId,
            AccountId = notification.AccountId,
            ReceivedFiles = [.. notification.ReceivedFiles],
            MissingFiles = [.. notification.MissingFiles],
            Status = ToWireStatus(notification.Status),
            FirstReceivedAt = ToWireTimestamp(notification.FirstReceivedAt),
            LastUpdatedAt = ToWireTimestamp(notification.LastUpdatedAt),
            CompletedAt = ToWireTimestamp(notification.CompletedAt),
            DeclaredFailedAt = ToWireTimestamp(notification.DeclaredFailedAt),
            ReasonCode = notification.ReasonCode,
            ReasonDetail = notification.ReasonDetail,
        };

    private static string ToWireStatus(SnapshotTrackingStatus status)
        => status switch
        {
            SnapshotTrackingStatus.Receiving => "Receiving",
            SnapshotTrackingStatus.Complete => "Complete",
            SnapshotTrackingStatus.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Unmapped snapshot tracking status; the org response contract has no wire value for it."),
        };

    private static string ToWireTimestamp(DateTime? value)
        => value is null ? string.Empty : ToWireTimestamp(value.Value);

    /// <summary>
    /// Formats a timestamp as round-trip ("O"), invariant culture. Tracking timestamps are
    /// stored as UTC but come back from SQL as <see cref="DateTimeKind.Unspecified"/>, which
    /// "O" would render without an offset, so the kind is set before formatting and the wire
    /// value always carries its <c>Z</c>.
    /// </summary>
    private static string ToWireTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

        return utc.ToString("O", CultureInfo.InvariantCulture);
    }
}
