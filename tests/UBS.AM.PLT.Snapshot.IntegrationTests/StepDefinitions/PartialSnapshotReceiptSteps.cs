using Reqnroll;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/PartialSnapshotReceipt.feature</c>. Drives real messages
/// straight through the production <c>ISnapshotMessageHandler</c> with Kafka bypassed and asserts
/// that each payload is recorded as it arrives, the snapshot stays RECEIVING until every required
/// file is present, and the index row is written only once the header completes the set.
/// The clock is anchored to a fixed-but-arbitrary instant inside the "new snapshot" step; every
/// timestamp assertion compares against clock state captured at send time, never against a literal.
/// The run-scoped <see cref="SnapshotFixture"/> and the scenario-scoped
/// <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll. Reqnroll creates one
/// instance of this class per scenario, so instance fields hold per-scenario state safely.
/// </summary>
[Binding]
public sealed class PartialSnapshotReceiptSteps
{
    // Arbitrary, fixed clock anchor. Its value is irrelevant to every assertion — timestamp checks
    // compare against clock state captured at send time, never against this instant.
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;
    private DateTimeOffset? _firstSendTime;
    private DateTimeOffset _lastSendTime;
    private SnapshotMessage? _lastMessage;

    public PartialSnapshotReceiptSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    private string ExpectedRootPath
    {
        get
        {
            var rootTime = _firstSendTime ?? throw new InvalidOperationException("No payload has been sent yet.");
            return $"portfolio_snapshots/year={rootTime:yyyy}/month={rootTime:MM}/accountId={_accountId}/snapshotId={_snapshotId}";
        }
    }

    [Given("a new snapshot for account \"(.*)\"")]
    public void GivenANewSnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("partial-recv");
    }

    [When("some time passes")]
    public void WhenSomeTimePasses()
    {
        _fixture.CurrentTime = _fixture.CurrentTime.AddMinutes(7);
    }

    [When("the orders payload arrives")]
    public Task WhenTheOrdersPayloadArrives() =>
        SendPayloadAsync("orders", TestPayloads.OrdersJson);

    [When("the portfolio payload arrives")]
    public Task WhenThePortfolioPayloadArrives() =>
        SendPayloadAsync("portfolio", TestPayloads.PortfolioJson);

    [When("the settings payload arrives")]
    public Task WhenTheSettingsPayloadArrives() =>
        SendPayloadAsync("settings", TestPayloads.SettingsJson);

    [When("the standard header payload arrives")]
    public Task WhenTheStandardHeaderPayloadArrives() =>
        SendPayloadAsync("header", TestPayloads.HeaderJson);

    [Then("the \"(.*)\" blob under the snapshot root contains the sent payload")]
    public async Task ThenTheBlobUnderTheSnapshotRootContainsTheSentPayload(string fileName)
    {
        var expectedBody = (_lastMessage ?? throw new InvalidOperationException("No payload has been sent yet."))
            .Payload;
        var blobText = await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{ExpectedRootPath}/{fileName}");
        Assert.Equal(expectedBody, blobText);
    }

    [Then("the snapshot tracking status is \"(.*)\"")]
    public async Task ThenTheSnapshotTrackingStatusIs(string status)
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(ParseStatus(status), tracking.Status);
    }

    [Then("the tracking row lists received files \"(.*)\"")]
    public async Task ThenTheTrackingRowListsReceivedFiles(string commaSeparatedFiles)
    {
        var expected = commaSeparatedFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(expected, tracking.ReceivedFiles);
    }

    [Then("the tracking root path matches the snapshot's ADLS path")]
    public async Task ThenTheTrackingRootPathMatchesTheSnapshotsAdlsPath()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(ExpectedRootPath, tracking.AdlsRootPath);
    }

    [Then("the tracking first-received and last-updated times both equal the arrival time")]
    public async Task ThenTheTrackingFirstReceivedAndLastUpdatedBothEqualTheArrivalTime()
    {
        var expected = (_firstSendTime ?? throw new InvalidOperationException("No payload has been sent yet.")).UtcDateTime;
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(expected, tracking.FirstReceivedAt);
        Assert.Equal(expected, tracking.LastUpdatedAt);
        Assert.Equal(tracking.FirstReceivedAt, tracking.LastUpdatedAt);
    }

    [Then("the tracking first-received time equals the initial arrival")]
    public async Task ThenTheTrackingFirstReceivedTimeEqualsTheInitialArrival()
    {
        var expected = (_firstSendTime ?? throw new InvalidOperationException("No payload has been sent yet.")).UtcDateTime;
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(expected, tracking.FirstReceivedAt);
    }

    [Then("the tracking last-updated time equals the most recent arrival")]
    public async Task ThenTheTrackingLastUpdatedTimeEqualsTheMostRecentArrival()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(_lastSendTime.UtcDateTime, tracking.LastUpdatedAt);
    }

    [Then("the tracking completed time is set")]
    public async Task ThenTheTrackingCompletedTimeIsSet()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.NotNull(tracking.CompletedAt);
    }

    [Then("no index row exists for the snapshot")]
    public async Task ThenNoIndexRowExistsForTheSnapshot()
    {
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("all required blobs exist under the snapshot root")]
    public async Task ThenAllRequiredBlobsExistUnderTheSnapshotRoot()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var rootPath = tracking.AdlsRootPath;
        foreach (var fileName in new[] { "header.json", "orders.json", "settings.json", "portfolio.json" })
        {
            Assert.True(
                await _fixture.BlobContainer.GetBlobClient($"{rootPath}/{fileName}").ExistsAsync(),
                $"Expected blob {rootPath}/{fileName} to exist.");
        }
    }

    [Then("an index row is written with the header details")]
    public async Task ThenAnIndexRowIsWrittenWithTheHeaderDetails()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var index = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);

        Assert.Equal(_snapshotId, index.SnapshotId);
        Assert.Equal(_accountId, index.AccountId);
        Assert.Equal(tracking.FirstReceivedAt, index.SnapshotDate);
        Assert.Equal(TestPayloads.HeaderEventType, index.EventType);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(TestPayloads.HeaderPayloadJson, index.DisplayData);
    }

    private async Task SendPayloadAsync(string payloadType, string payloadJson)
    {
        _firstSendTime ??= _fixture.CurrentTime;
        _lastSendTime = _fixture.CurrentTime;
        _lastMessage = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, payloadJson);
        await _fixture.Handler.HandleAsync(_lastMessage, CancellationToken.None);
    }

    private static SnapshotTrackingStatus ParseStatus(string status) => status switch
    {
        "RECEIVING" => SnapshotTrackingStatus.Receiving,
        "COMPLETE" => SnapshotTrackingStatus.Complete,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown tracking status."),
    };
}
