using Reqnroll;
using Reqnroll.BoDi;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

/// <summary>
/// Reqnroll lifecycle wiring for the Group A feature. The single, expensive
/// <see cref="SnapshotFixture"/> (real production object graph against real Azure
/// resources) is built once per test run in <see cref="BeforeTestRunAsync"/> and shared by
/// every scenario, mirroring the old xUnit <c>IClassFixture</c> lifetime.
/// </summary>
[Binding]
public sealed class ReqnrollHooks
{
    // Scenarios share this fixture's single Mock<TimeProvider>.CurrentTime, so — like the
    // HistoricalCleanupLock guarding shared dev-resource state elsewhere in this project —
    // no two scenarios may run concurrently. Parallelism is disabled via xunit.runner.json
    // ("parallelizeTestCollections": false) so each scenario owns CurrentTime uncontended.
    private static SnapshotFixture? _fixture;

    private readonly IObjectContainer _objectContainer;

    public ReqnrollHooks(IObjectContainer objectContainer)
    {
        _objectContainer = objectContainer;
    }

    private static SnapshotFixture Fixture =>
        _fixture ?? throw new InvalidOperationException("SnapshotFixture has not been initialised for this test run.");

    [BeforeTestRun]
    public static async Task BeforeTestRunAsync()
    {
        _fixture = new SnapshotFixture();
        await _fixture.CleanupExistingTestDataOnStartAsync();
    }

    [AfterTestRun]
    public static void AfterTestRun()
    {
        _fixture?.Dispose();
        _fixture = null;
    }

    // Expose the run-scoped fixture to constructor-injected step definitions.
    [BeforeScenario]
    public void RegisterFixture()
    {
        _objectContainer.RegisterInstanceAs(Fixture);
    }

    // Reqnroll always runs AfterScenario hooks regardless of scenario outcome, so this
    // surgical cleanup fires even when a scenario fails partway — matching the old
    // IntegrationTestBase.DisposeAsync behaviour. Cleanup failures propagate loudly.
    [AfterScenario]
    public async Task CleanupScenarioAsync(ScenarioFixtureContext scenarioContext)
    {
        if (!Fixture.TestSettings.CleanupAfterTest)
        {
            return;
        }

        foreach (var snapshotId in scenarioContext.RegisteredSnapshotIds)
        {
            await Fixture.Cleanup.CleanupSnapshotAsync(snapshotId);
        }
    }
}
