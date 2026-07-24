using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Business rules for <see cref="SnapshotGridFilter.Resolve"/> — no ASP.NET, no DB. Guards the
/// "never an unfiltered all-rows query" rule and the injected-clock 7-day default window.
/// </summary>
public sealed class SnapshotGridFilterResolveTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 9, 30, 0, TimeSpan.Zero);

    private readonly RecordingTimeProvider _timeProvider = new() { UtcNow = Now };

    [Fact]
    public void Empty_account_ids_is_rejected()
    {
        var filter = new SnapshotGridFilter { AccountIds = [] };

        Assert.Throws<SnapshotGridFilterValidationException>(
            () => SnapshotGridFilter.Resolve(filter, _timeProvider));
    }

    [Fact]
    public void Blank_only_account_ids_is_rejected()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["", "   "] };

        Assert.Throws<SnapshotGridFilterValidationException>(
            () => SnapshotGridFilter.Resolve(filter, _timeProvider));
    }

    [Fact]
    public void Omitted_window_fills_last_seven_days_from_the_clock()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["00675442A"] };

        var resolved = SnapshotGridFilter.Resolve(filter, _timeProvider);

        Assert.Equal(Now.UtcDateTime, resolved.ToDate);
        Assert.Equal(Now.UtcDateTime - TimeSpan.FromDays(7), resolved.FromDate);
    }

    [Fact]
    public void Provided_window_is_preserved()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["00675442A"],
            FromDate = from,
            ToDate = to,
        };

        var resolved = SnapshotGridFilter.Resolve(filter, _timeProvider);

        Assert.Equal(from, resolved.FromDate);
        Assert.Equal(to, resolved.ToDate);
    }

    [Fact]
    public void Inverted_window_is_rejected()
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["00675442A"],
            FromDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            ToDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        Assert.Throws<SnapshotGridFilterValidationException>(
            () => SnapshotGridFilter.Resolve(filter, _timeProvider));
    }

    [Fact]
    public void Account_ids_are_trimmed_and_blanks_dropped()
    {
        var filter = new SnapshotGridFilter { AccountIds = [" A ", "", "B"] };

        var resolved = SnapshotGridFilter.Resolve(filter, _timeProvider);

        Assert.Equal(["A", "B"], resolved.AccountIds);
    }
}
