namespace UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;

/// <summary>
/// Port for the completeness check's required-file list — read from
/// <c>SnapshotConfig</c> configuration (design §4), never hardcoded, so adding a new
/// payload type requires no code change.
/// </summary>
public interface IRequiredFilesProvider
{
    /// <summary>Throws for a <paramref name="snapshotType"/> not present in configuration.</summary>
    IReadOnlySet<string> GetRequiredFiles(string snapshotType);
}
