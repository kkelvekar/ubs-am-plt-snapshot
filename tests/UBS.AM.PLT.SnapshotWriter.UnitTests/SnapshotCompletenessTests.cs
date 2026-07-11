using UBS.AM.PLT.SnapshotWriter.Domain;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class SnapshotCompletenessTests
{
    [Fact]
    public void IsComplete_is_false_when_received_files_are_a_strict_subset_of_required()
    {
        var required = new HashSet<string> { "header.json", "instruments.json", "calculations.json", "settings.json" };
        var received = new List<string> { "header.json", "instruments.json" };

        Assert.False(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_true_on_exact_match()
    {
        var required = new HashSet<string> { "header.json", "instruments.json" };
        var received = new List<string> { "header.json", "instruments.json" };

        Assert.True(SnapshotCompleteness.IsComplete(received, required));
    }

    [Fact]
    public void IsComplete_is_true_when_received_files_are_a_superset_of_required()
    {
        var required = new HashSet<string> { "header.json", "instruments.json" };
        var received = new List<string> { "header.json", "instruments.json", "stray-extra.json" };

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
