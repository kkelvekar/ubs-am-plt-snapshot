using System.Text.Json;
using Microsoft.Extensions.Logging;
using UBS.AM.PLT.SnapshotWriter.Application;
using UBS.AM.PLT.SnapshotWriter.Domain;
using UBS.AM.PLT.SnapshotWriter.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

public class SnapshotMessageHandlerTests
{
    [Fact]
    public async Task HandleAsync_logs_snapshotId_accountId_and_payloadType()
    {
        var logger = new CapturingLogger<SnapshotMessageHandler>();
        var handler = new SnapshotMessageHandler(logger);

        using var payload = JsonDocument.Parse("""{"total":21}""");
        var message = new SnapshotMessage
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = "instruments",
            Stage = "PreTrade",
            PublishedAt = new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc),
            PublishedBy = "PortfolioCalculation",
            SchemaVersion = "1.0",
            Payload = payload.RootElement.Clone(),
        };

        await handler.HandleAsync(message, CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("corr98765", entry.State["SnapshotId"]);
        Assert.Equal("00675442A", entry.State["AccountId"]);
        Assert.Equal("instruments", entry.State["PayloadType"]);
    }
}
