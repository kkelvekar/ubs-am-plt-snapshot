using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Business rules for <see cref="SnapshotGridFilter.Resolve"/> — no ASP.NET, no DB. Guards the
/// "never an unfiltered all-rows query" rule and the "no implicit default window" rule: an
/// omitted date bound stays null (an open bound) and is never filled in for the caller.
/// </summary>
public sealed class SnapshotGridFilterResolveTests
{
    [Fact]
    public void Empty_account_ids_is_rejected()
    {
        var filter = new SnapshotGridFilter { AccountIds = [] };

        Assert.Throws<SnapshotGridFilterValidationException>(
            () => SnapshotGridFilter.Resolve(filter));
    }

    [Fact]
    public void Blank_only_account_ids_is_rejected()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["", "   "] };

        Assert.Throws<SnapshotGridFilterValidationException>(
            () => SnapshotGridFilter.Resolve(filter));
    }

    [Fact]
    public void Omitted_window_stays_null_on_both_bounds()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["00675442A"] };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Null(resolved.FromDate);
        Assert.Null(resolved.ToDate);
    }

    /// <summary>
    /// accountIds is the only mandatory field: a filter carrying nothing else must resolve
    /// cleanly, not throw, with no window invented for the caller and no event filter left behind.
    /// </summary>
    [Fact]
    public void Account_ids_only_resolves_with_no_window_and_no_event_type()
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["00675442A"],
            FromDate = null,
            ToDate = null,
            EventType = null,
        };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Equal(["00675442A"], resolved.AccountIds);
        Assert.Null(resolved.FromDate);
        Assert.Null(resolved.ToDate);
        Assert.Null(resolved.EventType);
    }

    /// <summary>
    /// One-sided windows are valid: the supplied bound is carried through and the omitted one
    /// stays an open bound rather than being defaulted or rejected as inverted.
    /// </summary>
    [Fact]
    public void From_only_window_is_preserved_with_open_upper_bound()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new SnapshotGridFilter { AccountIds = ["00675442A"], FromDate = from };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Equal(from, resolved.FromDate);
        Assert.Null(resolved.ToDate);
    }

    [Fact]
    public void To_only_window_is_preserved_with_open_lower_bound()
    {
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var filter = new SnapshotGridFilter { AccountIds = ["00675442A"], ToDate = to };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Equal(to, resolved.ToDate);
        Assert.Null(resolved.FromDate);
    }

    [Fact]
    public void Blank_event_type_resolves_to_null()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["00675442A"], EventType = "   " };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Null(resolved.EventType);
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

        var resolved = SnapshotGridFilter.Resolve(filter);

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
            () => SnapshotGridFilter.Resolve(filter));
    }

    [Fact]
    public void Account_ids_are_trimmed_and_blanks_dropped()
    {
        var filter = new SnapshotGridFilter { AccountIds = [" A ", "", "B"] };

        var resolved = SnapshotGridFilter.Resolve(filter);

        Assert.Equal(["A", "B"], resolved.AccountIds);
    }
}
