using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

/// <summary>
/// Exercises <see cref="SqlSnapshotIndexStore"/>'s UPSERT decisions (the <c>apply</c>
/// delegate it passes to <see cref="ISnapshotIndexRepository"/>) against the in-memory
/// <see cref="FakeSnapshotIndexRepository"/> — no EF Core, no SQL. The same rules against
/// the real repository/SQL Server are covered by <c>SnapshotCompletionIntegrationTests</c>.
/// </summary>
public sealed class SqlSnapshotIndexStoreTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);

    private readonly FakeSnapshotIndexRepository _repository = new();
    private readonly RecordingTimeProvider _timeProvider = new() { UtcNow = StartTime };
    private readonly SqlSnapshotIndexStore _store;

    public SqlSnapshotIndexStoreTests()
    {
        _store = new SqlSnapshotIndexStore(_repository, _timeProvider);
    }

    [Fact]
    public async Task Insert_path_sets_created_at()
    {
        var entry = CreateEntry("snap-1", eventType: "ModelChange");

        await _store.UpsertAsync(entry, CancellationToken.None);

        var row = _repository.Rows["snap-1"];
        Assert.Same(entry, row);
        Assert.Equal(StartTime.UtcDateTime, row.CreatedAt);
    }

    [Fact]
    public async Task Update_path_copies_the_row_content_but_never_touches_created_at()
    {
        await _store.UpsertAsync(CreateEntry("snap-1", eventType: "ModelChange"), CancellationToken.None);
        var original = _repository.Rows["snap-1"];

        _timeProvider.UtcNow = StartTime.AddHours(1);
        var redelivered = CreateEntry("snap-1", eventType: "Rebalance");
        await _store.UpsertAsync(redelivered, CancellationToken.None);

        var row = _repository.Rows["snap-1"];
        Assert.Same(original, row);
        Assert.Equal("Rebalance", row.EventType);
        Assert.Equal(redelivered.AccountId, row.AccountId);
        Assert.Equal(redelivered.SnapshotDate, row.SnapshotDate);
        Assert.Equal(redelivered.AdlsPath, row.AdlsPath);
        Assert.Same(redelivered.DisplayData, row.DisplayData);
        Assert.Equal(StartTime.UtcDateTime, row.CreatedAt);
    }

    private static SnapshotIndexEntity CreateEntry(string snapshotId, string eventType) => new()
    {
        SnapshotId = snapshotId,
        AccountId = "00675442A",
        SnapshotDate = StartTime.UtcDateTime,
        EventType = eventType,
        AdlsPath = "snapshots/year=2026/month=05/00675442A/snap-1_20260522061014",
        DisplayData = new SnapshotIndexDisplayData
        {
            Benchmark = "MCCHM2EQ",
            BaseCcy = "CHF",
            ProgramId = "123456",
            BatchId = "15884",
            NumOrders = 4,
            PtcAlerts = 0,
            OrderApprovedBy = "Anna Miller",
            OrderApprovedAt = new DateTime(2026, 5, 15, 6, 10, 14, DateTimeKind.Utc),
            OrderSentBy = "James Smith",
            OrderSentAt = new DateTime(2026, 5, 15, 6, 14, 22, DateTimeKind.Utc),
        },
    };
}
