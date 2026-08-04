using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

public sealed class FakeSnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly List<SnapshotMessage> _handled = [];

    public IReadOnlyList<SnapshotMessage> Handled => _handled;

    public Exception? ThrowOnHandle { get; set; }

    public Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        _handled.Add(message);

        return ThrowOnHandle is null ? Task.CompletedTask : Task.FromException(ThrowOnHandle);
    }
}
