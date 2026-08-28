using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Mode A coverage for the Load-snapshots grid Read API (solution design §7):
/// <see cref="IPortfolioSnapshotIndexQuery"/> + <see cref="SnapshotRowFlattener"/> exercised directly
/// against REAL Azure SQL. This is the only place that proves
/// <c>Database.SqlQueryRaw&lt;PortfolioSnapshotIndexRow&gt;</c> actually materialises
/// <c>dbo.PortfolioSnapshotIndex</c> columns (including the <c>DisplayData AS DisplayDataJson</c>
/// alias and the required-init/DateTime properties) onto the keyless read DTO — no unit
/// test can catch a column/property name mismatch.
/// <para>
/// Rows are seeded with a direct SQL INSERT against <see cref="SnapshotFixture.DbContextFactory"/>
/// rather than through <see cref="SnapshotFixture.Handler"/>: this is a read-path test, and a
/// direct insert is the only way to plant a <c>DisplayData</c> payload containing a key that
/// is not a known PortfolioSnapshotIndex DisplayData key (proving the zero-code-change flow-through — the
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
    // Unlike every other suite here, these tests assert on the SIZE of a result set selected by
    // account + date range, not on a single row fetched by snapshotId — so any row left behind by
    // an earlier test method or an earlier run would be counted too. Fixed account ids made that
    // correctness depend on cleanup having run, and the suite failed as soon as
    // IntegrationTestSettings.CleanupAfterTest was turned off to keep data for manual testing.
    // Generating the account ids per test instance (xunit constructs one per test method) makes
    // the queries structurally incapable of seeing another test's or another run's rows, so the
    // suite passes with the cleanup flags in any combination.
    private readonly string _accountA = NewAccountId();
    private readonly string _accountB = NewAccountId();

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

        // sidOlder: novel DisplayData key that no known PortfolioSnapshotIndex DisplayData shape covers —
        // proves zero-code-change flow-through end to end.
        await InsertIndexRowAsync(
            sidOlder,
            _accountA,
            new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidOlder}",
            """{"benchmark":"MSCI World","brandNewField":"xyz"}""",
            new DateTime(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc));

        // sidNewer: DisplayData carries a key ("accountId") that collides with a fixed field —
        // proves fixed-field precedence on collision.
        await InsertIndexRowAsync(
            sidNewer,
            _accountA,
            new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc),
            "CASH_FLOW",
            $"portfolio_snapshots/accountId={_accountA}/{sidNewer}",
            """{"benchmark":"S&P 500","accountId":"clash-value"}""",
            new DateTime(2026, 7, 12, 9, 0, 0, DateTimeKind.Utc));

        // Different account entirely — must never appear when the filter asks only for account A.
        await InsertIndexRowAsync(
            sidOtherAccount,
            _accountB,
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountB}/{sidOtherAccount}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 11, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [_accountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            });

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        // Only account A rows come back — account B's row is excluded by the AccountId IN filter.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(_accountA, r.AccountId));

        // ORDER BY SnapshotDate DESC — newest first.
        Assert.Equal(sidNewer, rows[0].SnapshotId);
        Assert.Equal(sidOlder, rows[1].SnapshotId);

        // Fixed-field materialisation via SqlQueryRaw<PortfolioSnapshotIndexRow> — every column, including
        // the DisplayData AS DisplayDataJson alias, correctly bound onto the keyless read DTO.
        var older = rows[1];
        Assert.Equal(sidOlder, older.SnapshotId);
        Assert.Equal(_accountA, older.AccountId);
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), older.SnapshotDate);
        Assert.Equal("REBALANCE", older.EventType);
        Assert.Equal($"portfolio_snapshots/accountId={_accountA}/{sidOlder}", older.AdlsPath);
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
        Assert.Equal(_accountA, newerFlat["accountId"]);
        Assert.NotEqual("clash-value", newerFlat["accountId"]);
    }

    [Fact]
    public async Task Grid_query_event_filter_matches_exact_and_combined_values()
    {
        var sidModelChange = NewSnapshotId("grid-evt-mc");
        var sidCombined = NewSnapshotId("grid-evt-combo");
        var sidCashflow = NewSnapshotId("grid-evt-cf");

        await InsertIndexRowAsync(
            sidModelChange,
            _accountA,
            new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            "ModelChange",
            $"portfolio_snapshots/accountId={_accountA}/{sidModelChange}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidCombined,
            _accountA,
            new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc),
            "ModelChange + Cashflow",
            $"portfolio_snapshots/accountId={_accountA}/{sidCombined}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidCashflow,
            _accountA,
            new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc),
            "Cashflow",
            $"portfolio_snapshots/accountId={_accountA}/{sidCashflow}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [_accountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
                EventType = "ModelChange",
            });

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.SnapshotId == sidModelChange && row.EventType == "ModelChange");
        Assert.Contains(rows, row => row.SnapshotId == sidCombined && row.EventType == "ModelChange + Cashflow");
        Assert.DoesNotContain(rows, row => row.SnapshotId == sidCashflow);
    }

    [Fact]
    public async Task Grid_query_event_filter_treats_like_wildcards_as_literal_text()
    {
        var sidLiteral = NewSnapshotId("grid-evt-lit");
        var sidWildcardDecoy = NewSnapshotId("grid-evt-wild");

        await InsertIndexRowAsync(
            sidLiteral,
            _accountA,
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            "Model%Change_Test[1]~",
            $"portfolio_snapshots/accountId={_accountA}/{sidLiteral}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidWildcardDecoy,
            _accountA,
            new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc),
            "ModelXChangeXTest1~",
            $"portfolio_snapshots/accountId={_accountA}/{sidWildcardDecoy}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 19, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [_accountA],
                EventType = "%Change_Test[1]~",
            });

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(sidLiteral, row.SnapshotId);
        Assert.Equal("Model%Change_Test[1]~", row.EventType);
    }

    [Fact]
    public async Task Grid_query_honors_date_range_filter()
    {
        var sidInRange = NewSnapshotId("grid-dr-in");
        var sidBeforeRange = NewSnapshotId("grid-dr-bef");

        await InsertIndexRowAsync(
            sidBeforeRange,
            _accountA,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidBeforeRange}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidInRange,
            _accountA,
            new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidInRange}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [_accountA],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            });

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
            _accountA,
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidAccountA}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidAccountB,
            _accountB,
            new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountB}/{sidAccountB}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 18, 9, 0, 0, DateTimeKind.Utc));

        using var query = BuildQuery();
        var filter = SnapshotGridFilter.Resolve(
            new SnapshotGridFilter
            {
                AccountIds = [_accountB],
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 7, 31),
            });

        var rows = await query.Service.QueryAsync(filter, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(sidAccountB, row.SnapshotId);
        Assert.Equal(_accountB, row.AccountId);
    }

    /// <summary>
    /// The accountIds-only request: From, To and Event are all null, and the API imposes no
    /// default for any of them, so every row of the account comes back however old it is - the
    /// last-7-days view is a UI convention, achieved by the client sending explicit dates.
    /// Regression guard for the original defect: a row dated well outside any "recent" window
    /// used to be filtered away silently by the implicit 7-day default. Goes through
    /// <see cref="IPortfolioSnapshotGridQueryHandler"/> rather than the read port, because
    /// Resolve-then-query against real SQL is exactly the composition under test - the other
    /// methods here Resolve an already-complete window and so never exercise the open one.
    /// The result no longer depends on "now" at all, so nothing here is clock-sensitive.
    /// </summary>
    [Fact]
    public async Task Grid_handler_applies_no_date_filter_when_only_account_ids_are_supplied()
    {
        var sidOld = NewSnapshotId("grid-nodef-old");
        var sidRecent = NewSnapshotId("grid-nodef-recent");

        await InsertIndexRowAsync(
            sidOld,
            _accountA,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidOld}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        await InsertIndexRowAsync(
            sidRecent,
            _accountA,
            new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc),
            "REBALANCE",
            $"portfolio_snapshots/accountId={_accountA}/{sidRecent}",
            """{"benchmark":"MSCI World"}""",
            new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc));

        using var scope = BuildGridHandler();

        // Exactly what the controller hands over for ?accountIds=X and nothing else.
        var requested = new SnapshotGridFilter
        {
            AccountIds = [_accountA],
            FromDate = null,
            ToDate = null,
            EventType = null,
        };

        var rows = await scope.Service.HandleAsync(requested, CancellationToken.None);

        // Both rows come back: no implicit window, so nothing is filtered out by date.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => (string?)r["snapshotId"] == sidOld);
        Assert.Contains(rows, r => (string?)r["snapshotId"] == sidRecent);
        Assert.All(rows, r => Assert.Equal(_accountA, r["accountId"]));
    }

    /// <summary>
    /// Account id owned by a single test method. The <c>IT-ACC-</c> prefix keeps it recognisable
    /// as integration-test data in the shared dev database; the rows themselves are still cleaned
    /// up by snapshotId (<see cref="IntegrationTestBase.DisposeAsync"/>) when cleanup is enabled.
    /// </summary>
    private static string NewAccountId() => $"IT-ACC-GRID-{Guid.NewGuid():N}";

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
    /// Builds <see cref="IPortfolioSnapshotIndexQuery"/> the same way the Api composition root does
    /// (<c>AddSqlReadInfrastructure</c>, in <c>Api/Program.cs</c>), rather than reusing
    /// <see cref="SnapshotFixture"/>'s write-side provider, which never registers the read port.
    /// The returned wrapper owns its own <see cref="ServiceProvider"/> and disposes it with the
    /// query, closing the pooled DbContext factory it creates.
    /// </summary>
    private ResolvedScope<IPortfolioSnapshotIndexQuery> BuildQuery()
    {
        var services = new ServiceCollection();
        services.AddSqlReadInfrastructure(Fixture.Configuration["Database:ConnectionString"] ?? string.Empty);
        return new ResolvedScope<IPortfolioSnapshotIndexQuery>(services.BuildServiceProvider());
    }

    /// <summary>
    /// Builds the Application-layer grid handler over the same read port, wired exactly the way
    /// <c>Api/Program.cs</c> wires it. No clock is registered because none is needed: the grid
    /// filter no longer defaults anything off "now".
    /// </summary>
    private ResolvedScope<IPortfolioSnapshotGridQueryHandler> BuildGridHandler()
    {
        var services = new ServiceCollection();
        services.AddPortfolioSnapshotReadFeatures();
        services.AddSqlReadInfrastructure(Fixture.Configuration["Database:ConnectionString"] ?? string.Empty);
        return new ResolvedScope<IPortfolioSnapshotGridQueryHandler>(services.BuildServiceProvider());
    }

    private sealed class ResolvedScope<TService> : IDisposable
        where TService : notnull
    {
        private readonly ServiceProvider _provider;

        public ResolvedScope(ServiceProvider provider)
        {
            _provider = provider;
            Service = provider.GetRequiredService<TService>();
        }

        public TService Service { get; }

        public void Dispose() => _provider.Dispose();
    }
}
