using Microsoft.EntityFrameworkCore;
using Reqnroll;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/UnexpectedPayloadHandling.feature</c>. An unexpected
/// payloadType (auditlog.json) that is not in the required-files set is rejected before the first
/// write, so it never reaches blob storage or received_files — whether it arrives before the
/// required files or after the snapshot has already completed. Either way the four required files
/// alone drive the snapshot to COMPLETE and the index row is written exactly once. The clock is
/// anchored to a
/// fixed-but-arbitrary instant inside the "out-of-contract snapshot" step. The run-scoped
/// <see cref="SnapshotFixture"/> and scenario-scoped <see cref="ScenarioFixtureContext"/> are
/// constructor-injected by Reqnroll, which creates one instance of this class per scenario, so
/// instance fields hold per-scenario state safely. Step text is deliberately distinct from the
/// other feature bindings so they never collide on an ambiguous match.
/// </summary>
[Binding]
public sealed class UnexpectedPayloadHandlingSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    // Baseline captured after the snapshot first reaches COMPLETE, before the extra file arrives.
    private SnapshotTrackingEntity? _trackingBaseline;
    private SnapshotIndexEntity? _indexBaseline;

    private Exception? _rejection;

    public UnexpectedPayloadHandlingSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("an out-of-contract snapshot for account \"(.*)\"")]
    public void GivenAnOutOfContractSnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("unexpected");
    }

    [When("an unexpected auditlog payload is delivered")]
    public async Task WhenAnUnexpectedAuditLogPayloadIsDelivered()
    {
        _rejection = await Record.ExceptionAsync(() => DeliverAsync("auditlog", TestPayloads.AuditLogJson));
    }

    [When("a required orders payload is stored")]
    public Task WhenARequiredOrdersPayloadIsStored() =>
        DeliverAsync("orders", TestPayloads.OrdersJson);

    [When("a required calculations payload is stored")]
    public Task WhenARequiredCalculationsPayloadIsStored() =>
        DeliverAsync("calculations", TestPayloads.CalculationsJson);

    [When("a required settings payload is stored")]
    public Task WhenARequiredSettingsPayloadIsStored() =>
        DeliverAsync("settings", TestPayloads.SettingsJson);

    [When("the completing header is stored")]
    public Task WhenTheCompletingHeaderIsStored() =>
        DeliverAsync("header", TestPayloads.HeaderJson);

    [When("the malformed-input clock moves forward by (.*) minutes")]
    public void WhenTheMalformedInputClockMovesForwardByMinutes(int minutes)
    {
        // Advance time so a spurious re-completion or re-insert would leak into a timestamp.
        _fixture.CurrentTime = _fixture.CurrentTime.AddMinutes(minutes);
    }

    [Then("the delivery is rejected as an unexpected payload type")]
    public void ThenTheDeliveryIsRejectedAsAnUnexpectedPayloadType()
    {
        // The base type is what the consumer branches on to commit rather than redeliver.
        var rejection = Assert.IsAssignableFrom<SnapshotMessageRejectedException>(_rejection);
        Assert.Equal(InvalidSnapshotEnvelopeException.UnexpectedPayloadTypeReason, rejection.ReasonCode);
    }

    [Then("nothing has been stored for the snapshot")]
    public async Task ThenNothingHasBeenStoredForTheSnapshot()
    {
        // Rejected before the first write: no tracking row, no index row, no blob folder.
        await using (var context = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            var trackingRowCount = await context.SnapshotTracking
                .AsNoTracking()
                .CountAsync(e => e.SnapshotId == _snapshotId);
            Assert.Equal(0, trackingRowCount);
        }

        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("the snapshot reaches COMPLETE with a completed time")]
    public async Task ThenTheSnapshotReachesCompleteWithACompletedTime()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
    }

    [Then("the snapshot reaches COMPLETE and is captured as the extra-file baseline")]
    public async Task ThenTheSnapshotReachesCompleteAndIsCapturedAsTheExtraFileBaseline()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        _trackingBaseline = tracking;
        _indexBaseline = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);
    }

    [Then("the completed snapshot lists exactly (.*) received files excluding \"(.*)\"")]
    public async Task ThenTheCompletedSnapshotListsExactlyReceivedFilesExcluding(int expectedCount, string fileName)
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(expectedCount, tracking.ReceivedFiles.Count);
        Assert.DoesNotContain(fileName, tracking.ReceivedFiles);
    }

    [Then("the \"(.*)\" blob is absent under the snapshot root")]
    public async Task ThenTheBlobIsAbsentUnderTheSnapshotRoot(string fileName)
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        var exists = await _fixture.BlobContainer
            .GetBlobClient($"{tracking.AdlsRootPath}/{fileName}")
            .ExistsAsync();
        Assert.False(exists, $"Expected no '{fileName}' blob under '{tracking.AdlsRootPath}'; the payload was rejected.");
    }

    [Then("an index row has been written for the snapshot")]
    public async Task ThenAnIndexRowHasBeenWrittenForTheSnapshot()
    {
        Assert.True(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("the snapshot is still COMPLETE with the same completed time as the baseline")]
    public async Task ThenTheSnapshotIsStillCompleteWithTheSameCompletedTimeAsTheBaseline()
    {
        var baseline = _trackingBaseline ?? throw new InvalidOperationException("No tracking baseline has been recorded.");
        var current = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, current.Status);
        Assert.Equal(baseline.CompletedAt, current.CompletedAt);
    }

    [Then("exactly one index row exists, identical to the extra-file baseline")]
    public async Task ThenExactlyOneIndexRowExistsIdenticalToTheExtraFileBaseline()
    {
        var baseline = _indexBaseline ?? throw new InvalidOperationException("No index baseline has been recorded.");

        await using (var context = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            var indexRowCount = await context.SnapshotIndex
                .AsNoTracking()
                .CountAsync(e => e.SnapshotId == _snapshotId);
            Assert.Equal(1, indexRowCount);
        }

        var current = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);
        Assert.Equal(baseline.CreatedAt, current.CreatedAt);
        Assert.Equal(baseline.EventType, current.EventType);
    }

    private async Task DeliverAsync(string payloadType, string body)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, body);
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }
}
