namespace UBS.AM.PLT.SnapshotWriter.IntegrationTests.Support;

/// <summary>
/// Scenario-scoped state resolved from Reqnroll's per-scenario DI container (Reqnroll
/// creates one instance per scenario and shares it with every step-definition and hook
/// that asks for it). It owns only the snapshotId-cleanup list, replacing the
/// <c>NewSnapshotId</c>/registration/dispose pattern of the xUnit <c>IntegrationTestBase</c>.
/// The shared run-scoped <see cref="SnapshotWriterFixture"/> (and its <c>CurrentTime</c>)
/// is deliberately NOT owned here — step definitions set <c>Fixture.CurrentTime</c> directly.
/// </summary>
public sealed class ScenarioFixtureContext
{
    private const string SnapshotIdPrefix = "it-";

    private readonly List<string> _snapshotIds = [];

    /// <summary>SnapshotIds created during this scenario, for post-scenario cleanup.</summary>
    public IReadOnlyList<string> RegisteredSnapshotIds => _snapshotIds;

    /// <summary>New unique snapshotId carrying the mandatory <c>it-</c> test marker.</summary>
    public string NewSnapshotId(string testName)
    {
        var snapshotId = $"{SnapshotIdPrefix}{testName}-{Guid.NewGuid():N}";
        RegisterSnapshotId(snapshotId);
        return snapshotId;
    }

    /// <summary>
    /// Tracks a snapshotId for post-scenario cleanup. Call immediately after generating the
    /// id, before sending any message, so cleanup fires even if the scenario fails partway.
    /// </summary>
    public void RegisterSnapshotId(string snapshotId)
    {
        // Hard safety guardrail: this project deletes from a real shared dev database,
        // so only ids unmistakably created by this test run may ever be registered.
        if (!snapshotId.StartsWith(SnapshotIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to track snapshotId '{snapshotId}' for cleanup: integration-test ids must start with '{SnapshotIdPrefix}'.",
                nameof(snapshotId));
        }

        _snapshotIds.Add(snapshotId);
    }
}
