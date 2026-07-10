using System.Text.Json;
using UBS.AM.PLT.SnapshotWriter.Domain;
using Xunit;

namespace UBS.AM.PLT.SnapshotWriter.UnitTests;

/// <summary>
/// Envelope deserialization against the wire format examples in solution design §4.
/// </summary>
public class SnapshotMessageSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private const string InstrumentsWireExample = """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "instruments",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:10:14Z",
          "publishedBy":   "PortfolioCalculation",
          "schemaVersion": "1.0",
          "payload": {
            "total": 21,
            "equities": [
              {
                "assetName":     "APPLE LTD",
                "sedol":         "BPBAJ01",
                "ccy":           "CHF",
                "region":        "EMEA",
                "targetPct":     1.52,
                "prevTargetPct": 1.52
              }
            ],
            "futures": [],
            "cash":    []
          }
        }
        """;

    private const string HeaderWireExample = """
        {
          "snapshotId":    "corr98765",
          "accountId":     "00675442A",
          "snapshotType":  "portfolio",
          "payloadType":   "header",
          "stage":         "PreTrade",
          "publishedAt":   "2026-05-22T06:14:22Z",
          "publishedBy":   "Portal",
          "schemaVersion": "1.0",
          "payload": {
            "eventType":       "ModelChange",
            "portfolioStatus": "ReadyToSend",
            "orderStatus":     "ReadyToSend",
            "benchmark":       "MCCHM2EQ",
            "baseCcy":         "CHF",
            "orderApprovedBy": "Anna Miller",
            "orderApprovedAt": "2026-05-15T06:10:14Z",
            "orderSentBy":     "James Smith",
            "numOrders":       4,
            "ptcAlerts":       0,
            "programId":       "123456",
            "batchId":         "15884"
          }
        }
        """;

    [Fact]
    public void Instruments_wire_example_maps_camelCase_envelope_fields()
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(InstrumentsWireExample, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("00675442A", message.AccountId);
        Assert.Equal("portfolio", message.SnapshotType);
        Assert.Equal("instruments", message.PayloadType);
        Assert.Equal("PreTrade", message.Stage);
        Assert.Equal(new DateTime(2026, 5, 22, 6, 10, 14, DateTimeKind.Utc), message.PublishedAt.ToUniversalTime());
        Assert.Equal("PortfolioCalculation", message.PublishedBy);
        Assert.Equal("1.0", message.SchemaVersion);
    }

    [Fact]
    public void Header_wire_example_maps_camelCase_envelope_fields()
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(HeaderWireExample, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("header", message.PayloadType);
        Assert.Equal("Portal", message.PublishedBy);
    }

    [Theory]
    [InlineData(InstrumentsWireExample)]
    [InlineData(HeaderWireExample)]
    public void Payload_stays_opaque_and_GetRawText_round_trips_original_payload_json(string wireJson)
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(wireJson, WebOptions);
        Assert.NotNull(message);

        using var original = JsonDocument.Parse(wireJson);
        var originalPayload = original.RootElement.GetProperty("payload");

        using var roundTripped = JsonDocument.Parse(message.Payload.GetRawText());

        Assert.True(
            JsonElement.DeepEquals(originalPayload, roundTripped.RootElement),
            "Payload.GetRawText() must be semantically identical to the original payload JSON.");
    }
}
