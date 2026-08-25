using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// Stub <see cref="ISnapshotMessageHandler"/> for <c>SnapshotRequestCommand</c> tests: throws a
/// configured exception from <see cref="HandleAsync"/> and records every
/// <see cref="RecordUnexpectedFailureAsync"/> call, optionally throwing from that too (to drive
/// the poison-record-failure-escalates-to-transient path).
/// </summary>
public sealed class FakeSnapshotMessageHandler : ISnapshotMessageHandler
{
    private readonly List<(SnapshotMessage Message, Exception Exception)> _recordedUnexpectedFailures = [];

    public Exception? ThrowOnHandle { get; set; }

    public Exception? ThrowOnRecordUnexpectedFailure { get; set; }

    public IReadOnlyList<(SnapshotMessage Message, Exception Exception)> RecordedUnexpectedFailures => _recordedUnexpectedFailures;

    public Task HandleAsync(SnapshotMessage message, CancellationToken cancellationToken)
    {
        if (ThrowOnHandle is not null)
        {
            throw ThrowOnHandle;
        }

        return Task.CompletedTask;
    }

    public Task RecordUnexpectedFailureAsync(SnapshotMessage message, Exception exception, CancellationToken cancellationToken)
    {
        _recordedUnexpectedFailures.Add((message, exception));

        if (ThrowOnRecordUnexpectedFailure is not null)
        {
            throw ThrowOnRecordUnexpectedFailure;
        }

        return Task.CompletedTask;
    }
}
