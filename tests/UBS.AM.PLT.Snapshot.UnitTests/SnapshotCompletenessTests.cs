using UBS.AM.PLT.Snapshot.Domain;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public class SnapshotCompletenessTests
{
    [Fact]
    public void IsComplete_is_false_when_received_files_are_a_strict_subset_of_required()
    {
        var required = new HashSet<string>
        {
            "header.json",
            "portfolio.json",
            "orders.json",
            "compliances.json",
            "orders-history.json",
            "settings.json",
        };
        var received = new List<string>
        {
            "header.json",
            "portfolio.json",
            "orders.json",
            "compliances.json",
            "settings.json",
        };

        Assert.False(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_true_when_all_six_portfolio_files_are_received_in_any_order()
    {
        var required = new HashSet<string>
        {
            "header.json",
            "portfolio.json",
            "orders.json",
            "compliances.json",
            "orders-history.json",
            "settings.json",
        };
        var received = new List<string>
        {
            "settings.json",
            "orders-history.json",
            "orders.json",
            "header.json",
            "compliances.json",
            "portfolio.json",
        };

        Assert.True(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_true_on_exact_match()
    {
        var required = new HashSet<string> { "header.json", "orders.json" };
        var received = new List<string> { "header.json", "orders.json" };

        Assert.True(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_true_when_received_files_are_a_superset_of_required()
    {
        var required = new HashSet<string> { "header.json", "orders.json" };
        var received = new List<string> { "header.json", "orders.json", "stray-extra.json" };

        Assert.True(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_false_when_no_files_received()
    {
        var required = new HashSet<string> { "header.json" };
        var received = new List<string>();

        Assert.False(SnapshotCompleteness.IsComplete(received, required));
    }
}
