using UBS.AM.PLT.Snapshot.Application.Contracts.Application;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// Resolves the caller-supplied route identifiers (SnapshotDetailRequest - 400-mappable
/// SnapshotDetailValidationException), resolves the snapshotId to its stored AdlsPath via
/// ISnapshotIndexQuery, and reads the payload blobs via ISnapshotPayloadQuery. The Api
/// controller only ever calls these two methods - it never talks to either port, the
/// validators or the composer directly. Payload text is passed through opaquely: it is only
/// ever returned as-is or embedded raw by SnapshotPayloadDocumentComposer.
/// </summary>
public sealed class PortfolioSnapshotDetailQueryHandler : IPortfolioSnapshotDetailQueryHandler
{
    private readonly ISnapshotIndexQuery _indexQuery;
    private readonly ISnapshotPayloadQuery _payloadQuery;

    public PortfolioSnapshotDetailQueryHandler(ISnapshotIndexQuery indexQuery, ISnapshotPayloadQuery payloadQuery)
    {
        _indexQuery = indexQuery;
        _payloadQuery = payloadQuery;
    }

    public async Task<string> GetPayloadAsync(string snapshotId, string payloadType, CancellationToken cancellationToken)
    {
        var resolvedSnapshotId = SnapshotDetailRequest.ResolveSnapshotId(snapshotId);
        var resolvedPayloadType = SnapshotDetailRequest.ResolvePayloadType(payloadType);

        var adlsPath = await ResolveAdlsPathAsync(resolvedSnapshotId, cancellationToken);

        var json = await _payloadQuery.ReadPayloadAsync(adlsPath, resolvedPayloadType, cancellationToken);

        return json ?? throw SnapshotNotFoundException.ForPayload(resolvedSnapshotId, resolvedPayloadType);
    }

    public async Task<string> GetAllPayloadsAsync(string snapshotId, CancellationToken cancellationToken)
    {
        var resolvedSnapshotId = SnapshotDetailRequest.ResolveSnapshotId(snapshotId);

        var adlsPath = await ResolveAdlsPathAsync(resolvedSnapshotId, cancellationToken);

        var payloads = await _payloadQuery.ReadAllPayloadsAsync(adlsPath, cancellationToken);

        if (payloads.Count == 0)
        {
            // An index row exists only for a COMPLETE snapshot, so an empty root folder is a
            // stored-data inconsistency rather than a server fault - the caller gets 404, not 500.
            throw SnapshotNotFoundException.ForNoPayloads(resolvedSnapshotId);
        }

        return SnapshotPayloadDocumentComposer.Compose(payloads);
    }

    private async Task<string> ResolveAdlsPathAsync(string resolvedSnapshotId, CancellationToken cancellationToken)
    {
        var adlsPath = await _indexQuery.GetAdlsPathAsync(resolvedSnapshotId, cancellationToken);

        return adlsPath ?? throw SnapshotNotFoundException.ForSnapshot(resolvedSnapshotId);
    }
}
