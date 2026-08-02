namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// One stored payload blob of a snapshot, as returned by the read port (solution design
/// section 10, Screen 2). <see cref="Json"/> is the raw blob text, byte-identical to what the
/// write path stored - it is carried as an opaque string and is never deserialised into a DTO.
/// </summary>
public sealed class SnapshotPayloadFile
{
    /// <summary>The <c>{payloadType}</c> segment of the blob filename, without the .json suffix.</summary>
    public required string PayloadType { get; init; }

    public required string Json { get; init; }
}
