using Reqnroll;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/RejectionRecording.feature</c>. Covers
/// <c>SnapshotMessageHandler.RecordAndPublishRejectionAsync</c>: a rejection with a storable
/// SnapshotId records a FAILED <c>snapshot_tracking</c> row and always publishes a rejection
/// response; a rejection whose SnapshotId itself is unstorable (over-long) records no row at
/// all but still publishes a response; and a FAILED row recovers to COMPLETE once every
/// required file for the snapshot subsequently arrives. The clock is anchored to a
/// fixed-but-arbitrary instant inside the "rejection-recording snapshot" steps. The run-scoped
/// <see cref="SnapshotFixture"/> and scenario-scoped <see cref="ScenarioFixtureContext"/> are
/// constructor-injected by Reqnroll, which creates one instance of this class per scenario, so
/// instance fields hold per-scenario state safely. Step text is deliberately distinct from the
/// other feature bindings so they never collide on an ambiguous match.
/// </summary>
[Binding]
public sealed class RejectionRecordingSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    private Exception? _rejection;

    public RejectionRecordingSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("a rejection-recording snapshot for account \"(.*)\"")]
    public void GivenARejectionRecordingSnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("rejection-recording");
    }

    [Given("a rejection-recording snapshot with a (\\d+)-character snapshot id")]
    public void GivenARejectionRecordingSnapshotWithACharacterSnapshotId(int length)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = "IT-ACC-009";

        // Base id already carries the mandatory "it-" cleanup marker; pad it out past
        // SnapshotFieldLimits.SnapshotIdMaxLength (100) so the handler treats it as
        // unstorable. Registered separately because it's a different string from the one
        // NewSnapshotId itself registered.
        var baseId = _scenario.NewSnapshotId("rejection-recording-unstorable");
        _snapshotId = baseId.PadRight(length, 'X');
        _scenario.RegisterSnapshotId(_snapshotId);
    }

    [When("an out-of-contract payload is delivered for the rejection-recording snapshot")]
    public async Task WhenAnOutOfContractPayloadIsDeliveredForTheRejectionRecordingSnapshot()
    {
        _rejection = await Record.ExceptionAsync(() => DeliverAsync("auditlog", TestPayloads.AuditLogJson));
    }

    [When("a well-formed orders payload is delivered for the rejection-recording snapshot")]
    public async Task WhenAWellFormedOrdersPayloadIsDeliveredForTheRejectionRecordingSnapshot()
    {
        _rejection = await Record.ExceptionAsync(() => DeliverAsync("orders", TestPayloads.OrdersJson));
    }

    [When("a required orders payload is delivered for the rejection-recording snapshot")]
    public Task WhenARequiredOrdersPayloadIsDeliveredForTheRejectionRecordingSnapshot() =>
        DeliverAsync("orders", TestPayloads.OrdersJson);

    [When("a required portfolio payload is delivered for the rejection-recording snapshot")]
    public Task WhenARequiredPortfolioPayloadIsDeliveredForTheRejectionRecordingSnapshot() =>
        DeliverAsync("portfolio", TestPayloads.PortfolioJson);

    [When("a required settings payload is delivered for the rejection-recording snapshot")]
    public Task WhenARequiredSettingsPayloadIsDeliveredForTheRejectionRecordingSnapshot() =>
        DeliverAsync("settings", TestPayloads.SettingsJson);

    [When("a required header payload is delivered for the rejection-recording snapshot")]
    public Task WhenARequiredHeaderPayloadIsDeliveredForTheRejectionRecordingSnapshot() =>
        DeliverAsync("header", TestPayloads.HeaderJson);

    [Then("the rejection-recording delivery is rejected as an unexpected payload type")]
    public void ThenTheRejectionRecordingDeliveryIsRejectedAsAnUnexpectedPayloadType()
    {
        var rejection = Assert.IsAssignableFrom<SnapshotMessageRejectedException>(_rejection);
        Assert.Equal(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, rejection.ReasonCode);
    }

    [Then("the rejection-recording delivery is rejected for an over-long field")]
    public void ThenTheRejectionRecordingDeliveryIsRejectedForAnOverLongField()
    {
        var rejection = Assert.IsAssignableFrom<SnapshotMessageRejectedException>(_rejection);
        Assert.Equal(InvalidSnapshotEnvelopeException.FieldTooLongReason, rejection.ReasonCode);
    }

    [Then("the rejection-recording snapshot has a FAILED tracking row with the reason recorded")]
    public async Task ThenTheRejectionRecordingSnapshotHasAFailedTrackingRowWithTheReasonRecorded()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Failed, tracking.Status);
        Assert.NotNull(tracking.Reason);
        Assert.Contains(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, tracking.Reason);
        Assert.NotNull(tracking.DeclaredFailedAt);

        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("no tracking row exists for the rejection-recording snapshot")]
    public async Task ThenNoTrackingRowExistsForTheRejectionRecordingSnapshot()
    {
        Assert.False(await SnapshotTestHelpers.TrackingRowExistsAsync(_fixture, _snapshotId));
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("a rejection response was published for the rejection-recording snapshot")]
    public void ThenARejectionResponseWasPublishedForTheRejectionRecordingSnapshot()
    {
        var notification = Assert.Single(
            _fixture.ResponsePublisher.PublishedFor(_snapshotId),
            n => n.Status == SnapshotTrackingStatus.Failed);

        Assert.NotEmpty(notification.ReasonCode);
        Assert.NotEmpty(notification.ReasonDetail);
        Assert.NotNull(notification.DeclaredFailedAt);
    }

    [Then("the rejection-recording snapshot reaches COMPLETE with the reason and declared-failed time cleared")]
    public async Task ThenTheRejectionRecordingSnapshotReachesCompleteWithTheReasonAndDeclaredFailedTimeCleared()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
        Assert.Null(tracking.Reason);
        Assert.Null(tracking.DeclaredFailedAt);
    }

    [Then("a rejection-recording index row has been written matching the sent header")]
    public async Task ThenARejectionRecordingIndexRowHasBeenWrittenMatchingTheSentHeader()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var index = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);

        Assert.Equal(_snapshotId, index.SnapshotId);
        Assert.Equal(_accountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
        Assert.Equal(TestPayloads.HeaderEventType, index.EventType);
        Assert.Equal(TestPayloads.HeaderJson, index.DisplayData);

        // The root path pinned by MarkRejectedAsync's FAILED insert is string.Empty (not
        // null, since AdlsRootPath is non-nullable) -- it must never survive into the real
        // blob path once the snapshot recovers and its first real payload is written.
        Assert.False(string.IsNullOrEmpty(tracking.AdlsRootPath));
        Assert.Contains($"accountId={_accountId}/snapshotId={_snapshotId}", tracking.AdlsRootPath);

        foreach (var fileName in new[] { "orders.json", "portfolio.json", "settings.json", "header.json" })
        {
            Assert.True(
                await _fixture.BlobContainer.GetBlobClient($"{tracking.AdlsRootPath}/{fileName}").ExistsAsync(),
                $"Expected blob {tracking.AdlsRootPath}/{fileName} to exist.");
        }
    }

    private async Task DeliverAsync(string payloadType, string body)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, body);
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }
}
