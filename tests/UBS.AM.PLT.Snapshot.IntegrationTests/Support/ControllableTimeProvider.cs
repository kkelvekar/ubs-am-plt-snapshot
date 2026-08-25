namespace UBS.AM.PLT.Snapshot.IntegrationTests.Support;

/// <summary>
/// <see cref="TimeProvider"/> whose timers never fire on their own: <c>Task.Delay(delay,
/// timeProvider, ct)</c> stays pending until the test explicitly calls <see cref="ReleasePark"/>,
/// so Mode A can assert <c>SnapshotRequestCommand</c>'s transient-path park is observably
/// pending and then complete it deterministically instead of waiting out a real 30 seconds.
/// </summary>
public sealed class ControllableTimeProvider : TimeProvider
{
    private readonly List<PendingTimer> _pendingTimers = [];

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new PendingTimer(callback, state);
        _pendingTimers.Add(timer);
        return timer;
    }

    /// <summary>Fires every still-pending timer's callback, releasing any awaiting Task.Delay.</summary>
    public void ReleasePark()
    {
        foreach (var timer in _pendingTimers.ToArray())
        {
            timer.Fire();
        }

        _pendingTimers.Clear();
    }

    private sealed class PendingTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
