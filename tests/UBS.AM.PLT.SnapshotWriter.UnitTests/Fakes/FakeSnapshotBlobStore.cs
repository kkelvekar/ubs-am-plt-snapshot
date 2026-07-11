using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

public sealed class FakeSnapshotBlobStore : ISnapshotBlobStore
{
    private readonly List<SnapshotMessage> _written = [];

    public IReadOnlyList<SnapshotMessage> Written => _written;

    public Exception? ThrowOnWrite { get; set; }

    public Task<string> WriteAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        if (ThrowOnWrite is not null)
        {
            throw ThrowOnWrite;
        }

        _written.Add(message);
        return Task.FromResult(SnapshotBlobPath.RootFolder(message));
    }
}
