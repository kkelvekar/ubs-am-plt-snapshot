using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

namespace UBS.AM.PLT.Snapshot.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="ISnapshotPayloadQuery"/>: <see cref="Payloads"/> seeds single-file
/// reads by (adlsRootPath, payloadType) and <see cref="AllPayloads"/> seeds the all-files
/// read. <see cref="SingleReads"/> / <see cref="AllReads"/> record every call so a test can
/// assert the port was reached with the stored path verbatim - or never reached at all when
/// validation or the index lookup should have short-circuited first.
/// </summary>
internal sealed class FakeSnapshotPayloadQuery : ISnapshotPayloadQuery
{
    public Dictionary<(string AdlsRootPath, string PayloadType), string> Payloads { get; } = [];

    public List<SnapshotPayloadFile> AllPayloads { get; } = [];

    public List<(string AdlsRootPath, string PayloadType)> SingleReads { get; } = [];

    public List<string> AllReads { get; } = [];

    public Task<string?> ReadPayloadAsync(string adlsRootPath, string payloadType, CancellationToken cancellationToken)
    {
        SingleReads.Add((adlsRootPath, payloadType));
        Payloads.TryGetValue((adlsRootPath, payloadType), out var json);
        return Task.FromResult(json);
    }

    public Task<IReadOnlyList<SnapshotPayloadFile>> ReadAllPayloadsAsync(string adlsRootPath, CancellationToken cancellationToken)
    {
        AllReads.Add(adlsRootPath);
        return Task.FromResult<IReadOnlyList<SnapshotPayloadFile>>(AllPayloads);
    }
}
