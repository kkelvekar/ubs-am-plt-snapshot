using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakeRequiredFilesProvider : IRequiredFilesProvider
{
    public Dictionary<string, IReadOnlySet<string>> RequiredFilesByType { get; } = [];

    public List<string> Calls { get; } = [];

    public IReadOnlySet<string> GetRequiredFiles(string snapshotType)
    {
        Calls.Add(snapshotType);

        if (!RequiredFilesByType.TryGetValue(snapshotType, out var required))
        {
            throw new KeyNotFoundException($"No SnapshotConfig entry configured for snapshotType '{snapshotType}'.");
        }

        return required;
    }
}
