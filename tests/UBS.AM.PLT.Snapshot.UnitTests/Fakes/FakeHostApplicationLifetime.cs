using Microsoft.Extensions.Hosting;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// Records <see cref="StopApplication"/> calls so tests can assert the consumer triggers
/// host shutdown on a fatal consume-loop failure. The cancellation tokens are exposed but
/// never signalled — the consumer only calls <see cref="StopApplication"/>.
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
