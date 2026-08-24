using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/InfrastructureFailureDuringWrite.feature</c>. Mode A covers the
/// deterministic, Kafka-independent slice of design doc §9: an unreachable downstream dependency
/// during the write order propagates out of the handler unchanged (so the live consumer never
/// commits the offset — recovery is forward, via redelivery) and leaves no durable state in the
/// system of record. The retry / Critical operations-alert half of §9 lives at the command edge —
/// one Critical alert followed by an immediate non-zero process exit so Kubernetes restarts the
/// container and Kafka redelivers from the last committed offset — and is proved live in Mode B,
/// since Mode A bypasses the consumer entirely.
///
/// Each scenario builds a SECOND, fault-injected object graph (the same
/// <c>AddApplication</c>/<c>AddInfrastructure</c> wiring the fixture uses, with one dependency's
/// config repointed at an unreachable endpoint) while asserting absence of durable state against the
/// fixture's REAL, reachable resources. The fault-injected provider is disposed after the scenario.
/// The clock is anchored to a fixed-but-arbitrary instant inside the "clock starts" step. The
/// run-scoped <see cref="SnapshotFixture"/> and scenario-scoped
/// <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll, which creates one
/// instance of this class per scenario, so instance fields hold per-scenario state safely. Step text
/// is deliberately distinct from the other feature bindings so they never collide on an ambiguous
/// match.
/// </summary>
[Binding]
public sealed class InfrastructureFailureDuringWriteSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    // The SECOND, fault-injected object graph. Built in a Given step, disposed after the scenario.
    private ServiceProvider? _faultProvider;

    // Captured at handling time so the blob-root assertion resolves the same folder the handler
    // would have written under (the clock is not advanced during these scenarios).
    private SnapshotMessage? _message;
    private DateTimeOffset _handledAt;

    private Exception? _handlerException;

    public InfrastructureFailureDuringWriteSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("the infra-unavailability clock starts")]
    public void GivenTheInfraUnavailabilityClockStarts()
    {
        _fixture.CurrentTime = AnchorTime;
    }

    [Given("a fault-injected graph with SQL repointed to an unreachable endpoint")]
    public void GivenAFaultInjectedGraphWithSqlRepointed()
    {
        // Closed port (9) + Connect Timeout=2 => a fast SqlException, no auth prompt, no hang.
        // Plain (non-AAD) auth avoids any token-acquisition round trip. Per the write order the
        // handler's very first step (GetRootPathAsync) is a SQL read, so the failure surfaces there.
        _faultProvider = BuildFaultInjectedProvider(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] =
                "Server=tcp:localhost,9;Database=fault-injected;Connect Timeout=2;Encrypt=False;TrustServerCertificate=True",
        });
    }

    [Given("a fault-injected graph with blob storage repointed to an unreachable endpoint")]
    public void GivenAFaultInjectedGraphWithBlobStorageRepointed()
    {
        // ServiceUri wins over ConnectionString in BlobContainerClientFactory; port 1 refuses
        // immediately, and AzureBlobSnapshotStore sets Retry.MaxRetries=0, so the first blob op
        // fails fast instead of being absorbed by SDK retry. SQL is fully reachable here, so a blob
        // failure leaving ZERO tracking/index rows proves the strict blob-first write order.
        _faultProvider = BuildFaultInjectedProvider(new Dictionary<string, string?>
        {
            ["BlobStorage:ServiceUri"] = "https://127.0.0.1:1/",
        });
    }

    [Given("an infra-unavailability snapshot for account \"(.*)\"")]
    public void GivenAnInfraUnavailabilitySnapshotForAccount(string accountId)
    {
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("infra-fail");
    }

    [When("the orders payload is handled against the fault-injected graph")]
    public async Task WhenTheOrdersPayloadIsHandledAgainstTheFaultInjectedGraph()
    {
        var provider = _faultProvider ?? throw new InvalidOperationException("The fault-injected graph has not been built yet.");
        var handler = provider.GetRequiredService<ISnapshotMessageHandler>();

        _handledAt = _fixture.CurrentTime;
        _message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, "orders", TestPayloads.OrdersJson);
        _handlerException = await Record.ExceptionAsync(() => handler.HandleAsync(_message, CancellationToken.None));
    }

    [Then("the handler surfaces an infrastructure error")]
    public void ThenTheHandlerSurfacesAnInfrastructureError()
    {
        // The infra failure propagates out of the handler — in the live system this is the exception
        // the command edge treats as retryable, terminates for Kubernetes restart, and never commits.
        Assert.NotNull(_handlerException);
    }

    [Then("no blob exists under the infra-unavailability snapshot root")]
    public async Task ThenNoBlobExistsUnderTheSnapshotRoot()
    {
        var message = _message ?? throw new InvalidOperationException("No message has been handled yet.");
        var expectedRoot = SnapshotBlobPath.RootFolder(message, _handledAt);

        var blobNames = new List<string>();
        await foreach (var blob in _fixture.BlobContainer.GetBlobsAsync(prefix: expectedRoot))
        {
            blobNames.Add(blob.Name);
        }

        Assert.Empty(blobNames);
    }

    [Then("no tracking row exists for the infra-unavailability snapshot")]
    public async Task ThenNoTrackingRowExists()
    {
        await using var context = await _fixture.DbContextFactory.CreateDbContextAsync();
        var count = await context.SnapshotTracking
            .AsNoTracking()
            .CountAsync(e => e.SnapshotId == _snapshotId);
        Assert.Equal(0, count);
    }

    [Then("no index row exists for the infra-unavailability snapshot")]
    public async Task ThenNoIndexRowExists()
    {
        await using var context = await _fixture.DbContextFactory.CreateDbContextAsync();
        var count = await context.PortfolioSnapshotIndex
            .AsNoTracking()
            .CountAsync(e => e.SnapshotId == _snapshotId);
        Assert.Equal(0, count);
    }

    [AfterScenario]
    public void DisposeFaultInjectedProvider()
    {
        _faultProvider?.Dispose();
        _faultProvider = null;
    }

    private ServiceProvider BuildFaultInjectedProvider(Dictionary<string, string?> overrides)
    {
        // Same production object graph the fixture builds, but with one dependency's config repointed
        // at an unreachable endpoint. No IHost is started, so the registered message consumer service
        // hosted service never runs — the step calls the handler directly.
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_fixture.Configuration)
            .AddInMemoryCollection(overrides)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_fixture.MockTime.Object);
        // Kafka is bypassed here as in the fixture, so the publisher port needs the same
        // stand-in. These scenarios never reach completion, so nothing is ever published.
        services.AddSingleton<ISnapshotResponsePublisher>(_fixture.ResponsePublisher);
        services.AddApplication()
            .AddSqlInfrastructure(configuration["Database:ConnectionString"] ?? string.Empty)
            .AddAdlsInfrastructure(
                configuration["BlobStorage:ServiceUri"] ?? string.Empty,
                configuration["BlobStorage:ContainerName"] ?? string.Empty,
                configuration["BlobStorage:ConnectionString"])
            .AddSnapshotConfigInfrastructure();
        return services.BuildServiceProvider();
    }
}
