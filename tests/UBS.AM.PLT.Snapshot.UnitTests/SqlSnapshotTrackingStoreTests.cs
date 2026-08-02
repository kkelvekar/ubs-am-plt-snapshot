using System.Globalization;
using UBS.AM.PLT.Snapshot.Application.Features.SnapshotIngestion;
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
        var message = CreateMessage("snap-1", "orders");

        var entry = await _store.UpsertReceivedAsync(message, RootPath, CancellationToken.None);

        Assert.Same(entry, _repository.Rows["snap-1"]);
        Assert.Equal("snap-1", entry.SnapshotId);
        Assert.Equal(message.AccountId, entry.AccountId);
        Assert.Equal(message.SnapshotType, entry.SnapshotType);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(["orders.json"], entry.ReceivedFiles);
        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
        Assert.Equal(StartTime.UtcDateTime, entry.LastUpdatedAt);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public async Task Update_path_with_a_duplicate_filename_only_advances_last_updated_at()
    {
        var message = CreateMessage("snap-1", "orders");
        await _store.UpsertReceivedAsync(message, RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.UpsertReceivedAsync(message, "some/other/root", CancellationToken.None);

        Assert.Equal(["orders.json"], entry.ReceivedFiles);
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
            CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        Assert.Equal(["header.json", "orders.json"], entry.ReceivedFiles);
    }

    [Fact]
    public async Task Update_path_on_a_complete_row_never_regresses_status_or_first_write_fields()
    {
        var completedAt = StartTime.AddMinutes(10).UtcDateTime;
        _repository.Rows["snap-1"] = CreateCompleteRow("snap-1", completedAt);

        _timeProvider.UtcNow = StartTime.AddHours(2);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "orders"), "some/other/root", CancellationToken.None);

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
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

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

    [Fact]
    public async Task MarkRejected_on_a_missing_row_inserts_a_minimal_failed_row()
    {
        // The snapshot's very first message was the bad one: nothing describes it yet, and a
        // row is still inserted so the rejection is visible in SQL, not only in the log.
        var entry = await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);

        Assert.Same(entry, _repository.Rows["snap-1"]);
        Assert.Equal("snap-1", entry.SnapshotId);
        Assert.Equal("00675442A", entry.AccountId);
        Assert.Equal("portfolio", entry.SnapshotType);
        Assert.Equal(string.Empty, entry.AdlsRootPath);
        Assert.Empty(entry.ReceivedFiles);
        Assert.Equal(SnapshotTrackingStatus.Failed, entry.Status);
        Assert.Equal("FIELD_TOO_LONG: accountId is too long", entry.Reason);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
        Assert.Equal(StartTime.UtcDateTime, entry.LastUpdatedAt);
        Assert.Equal(StartTime.UtcDateTime, entry.DeclaredFailedAt);
        Assert.Null(entry.CompletedAt);
    }

    [Fact]
    public async Task MarkRejected_on_a_receiving_row_fails_it_without_losing_what_already_arrived()
    {
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);

        Assert.Equal(SnapshotTrackingStatus.Failed, entry.Status);
        Assert.Equal("FIELD_TOO_LONG: accountId is too long", entry.Reason);
        Assert.Equal(StartTime.AddMinutes(5).UtcDateTime, entry.DeclaredFailedAt);
        Assert.Equal(StartTime.AddMinutes(5).UtcDateTime, entry.LastUpdatedAt);

        // Everything the snapshot already achieved is untouched, so a later valid message can
        // still complete it.
        Assert.Equal(["orders.json"], entry.ReceivedFiles);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
    }

    [Fact]
    public async Task MarkRejected_on_an_already_failed_row_only_refreshes_the_reason_and_last_updated_at()
    {
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.MarkRejectedAsync(
            CreateRejection("MALFORMED_PAYLOAD_JSON", "payload is not valid JSON"),
            CancellationToken.None);

        Assert.Equal(SnapshotTrackingStatus.Failed, entry.Status);
        Assert.Equal("MALFORMED_PAYLOAD_JSON: payload is not valid JSON", entry.Reason);
        Assert.Equal(StartTime.AddMinutes(5).UtcDateTime, entry.LastUpdatedAt);

        // First-failure fields are not restamped by a repeat.
        Assert.Equal(StartTime.UtcDateTime, entry.DeclaredFailedAt);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
    }

    [Fact]
    public async Task MarkRejected_leaves_a_complete_row_entirely_untouched()
    {
        // COMPLETE is terminal: the index row is already written, and a late bad message for
        // that snapshot says nothing about it.
        var completedAt = StartTime.AddMinutes(10).UtcDateTime;
        _repository.Rows["snap-1"] = CreateCompleteRow("snap-1", completedAt);

        _timeProvider.UtcNow = StartTime.AddHours(2);
        var entry = await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);

        Assert.Equal(SnapshotTrackingStatus.Complete, entry.Status);
        Assert.Null(entry.Reason);
        Assert.Null(entry.DeclaredFailedAt);
        Assert.Equal(completedAt, entry.CompletedAt);
        Assert.Equal(completedAt, entry.LastUpdatedAt);
        Assert.Equal(["header.json", "orders.json", "calculations.json", "settings.json"], entry.ReceivedFiles);
    }

    [Fact]
    public async Task MarkRejected_stores_an_empty_accountId_when_the_message_value_does_not_fit_its_column()
    {
        // AccountId is NOT NULL: the value that made the message invalid must not fail the
        // INSERT that records the rejection. Visibility of the FAILED row wins.
        var overlong = new string('a', SnapshotFieldLimits.AccountIdMaxLength + 1);

        var entry = await _store.MarkRejectedAsync(
            CreateRejection() with { AccountId = overlong },
            CancellationToken.None);

        Assert.Equal(string.Empty, entry.AccountId);
        Assert.Equal("portfolio", entry.SnapshotType);
    }

    [Fact]
    public async Task MarkRejected_stores_an_empty_snapshotType_when_the_message_value_is_null()
    {
        var entry = await _store.MarkRejectedAsync(
            CreateRejection() with { SnapshotType = null },
            CancellationToken.None);

        Assert.Equal(string.Empty, entry.SnapshotType);
    }

    [Fact]
    public async Task Update_path_recovers_a_failed_row_to_receiving_and_clears_the_rejection()
    {
        // FAILED is not terminal: a later VALID message means files are still arriving, so the
        // snapshot returns to RECEIVING and can complete normally.
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "header"), RootPath, CancellationToken.None);
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Null(entry.Reason);
        Assert.Null(entry.DeclaredFailedAt);
        Assert.Equal(["header.json", "orders.json"], entry.ReceivedFiles);
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
        Assert.Equal(StartTime.AddMinutes(5).UtcDateTime, entry.LastUpdatedAt);
    }

    [Fact]
    public async Task Update_path_backfills_the_root_path_of_a_rejection_only_failed_row()
    {
        // MarkRejectedAsync's INSERT path stores an empty adls_root_path — no blob was ever
        // written for a pure rejection. The first REAL write is the first-write pin arriving
        // late, so it must fill the column in; otherwise completion reads the header from the
        // container root, 404s, and the recovered snapshot can never complete.
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);
        Assert.Equal(string.Empty, _repository.Rows["snap-1"].AdlsRootPath);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Null(entry.Reason);
        Assert.Null(entry.DeclaredFailedAt);
        Assert.Equal(["orders.json"], entry.ReceivedFiles);
        Assert.Equal(RootPath, await _store.GetRootPathAsync("snap-1", CancellationToken.None));
    }

    [Fact]
    public async Task Update_path_backfills_the_root_path_only_once_and_never_moves_it_afterwards()
    {
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "calculations"), "some/other/root", CancellationToken.None);

        // Backfilled once; from then on it is a pinned path like any other and is never moved.
        Assert.Equal(RootPath, entry.AdlsRootPath);
    }

    [Fact]
    public async Task A_rejection_after_real_files_never_blanks_or_moves_the_pinned_root_path()
    {
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "header"), RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(5);
        var rejected = await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);
        Assert.Equal(RootPath, rejected.AdlsRootPath);

        _timeProvider.UtcNow = StartTime.AddMinutes(9);
        var entry = await _store.UpsertReceivedAsync(
            CreateMessage("snap-1", "orders"), "some/other/root", CancellationToken.None);

        // The backfill guard sees a non-empty path and leaves it alone, so recovery keeps
        // writing into the folder the snapshot's earlier files already landed in.
        Assert.Equal(RootPath, entry.AdlsRootPath);
        Assert.Equal(SnapshotTrackingStatus.Receiving, entry.Status);
        Assert.Equal(["header.json", "orders.json"], entry.ReceivedFiles);
        Assert.Equal(StartTime.UtcDateTime, entry.FirstReceivedAt);
    }

    [Fact]
    public async Task MarkComplete_completes_a_snapshot_recovered_from_a_rejection()
    {
        // End to end on the recovery path: rejected, recovered, then completed as usual.
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        _timeProvider.UtcNow = StartTime.AddMinutes(9);
        await _store.MarkCompleteAsync("snap-1", CancellationToken.None);

        var entry = _repository.Rows["snap-1"];
        Assert.Equal(SnapshotTrackingStatus.Complete, entry.Status);
        Assert.Null(entry.Reason);
        Assert.Null(entry.DeclaredFailedAt);
        Assert.Equal(StartTime.AddMinutes(9).UtcDateTime, entry.CompletedAt);
    }

    [Fact]
    public async Task GetRootPath_returns_null_for_a_rejection_only_failed_row()
    {
        // The FAILED row inserted for a snapshot whose first message was bad stores an empty
        // adls_root_path — no blob was ever written for it. That is "not yet pinned", so the
        // caller must get null and compute a fresh root path; an empty string would survive
        // its ?? and send every blob of the recovering snapshot to the container root.
        await _store.MarkRejectedAsync(CreateRejection(), CancellationToken.None);
        Assert.Equal(string.Empty, _repository.Rows["snap-1"].AdlsRootPath);

        Assert.Null(await _store.GetRootPathAsync("snap-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetRootPath_returns_null_when_no_tracking_row_exists()
    {
        Assert.Null(await _store.GetRootPathAsync("snap-missing", CancellationToken.None));
    }

    [Fact]
    public async Task GetRootPath_returns_the_pinned_path_of_a_receiving_row()
    {
        await _store.UpsertReceivedAsync(CreateMessage("snap-1", "orders"), RootPath, CancellationToken.None);

        Assert.Equal(RootPath, await _store.GetRootPathAsync("snap-1", CancellationToken.None));
    }

    [Fact]
    public async Task GetRootPath_returns_the_pinned_path_of_a_complete_row()
    {
        _repository.Rows["snap-1"] = CreateCompleteRow("snap-1", StartTime.AddMinutes(10).UtcDateTime);

        Assert.Equal(RootPath, await _store.GetRootPathAsync("snap-1", CancellationToken.None));
    }

    private static SnapshotRejectionRecord CreateRejection(
        string reasonCode = "FIELD_TOO_LONG",
        string reasonDetail = "accountId is too long") => new()
        {
            SnapshotId = "snap-1",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            ReasonCode = reasonCode,
            ReasonDetail = reasonDetail,
        };

    private static SnapshotTrackingEntity CreateCompleteRow(string snapshotId, DateTime completedAt) => new()
    {
        SnapshotId = snapshotId,
        AccountId = "00675442A",
        SnapshotType = "portfolio",
        AdlsRootPath = RootPath,
        ReceivedFiles = ["header.json", "orders.json", "calculations.json", "settings.json"],
        Status = SnapshotTrackingStatus.Complete,
        FirstReceivedAt = StartTime.UtcDateTime,
        LastUpdatedAt = completedAt,
        CompletedAt = completedAt,
    };

    private static SnapshotMessage CreateMessage(string snapshotId, string payloadType)
        => new()
        {
            SnapshotId = snapshotId,
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            PublishedAt = StartTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            PublishedBy = "PortfolioCalculation",
            Payload = """{"total":21}""",
        };
}
