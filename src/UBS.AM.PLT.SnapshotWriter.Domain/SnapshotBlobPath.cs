using System.Globalization;

namespace UBS.AM.PLT.SnapshotWriter.Domain;

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
    /// </summary>
    public static string RootFolder(SnapshotMessage message)
    {
        var publishedAtUtc = message.PublishedAt.ToUniversalTime();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{message.SnapshotType}_snapshots/year={publishedAtUtc:yyyy}/month={publishedAtUtc:MM}/accountId={message.AccountId}/snapshotId={message.SnapshotId}");
    }

    /// <summary>
    /// Full blob name within the container: root folder plus <c>{payloadType}.json</c>.
    /// </summary>
    public static string FullPath(SnapshotMessage message)
        => $"{RootFolder(message)}/{message.PayloadType}.json";
}
