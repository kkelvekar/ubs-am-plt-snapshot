using UBS.AM.PLT.SnapshotWriter.Application.Interfaces.Infrastructure;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

public sealed class FakeSnapshotTrackingStore : ISnapshotTrackingStore
{
    private readonly List<(SnapshotMessage Message, string AdlsRootPath)> _upserts = [];

    public IReadOnlyList<(SnapshotMessage Message, string AdlsRootPath)> Upserts => _upserts;

    public Exception? ThrowOnUpsert { get; set; }

    public Task<SnapshotTrackingEntry> UpsertReceivedAsync(
        SnapshotMessage message,
        string adlsRootPath,
        CancellationToken cancellationToken)
    {
        if (ThrowOnUpsert is not null)
        {
            throw ThrowOnUpsert;
        }

        _upserts.Add((message, adlsRootPath));

        return Task.FromResult(new SnapshotTrackingEntry
        {
            SnapshotId = message.SnapshotId,
            AccountId = message.AccountId,
            SnapshotType = message.SnapshotType,
            AdlsRootPath = adlsRootPath,
            ReceivedFiles = [SnapshotBlobPath.FileName(message.PayloadType)],
            Status = SnapshotTrackingStatus.Receiving,
            FirstReceivedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            LastUpdatedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
        });
    }
}
