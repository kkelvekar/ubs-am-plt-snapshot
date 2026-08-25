using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll;
using Ubs.Advantage.Core.Infrastructure.Commands;
using Ubs.Advantage.Core.Messaging.Kafka.Models;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Application;
using UBS.AM.PLT.Snapshot.Application.Contracts;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka.Commands;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/FailureClassification.feature</c>. Mode A covers
/// <see cref="SnapshotRequestCommand"/>'s three-outcome classification directly (one layer
/// below the Kafka consumer, which Mode B alone reaches): a fault-injected object graph is
/// built the same way <c>InfrastructureFailureDuringWriteSteps</c> builds its second graph, but
/// with a <see cref="FakeHostApplicationLifetime"/> and <see cref="ControllableTimeProvider"/>
/// substituted for the fixture's defaults, so the command is resolved directly (no Kafka
/// consumer, no DI registration for it exists in this test process) and its 30-second park is
/// observable and releasable instead of a real wait. Assertions run against the fixture's REAL,
/// reachable Azure SQL and blob resources. The run-scoped <see cref="SnapshotFixture"/> and
/// scenario-scoped <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll,
/// which creates one instance of this class per scenario, so instance fields hold per-scenario
/// state safely. <see cref="Environment.ExitCode"/> is process-global; it is saved before each
/// fault-injected scenario and restored in <see cref="DisposeFaultProvider"/>.
/// </summary>
[Binding]
public sealed class FailureClassificationSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    private ServiceProvider? _faultProvider;
    private FakeHostApplicationLifetime? _lifetime;
    private ControllableTimeProvider? _park;
    private Task<CommandResult>? _executeTask;
    private CommandResult? _result;
    private int _originalExitCode;

    public FailureClassificationSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("the failure-classification clock starts")]
    public void GivenTheFailureClassificationClockStarts() => _fixture.CurrentTime = AnchorTime;

    [Given("a failure-classification snapshot for account \"(.*)\"")]
    public void GivenAFailureClassificationSnapshotForAccount(string accountId)
    {
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("failure-classification");
    }

    [Given("a fault-injected command graph with SQL repointed to an unreachable endpoint")]
    public void GivenAFaultInjectedCommandGraphWithSqlRepointedToAnUnreachableEndpoint()
    {
        _originalExitCode = Environment.ExitCode;
        _lifetime = new FakeHostApplicationLifetime();
        _park = new ControllableTimeProvider();

        // Closed port (9) + Connect Timeout=2, the same pattern InfrastructureFailureDuringWriteSteps
        // uses: a fast connection failure, no auth prompt, no hang.
        _faultProvider = BuildCommandProvider(
            new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] =
                    "Server=tcp:localhost,9;Database=fault-injected;Connect Timeout=2;Encrypt=False;TrustServerCertificate=True",
            });
    }

    [Given("a reachable command graph with blob writes replaced by a simulated code defect")]
    public void GivenAReachableCommandGraphWithBlobWritesReplacedBySimulatedCodeDefect()
    {
        _originalExitCode = Environment.ExitCode;
        _lifetime = new FakeHostApplicationLifetime();
        _park = new ControllableTimeProvider();

        // Every dependency stays reachable -- only the blob write itself is swapped for a stub
        // that throws a plain code-defect-shaped exception no ITransientFailureClassifier
        // recognises, so the command takes the poison path and its RecordUnexpectedFailureAsync
        // call succeeds against the real, reachable SQL.
        _faultProvider = BuildCommandProvider(
            overrides: [],
            additionalRegistrations: services => services.AddSingleton<ISnapshotBlobStore>(
                new ThrowingSnapshotBlobStore(new InvalidOperationException("simulated code defect"))));
    }

    [When("the orders request is executed against the fault-injected command")]
    public void WhenTheOrdersRequestIsExecutedAgainstTheFaultInjectedCommand()
        => _executeTask = ResolveCommand().ExecuteAsync(CreateRequestMessage());

    [Then("the command execution is still pending")]
    public void ThenTheCommandExecutionIsStillPending()
    {
        var task = _executeTask ?? throw new InvalidOperationException("No command has been executed yet.");
        Assert.False(task.IsCompleted);
    }

    [Then("the fault-injected host was stopped exactly once with a non-zero exit code")]
    public async Task ThenTheFaultInjectedHostWasStoppedExactlyOnceWithANonZeroExitCode()
    {
        var lifetime = _lifetime ?? throw new InvalidOperationException("No lifetime has been created yet.");

        // Against a real fault-injected SQL endpoint the connection attempt (and the
        // record-failure escalation's own connection attempt) takes real wall-clock time
        // before SnapshotRequestCommand reaches its transient handling, unlike the unit tests'
        // synchronously-throwing fake handler -- poll rather than assert immediately.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (lifetime.StopApplicationCallCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, lifetime.StopApplicationCallCount);
        Assert.Equal(1, Environment.ExitCode);
    }

    [When("the park is released")]
    public async Task WhenTheParkIsReleased()
    {
        var park = _park ?? throw new InvalidOperationException("No park has been created yet.");
        var task = _executeTask ?? throw new InvalidOperationException("No command has been executed yet.");
        park.ReleasePark();
        _result = await task;
    }

    [Then("the fault-injected command reports failure")]
    public void ThenTheFaultInjectedCommandReportsFailure()
    {
        var result = _result ?? throw new InvalidOperationException("The command has not completed yet.");
        Assert.False(result.IsSuccess);
    }

    [Then("the fault-injected command reports success")]
    public async Task ThenTheFaultInjectedCommandReportsSuccess()
    {
        var task = _executeTask ?? throw new InvalidOperationException("No command has been executed yet.");
        _result = await task;
        Assert.True(_result.IsSuccess);
    }

    [Then("the failure-classification host was never stopped")]
    public void ThenTheFailureClassificationHostWasNeverStopped()
    {
        var lifetime = _lifetime ?? throw new InvalidOperationException("No lifetime has been created yet.");
        Assert.Equal(0, lifetime.StopApplicationCallCount);
    }

    [Then("no blob exists under the failure-classification snapshot root")]
    public async Task ThenNoBlobExistsUnderTheFailureClassificationSnapshotRoot()
    {
        var blobNames = new List<string>();
        await foreach (var blob in _fixture.BlobContainer.GetBlobsAsync(prefix: $"accountId={_accountId}/snapshotId={_snapshotId}"))
        {
            blobNames.Add(blob.Name);
        }

        Assert.Empty(blobNames);
    }

    [Then("no tracking row exists for the failure-classification snapshot")]
    public async Task ThenNoTrackingRowExistsForTheFailureClassificationSnapshot()
        => Assert.False(await SnapshotTestHelpers.TrackingRowExistsAsync(_fixture, _snapshotId));

    [Then("no index row exists for the failure-classification snapshot")]
    public async Task ThenNoIndexRowExistsForTheFailureClassificationSnapshot()
        => Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));

    [Then("the failure-classification snapshot has a FAILED tracking row with reason UNEXPECTED_ERROR")]
    public async Task ThenTheFailureClassificationSnapshotHasAFailedTrackingRowWithReasonUnexpectedError()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Failed, tracking.Status);
        Assert.NotNull(tracking.Reason);
        Assert.Contains("UNEXPECTED_ERROR", tracking.Reason);
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("a Failed response was published for the failure-classification snapshot")]
    public void ThenAFailedResponseWasPublishedForTheFailureClassificationSnapshot()
    {
        var notification = Assert.Single(
            _fixture.ResponsePublisher.PublishedFor(_snapshotId),
            n => n.Status == SnapshotTrackingStatus.Failed);
        Assert.Equal("UNEXPECTED_ERROR", notification.ReasonCode);
    }

    [When("a required orders payload is delivered for the failure-classification snapshot through the fixture handler")]
    public async Task WhenARequiredOrdersPayloadIsDeliveredForTheFailureClassificationSnapshotThroughTheFixtureHandler()
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, "orders", TestPayloads.OrdersJson);
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }

    [Then("the failure-classification snapshot has returned to RECEIVING")]
    public async Task ThenTheFailureClassificationSnapshotHasReturnedToReceiving()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
    }

    [AfterScenario]
    public void DisposeFaultProvider()
    {
        _faultProvider?.Dispose();
        _faultProvider = null;
        Environment.ExitCode = _originalExitCode;
    }

    private SnapshotRequestCommand ResolveCommand()
    {
        var provider = _faultProvider ?? throw new InvalidOperationException("The command graph has not been built yet.");
        var lifetime = _lifetime ?? throw new InvalidOperationException("No lifetime has been created yet.");
        var park = _park ?? throw new InvalidOperationException("No park has been created yet.");

        return new SnapshotRequestCommand(
            NullLogger<SnapshotRequestCommand>.Instance,
            provider.GetRequiredService<ISnapshotMessageHandler>(),
            lifetime,
            provider.GetServices<ITransientFailureClassifier>(),
            park);
    }

    private Message<string, SnapshotRequest> CreateRequestMessage()
        => new(
            _snapshotId,
            new SnapshotRequest
            {
                SnapshotId = _snapshotId,
                AccountId = _accountId,
                SnapshotType = "portfolio",
                PayloadType = "orders",
                PublishedAt = _fixture.CurrentTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                PublishedBy = "PortfolioCalculation",
                Payload = TestPayloads.OrdersJson,
            },
            []);

    private ServiceProvider BuildCommandProvider(
        Dictionary<string, string?> overrides,
        Action<IServiceCollection>? additionalRegistrations = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(_fixture.Configuration)
            .AddInMemoryCollection(overrides)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_fixture.MockTime.Object);
        services.AddSingleton<ISnapshotResponsePublisher>(_fixture.ResponsePublisher);
        services.AddApplication()
            .AddSqlInfrastructure(configuration["Database:ConnectionString"] ?? string.Empty)
            .AddAdlsInfrastructure(
                configuration["BlobStorage:ServiceUri"] ?? string.Empty,
                configuration["BlobStorage:ContainerName"] ?? string.Empty,
                configuration["BlobStorage:ConnectionString"])
            .AddSnapshotConfigInfrastructure();

        additionalRegistrations?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Stands in for a code defect in the blob adapter: throws an exception no
    /// <see cref="ITransientFailureClassifier"/> recognises, so <see cref="SnapshotRequestCommand"/>
    /// takes the poison path.
    /// </summary>
    private sealed class ThrowingSnapshotBlobStore(Exception exception) : ISnapshotBlobStore
    {
        public Task WriteAsync(SnapshotMessage message, string rootPath, CancellationToken cancellationToken)
            => throw exception;

        public Task<string> ReadHeaderAsync(string adlsRootPath, CancellationToken cancellationToken)
            => throw new NotSupportedException("Not exercised by this scenario.");
    }
}
