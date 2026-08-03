using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Mode A coverage for the Load-snapshots grid Read API (solution design §7):
/// <see cref="ISnapshotIndexQuery"/> + <see cref="SnapshotRowFlattener"/> exercised directly
/// against REAL Azure SQL. This is the only place that proves
/// <c>Database.SqlQueryRaw&lt;SnapshotIndexRow&gt;</c> actually materialises
/// <c>dbo.PortfolioSnapshotIndex</c> columns (including the <c>DisplayData AS DisplayDataJson</c>
/// alias and the required-init/DateTime properties) onto the keyless read DTO — no unit
/// test can catch a column/property name mismatch.
/// <para>
/// Rows are seeded with a direct SQL INSERT against <see cref="SnapshotFixture.DbContextFactory"/>
/// rather than through <see cref="SnapshotFixture.Handler"/>: this is a read-path test, and a
/// direct insert is the only way to plant a <c>DisplayData</c> payload containing a key not
/// modelled on <c>SnapshotIndexDisplayData</c> (proving the zero-code-change flow-through — the
/// write-side value converter would otherwise round-trip through the typed POCO and drop it).
/// </para>
/// <para>
/// The read port is built the same way the Api composition root builds it
/// (<c>AddSqlReadInfrastructure</c>) rather than re-using <see cref="SnapshotFixture.Handler"/>'s
/// provider, which only wires the write-side stores.
/// </para>
/// </summary>
public sealed class PortfolioSnapshotGridReadTests : IntegrationTestBase, IClassFixture<SnapshotFixture>
{
    // Fresh account ids — IT-ACC-001..008 are used by other groups (see MalformedInputTests);
    // IT-ACC-009 is reserved in appsettings.json's TestAccountIds for this test group.
    // IT-ACC-010R is a read-only-scratch id deliberately NOT in TestAccountIds' startup-cleanup
    // list: every row this suite writes is deleted by snapshotId in DisposeAsync regardless
    // (IntegrationTestBase / IntegrationTestCleanup.CleanupSnapshotAsync), so no separate
    // account-level cleanup entry is required for it.
    private const string AccountA = "IT-ACC-009";
    private const string AccountB = "IT-ACC-010R";

    public PortfolioSnapshotGridReadTests(SnapshotFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Grid_query_flattens_rows_with_novel_display_key_and_fixed_field_precedence_newest_first()
    {
        var sidOlder = NewSnapshotId("grid-older");
        var sidNewer = NewSnapshotId("grid-newer");
        var sidOtherAccount = NewSnapshotId("grid-othacct");

        // sidOlder: novel DisplayData key not modelled on SnapshotIndexDisplayData at all —
        // proves zero-code-change flow-through end to end.
        await InsertIndexRowAsync(
            sidOlder,
            AccountA,
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountA}/{sidOlder}",
            """{"benchmark":"MSCI World","brandNewField":"xyz"}""",
            new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc));

        // sidNewer: DisplayData carries a key ("accountId") that collides with a fixed field —
        // proves fixed-field precedence on collision.
        await InsertIndexRowAsync(
            sidNewer,
            AccountA,
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            "CASH_FLOW",
            $"portfolio_snapshots/accountId={AccountA}/{sidNewer}",
            """{"benchmark":"S&P 500","accountId":"clash-value"}""",
            new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc));

        // Different account entirely — must never appear when the filter asks only for AccountA.
        await InsertIndexRowAsync(
            sidOtherAccount,
            AccountB,
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountB}/{sidOtherAccount}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [AccountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            },
            Fixture.MockTime.Object);

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        // Only AccountA rows come back — AccountB's row is excluded by the AccountId IN filter.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(AccountA, r.AccountId));

        // ORDER BY SnapshotDate DESC — newest first.
        Assert.Equal(sidNewer, rows[0].SnapshotId);
        Assert.Equal(sidOlder, rows[1].SnapshotId);

        // Fixed-field materialisation via SqlQueryRaw<SnapshotIndexRow> — every column, including
        // the DisplayData AS DisplayDataJson alias, correctly bound onto the keyless read DTO.
        var older = rows[1];
        Assert.Equal(sidOlder, older.SnapshotId);
        Assert.Equal(AccountA, older.AccountId);
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), older.SnapshotDate);
        Assert.Equal("REBALANCE", older.EventType);
        Assert.Equal($"portfolio_snapshots/accountId={AccountA}/{sidOlder}", older.AdlsPath);
        Assert.Equal(new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc), older.CreatedAt);
        Assert.Contains("brandNewField", older.DisplayDataJson);

        var flattened = SnapshotRowFlattener.Flatten(rows);

        // Novel DisplayData key flows through to the flat row with zero code change.
        var olderFlat = flattened.Single(f => (string?)f["snapshotId"] == sidOlder);
        Assert.True(olderFlat.ContainsKey("brandNewField"));
        Assert.Equal("xyz", ((JsonElement)olderFlat["brandNewField"]!).GetString());

        // Fixed-field precedence: the real AccountId column wins over DisplayData's colliding
        // "accountId" key.
        var newerFlat = flattened.Single(f => (string?)f["snapshotId"] == sidNewer);
        Assert.Equal(AccountA, newerFlat["accountId"]);
        Assert.NotEqual("clash-value", newerFlat["accountId"]);
    }

    [Fact]
    public async Task Grid_query_honors_event_type_filter()
    {
        var sidRebalance = NewSnapshotId("grid-evt-reb");
        var sidCashFlow = NewSnapshotId("grid-evt-cf");

        await InsertIndexRowAsync(
            sidRebalance,
            AccountA,
            new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountA}/{sidRebalance}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidCashFlow,
            AccountA,
            new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc),
            "CASH_FLOW",
            $"portfolio_snapshots/accountId={AccountA}/{sidCashFlow}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [AccountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
                EventType = "CASH_FLOW",
            },
            Fixture.MockTime.Object);

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(sidCashFlow, row.SnapshotId);
        Assert.Equal("CASH_FLOW", row.EventType);
    }

    [Fact]
    public async Task Grid_query_honors_date_range_filter()
    {
        var sidInRange = NewSnapshotId("grid-dr-in");
        var sidBeforeRange = NewSnapshotId("grid-dr-bef");

        await InsertIndexRowAsync(
            sidBeforeRange,
            AccountA,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountA}/{sidBeforeRange}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidInRange,
            AccountA,
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountA}/{sidInRange}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [AccountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            },
            Fixture.MockTime.Object);

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(sidInRange, row.SnapshotId);
    }

    [Fact]
    public async Task Grid_query_honors_account_id_in_filter()
    {
        var sidAccountA = NewSnapshotId("grid-acct-a");
        var sidAccountB = NewSnapshotId("grid-acct-b");

        await InsertIndexRowAsync(
            sidAccountA,
            AccountA,
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountA}/{sidAccountA}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidAccountB,
            AccountB,
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={AccountB}/{sidAccountB}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [AccountB],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            },
            Fixture.MockTime.Object);

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(sidAccountB, row.SnapshotId);
        Assert.Equal(AccountB, row.AccountId);
    }

    private async Task InsertIndexRowAsync(
        string snapshotId,
        string accountId,
        DateTime snapshotDate,
        string eventType,
        string adlsPath,
        string displayDataJson,
        DateTime createdAt)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO dbo.PortfolioSnapshotIndex (SnapshotId, AccountId, SnapshotDate, EventType, AdlsPath, DisplayData, CreatedAt)
             VALUES ({snapshotId}, {accountId}, {snapshotDate}, {eventType}, {adlsPath}, {displayDataJson}, {createdAt})
             """);
    }

    /// <summary>
    /// Builds <see cref="ISnapshotIndexQuery"/> the same way the Api composition root does
    /// (<c>AddSqlReadInfrastructure</c>, in <c>Api/Program.cs</c>), rather than reusing
    /// <see cref="SnapshotFixture"/>'s write-side provider, which never registers the read port.
    /// The returned wrapper owns its own <see cref="ServiceProvider"/> and disposes it with the
    /// query, closing the pooled DbContext factory it creates.
    /// </summary>
    private QueryScope BuildQuery()
    {
        var services = new ServiceCollection();
        services.AddSqlReadInfrastructure(Fixture.Configuration["Database:ConnectionString"] ?? string.Empty);
        var provider = services.BuildServiceProvider();
        return new QueryScope(provider);
    }

    private sealed class QueryScope : IDisposable
    {
        private readonly ServiceProvider _provider;

        public QueryScope(ServiceProvider provider)
        {
            _provider = provider;
            Service = provider.GetRequiredService<ISnapshotIndexQuery>();
        }

        public ISnapshotIndexQuery Service { get; }

        public void Dispose() => _provider.Dispose();
    }
}
