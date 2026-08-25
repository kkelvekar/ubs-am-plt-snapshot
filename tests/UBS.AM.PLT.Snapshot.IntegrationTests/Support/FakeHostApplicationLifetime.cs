using Microsoft.Extensions.Hosting;

namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

/// <summary>
/// Records <see cref="StopApplication"/> calls so Mode A can assert
/// <c>SnapshotRequestCommand</c> triggers host shutdown on a transient infrastructure failure,
/// without an actual host ever running in this test process. The cancellation tokens are
/// exposed but never signalled -- the command only calls <see cref="StopApplication"/>.
/// </summary>
public sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();

    public int StopApplicationCallCount { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication()
    {
        StopApplicationCallCount++;
        _stopping.Cancel();
    }
}
