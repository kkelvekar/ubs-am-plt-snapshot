using Microsoft.EntityFrameworkCore;
using Reqnroll;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/RedeliveryAfterOffsetCommitFailure.feature</c>. Mode A covers
/// only the deterministic, Kafka-independent slice: redelivering the completing (header) message
/// for an already-COMPLETE snapshot (design doc §8 Scenario 5: index write and MarkComplete
/// succeed, the following offset commit fails, the consumer redelivers). The remaining
/// fault-injection cases require the live Kafka consume/commit path and live in Mode B
/// (<c>tools/fault-injection.ps1</c>). The clock is anchored to a fixed-but-arbitrary instant
/// inside the "failure-scenario snapshot" step. The run-scoped <see cref="SnapshotFixture"/>
/// and scenario-scoped <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll,
/// which creates one instance of this class per scenario, so instance fields hold per-scenario
/// state safely. Step text is deliberately distinct from the other feature bindings so they never
/// collide on an ambiguous match.
/// </summary>
[Binding]
public sealed class RedeliveryAfterOffsetCommitFailureSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    // The completing (header) message is created once and reused for redelivery, exactly as the
    // live consumer would re-hand the same envelope after a failed offset commit.
    private SnapshotMessage? _headerMessage;

    // Baseline captured after the first (successful) completing delivery, before redelivery.
    private SnapshotTrackingEntity? _trackingBaseline;
    private SnapshotIndexEntity? _indexBaseline;
    private string? _headerBlobBaseline;

    private Exception? _redeliveryException;

    public RedeliveryAfterOffsetCommitFailureSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("a failure-scenario snapshot for account \"(.*)\"")]
    public void GivenAFailureScenarioSnapshotForAccount(string accountId)
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = accountId;
        _snapshotId = _scenario.NewSnapshotId("offset-fail");
    }

    [When("the snapshot receives an orders payload")]
    public Task WhenTheSnapshotReceivesAnOrdersPayload() =>
        DeliverAsync("orders", TestPayloads.OrdersJson);

    [When("the snapshot receives a calculations payload")]
    public Task WhenTheSnapshotReceivesACalculationsPayload() =>
        DeliverAsync("calculations", TestPayloads.CalculationsJson);

    [When("the snapshot receives a settings payload")]
    public Task WhenTheSnapshotReceivesASettingsPayload() =>
        DeliverAsync("settings", TestPayloads.SettingsJson);

    [When("the completing header message is delivered for the first time")]
    public async Task WhenTheCompletingHeaderMessageIsDeliveredForTheFirstTime()
    {
        // The header is the 4th and final required file: in the live system this HandleAsync is
        // the call that would be followed by a failed consumer.Commit() (§8 Scenario 5).
        _headerMessage = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, "header", TestPayloads.HeaderJson);
        await _fixture.Handler.HandleAsync(_headerMessage, CancellationToken.None);
    }

    [Then("the snapshot is COMPLETE and its tracking, index and header blob are recorded as the redelivery baseline")]
    public async Task ThenTheSnapshotIsCompleteAndBaselineIsRecorded()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);

        _trackingBaseline = tracking;
        _indexBaseline = await SnapshotTestHelpers.GetIndexAsync(_fixture, _snapshotId);
        _headerBlobBaseline = await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/header.json");
    }

    [When("the failure-scenario clock advances by (.*) minutes")]
    public void WhenTheFailureScenarioClockAdvancesByMinutes(int minutes)
    {
        // Advance time so a naive re-insert or re-touch would visibly leak into created_at /
        // completed_at if the redelivery re-ran instead of no-op'ing.
        _fixture.CurrentTime = _fixture.CurrentTime.AddMinutes(minutes);
    }

    [When("the same completing header message is redelivered")]
    public async Task WhenTheSameCompletingHeaderMessageIsRedelivered()
    {
        var header = _headerMessage ?? throw new InvalidOperationException("The completing header message has not been delivered yet.");
        _redeliveryException = await Record.ExceptionAsync(() => _fixture.Handler.HandleAsync(header, CancellationToken.None));
    }

    [Then("the redelivery raises no error")]
    public void ThenTheRedeliveryRaisesNoError()
    {
        Assert.Null(_redeliveryException);
    }

    [Then("exactly one tracking row exists, still COMPLETE and unchanged from the redelivery baseline")]
    public async Task ThenExactlyOneTrackingRowStillCompleteUnchanged()
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
    }

    [Then("exactly one index row exists, unchanged from the redelivery baseline")]
    public async Task ThenExactlyOneIndexRowUnchanged()
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
        Assert.Equal(baseline.AccountId, current.AccountId);
        Assert.Equal(baseline.AdlsPath, current.AdlsPath);
        Assert.Equal(baseline.SnapshotDate, current.SnapshotDate);
        Assert.Equal(baseline.EventType, current.EventType);
        Assert.Equal(baseline.DisplayData.Benchmark, current.DisplayData.Benchmark);
        Assert.Equal(baseline.DisplayData.BatchId, current.DisplayData.BatchId);
    }

    [Then("the header blob still holds the originally sent header content")]
    public async Task ThenTheHeaderBlobStillHoldsTheOriginallySentHeaderContent()
    {
        var baselineBlob = _headerBlobBaseline ?? throw new InvalidOperationException("No header blob baseline has been recorded.");
        var header = _headerMessage ?? throw new InvalidOperationException("The completing header message has not been delivered yet.");
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);

        var current = await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/header.json");
        Assert.Equal(baselineBlob, current);
        Assert.Equal(header.Payload, current);
    }

    private async Task DeliverAsync(string payloadType, string payloadJson)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, payloadJson);
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }
}
