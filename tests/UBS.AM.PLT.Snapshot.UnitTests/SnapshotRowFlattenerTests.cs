using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Models;
using UBS.AM.PLT.Snapshot.ReadApi;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Flattening rules for the grid response: dynamic display keys pass through untouched, fixed
/// columns win any collision, and absent/blank/invalid display JSON degrades to fixed fields
/// only — never a throw.
/// </summary>
public sealed class SnapshotRowFlattenerTests
{
    [Fact]
    public void Novel_display_key_flows_through_to_output()
    {
        // "brandNewField" is not modelled on SnapshotIndexDisplayData — it must still surface.
        var row = CreateRow(displayDataJson: """{"benchmark":"MCCHM2EQ","brandNewField":"hello"}""");

        var flat = SnapshotRowFlattener.Flatten([row])[0];

        Assert.Equal("MCCHM2EQ", AsString(flat["benchmark"]));
        Assert.Equal("hello", AsString(flat["brandNewField"]));
    }

    [Fact]
    public void Fixed_field_wins_on_collision_with_display_key()
    {
        var row = CreateRow(
            accountId: "00675442A",
            displayDataJson: """{"accountId":"SPOOFED","benchmark":"MCCHM2EQ"}""");

        var flat = SnapshotRowFlattener.Flatten([row])[0];

        // Fixed column stays the real string value, not the JSON element from display data.
        Assert.Equal("00675442A", flat["accountId"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void Blank_or_invalid_display_json_yields_only_fixed_fields(string? displayDataJson)
    {
        var row = CreateRow(displayDataJson: displayDataJson!);

        var flat = SnapshotRowFlattener.Flatten([row])[0];

        Assert.Equal(
            new[] { "snapshotId", "accountId", "snapshotDate", "eventType", "adlsPath", "createdAt" },
            flat.Keys);
    }

    private static string? AsString(object? value) =>
        value is JsonElement element ? element.GetString() : value?.ToString();

    private static SnapshotIndexRow CreateRow(
        string accountId = "00675442A",
        string displayDataJson = "{}") => new()
        {
            SnapshotId = "snap-1",
            AccountId = accountId,
            SnapshotDate = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            EventType = "ModelChange",
            AdlsPath = "snapshots/year=2026/month=05/00675442A/snap-1",
            CreatedAt = new DateTime(2026, 5, 22, 6, 10, 15, DateTimeKind.Utc),
            DisplayDataJson = displayDataJson,
        };
}
