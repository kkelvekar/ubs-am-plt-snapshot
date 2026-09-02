using UBS.AM.PLT.Snapshot.Domain;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

public class SnapshotBlobPathTests
{
    private static readonly DateTimeOffset ArrivalTime = new(2026, 5, 22, 6, 10, 14, TimeSpan.Zero);

    [Fact]
    public void FullPath_follows_design_doc_convention()
    {
        var message = CreateMessage();

        var fullPath = SnapshotBlobPath.FullPath(SnapshotBlobPath.RootFolder(message, ArrivalTime), message.PayloadType);

        Assert.Equal(
            "portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId=corr98765/orders.json",
            fullPath);
    }

    [Fact]
    public void RootFolder_has_no_trailing_slash_and_no_container_prefix()
    {
        var message = CreateMessage();

        var rootFolder = SnapshotBlobPath.RootFolder(message, ArrivalTime);

        Assert.Equal(
            "portfolio_snapshots/year=2026/month=05/accountId=00675442A/snapshotId=corr98765",
            rootFolder);
        Assert.False(rootFolder.EndsWith('/'));
        Assert.DoesNotContain("ubsadvsnapshots", rootFolder);
    }

    [Fact]
    public void RootFolder_uses_the_arrival_time_not_the_message_published_at_for_year_and_month()
    {
        // PublishedAt (December 2025) and arrival (January 2026) fall in different
        // months AND years — the path must follow arrival, never PublishedAt.
        var message = CreateMessage(publishedAt: "2025-12-31T23:59:58Z");
        var arrival = new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero);

        var rootFolder = SnapshotBlobPath.RootFolder(message, arrival);

        Assert.Equal(
            "portfolio_snapshots/year=2026/month=01/accountId=00675442A/snapshotId=corr98765",
            rootFolder);
    }

    [Theory]
    [InlineData(1, "month=01")]
    [InlineData(9, "month=09")]
    [InlineData(11, "month=11")]
    public void Month_is_zero_padded_to_two_digits(int month, string expectedSegment)
    {
        var message = CreateMessage();
        var arrival = new DateTimeOffset(2026, month, 3, 8, 0, 0, TimeSpan.Zero);

        Assert.Contains($"/{expectedSegment}/", SnapshotBlobPath.RootFolder(message, arrival));
    }

    [Theory]
    [InlineData("header", "header.json")]
    [InlineData("orders", "orders.json")]
    [InlineData("compliances", "compliances.json")]
    [InlineData("orders-history", "orders-history.json")]
    [InlineData("portfolio", "portfolio.json")]
    public void FileName_is_payloadType_with_json_extension(string payloadType, string expected)
    {
        Assert.Equal(expected, SnapshotBlobPath.FileName(payloadType));
    }

    [Fact]
    public void FullPath_ends_with_FileName_of_the_payloadType()
    {
        var message = CreateMessage();
        var rootFolder = SnapshotBlobPath.RootFolder(message, ArrivalTime);

        Assert.EndsWith(
            $"/{SnapshotBlobPath.FileName(message.PayloadType)}",
            SnapshotBlobPath.FullPath(rootFolder, message.PayloadType));
    }

    [Fact]
    public void Top_folder_is_derived_from_snapshotType()
    {
        var message = CreateMessage(snapshotType: "position");

        Assert.StartsWith("position_snapshots/", SnapshotBlobPath.RootFolder(message, ArrivalTime));
    }

    private static SnapshotMessage CreateMessage(
        string snapshotType = "portfolio",
        string publishedAt = "2026-05-22T06:10:14Z")
        => new()
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = snapshotType,
            PayloadType = "orders",
            PublishedAt = publishedAt,
            PublishedBy = "PortfolioCalculation",
            Payload = """{"total":21}""",
        };
}
