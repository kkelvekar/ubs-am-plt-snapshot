namespace UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;

/// <summary>
/// Deterministic <see cref="TimeProvider"/> for consumer tests: <see cref="UtcNow"/> is
/// settable so timestamps like BlockedSince can be asserted exactly, and every timer due
/// time requested via <c>Task.Delay(delay, timeProvider, ct)</c> is recorded in
/// <see cref="Delays"/> while the actual wait is collapsed to zero so tests stay fast.
/// </summary>
public sealed class RecordingTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 5, 22, 6, 0, 0, TimeSpan.Zero);

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero)
        {
            Delays.Add(dueTime);
        }

        // Fire immediately (via a real zero-due timer) so recorded delays never slow tests.
        return System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}
