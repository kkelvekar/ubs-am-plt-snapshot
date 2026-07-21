namespace UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

/// <summary>
/// Port for the completeness check's required-file list, keyed by snapshotType. The list's
/// source is an adapter concern; this port stays source-neutral.
/// </summary>
public interface IRequiredFilesProvider
{
    /// <summary>Throws for a <paramref name="snapshotType"/> not present in the required-files map.</summary>
    IReadOnlySet<string> GetRequiredFiles(string snapshotType);
}
