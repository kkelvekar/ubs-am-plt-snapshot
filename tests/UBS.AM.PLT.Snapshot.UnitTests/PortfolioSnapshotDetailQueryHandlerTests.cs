using System.Text.Json;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;
using UBS.AM.PLT.Snapshot.UnitTests.Fakes;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Business rules for the snapshot-detail read (solution design section 10, Screen 2) — no
/// ASP.NET, no SQL, no blob client. Guards that route identifiers are validated before either
/// port is touched (nothing path-unsafe can ever reach the blob client), that a missing index
/// row or missing blob surfaces as not-found rather than a fault, and that stored payload text
/// is handed back byte-for-byte.
/// </summary>
public sealed class PortfolioSnapshotDetailQueryHandlerTests
{
    private const string SnapshotId = "s1";
    private const string AdlsPath = "portfolio_snapshots/year=2026/month=07/accountId=A1/snapshotId=s1";

    private readonly FakePortfolioSnapshotIndexQuery _indexQuery = new();
    private readonly FakeSnapshotPayloadQuery _payloadQuery = new();

    private PortfolioSnapshotDetailQueryHandler CreateHandler() => new(_indexQuery, _payloadQuery);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_snapshot_id_is_rejected_by_both_methods(string? snapshotId)
    {
        var handler = CreateHandler();

        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetPayloadAsync(snapshotId!, "header", CancellationToken.None));
        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetAllPayloadsAsync(snapshotId!, CancellationToken.None));

        // Rejected at the edge of the use case - neither port was reached.
        Assert.Empty(_indexQuery.AdlsPathLookups);
        Assert.Empty(_payloadQuery.SingleReads);
        Assert.Empty(_payloadQuery.AllReads);
    }

    [Fact]
    public async Task Over_long_snapshot_id_is_rejected()
    {
        var handler = CreateHandler();
        var tooLong = new string('s', 101);

        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetPayloadAsync(tooLong, "header", CancellationToken.None));
        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetAllPayloadsAsync(tooLong, CancellationToken.None));

        Assert.Empty(_indexQuery.AdlsPathLookups);
    }

    [Theory]
    [InlineData("../header")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("header.json")]
    [InlineData("he ader")]
    [InlineData("")]
    public async Task Path_unsafe_payload_type_is_rejected(string payloadType)
    {
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        var handler = CreateHandler();

        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetPayloadAsync(SnapshotId, payloadType, CancellationToken.None));

        // No traversal sequence can reach the blob client.
        Assert.Empty(_payloadQuery.SingleReads);
    }

    [Fact]
    public async Task Over_long_payload_type_is_rejected()
    {
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        var handler = CreateHandler();

        await Assert.ThrowsAsync<SnapshotDetailValidationException>(
            () => handler.GetPayloadAsync(SnapshotId, new string('p', 101), CancellationToken.None));

        Assert.Empty(_payloadQuery.SingleReads);
    }

    [Fact]
    public async Task Unknown_snapshot_id_is_not_found()
    {
        var handler = CreateHandler();

        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => handler.GetPayloadAsync(SnapshotId, "header", CancellationToken.None));
        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => handler.GetAllPayloadsAsync(SnapshotId, CancellationToken.None));

        // Only a COMPLETE snapshot has an index row, so the blob read never happens.
        Assert.Empty(_payloadQuery.SingleReads);
        Assert.Empty(_payloadQuery.AllReads);
    }

    [Fact]
    public async Task Missing_payload_for_known_snapshot_is_not_found()
    {
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        var handler = CreateHandler();

        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => handler.GetPayloadAsync(SnapshotId, "portfolio", CancellationToken.None));

        Assert.Equal([(AdlsPath, "portfolio")], _payloadQuery.SingleReads);
    }

    [Fact]
    public async Task Single_payload_is_returned_verbatim_from_the_stored_path()
    {
        const string stored = """{"nav": 5555.50,  "note":"x"}""";
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        _payloadQuery.Payloads[(AdlsPath, "header")] = stored;
        var handler = CreateHandler();

        // Identifiers arrive padded: they must be trimmed before either port sees them.
        var json = await handler.GetPayloadAsync($"  {SnapshotId} ", " header ", CancellationToken.None);

        Assert.Equal(stored, json);
        Assert.Equal([SnapshotId], _indexQuery.AdlsPathLookups);
        // The stored AdlsPath is used verbatim, never recomputed.
        Assert.Equal([(AdlsPath, "header")], _payloadQuery.SingleReads);
    }

    [Fact]
    public async Task All_payloads_compose_one_document_embedding_each_stored_text_verbatim()
    {
        const string headerJson = """{"nav": 5555.50,  "note":"x"}""";
        const string ordersJson = """[{"id":1},{"id":2}]""";
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        _payloadQuery.AllPayloads.Add(new SnapshotPayloadFile { PayloadType = "header", Json = headerJson });
        _payloadQuery.AllPayloads.Add(new SnapshotPayloadFile { PayloadType = "orders", Json = ordersJson });
        var handler = CreateHandler();

        var json = await handler.GetAllPayloadsAsync(SnapshotId, CancellationToken.None);

        // Load-bearing formatting survives byte-for-byte: trailing zero, internal double space.
        Assert.Contains(headerJson, json, StringComparison.Ordinal);
        Assert.Contains(ordersJson, json, StringComparison.Ordinal);
        Assert.Equal([AdlsPath], _payloadQuery.AllReads);

        // JsonDocument is allowed in tests only - production code never parses a payload.
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.True(document.RootElement.TryGetProperty("header", out _));
        Assert.True(document.RootElement.TryGetProperty("orders", out _));
    }

    [Fact]
    public async Task Snapshot_with_no_stored_payloads_is_not_found()
    {
        _indexQuery.AdlsPaths[SnapshotId] = AdlsPath;
        var handler = CreateHandler();

        // An index row exists only for a COMPLETE snapshot, so an empty folder is a stored-data
        // inconsistency - still 404 for the caller, never a 500.
        await Assert.ThrowsAsync<SnapshotNotFoundException>(
            () => handler.GetAllPayloadsAsync(SnapshotId, CancellationToken.None));
    }
}
