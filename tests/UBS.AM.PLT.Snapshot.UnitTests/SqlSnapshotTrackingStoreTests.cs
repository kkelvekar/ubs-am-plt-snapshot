using System.Text.Json;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Exercises <see cref="SqlSnapshotTrackingStore"/>'s upsert/idempotency decisions (the
/// <c>apply</c> delegates it passes to <see cref="ISnapshotTrackingRepository"/>) against
/// the in-memory <see cref="FakeSnapshotTrackingRepository"/> — no EF Core, no SQL. The
/// same rules against the real repository/SQL Server are covered by
/// <c>SnapshotTrackingIntegrationTests</c>.
/// </summary>
public sealed class SqlSnapshotTrackingStoreTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);
    private const string RootPath = "snapshots/year=2026/month=05/00675442A/snap-1_20260522061014";

    private readonly FakeSnapshotTrackingRepository _repository = new();
    private readonly RecordingTimeProvider _timeProvider = new() { UtcNow = StartTime };
    private readonly SqlSnapshotTrackingStore _store;

    public SqlSnapshotTrackingStoreTests()
    {
        _store = new SqlSnapshotTrackingStore(_repository, _timeProvider);
    }

    [Fact]
    public async Task Insert_path_creates_a_receiving_row_with_all_fields_set()
    {
        var message = CreateMessage("snap-1", "instruments");

        var entry = await _store.UpsertReceivedAsync(message, RootPath, CancellationToken.None);

        Assert.Same(entry, _repository.Rows["snap-1"]);
        Assert.Equal("snap-1", entry.SnapshotId);
        Assert.Equal(message.AccountId, entry.AccountId);
        Assert.Equal(message.SnapshotType, entry.SnapshotType);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(["instruments.json"], entry.ReceivedFiles);
        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
        Assert.Equal(StartTime.UtcDateTime, entry.LastUpdatedAt);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public async Task Update_path_with_a_duplicate_filename_only_advances_last_updated_at()
    {
        var message = CreateMessage("snap-1", "instruments");
        await _store.UpsertReceivedAsync(message, RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.UpsertReceivedAsync(message, "some/other/root", CancellationToken.None);

        Assert.Equal(["instruments.json"], entry.ReceivedFiles);
        Assert.Equal(StartTime.AddMinutes(5).UtcDateTime, entry.LastUpdatedAt);
        // First-write-wins fields are never touched on the update path.
        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
    }

    [Fact]
    public async Task Update_path_appends_a_new_filename_via_ordinal_set_union()
    {
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "header"), RootPath, CancellationToken.None);

        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "instruments"), RootPath, CancellationToken.None);

        Assert.Equal(["header.json", "instruments.json"], entry.ReceivedFiles);
    }

    [Fact]
    public async Task Update_path_on_a_complete_row_never_regresses_status_or_first_write_fields()
    {
        var completedAt = StartTime.AddMinutes(10).UtcDateTime;
        _repository.Rows["snap-1"] = CreateCompleteRow("snap-1", completedAt);

        _timeProvider.UtcNow = StartTime.AddHours(2);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "instruments"), "some/other/root", CancellationToken.None);

        Assert.Equal(SnapshotTrackingStatus.Complete, entry.Status);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
        Assert.Equal(completedAt, entry.CompletedAt);
        Assert.Equal(StartTime.AddHours(2).UtcDateTime, entry.LastUpdatedAt);
    }

    [Fact]
    public async Task MarkComplete_on_a_missing_row_throws()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.MarkCompleteAsync("snap-missing", CancellationToken.None));

        Assert.Equal(
            "Cannot mark snapshot 'snap-missing' complete: no tracking row exists.",
            exception.Message);
    }

    [Fact]
    public async Task MarkComplete_on_a_receiving_row_sets_complete_and_completed_at()
    {
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "instruments"), RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(3);
        await _store.MarkCompleteAsync("snap-1", CancellationToken.None);

        var entry = _repository.Rows["snap-1"];
        Assert.Equal(SnapshotTrackingStatus.Complete, entry.Status);
        Assert.Equal(StartTime.AddMinutes(3).UtcDateTime, entry.CompletedAt);
    }

    [Fact]
    public async Task MarkComplete_on_an_already_complete_row_leaves_completed_at_unchanged()
    {
        var completedAt = StartTime.AddMinutes(10).UtcDateTime;
        _repository.Rows["snap-1"] = CreateCompleteRow("snap-1", completedAt);

        _timeProvider.UtcNow = StartTime.AddHours(2);
        await _store.MarkCompleteAsync("snap-1", CancellationToken.None);

        var entry = _repository.Rows["snap-1"];
        Assert.Equal(SnapshotTrackingStatus.Complete, entry.Status);
        Assert.Equal(completedAt, entry.CompletedAt);
    }

    private static SnapshotTrackingEntity CreateCompleteRow(string snapshotId, DateTime completedAt) => new()
    {
        SnapshotId = snapshotId,
        AccountId = "00675442A",
        SnapshotType = "portfolio",
        AdlsRootPath = RootPath,
        ReceivedFiles = ["header.json", "instruments.json", "calculations.json", "settings.json"],
        Status = SnapshotTrackingStatus.Complete,
        FirstReceivedAt = StartTime.UtcDateTime,
        LastUpdatedAt = completedAt,
        CompletedAt = completedAt,
    };

    private static SnapshotMessage CreateMessage(string snapshotId, string payloadType)
    {
        using var payload = JsonDocument.Parse("""{"total":21}""");
        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = StartTime.UtcDateTime,
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };
    }
}
