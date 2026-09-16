using Microsoft.Extensions.Logging;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;

public sealed class LiveTestSnapshotCleanup(
    ILiveTestSnapshotCleanupStore database,
    ILiveTestSnapshotBlobCleanup blobs,
    ILogger<LiveTestSnapshotCleanup> logger)
{
    public async Task<LiveTestCleanupResult> DeleteAsync(CancellationToken cancellationToken)
    {
        var databaseLocations = await database.ListAsync(cancellationToken);
        var blobLocations = await blobs.ListAsync(cancellationToken);
        var locations = databaseLocations.Concat(blobLocations).Distinct().ToArray();

        // Validate every candidate before the first deletion; never trust a stored path.
        foreach (var location in locations)
        {
            Validate(location);
        }

        var deletedSnapshots = 0;
        var deletedBlobs = 0;
        var deletedRows = 0;
        foreach (var snapshot in locations.GroupBy(location => location.SnapshotId, StringComparer.Ordinal))
        {
            foreach (var location in snapshot.Where(location => location.RootPath.Length > 0))
            {
                deletedBlobs += await blobs.DeleteAsync(location, cancellationToken);
            }

            // Retain SQL paths if storage deletion fails so a subsequent call can retry.
            deletedRows += await database.DeleteAsync(snapshot.Key, cancellationToken);
            deletedSnapshots++;
            logger.LogInformation(
                "Deleted live-test snapshot; snapshotId={SnapshotId}, accountId={AccountId}, payloadType={PayloadType}",
                snapshot.Key, snapshot.First().AccountId, "all");
        }

        return new LiveTestCleanupResult(deletedSnapshots, deletedBlobs, deletedRows);
    }

    public static void Validate(LiveTestSnapshotLocation location)
    {
        if (!LiveTestSnapshot.IsTestId(location.SnapshotId)
            || (location.RootPath.Length > 0
                && (!LiveTestSnapshot.TryGetLocation(location.RootPath, out var snapshotId, out var accountId)
                    || snapshotId != location.SnapshotId || accountId != location.AccountId)))
        {
            throw new InvalidOperationException("Refusing to delete a snapshot without a valid live-test identity and matching storage path.");
        }
    }
}
