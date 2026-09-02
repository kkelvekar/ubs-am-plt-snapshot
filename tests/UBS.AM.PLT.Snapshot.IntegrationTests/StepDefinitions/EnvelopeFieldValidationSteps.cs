using Reqnroll;
using UBS.AM.PLT.Snapshot.Application.Exceptions;
using UBS.AM.PLT.Snapshot.Domain;
using UBS.AM.PLT.Snapshot.Domain.Entities;
using UBS.AM.PLT.Snapshot.IntegrationTests.Support;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.StepDefinitions;

/// <summary>
/// Step definitions for <c>Features/EnvelopeFieldValidation.feature</c>. SnapshotEnvelopeValidator
/// rejects an identity field over the 100-character limit (SnapshotFieldLimits) before the first
/// write, the same non-retryable rejection category as unexpected-payload-type. The clock is
/// anchored to a fixed-but-arbitrary instant inside the "field-validation snapshot" step. The
/// run-scoped <see cref="SnapshotFixture"/> and scenario-scoped <see cref="ScenarioFixtureContext"/>
/// are constructor-injected by Reqnroll, which creates one instance of this class per scenario, so
/// instance fields hold per-scenario state safely. Step text is deliberately distinct from the
/// other feature bindings so they never collide on an ambiguous match.
/// </summary>
[Binding]
public sealed class EnvelopeFieldValidationSteps
{
    private static readonly DateTimeOffset AnchorTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SnapshotFixture _fixture;
    private readonly ScenarioFixtureContext _scenario;

    private string _snapshotId = string.Empty;
    private string _accountId = string.Empty;

    private Exception? _rejection;

    public EnvelopeFieldValidationSteps(SnapshotFixture fixture, ScenarioFixtureContext scenario)
    {
        _fixture = fixture;
        _scenario = scenario;
    }

    [Given("a field-validation snapshot with a well-formed account id")]
    public void GivenAFieldValidationSnapshotWithAWellFormedAccountId()
    {
        _fixture.CurrentTime = AnchorTime;
        _accountId = "IT-ACC-008";
        _snapshotId = _scenario.NewSnapshotId("fieldvalidation");
    }

    [When("a payload arrives naming a 101-character account id")]
    public async Task WhenAPayloadArrivesNamingA101CharacterAccountId()
    {
        var overLongAccountId = new string('A', 101);
        var message = SnapshotTestHelpers.CreateMessage(
            _fixture, _snapshotId, overLongAccountId, "orders", TestPayloads.OrdersJson);
        _rejection = await Record.ExceptionAsync(() => _fixture.Handler.HandleAsync(message, CancellationToken.None));
    }

    [When("the field-validation orders payload arrives")]
    public Task WhenTheFieldValidationOrdersPayloadArrives() =>
        DeliverAsync("orders", TestPayloads.OrdersJson);

    [When("the field-validation portfolio payload arrives")]
    public Task WhenTheFieldValidationPortfolioPayloadArrives() =>
        DeliverAsync("portfolio", TestPayloads.PortfolioJson);

    [When("the field-validation compliances payload arrives")]
    public Task WhenTheFieldValidationCompliancesPayloadArrives() =>
        DeliverAsync("compliances", TestPayloads.CompliancesJson);

    [When("the field-validation orders-history payload arrives")]
    public Task WhenTheFieldValidationOrdersHistoryPayloadArrives() =>
        DeliverAsync("orders-history", TestPayloads.OrdersHistoryJson);

    [When("the field-validation settings payload arrives")]
    public Task WhenTheFieldValidationSettingsPayloadArrives() =>
        DeliverAsync("settings", TestPayloads.SettingsJson);

    [When("the field-validation header payload arrives")]
    public Task WhenTheFieldValidationHeaderPayloadArrives() =>
        DeliverAsync("header", TestPayloads.HeaderJson);

    [Then("the field-validation delivery is rejected for an over-long field")]
    public void ThenTheFieldValidationDeliveryIsRejectedForAnOverLongField()
    {
        // The base type is what the consumer branches on to commit rather than redeliver.
        var rejection = Assert.IsAssignableFrom<SnapshotMessageRejectedException>(_rejection);
        Assert.Equal(InvalidSnapshotEnvelopeException.FieldTooLongReason, rejection.ReasonCode);
    }

    [Then("the field-validation snapshot has a FAILED tracking row and nothing else stored")]
    public async Task ThenTheFieldValidationSnapshotHasAFailedTrackingRowAndNothingElseStored()
    {
        // Rejected before the first write, but SnapshotId itself is storable here, so
        // MarkRejectedAsync records a FAILED row (status + reason only) — no blob, no index row.
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Failed, tracking.Status);
        Assert.NotNull(tracking.Reason);
        Assert.Contains(InvalidSnapshotEnvelopeException.FieldTooLongReason, tracking.Reason);

        Assert.False(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    [Then("the field-validation snapshot reaches COMPLETE with a completed time")]
    public async Task ThenTheFieldValidationSnapshotReachesCompleteWithACompletedTime()
    {
        var tracking = await SnapshotTestHelpers.GetTrackingAsync(_fixture, _snapshotId);
        Assert.Equal(SnapshotTrackingStatus.Complete, tracking.Status);
        Assert.NotNull(tracking.CompletedAt);
    }

    [Then("a field-validation index row has been written")]
    public async Task ThenAFieldValidationIndexRowHasBeenWritten()
    {
        Assert.True(await SnapshotTestHelpers.IndexRowExistsAsync(_fixture, _snapshotId));
    }

    private async Task DeliverAsync(string payloadType, string body)
    {
        var message = SnapshotTestHelpers.CreateMessage(_fixture, _snapshotId, _accountId, payloadType, body);
        await _fixture.Handler.HandleAsync(message, CancellationToken.None);
    }
}
