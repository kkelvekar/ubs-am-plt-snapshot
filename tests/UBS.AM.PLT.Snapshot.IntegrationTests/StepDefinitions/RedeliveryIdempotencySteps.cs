using Microsoft.EntityFrameworkCore;
using Reqnroll;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/RedeliveryIdempotency.feature</c>. The first scenario drives a
/// full 4-payload snapshot to COMPLETE then redelivers the byte-identical NON-header payload,
/// proving the update path touches only last_updated_at while the index UPSERT never re-runs (no
/// duplicate row). The second delivers the same payloadType twice before completion, proving
/// received_files de-duplicates the filename yet the remaining required files still drive the set
/// to COMPLETE. The clock is anchored to a fixed-but-arbitrary instant inside the "redelivery
/// snapshot" step. The run-scoped <see cref="SnapshotFixture"/> and scenario-scoped
/// <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll, which creates one
/// instance of this class per scenario, so instance fields hold per-scenario state safely. Step
/// text is deliberately distinct from the other feature bindings so they never collide on an
/// ambiguous match.
/// </summary>
[Binding]
public sealed class RedeliveryIdempotencySteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    // Sent messages kept by payloadType so a redelivery reuses the exact same envelope the
    // consumer would re-hand after a redelivery, not a freshly built duplicate.
    private readonly Dictionary<string, SnapshotMessage> _sentMessages = new(StringComparer.Ordinal);

    // Baseline captured after the snapshot first reaches COMPLETE, before the redelivery.
    private SnapshotTrackingEntity? _trackingBaseline;
    private SnapshotIndexEntity? _indexBaseline;
    private string? _ordersBlobBaseline;

    private Exception? _redeliveryException;

    public RedeliveryIdempotencySteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("a redelivery snapshot for account \"(.*)\"")]
    public void GivenARedeliverySnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("redelivery");
    }

    [When("an orders payload is delivered")]
    [When("the same orders payload is delivered again")]
    public Task WhenAnOrdersPayloadIsDelivered() =>
        DeliverAsync("orders", TestPayloads.OrdersJson);

    [When("a calculations payload is delivered")]
    public Task WhenACalculationsPayloadIsDelivered() =>
        DeliverAsync("calculations", TestPayloads.CalculationsJson);

    [When("a settings payload is delivered")]
    public Task WhenASettingsPayloadIsDelivered() =>
        DeliverAsync("settings", TestPayloads.SettingsJson);

    [When("the completing header payload is delivered")]
    public Task WhenTheCompletingHeaderPayloadIsDelivered() =>
        DeliverAsync("header", TestPayloads.HeaderJson);

    [When("the redelivery clock advances by (.*) minutes")]
    public void WhenTheRedeliveryClockAdvancesByMinutes(int minutes)
    {
        // Advance time so a spurious re-completion or re-insert would leak into a timestamp.
        _fixture.CurrentTime = _fixture.CurrentTime.AddMinutes(minutes);
    }

    [Then("the snapshot is COMPLETE and captured as the redelivery baseline")]
    public async Task ThenTheSnapshotIsCompleteAndCapturedAsBaseline()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        _trackingBaseline = tracking;
        _indexBaseline = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);
        _ordersBlobBaseline =
            await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/orders.json");
    }

    [When("the same \"(.*)\" payload is redelivered")]
    public async Task WhenTheSamePayloadIsRedelivered(string payloadType)
    {
        var message = SentMessage(payloadType);
        _redeliveryException =
            await Record.ExceptionAsync(() => _fixture.Handler.HandleAsync(message, CancellationToken.None));
    }

    [Then("the redelivery completes without error")]
    public void ThenTheRedeliveryCompletesWithoutError()
    {
        Assert.Null(_redeliveryException);
    }

    [Then("exactly one tracking row remains, COMPLETE, with only its last-updated time advanced")]
    public async Task ThenExactlyOneTrackingRowRemainsCompleteWithOnlyLastUpdatedAdvanced()
    {
        var baseline = _trackingBaseline ?? throw new InvalidOperationException("No tracking baseline has been recorded.");

        await using (var context = await _fixture.DbContextFactory.CreateDbContextAsync())
        {
            var trackingRowCount = await context.SnapshotTracking
                .AsNoTracking()
                .CountAsync(e => e.SnapshotId == _snapshotId);
            Assert.Equal(1, trackingRowCount);
        }

        var current = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, current.Status);
        Assert.Equal(baseline.CompletedAt, current.CompletedAt);
        Assert.Equal(baseline.FirstReceivedAt, current.FirstReceivedAt);
        Assert.Equal(baseline.AdlsRootPath, current.AdlsRootPath);
        Assert.Equal(baseline.ReceivedFiles, current.ReceivedFiles);
        Assert.True(current.LastUpdatedAt > baseline.LastUpdatedAt);
    }

    [Then("exactly one index row remains, unchanged from the redelivery baseline")]
    public async Task ThenExactlyOneIndexRowRemainsUnchanged()
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
        Assert.Equal(baseline.AdlsPath, current.AdlsPath);
        Assert.Equal(baseline.EventType, current.EventType);
        Assert.Equal(baseline.SnapshotDate, current.SnapshotDate);
    }

    [Then("the redelivered \"(.*)\" blob is byte-identical to the originally sent payload")]
    public async Task ThenTheRedeliveredBlobIsByteIdentical(string payloadType)
    {
        var baselineBlob = _ordersBlobBaseline ?? throw new InvalidOperationException("No blob baseline has been recorded.");
        var message = SentMessage(payloadType);
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);

        var current = await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/{payloadType}.json");
        Assert.Equal(baselineBlob, current);
        Assert.Equal(message.Payload, current);
    }

    [Then("the pre-completion snapshot is RECEIVING with received files \"(.*)\"")]
    public async Task ThenThePreCompletionSnapshotIsReceivingWithReceivedFiles(string commaSeparatedFiles)
    {
        var expected = commaSeparatedFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(expected, tracking.ReceivedFiles);
    }

    [Then("no index row exists yet for the snapshot")]
    public async Task ThenNoIndexRowExistsYetForTheSnapshot()
    {
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("the snapshot is COMPLETE with a completed time")]
    public async Task ThenTheSnapshotIsCompleteWithACompletedTime()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
    }

    [Then("\"(.*)\" appears exactly once among the (.*) received files")]
    public async Task ThenFileAppearsExactlyOnceAmongReceivedFiles(string fileName, int expectedCount)
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Single(tracking.ReceivedFiles, f => f == fileName);
        Assert.Equal(expectedCount, tracking.ReceivedFiles.Count);
    }

    [Then("an index row now exists for the snapshot")]
    public async Task ThenAnIndexRowNowExistsForTheSnapshot()
    {
        Assert.True(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    private async Task DeliverAsync(string payloadType, string payloadJson)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, payloadJson);
        _sentMessages[payloadType] = message;
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }

    private SnapshotMessage SentMessage(string payloadType) =>
        _sentMessages.TryGetValue(payloadType, out var message)
            ? message
            : throw new InvalidOperationException($"No '{payloadType}' payload has been sent in this scenario.");
}
