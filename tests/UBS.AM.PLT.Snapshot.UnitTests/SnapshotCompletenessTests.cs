using UBS.AM.PLT.Snapshot.Domain;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public class SnapshotCompletenessTests
{
    [Fact]
    public void IsComplete_is_false_when_received_files_are_a_strict_subset_of_required()
    {
        var required = new HashSet<string> { "header.json", "orders.json", "portfolio.json", "settings.json" };
        var received = new List<string> { "header.json", "orders.json" };

        Assert.False(SnapshotCompleteness.IsComplete(received, required));
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
