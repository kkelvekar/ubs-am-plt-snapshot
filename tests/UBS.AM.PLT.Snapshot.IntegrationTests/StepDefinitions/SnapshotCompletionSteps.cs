using Reqnroll;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/SnapshotCompletion.feature</c>. Drives real messages straight
/// through the production <c>ISnapshotMessageHandler</c> with Kafka bypassed and asserts a snapshot
/// completes only once every required file has arrived, writing the audit index row exactly once,
/// independent of arrival order (canonical and shuffled both reach the same well-formed end state).
/// The clock is anchored to a fixed-but-arbitrary instant inside the "completion snapshot" step.
/// The run-scoped <see cref="SnapshotFixture"/> and the scenario-scoped
/// <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll, which creates one
/// instance of this class per scenario, so instance fields hold per-scenario state safely. Step
/// text is deliberately distinct from the other feature bindings so they never collide on an
/// ambiguous match.
/// </summary>
[Binding]
public sealed class SnapshotCompletionSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;
    private SnapshotMessage? _lastMessage;

    public SnapshotCompletionSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("a completion snapshot for account \"(.*)\"")]
    public void GivenACompletionSnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("completion");
    }

    [When("the orders payload is received")]
    public Task WhenTheOrdersPayloadIsReceived() =>
        SendPayloadAsync("orders", TestPayloads.OrdersJson);

    [When("the portfolio payload is received")]
    public Task WhenThePortfolioPayloadIsReceived() =>
        SendPayloadAsync("portfolio", TestPayloads.PortfolioJson);

    [When("the settings payload is received")]
    public Task WhenTheSettingsPayloadIsReceived() =>
        SendPayloadAsync("settings", TestPayloads.SettingsJson);

    [When("the standard header payload is received")]
    public Task WhenTheStandardHeaderPayloadIsReceived() =>
        SendPayloadAsync("header", TestPayloads.HeaderJson);

    [Then("the snapshot is still receiving with no index row")]
    public async Task ThenTheSnapshotIsStillReceivingWithNoIndexRow()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("the snapshot tracking is COMPLETE with a completed time")]
    public async Task ThenTheSnapshotTrackingIsCompleteWithACompletedTime()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
    }

    [Then("the \"(.*)\" blob holds the sent payload")]
    public async Task ThenTheBlobHoldsTheSentPayload(string fileName)
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var expectedBody = (_lastMessage ?? throw new InvalidOperationException("No payload has been sent yet."))
            .Payload;
        var blobText = await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/{fileName}");
        Assert.Equal(expectedBody, blobText);
    }

    [Then("the completion index row matches the sent header")]
    public async Task ThenTheCompletionIndexRowMatchesTheSentHeader()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var index = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);

        Assert.Equal(_snapshotId, index.SnapshotId);
        Assert.Equal(_accountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(TestPayloads.HeaderEventType, index.EventType);
        Assert.Equal(TestPayloads.HeaderJson, index.DisplayData);
    }

    [Then("a receiving response was published listing the outstanding files")]
    public void ThenAReceivingResponseWasPublishedListingTheOutstandingFiles()
    {
        var notification = Assert.Single(
            _fixture.ResponsePublisher.PublishedFor(_snapshotId),
            n => n.Status == SnapshotTrackingStatus.Receiving);

        Assert.Equal(_accountId, notification.AccountId);
        Assert.Equal(["orders.json"], notification.ReceivedFiles);
        Assert.Equal(["header.json", "portfolio.json", "settings.json"], notification.MissingFiles);
        Assert.Null(notification.CompletedAt);
    }

    [Then("exactly one completion response was published for the snapshot")]
    public async Task ThenExactlyOneCompletionResponseWasPublishedForTheSnapshot()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var notification = Assert.Single(
            _fixture.ResponsePublisher.PublishedFor(_snapshotId),
            n => n.Status == SnapshotTrackingStatus.Complete);

        Assert.Equal(_accountId, notification.AccountId);
        Assert.Empty(notification.MissingFiles);
        Assert.Equal(
            new[] { "header.json", "orders.json", "portfolio.json", "settings.json" },
            notification.ReceivedFiles.Order());
        Assert.Equal(tracking.FirstReceivedAt, notification.FirstReceivedAt);
        Assert.NotNull(notification.CompletedAt);
        Assert.Null(notification.DeclaredFailedAt);
    }

    [Then("the snapshot end state is a well-formed completed snapshot")]
    public async Task ThenTheSnapshotEndStateIsAWellFormedCompletedSnapshot()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        foreach (var fileName in new[] { "header.json", "orders.json", "portfolio.json", "settings.json" })
        {
            Assert.True(
                await _fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/{fileName}").ExistsAsync(),
                $"Expected blob {tracking.AdlsRootPath}/{fileName} to exist.");
        }

        var index = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);

        Assert.Equal(_snapshotId, index.SnapshotId);
        Assert.Equal(_accountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(tracking.FirstReceivedAt, index.SnapshotDate);
        Assert.Equal(TestPayloads.HeaderEventType, index.EventType);
        Assert.Equal(TestPayloads.HeaderJson, index.DisplayData);
    }

    private async Task SendPayloadAsync(string payloadType, string payloadJson)
    {
        _lastMessage = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, payloadJson);
        await _fixture.Handler.HandleAsync(_lastMessage, CancellationToken.None);
    }
}
