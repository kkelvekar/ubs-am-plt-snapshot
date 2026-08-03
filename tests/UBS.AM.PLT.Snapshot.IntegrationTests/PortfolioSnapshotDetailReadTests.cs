using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Mode A coverage for the View-a-snapshot-detail Read API (solution design section 10,
/// Screen 2): <see cref="IPortfolioSnapshotDetailQueryHandler"/> exercised against REAL Azure
/// SQL (index-row lookup via <c>IPortfolioSnapshotIndexQuery</c>) and REAL ADLS Gen2 blob storage
/// (<c>ISnapshotPayloadQuery</c> on <see cref="AzureBlobSnapshotStore"/>).
/// <para>
/// Snapshots are seeded by driving the REAL write pipeline (<see cref="SnapshotFixture.Handler"/>),
/// never by inserting rows/blobs directly, so every read assertion here proves the full
/// write-then-read round trip: blob write -> tracking upsert -> completeness check -> index
/// UPSERT on the way in, index lookup -> blob read on the way out.
/// </para>
/// <para>
/// The read graph is built the same way the Api composition root builds it
/// (<c>AddPortfolioSnapshotDetail</c> + <c>AddSqlReadInfrastructure</c> +
/// <c>AddAdlsReadInfrastructure</c>, see <c>Api/Program.cs</c>) rather than reusing
/// <see cref="SnapshotFixture.Handler"/>'s provider, which only wires the write-side stores.
/// </para>
/// </summary>
public sealed class PortfolioSnapshotDetailReadTests : IntegrationTestBase, IClassFixture<SnapshotFixture>
{
    // Fresh account id — IT-ACC-001..009 are used by other groups (see MalformedInputTests,
    // PortfolioSnapshotGridReadTests); IT-ACC-010 is reserved in appsettings.json's
    // TestAccountIds for this test group.
    private const string AccountId = "IT-ACC-010";

    // Distinct, formatting-sensitive bodies per payload type — trailing-zero decimals and an
    // internal double space in each — so a byte-for-byte Assert.Equal / substring match can
    // only pass if the value truly round-tripped verbatim (no re-serialisation anywhere on
    // either the write or the read side would preserve these).
    private const string HeaderJson = """
        {"eventType":"REBALANCE",  "benchmark":"MSCI World","numOrders":17.0}
        """;

    private const string OrdersJson = """
        {"positions":[{"isin":"CH0038863350",  "qty":250.50}]}
        """;

    private const string CalculationsJson = """
        {"nav":5555.00,  "ccy":"CHF"}
        """;

    private const string SettingsJson = """
        {"tolerance":0.050,  "flag":true}
        """;

    public PortfolioSnapshotDetailReadTests(SnapshotFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Single_payload_read_returns_the_stored_blob_verbatim()
    {
        var snapshotId = await CreateCompleteSnapshotAsync("detail-single");

        using var query = BuildQuery();
        var json = await query.Service.GetPayloadAsync(snapshotId, "orders", CancellationToken.None);

        Assert.Equal(OrdersJson, json);
    }

    [Fact]
    public async Task All_payloads_read_returns_one_object_keyed_by_payload_type_embedding_each_verbatim()
    {
        var snapshotId = await CreateCompleteSnapshotAsync("detail-all");

        using var query = BuildQuery();
        var json = await query.Service.GetAllPayloadsAsync(snapshotId, CancellationToken.None);

        // JsonDocument.Parse is allowed in tests only — production code never parses a
        // payload (AGENTS.md invariant 5); this asserts on the composed document's shape.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
        Assert.True(document.RootElement.TryGetProperty("header", out _));
        Assert.True(document.RootElement.TryGetProperty("orders", out _));
        Assert.True(document.RootElement.TryGetProperty("calculations", out _));
        Assert.True(document.RootElement.TryGetProperty("settings", out _));

        // Each seeded payload string appears byte-for-byte inside the raw document text —
        // WriteRawValue embedding, not a parse-and-rewrite, so whitespace/number formatting
        // is untouched.
        Assert.Contains(HeaderJson, json, StringComparison.Ordinal);
        Assert.Contains(OrdersJson, json, StringComparison.Ordinal);
        Assert.Contains(CalculationsJson, json, StringComparison.Ordinal);
        Assert.Contains(SettingsJson, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Known_snapshot_with_absent_payload_type_is_not_found()
    {
        var snapshotId = await CreateCompleteSnapshotAsync("detail-absent");

        using var query = BuildQuery();

        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => query.Service.GetPayloadAsync(snapshotId, "auditlog", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_snapshot_id_is_not_found_from_both_methods()
    {
        // Registered for cleanup (belt-and-braces — nothing is ever written for it) but never
        // sent to the handler, so no index row and no blob folder exist for it.
        var snapshotId = NewSnapshotId("detail-unknown");

        using var query = BuildQuery();

        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => query.Service.GetPayloadAsync(snapshotId, "header", CancellationToken.None));
        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => query.Service.GetAllPayloadsAsync(snapshotId, CancellationToken.None));
    }

    [Fact]
    public async Task Incomplete_snapshot_payload_read_is_not_found_even_though_the_blob_exists()
    {
        var snapshotId = NewSnapshotId("detail-incomplete");

        // Only the orders payload arrives — no index row is ever written for this snapshot,
        // even though the orders.json blob now exists in ADLS. This proves the read is
        // index-gated (via IPortfolioSnapshotIndexQuery.GetAdlsPathAsync), not blob-existence-gated.
        var message = SnapshotTestHelpers.CreateMessage(Fixture, snapshotId, AccountId, "orders", OrdersJson);
        await Fixture.Handler.HandleAsync(message, CancellationToken.None);

        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(Fixture, snapshotId));
        Assert.True(await Fixture.BlobContainer.GetBlobClient(
            $"{(await SnapshotTestHelpers.GetTrackingAsync(Fixture, snapshotId)).AdlsRootPath}/orders.json").ExistsAsync());

        using var query = BuildQuery();

        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => query.Service.GetPayloadAsync(snapshotId, "orders", CancellationToken.None));
    }

    /// <summary>
    /// Sends all four required payloads (design §4's declarative required-files map) through
    /// the real handler so the snapshot reaches COMPLETE with an index row, then returns its
    /// snapshotId. Order is deliberately not "header first": header last exercises the same
    /// out-of-order arrival path already proven by <c>SnapshotCompletionSteps</c>, so this
    /// suite is not relying on a lucky arrival order to reach COMPLETE.
    /// </summary>
    private async Task<string> CreateCompleteSnapshotAsync(string testName)
    {
        var snapshotId = NewSnapshotId(testName);

        foreach (var (payloadType, payloadJson) in new[]
                 {
                     ("orders", OrdersJson),
                     ("calculations", CalculationsJson),
                     ("settings", SettingsJson),
                     ("header", HeaderJson),
                 })
        {
            var message = SnapshotTestHelpers.CreateMessage(Fixture, snapshotId, AccountId, payloadType, payloadJson);
            await Fixture.Handler.HandleAsync(message, CancellationToken.None);
        }

        Assert.True(await SnapshotTestHelpers.IndexRowExistsAsync(Fixture, snapshotId));

        return snapshotId;
    }

    /// <summary>
    /// Builds <see cref="IPortfolioSnapshotDetailQueryHandler"/> the same way the Api
    /// composition root does (<c>AddPortfolioSnapshotDetail</c> + <c>AddSqlReadInfrastructure</c>
    /// + <c>AddAdlsReadInfrastructure</c>, in <c>Api/Program.cs</c>), rather than reusing
    /// <see cref="SnapshotFixture"/>'s write-side provider, which never registers the read
    /// ports. The returned wrapper owns its own <see cref="ServiceProvider"/> and disposes it
    /// with the query.
    /// </summary>
    private QueryScope BuildQuery()
    {
        var services = new ServiceCollection();
        services.AddPortfolioSnapshotDetail();
        services.AddSqlReadInfrastructure(Fixture.Configuration["Database:ConnectionString"] ?? string.Empty);
        services.AddAdlsReadInfrastructure(
            Fixture.Configuration["BlobStorage:ServiceUri"] ?? string.Empty,
            Fixture.Configuration["BlobStorage:ContainerName"] ?? string.Empty,
            Fixture.Configuration["BlobStorage:ConnectionString"]);
        var provider = services.BuildServiceProvider();
        return new QueryScope(provider);
    }

    private sealed class QueryScope : IDisposable
    {
        private readonly ServiceProvider _provider;

        public QueryScope(ServiceProvider provider)
        {
            _provider = provider;
            Service = provider.GetRequiredService<IPortfolioSnapshotDetailQueryHandler>();
        }

        public IPortfolioSnapshotDetailQueryHandler Service { get; }

        public void Dispose() => _provider.Dispose();
    }
}
