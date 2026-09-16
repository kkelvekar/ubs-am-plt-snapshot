namespace UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;

public sealed record LiveTestSnapshotLocation(string SnapshotId, string AccountId, string RootPath);

public sealed record LiveTestCleanupResult(int DeletedSnapshots, int DeletedBlobs, int DeletedDatabaseRows);
