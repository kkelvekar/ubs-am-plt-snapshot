namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotDetail;

/// <summary>
/// Thrown by SnapshotDetailRequest when a caller-supplied identifier fails a snapshot-detail
/// business rule (blank, over-long, or path-unsafe payload type). The message is caller-safe -
/// it echoes only the rule and values the caller already supplied. The Api edge maps this to
/// 400 BadRequest.
/// </summary>
public sealed class SnapshotDetailValidationException : Exception
{
    public SnapshotDetailValidationException(string message) : base(message)
    {
    }
}
