using System.Text.Json;
using UBS.AM.PLT.Snapshot.Domain;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Envelope deserialization against the org-approved wire schema
/// (<c>docs/snapshot-request.schema.json</c>) and the examples in solution design §4:
/// seven string properties, PascalCase on the wire, <c>Payload</c> a string of
/// already-serialised JSON.
/// </summary>
public class SnapshotMessageSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private const string OrdersPayloadJson =
        """{"total":21,"equities":[{"assetName":"APPLE LTD","sedol":"BPBAJ01","ccy":"CHF","region":"EMEA","targetPct":1.52,"prevTargetPct":1.52}],"futures":[],"cash":[]}""";

    private const string HeaderPayloadJson =
        """{"eventType":"ModelChange","portfolioStatus":"ReadyToSend","orderStatus":"ReadyToSend","benchmark":"MCCHM2EQ","baseCcy":"CHF","orderApprovedBy":"Anna Miller","orderApprovedAt":"2026-05-15T06:10:14Z","orderSentBy":"James Smith","numOrders":4,"ptcAlerts":0,"programId":"123456","batchId":"15884"}""";

    private const string OrdersWireExample = """
        {
          "SnapshotId":   "corr98765",
          "AccountId":    "00675442A",
          "SnapshotType": "portfolio",
          "PayloadType":  "orders",
          "PublishedAt":  "2026-05-22T06:10:14Z",
          "PublishedBy":  "PortfolioCalculation",
          "Payload":      "{\"total\":21,\"equities\":[{\"assetName\":\"APPLE LTD\",\"sedol\":\"BPBAJ01\",\"ccy\":\"CHF\",\"region\":\"EMEA\",\"targetPct\":1.52,\"prevTargetPct\":1.52}],\"futures\":[],\"cash\":[]}"
        }
        """;

    private const string HeaderWireExample = """
        {
          "SnapshotId":   "corr98765",
          "AccountId":    "00675442A",
          "SnapshotType": "portfolio",
          "PayloadType":  "header",
          "PublishedAt":  "2026-05-22T06:14:22Z",
          "PublishedBy":  "Portal",
          "Payload":      "{\"eventType\":\"ModelChange\",\"portfolioStatus\":\"ReadyToSend\",\"orderStatus\":\"ReadyToSend\",\"benchmark\":\"MCCHM2EQ\",\"baseCcy\":\"CHF\",\"orderApprovedBy\":\"Anna Miller\",\"orderApprovedAt\":\"2026-05-15T06:10:14Z\",\"orderSentBy\":\"James Smith\",\"numOrders\":4,\"ptcAlerts\":0,\"programId\":\"123456\",\"batchId\":\"15884\"}"
        }
        """;

    [Fact]
    public void Orders_wire_example_maps_PascalCase_envelope_fields()
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(OrdersWireExample, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("00675442A", message.AccountId);
        Assert.Equal("portfolio", message.SnapshotType);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("2026-05-22T06:10:14Z", message.PublishedAt);
        Assert.Equal("PortfolioCalculation", message.PublishedBy);
    }

    [Fact]
    public void Header_wire_example_maps_PascalCase_envelope_fields()
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(HeaderWireExample, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("header", message.PayloadType);
        Assert.Equal("2026-05-22T06:14:22Z", message.PublishedAt);
        Assert.Equal("Portal", message.PublishedBy);
    }

    [Theory]
    [InlineData(OrdersWireExample, OrdersPayloadJson)]
    [InlineData(HeaderWireExample, HeaderPayloadJson)]
    public void Payload_stays_opaque_text_identical_to_the_published_string(string wireJson, string expectedPayload)
    {
        var message = JsonSerializer.Deserialize<SnapshotMessage>(wireJson, WebOptions);

        Assert.NotNull(message);
        Assert.Equal(expectedPayload, message.Payload);
    }

    [Fact]
    public void CamelCase_envelope_still_deserialises()
    {
        // JsonSerializerDefaults.Web is case-insensitive, so a legacy camelCase publisher
        // keeps binding against the same contract.
        const string camelCaseWire = """
            {
              "snapshotId":   "corr98765",
              "accountId":    "00675442A",
              "snapshotType": "portfolio",
              "payloadType":  "orders",
              "publishedAt":  "2026-05-22T06:10:14Z",
              "publishedBy":  "PortfolioCalculation",
              "payload":      "{\"total\":21}"
            }
            """;

        var message = JsonSerializer.Deserialize<SnapshotMessage>(camelCaseWire, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("00675442A", message.AccountId);
        Assert.Equal("portfolio", message.SnapshotType);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("2026-05-22T06:10:14Z", message.PublishedAt);
        Assert.Equal("PortfolioCalculation", message.PublishedBy);
        Assert.Equal("""{"total":21}""", message.Payload);
    }

    [Fact]
    public void Envelope_with_unknown_extra_properties_still_binds()
    {
        // The org schema declares additionalProperties: true — extra fields (including the
        // retired stage/schemaVersion) must be tolerated, never rejected.
        const string wireWithExtras = """
            {
              "SnapshotId":    "corr98765",
              "AccountId":     "00675442A",
              "SnapshotType":  "portfolio",
              "PayloadType":   "orders",
              "PublishedAt":   "2026-05-22T06:10:14Z",
              "PublishedBy":   "PortfolioCalculation",
              "Payload":       "{\"total\":21}",
              "Stage":         "PreTrade",
              "SchemaVersion": "1.0",
              "SomethingNew":  { "nested": [1, 2, 3] }
            }
            """;

        var message = JsonSerializer.Deserialize<SnapshotMessage>(wireWithExtras, WebOptions);

        Assert.NotNull(message);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("""{"total":21}""", message.Payload);
    }
}
