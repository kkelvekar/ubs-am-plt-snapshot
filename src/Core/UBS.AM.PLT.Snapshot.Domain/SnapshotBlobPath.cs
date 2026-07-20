using System.Globalization;

namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Pure builder for the blob naming convention, per solution design §5:
/// <c>{snapshotType}_snapshots/year={yyyy}/month={MM}/accountId={accountId}/snapshotId={snapshotId}/{payloadType}.json</c>.
/// The container name is never part of the path.
/// </summary>
public static class SnapshotBlobPath
{
    /// <summary>
    /// Snapshot root folder — everything up to and including the snapshotId segment,
    /// no trailing slash. Stored later as the tracking row's adls_root_path (design §6).
    /// The year/month segments come from <paramref name="firstArrivalUtc"/> — the arrival
    /// time of the snapshot's FIRST payload, pinned once per snapshot by the caller —
    /// never from <c>message.PublishedAt</c>, so payloads whose publish timestamps
    /// straddle a month/year boundary still land under one folder.
    /// </summary>
    public static string RootFolder(SnapshotMessage message, DateTimeOffset firstArrivalUtc)
    {
        var arrivalUtc = firstArrivalUtc.ToUniversalTime();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{message.SnapshotType}_snapshots/year={arrivalUtc:yyyy}/month={arrivalUtc:MM}/accountId={message.AccountId}/snapshotId={message.SnapshotId}");
    }

    /// <summary>
    /// Leaf filename for a payload type: <c>{payloadType}.json</c>. Single source of
    /// filename truth — the values stored in the tracking row's received_files and the
    /// SnapshotConfig requiredFiles entries follow this same convention, so completeness
    /// comparison is always name-to-name.
    /// </summary>
    public static string FileName(string payloadType)
        => $"{payloadType}.json";

    /// <summary>
    /// Full blob name within the container: root folder plus <see cref="FileName"/>.
    /// Takes the already-resolved root folder (pinned per snapshot) rather than deriving
    /// one, so no caller can accidentally compute a path from per-message timestamps.
    /// </summary>
    public static string FullPath(string rootFolder, string payloadType)
        => $"{rootFolder}/{FileName(payloadType)}";
}
