using Reqnroll;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;
using UBS.AM.PLT.SnapshotWriter.IntegrationTests.Support;

namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/SnapshotConcurrencyIsolation.feature</c>. The invariant under
/// test is that the SQL-keyed write path (tracking/index keyed by snapshotId) never lets one
/// snapshot's processing observe or mutate another's, regardless of how their messages interleave
/// on a single consumer thread. Two snapshots that must be proven distinct receive DIFFERENT
/// canned instruments payloads (one "gets its instruments payload", the other "gets its own
/// distinct instruments payload") so their blobs can be shown to differ. The clock is anchored to a
/// fixed-but-arbitrary instant inside the "concurrent snapshot processing begins" step. The
/// run-scoped <see cref="SnapshotWriterFixture"/> and the scenario-scoped
/// <see cref="ScenarioFixtureContext"/> are constructor-injected by Reqnroll, which creates one
/// instance of this class per scenario, so instance fields hold per-scenario state safely. Step
/// text is deliberately distinct from the other feature bindings so they never collide on an
/// ambiguous match.
/// </summary>
[Binding]
public sealed class SnapshotConcurrencyIsolationSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotWriterFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    // Per-scenario state, keyed by the human label used in the feature ("A", "B", "S1", "S2").
    private readonly Dictionary<string, string> _snapshotIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accountIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, SnapshotMessage>> _sentMessages = new(StringComparer.Ordinal);

    private SnapshotTrackingEntity? _baselineTracking;
    private SnapshotIndexEntity? _baselineIndex;

    public SnapshotConcurrencyIsolationSteps(SnapshotWriterFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("concurrent snapshot processing begins")]
    public void GivenConcurrentSnapshotProcessingBegins()
    {
        _fixture.CurrentTime = AnchorTime;
    }

    [Given("snapshot \"(.*)\" is registered under account \"(.*)\"")]
    [When("snapshot \"(.*)\" is registered under account \"(.*)\"")]
    public void GivenSnapshotIsRegisteredUnderAccount(string label, string accountId)
    {
        _snapshotIds[label] = _scenario.NewSnapshotId($"conc-{label}");
        _accountIds[label] = accountId;
        _sentMessages[label] = new Dictionary<string, SnapshotMessage>(StringComparer.Ordinal);
    }

    [When("snapshot \"(.*)\" gets its instruments payload")]
    public Task WhenSnapshotGetsItsInstrumentsPayload(string label) =>
        SendPayloadAsync(label, "instruments", TestPayloads.InstrumentsJson);

    [When("snapshot \"(.*)\" gets its own distinct instruments payload")]
    public Task WhenSnapshotGetsItsOwnDistinctInstrumentsPayload(string label) =>
        SendPayloadAsync(label, "instruments", TestPayloads.InstrumentsJsonAlt);

    [When("snapshot \"(.*)\" gets its calculations payload")]
    public Task WhenSnapshotGetsItsCalculationsPayload(string label) =>
        SendPayloadAsync(label, "calculations", TestPayloads.CalculationsJson);

    [When("snapshot \"(.*)\" gets its settings payload")]
    public Task WhenSnapshotGetsItsSettingsPayload(string label) =>
        SendPayloadAsync(label, "settings", TestPayloads.SettingsJson);

    [When("snapshot \"(.*)\" gets its standard header payload")]
    public Task WhenSnapshotGetsItsStandardHeaderPayload(string label) =>
        SendPayloadAsync(label, "header", TestPayloads.HeaderJson);

    [When("20 minutes pass")]
    public void WhenTwentyMinutesPass()
    {
        _fixture.CurrentTime = _fixture.CurrentTime.AddMinutes(20);
    }

    [Then("snapshot \"(.*)\" is RECEIVING with received files \"(.*)\"")]
    public async Task ThenSnapshotIsReceivingWithReceivedFiles(string label, string commaSeparatedFiles)
    {
        var expected = commaSeparatedFiles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tracking = await GetTrackingAsync(label);
        Assert.Equal(SnapshotTrackingStatus.Receiving, tracking.Status);
        Assert.Equal(expected, tracking.ReceivedFiles);
    }

    [Then("snapshot \"(.*)\" and snapshot \"(.*)\" have different tracking roots")]
    public async Task ThenTheTwoSnapshotsHaveDifferentTrackingRoots(string firstLabel, string secondLabel)
    {
        var first = await GetTrackingAsync(firstLabel);
        var second = await GetTrackingAsync(secondLabel);
        Assert.NotEqual(first.AdlsRootPath, second.AdlsRootPath);
    }

    [Then("snapshot \"(.*)\" tracking root contains the segment \"(.*)\"")]
    public async Task ThenSnapshotTrackingRootContainsTheSegment(string label, string segment)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.Contains(segment, tracking.AdlsRootPath);
    }

    [Then("snapshot \"(.*)\" has no index row")]
    public async Task ThenSnapshotHasNoIndexRow(string label)
    {
        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, SnapshotId(label)));
    }

    [Then("snapshot \"(.*)\" is COMPLETE with a completed time and an index row")]
    public async Task ThenSnapshotIsCompleteWithACompletedTimeAndAnIndexRow(string label)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
        Assert.True(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, SnapshotId(label)));
    }

    [Then("snapshot \"(.*)\" received files exclude \"(.*)\"")]
    public async Task ThenSnapshotReceivedFilesExclude(string label, string fileName)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.DoesNotContain(fileName, tracking.ReceivedFiles);
    }

    [Then("snapshot \"(.*)\" has no completed time")]
    public async Task ThenSnapshotHasNoCompletedTime(string label)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.Null(tracking.CompletedAt);
    }

    [Then("snapshot \"(.*)\" is COMPLETE")]
    public async Task ThenSnapshotIsComplete(string label)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
    }

    [Then("snapshot \"(.*)\" index row has account \"(.*)\" and adls path equal to its tracking root")]
    public async Task ThenSnapshotIndexRowHasAccountAndAdlsPathEqualToItsTrackingRoot(string label, string accountId)
    {
        var tracking = await GetTrackingAsync(label);
        var index = await SnapshotTestHelpers.GetIndexAsync(_fixture, SnapshotId(label));
        Assert.Equal(accountId, index.AccountId);
        Assert.Equal(tracking.AdlsRootPath, index.AdlsPath);
    }

    [Then("snapshot \"(.*)\" instruments blob equals its sent instruments payload")]
    public async Task ThenSnapshotInstrumentsBlobEqualsItsSentInstrumentsPayload(string label)
    {
        var expected = SentMessage(label, "instruments").Payload.GetRawText();
        var blobText = await DownloadInstrumentsBlobAsync(label);
        Assert.Equal(expected, blobText);
    }

    [Then("the two instruments blobs differ")]
    public async Task ThenTheTwoInstrumentsBlobsDiffer()
    {
        var a = await DownloadInstrumentsBlobAsync("A");
        var b = await DownloadInstrumentsBlobAsync("B");
        Assert.NotEqual(a, b);
    }

    [Then("snapshot \"(.*)\" tracking and index are recorded as the isolation baseline")]
    public async Task ThenSnapshotTrackingAndIndexAreRecordedAsTheIsolationBaseline(string label)
    {
        _baselineTracking = await GetTrackingAsync(label);
        _baselineIndex = await SnapshotTestHelpers.GetIndexAsync(_fixture, SnapshotId(label));
    }

    [Then("snapshot \"(.*)\" tracking is unchanged from the isolation baseline")]
    public async Task ThenSnapshotTrackingIsUnchangedFromTheIsolationBaseline(string label)
    {
        var baseline = _baselineTracking ?? throw new InvalidOperationException("No tracking baseline has been recorded.");
        var current = await GetTrackingAsync(label);
        Assert.Equal(baseline.ReceivedFiles, current.ReceivedFiles);
        Assert.Equal(baseline.Status, current.Status);
        Assert.Equal(baseline.CompletedAt, current.CompletedAt);
        Assert.Equal(baseline.LastUpdatedAt, current.LastUpdatedAt);
        Assert.Equal(baseline.FirstReceivedAt, current.FirstReceivedAt);
        Assert.Equal(baseline.AdlsRootPath, current.AdlsRootPath);
    }

    [Then("snapshot \"(.*)\" index is unchanged from the isolation baseline")]
    public async Task ThenSnapshotIndexIsUnchangedFromTheIsolationBaseline(string label)
    {
        var baseline = _baselineIndex ?? throw new InvalidOperationException("No index baseline has been recorded.");
        var current = await SnapshotTestHelpers.GetIndexAsync(_fixture, SnapshotId(label));
        Assert.Equal(baseline.SnapshotId, current.SnapshotId);
        Assert.Equal(baseline.AccountId, current.AccountId);
        Assert.Equal(baseline.SnapshotDate, current.SnapshotDate);
        Assert.Equal(baseline.AdlsPath, current.AdlsPath);
        Assert.Equal(baseline.CreatedAt, current.CreatedAt);
    }

    [Then("snapshot \"(.*)\" and snapshot \"(.*)\" tracking roots differ but share the account prefix")]
    public async Task ThenTrackingRootsDifferButShareTheAccountPrefix(string firstLabel, string secondLabel)
    {
        var first = await GetTrackingAsync(firstLabel);
        var second = await GetTrackingAsync(secondLabel);
        Assert.NotEqual(first.AdlsRootPath, second.AdlsRootPath);
        var sharedPrefix = first.AdlsRootPath[..first.AdlsRootPath.LastIndexOf('/')];
        Assert.StartsWith(sharedPrefix, second.AdlsRootPath, StringComparison.Ordinal);
    }

    [Then("snapshot \"(.*)\" tracking root contains its own snapshotId")]
    public async Task ThenSnapshotTrackingRootContainsItsOwnSnapshotId(string label)
    {
        var tracking = await GetTrackingAsync(label);
        Assert.Contains($"snapshotId={SnapshotId(label)}", tracking.AdlsRootPath);
    }

    [Then("the index rows of snapshot \"(.*)\" and snapshot \"(.*)\" have different snapshotIds")]
    public async Task ThenTheIndexRowsHaveDifferentSnapshotIds(string firstLabel, string secondLabel)
    {
        var first = await SnapshotTestHelpers.GetIndexAsync(_fixture, SnapshotId(firstLabel));
        var second = await SnapshotTestHelpers.GetIndexAsync(_fixture, SnapshotId(secondLabel));
        Assert.NotEqual(first.SnapshotId, second.SnapshotId);
    }

    private async Task SendPayloadAsync(string label, string payloadType, string payloadJson)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, SnapshotId(label), AccountId(label), payloadType, payloadJson);
        _sentMessages[label][payloadType] = message;
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }

    private async Task<string> DownloadInstrumentsBlobAsync(string label)
    {
        var tracking = await GetTrackingAsync(label);
        return await SnapshotTestHelpers.DownloadBlobTextAsync(_fixture, $"{tracking.AdlsRootPath}/instruments.json");
    }

    private Task<SnapshotTrackingEntity> GetTrackingAsync(string label) =>
        SnapshotTestHelpers.GetTrackingAsync(_fixture, SnapshotId(label));

    private string SnapshotId(string label) =>
        _snapshotIds.TryGetValue(label, out var id)
            ? id
            : throw new InvalidOperationException($"Snapshot label '{label}' has not been registered.");

    private string AccountId(string label) =>
        _accountIds.TryGetValue(label, out var accountId)
            ? accountId
            : throw new InvalidOperationException($"Snapshot label '{label}' has not been registered.");

    private SnapshotMessage SentMessage(string label, string payloadType) =>
        _sentMessages.TryGetValue(label, out var byType) && byType.TryGetValue(payloadType, out var message)
            ? message
            : throw new InvalidOperationException($"No '{payloadType}' payload has been sent for snapshot label '{label}'.");
}
