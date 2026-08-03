using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// The all-payloads composer must embed stored blob text raw (AGENTS.md invariant 5): the
/// bytes go into the response exactly as stored, so number formatting, internal whitespace and
/// key order all survive. A round-trip through a DTO or a re-serialise would break every
/// assertion here.
/// </summary>
public sealed class SnapshotPayloadDocumentComposerTests
{
    [Fact]
    public void Stored_text_is_embedded_verbatim()
    {
        // Every detail here is destroyed by a parse-and-re-serialise: the trailing zero on the
        // number, the double space after the comma, and the declared key order.
        const string stored = """{"nav": 5555.50,  "zeta":"z","alpha":"a"}""";
        List<SnapshotPayloadFile> payloads = [new() { PayloadType = "header", Json = stored }];

        var json = SnapshotPayloadDocumentComposer.Compose(payloads);

        Assert.Contains(stored, json, StringComparison.Ordinal);
        Assert.Equal($$"""{"header":{{stored}}}""", json);
    }

    [Fact]
    public void Every_payload_becomes_one_property_keyed_by_payload_type()
    {
        List<SnapshotPayloadFile> payloads =
        [
            new() { PayloadType = "header", Json = """{"a":1}""" },
            new() { PayloadType = "orders", Json = """[1,2,3]""" },
            new() { PayloadType = "calculations", Json = """{"b":[{"c":null}]}""" },
        ];

        var json = SnapshotPayloadDocumentComposer.Compose(payloads);

        // JsonDocument is allowed in tests only - production code never parses a payload.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("header").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("orders").ValueKind);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("calculations").ValueKind);
    }

    [Fact]
    public void Empty_list_composes_an_empty_object()
    {
        Assert.Equal("{}", SnapshotPayloadDocumentComposer.Compose([]));
    }

    [Fact]
    public void Invalid_stored_payload_throws_rather_than_composing_a_corrupt_document()
    {
        List<SnapshotPayloadFile> payloads = [new() { PayloadType = "header", Json = "{oops" }];

        // The write path syntax-checks every payload before storing, so invalid stored text is
        // corruption. It must surface as a server fault (500 via the global handler), never as
        // a 200 carrying a broken body.
        Assert.ThrowsAny<Exception>(() => SnapshotPayloadDocumentComposer.Compose(payloads));
    }
}
