namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.SnapshotConfig;

/// <summary>
/// One entry of the library-owned required-files map (<see cref="SnapshotConfigDefinition"/>),
/// keyed by snapshotType, listing the files a snapshot of that type must receive.
/// </summary>
public sealed class SnapshotTypeConfig
{
    public string[] RequiredFiles { get; init; } = [];
}
