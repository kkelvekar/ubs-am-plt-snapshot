namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// Thrown by SnapshotGridFilter.Resolve when the caller filter fails a grid business
/// rule (empty account list, inverted date window). The Api edge maps this to 400 BadRequest.
/// </summary>
public sealed class SnapshotGridFilterValidationException : Exception
{
    public SnapshotGridFilterValidationException(string message) : base(message)
    {
    }
}
