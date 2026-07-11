using System.Text.Json;
using UBS.AM.PLT.SnapshotWriter.Domain;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class SnapshotBlobPathTests
{
    [Fact]
    public void FullPath_follows_design_doc_convention()
    {
        var message = CreateMessage();

        var fullPath = SnapshotBlobPath.FullPath(message);

        Assert.Equal(
            "portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId=corr98765/instruments.json",
            fullPath);
    }

    [Fact]
    public void RootFolder_has_no_trailing_slash_and_no_container_prefix()
    {
        var message = CreateMessage();

        var rootFolder = SnapshotBlobPath.RootFolder(message);

        Assert.Equal(
            "portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId=corr98765",
            rootFolder);
        Assert.False(rootFolder.EndsWith('/'));
        Assert.DoesNotContain("ubsadvsnapshots", rootFolder);
    }

    [Theory]
    [InlineData(1, "month=01")]
    [InlineData(9, "month=09")]
    [InlineData(11, "month=11")]
    public void Month_is_zero_padded_to_two_digits(int month, string expectedSegment)
    {
        var message = CreateMessage(publishedAt: new DateTime(2026, month, 3, 8, 0, 0, DateTimeKind.Utc));

        Assert.Contains($"/{expectedSegment}/", SnapshotBlobPath.FullPath(message));
    }

    [Fact]
    public void Top_folder_is_derived_from_snapshotType()
    {
        var message = CreateMessage(snapshotType: "position");

        Assert.StartsWith("position_snapshots/", SnapshotBlobPath.FullPath(message));
    }

    private static SnapshotMessage CreateMessage(
        string snapshotType = "portfolio",
        DateTime? publishedAt = null)
    {
        using var payload = JsonDocument.Parse("""{"total":21}""");
        return new SnapshotMessage
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = snapshotType,
            PayloadType = "instruments",
            Stage = "PreTrade",
            PublishedAt = publishedAt ?? new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };
    }
}
