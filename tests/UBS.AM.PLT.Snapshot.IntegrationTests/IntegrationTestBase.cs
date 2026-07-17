namespace UBS.AM.PLT.Snapshot.IntegrationTests;

/// <summary>
/// Per-test-instance base (xunit creates a fresh instance per test method) providing
/// snapshotId generation and surgical post-test cleanup against the REAL shared dev
/// Azure resources. Cleanup is restricted to <c>it-</c>-prefixed ids and the configured
/// integration-test account ids, so manual live testing data cannot be touched. Cleanup
/// failures propagate loudly: orphaned test data should be visible, not hidden.
/// </summary>
public abstract class IntegrationTestBase : IAsyncLifetime
{
    private readonly List<string> _snapshotIds = [];

    protected IntegrationTestBase(SnapshotFixture fixture)
    {
        Fixture = fixture;
    }

    protected SnapshotFixture Fixture { get; }

    public Task InitializeAsync() => Fixture.CleanupExistingTestDataOnStartAsync();

    public async Task DisposeAsync()
    {
        if (!Fixture.TestSettings.CleanupAfterTest)
        {
            return;
        }

        foreach (var snapshotId in _snapshotIds)
        {
            await Fixture.Cleanup.CleanupSnapshotAsync(snapshotId);
        }
    }

    /// <summary>New unique snapshotId carrying the mandatory <c>it-</c> test marker.</summary>
    protected string NewSnapshotId(string testName)
    {
        var snapshotId = $"it-{testName}-{Guid.NewGuid():N}";
        RegisterSnapshotId(snapshotId);
        return snapshotId;
    }

    /// <summary>
    /// Tracks a snapshotId for post-test cleanup. Call immediately after generating the
    /// id, before sending any message, so cleanup fires even if the test fails partway.
    /// </summary>
    protected void RegisterSnapshotId(string snapshotId)
    {
        // Hard safety guardrail: this project deletes from a real shared dev database,
        // so only ids unmistakably created by this test run may ever be registered.
        if (!snapshotId.StartsWith("it-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to track snapshotId '{snapshotId}' for cleanup: integration-test ids must start with 'it-'.",
                nameof(snapshotId));
        }

        _snapshotIds.Add(snapshotId);
    }
}
