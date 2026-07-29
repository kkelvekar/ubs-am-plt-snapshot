using System.Text.Json;
using UBS.Advantage.CommunicationModels.Snapshot;
using UBS.AM.PLT.Snapshot.Infrastructure.Kafka;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The org shared-library <see cref="SnapshotRequest"/> (simulated NuGet type) and its
/// mapping onto the domain envelope. Covers the 1:1 field mapping, the opaque
/// <c>Payload</c> string surviving untouched, PascalCase/camelCase binding via
/// <see cref="JsonSerializerDefaults.Web"/>, and the accepted behavioural delta where the
/// org DTO's non-required optional fields default to <see cref="string.Empty"/>.
/// </summary>
public class SnapshotRequestMapperTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private const string OrdersPayloadJson =
        """{"total":21,"equities":[{"assetName":"APPLE LTD","sedol":"BPBAJ01","ccy":"CHF","region":"EMEA","targetPct":1.52,"prevTargetPct":1.52}],"futures":[],"cash":[]}""";

    [Fact]
    public void ToDomain_maps_all_seven_fields_one_to_one()
    {
        var request = new SnapshotRequest
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = "orders",
            PublishedAt = "2026-05-22T06:10:14Z",
            PublishedBy = "PortfolioCalculation",
            Payload = OrdersPayloadJson,
        };

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("00675442A", message.AccountId);
        Assert.Equal("portfolio", message.SnapshotType);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("2026-05-22T06:10:14Z", message.PublishedAt);
        Assert.Equal("PortfolioCalculation", message.PublishedBy);
        Assert.Equal(OrdersPayloadJson, message.Payload);
    }

    [Fact]
    public void ToDomain_carries_payload_across_as_the_same_opaque_text()
    {
        // Byte-identical, not re-serialised: the blob write must reproduce exactly what the
        // publisher sent, including its whitespace and property order.
        const string oddlyFormattedPayload = """{  "total" : 21 ,   "cash" : [ ]  }""";

        var request = new SnapshotRequest
        {
            SnapshotId = "corr98765",
            AccountId = "00675442A",
            SnapshotType = "portfolio",
            PayloadType = "orders",
            PublishedAt = "2026-05-22T06:10:14Z",
            PublishedBy = "PortfolioCalculation",
            Payload = oddlyFormattedPayload,
        };

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal(oddlyFormattedPayload, message.Payload);
        Assert.Same(request.Payload, message.Payload);
    }

    [Fact]
    public void Missing_optional_field_maps_to_empty_string_not_a_deserialisation_failure()
    {
        // Accepted, deliberate behavioural delta from the org contract: AccountId (like
        // PublishedAt/PublishedBy) is non-required on the org DTO with a string.Empty
        // default, so a wire message omitting it now binds to "" and maps through instead
        // of throwing, as it would have against the all-required domain envelope. The
        // mapper deliberately does not compensate — tightening validation is a separate
        // slice.
        const string wireWithoutAccountId = """
            {
              "SnapshotId":   "corr98765",
              "SnapshotType": "portfolio",
              "PayloadType":  "orders",
              "PublishedAt":  "2026-05-22T06:10:14Z",
              "PublishedBy":  "PortfolioCalculation",
              "Payload":      "{\"total\":21}"
            }
            """;

        var request = JsonSerializer.Deserialize<SnapshotRequest>(wireWithoutAccountId, WebOptions);

        Assert.NotNull(request);

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal(string.Empty, message.AccountId);
        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("""{"total":21}""", message.Payload);
    }

    [Fact]
    public void Missing_optional_publishedAt_and_publishedBy_also_map_to_empty_strings()
    {
        var request = new SnapshotRequest
        {
            SnapshotId = "corr98765",
            SnapshotType = "portfolio",
            PayloadType = "orders",
            Payload = """{"total":21}""",
        };

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal(string.Empty, message.AccountId);
        Assert.Equal(string.Empty, message.PublishedAt);
        Assert.Equal(string.Empty, message.PublishedBy);
    }

    [Fact]
    public void SnapshotRequest_binds_the_PascalCase_wire_contract()
    {
        const string pascalCaseWire = """
            {
              "SnapshotId":   "corr98765",
              "AccountId":    "00675442A",
              "SnapshotType": "portfolio",
              "PayloadType":  "orders",
              "PublishedAt":  "2026-05-22T06:10:14Z",
              "PublishedBy":  "PortfolioCalculation",
              "Payload":      "{\"total\":21}"
            }
            """;

        var request = JsonSerializer.Deserialize<SnapshotRequest>(pascalCaseWire, WebOptions);

        Assert.NotNull(request);
        Assert.Equal("corr98765", request.SnapshotId);
        Assert.Equal("00675442A", request.AccountId);
        Assert.Equal("portfolio", request.SnapshotType);
        Assert.Equal("orders", request.PayloadType);
        Assert.Equal("2026-05-22T06:10:14Z", request.PublishedAt);
        Assert.Equal("PortfolioCalculation", request.PublishedBy);
        Assert.Equal("""{"total":21}""", request.Payload);
    }

    [Fact]
    public void SnapshotRequest_binds_a_camelCase_wire_envelope_too()
    {
        // JsonSerializerDefaults.Web is case-insensitive, so a legacy camelCase publisher
        // keeps binding against the same org DTO.
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

        var request = JsonSerializer.Deserialize<SnapshotRequest>(camelCaseWire, WebOptions);

        Assert.NotNull(request);

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("00675442A", message.AccountId);
        Assert.Equal("portfolio", message.SnapshotType);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("2026-05-22T06:10:14Z", message.PublishedAt);
        Assert.Equal("PortfolioCalculation", message.PublishedBy);
        Assert.Equal("""{"total":21}""", message.Payload);
    }

    [Fact]
    public void SnapshotRequest_tolerates_unknown_extra_properties()
    {
        // The org schema declares additionalProperties: true — extra fields must be
        // tolerated, never rejected.
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

        var request = JsonSerializer.Deserialize<SnapshotRequest>(wireWithExtras, WebOptions);

        Assert.NotNull(request);

        var message = SnapshotRequestMapper.ToDomain(request);

        Assert.Equal("corr98765", message.SnapshotId);
        Assert.Equal("orders", message.PayloadType);
        Assert.Equal("""{"total":21}""", message.Payload);
    }
}
