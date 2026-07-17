namespace UBS.AM.PLT.Snapshot.Domain;

/// <summary>
/// Pure completeness check, per solution design §6: a snapshot is complete once every
/// required filename has been received. Set containment (required ⊆ received), not
/// strict equality — deliberately tolerates stray extra files.
/// </summary>
public static class SnapshotCompleteness
{
    public static bool IsComplete(IReadOnlyCollection<string> receivedFiles, IReadOnlySet<string> requiredFiles)
        => requiredFiles.IsSubsetOf(receivedFiles);
}
