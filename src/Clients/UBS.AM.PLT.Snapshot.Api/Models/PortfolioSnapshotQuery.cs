namespace UBS.AM.PLT.Snapshot.Api.Models;

/// <summary>
/// HTTP request DTO for <c>GET /api/portfolio-snapshots</c>. Binds the grid query string:
/// <c>?accountIds=..&amp;accountIds=..&amp;from=..&amp;to=..&amp;event=..</c>. Mapped to the
/// Application-layer <c>SnapshotGridFilter</c> in the controller; carries no behaviour.
/// </summary>
public sealed class PortfolioSnapshotQuery
{
    public string[] AccountIds { get; init; } = [];

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    public string? Event { get; init; }
}
