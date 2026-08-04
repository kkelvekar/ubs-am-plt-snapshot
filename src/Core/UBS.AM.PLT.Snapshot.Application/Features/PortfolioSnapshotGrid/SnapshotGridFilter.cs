namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// The Load-snapshots grid query, in Application terms (solution design section 7). Built from
/// the HTTP request at the edge, then normalised by Resolve into a filter with a validated
/// account list before it reaches the query port. AccountIds is the only mandatory filter;
/// the API applies no implicit default to the date window (a last-7-days view is a UI
/// convention: the client sends explicit From/To when it wants one).
/// </summary>
public sealed record SnapshotGridFilter
{
    public required IReadOnlyCollection<string> AccountIds { get; init; }

    public DateTime? FromDate { get; init; }

    public DateTime? ToDate { get; init; }

    public string? EventType { get; init; }

    /// <summary>
    /// Business rule for the grid query, deliberately free of ASP.NET/DB dependencies so it
    /// unit-tests in isolation. Rejects an empty account list (never allow an unfiltered
    /// all-rows query) and trims blanks out of it. From, To and EventType are carried through
    /// untouched when supplied and left null when omitted - an omitted bound is an open bound,
    /// never a defaulted one, so a one-sided window is valid and an absent window filters
    /// nothing at all.
    /// </summary>
    /// <exception cref="SnapshotGridFilterValidationException">
    /// Thrown when no non-blank account id is supplied, or when both bounds are supplied and
    /// the window is inverted.
    /// </exception>
    public static SnapshotGridFilter Resolve(SnapshotGridFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var accountIds = (filter.AccountIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();

        if (accountIds.Length == 0)
        {
            throw new SnapshotGridFilterValidationException(
                "At least one accountId is required; an unfiltered all-rows query is not allowed.");
        }

        var fromDate = filter.FromDate;
        var toDate = filter.ToDate;

        if (fromDate is not null && toDate is not null && fromDate > toDate)
        {
            throw new SnapshotGridFilterValidationException(
                $"fromDate ({fromDate:o}) must not be after toDate ({toDate:o}).");
        }

        var eventType = string.IsNullOrWhiteSpace(filter.EventType) ? null : filter.EventType.Trim();

        return new SnapshotGridFilter
        {
            AccountIds = accountIds,
            FromDate = fromDate,
            ToDate = toDate,
            EventType = eventType,
        };
    }
}
