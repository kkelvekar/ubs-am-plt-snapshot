namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.SnapshotConfig;

/// <summary>
/// Bound from one entry of the <c>SnapshotConfig</c> configuration section, keyed by
/// snapshotType (design §4), e.g. <c>SnapshotConfig:portfolio:requiredFiles</c>.
/// </summary>
public sealed class SnapshotTypeConfig
{
    public string[] RequiredFiles { get; set; } = [];
}
