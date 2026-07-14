using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Application.Interfaces;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Infrastructure;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests;

/// <summary>
/// Group I — infra unavailability (TC-27, TC-28), design doc §9. Mode A covers the
/// deterministic, Kafka-independent piece: when a downstream dependency is unreachable
/// during the write order (blob write → tracking upsert → completeness check → index
/// UPSERT), the exception propagates out of the handler unchanged (so the live consumer
/// would never commit the offset — recovery is forward, via redelivery) and no durable
/// state that would make the snapshot look processed is created in the system of record.
///
/// The retry cadence / operations-alert half of §9 (0s/5s/30s/30s..., LogCritical at the
/// 3rd consecutive failure) lives in the consumer, not the handler, and is asserted
/// deterministically in the unit test
/// <c>KafkaSnapshotConsumerTests.Retry_delay_sequence_follows_configured_cadence_and_then_holds_at_max</c>.
/// The real broker offset-lag + Critical-alert proof against the live consume/commit path
/// is exercised in Mode B — see the Group I section of <c>tools/fault-injection.ps1</c>.
///
/// Each test builds a SECOND, fault-injected object graph (real
/// <c>AddApplication</c>/<c>AddInfrastructure</c> wiring, one dependency repointed at an
/// unreachable endpoint via in-memory config override) while asserting absence of durable
/// state against the fixture's REAL, reachable resources.
/// </summary>
public sealed class GroupIInfraUnavailabilityTests : IntegrationTestBase, IClassFixture<SnapshotWriterFixture>
{
    // Fresh account id — IT-ACC-001..008 are used by Groups A/B/C/F/G/H.
    private const string AccountId = "IT-ACC-009";

    public GroupIInfraUnavailabilityTests(SnapshotWriterFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task Tc27_sql_unreachable_during_write_propagates_and_leaves_no_tracking_or_index_row()
    {
        // TC-27: Azure SQL unreachable. Fault graph = real blob store, SQL repointed at a
        // closed local port with a short connect timeout so the test fails fast instead of
        // hanging. Per the write order the handler's very first step (GetRootPathAsync) is a
        // SQL read, so the failure surfaces there; blob presence/absence therefore varies
        // and is deliberately NOT asserted (design finding 4).
        var snapshotId = NewSnapshotId("tc27");

        await using var provider = BuildFaultInjectedProvider(new Dictionary<string, string?>
        {
            // Closed port (9) + Connect Timeout=2 => a fast SqlException, no auth prompt,
            // no hang. Plain (non-AAD) auth avoids any token-acquisition round trip.
            ["Database:ConnectionString"] =
                "Server=tcp:localhost,9;Database=fault-injected;Connect Timeout=2;Encrypt=False;TrustServerCertificate=True",
        });
        var handler = provider.GetRequiredService<ISnapshotMessageHandler>();

        var message = CreateMessage(snapshotId, "instruments", """{"positions":[{"isin":"CH0038863350","qty":250}]}""");

        var exception = await Record.ExceptionAsync(() => handler.HandleAsync(message, CancellationToken.None));

        // The infra failure propagates out of the handler — in the live system this is the
        // exception the consumer catches to seek-back/retry and never commit the offset.
        Assert.NotNull(exception);

        // No durable state in the system of record: the snapshot never became visible.
        await AssertNoTrackingRowAsync(snapshotId);
        await AssertNoIndexRowAsync(snapshotId);
    }

    [Fact]
    public async Task Tc28_adls_unreachable_during_write_propagates_and_writes_nothing_durable()
    {
        // TC-28: ADLS unreachable. Fault graph = real SQL, blob store repointed at an
        // unreachable endpoint (127.0.0.1:1, connection refused). AzureBlobSnapshotStore
        // already sets Retry.MaxRetries=0, so the failure surfaces on the first write
        // instead of being absorbed by SDK retry — matching the TC-16 override mechanism.
        // SQL is fully reachable here, yet the strict blob-first write order means a blob
        // failure must leave ZERO tracking/index rows: that ordering is what this asserts.
        var startTime = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);
        Fixture.CurrentTime = startTime;
        var snapshotId = NewSnapshotId("tc28");

        await using var provider = BuildFaultInjectedProvider(new Dictionary<string, string?>
        {
            // ServiceUri wins over ConnectionString in BlobContainerClientFactory; port 1
            // refuses immediately, so with MaxRetries=0 the first blob op fails fast.
            ["BlobStorage:ServiceUri"] = "https://127.0.0.1:1/",
        });
        var handler = provider.GetRequiredService<ISnapshotMessageHandler>();

        var message = CreateMessage(snapshotId, "instruments", """{"positions":[{"isin":"CH0038863350","qty":250}]}""");

        var exception = await Record.ExceptionAsync(() => handler.HandleAsync(message, CancellationToken.None));

        Assert.NotNull(exception);

        // Zero durable writes: no blob at the expected root, no tracking row, no index row.
        var expectedRoot = SnapshotBlobPath.RootFolder(message, startTime);
        var blobNames = new List<string>();
        await foreach (var blob in Fixture.BlobContainer.GetBlobsAsync(prefix: expectedRoot))
        {
            blobNames.Add(blob.Name);
        }

        Assert.Empty(blobNames);
        await AssertNoTrackingRowAsync(snapshotId);
        await AssertNoIndexRowAsync(snapshotId);
    }

    private ServiceProvider BuildFaultInjectedProvider(Dictionary<string, string?> overrides)
    {
        // Same production object graph the fixture builds, but with one dependency's config
        // repointed at an unreachable endpoint. No IHost is started, so the registered
        // KafkaSnapshotConsumer hosted service never runs — tests call the handler directly.
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(Fixture.Configuration)
            .AddInMemoryCollection(overrides)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Fixture.MockTime.Object);
        services.AddApplication().AddInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private SnapshotMessage CreateMessage(string snapshotId, string payloadType, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);

        return new SnapshotMessage
        {
            SnapshotId = snapshotId,
            AccountId = AccountId,
            SnapshotType = "portfolio",
            PayloadType = payloadType,
            Stage = "PreTrade",
            PublishedAt = Fixture.CurrentTime.UtcDateTime,
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = document.RootElement.Clone(),
        };
    }

    private async Task AssertNoTrackingRowAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        var count = await context.SnapshotTracking
            .AsNoTracking()
            .CountAsync(e => e.SnapshotId == snapshotId);
        Assert.Equal(0, count);
    }

    private async Task AssertNoIndexRowAsync(string snapshotId)
    {
        await using var context = await Fixture.DbContextFactory.CreateDbContextAsync();
        var count = await context.SnapshotIndex
            .AsNoTracking()
            .CountAsync(e => e.SnapshotId == snapshotId);
        Assert.Equal(0, count);
    }
}
