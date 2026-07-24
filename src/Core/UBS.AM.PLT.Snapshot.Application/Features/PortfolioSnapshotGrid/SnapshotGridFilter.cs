namespace UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;

/// <summary>
/// The Load-snapshots grid query, in Application terms (solution design section 7). Built from
/// the HTTP request at the edge, then normalised by Resolve into a filter with a
/// concrete date window and validated account list before it reaches the query port.
/// </summary>
public sealed record SnapshotGridFilter
{
    /// <summary>Default look-back window applied when the caller omits From/To.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    public required IReadOnlyCollection<string> AccountIds { get; init; }

    public DateTime? FromDate { get; init; }

    public DateTime? ToDate { get; init; }

    public string? EventType { get; init; }

    /// <summary>
    /// Business rule for the grid query, deliberately free of ASP.NET/DB dependencies so it
    /// unit-tests in isolation. Rejects an empty account list (never allow an unfiltered
    /// all-rows query), fills an omitted date window to the last 7 days using the injected
    /// timeProvider (never DateTime.UtcNow), trims blanks out of the
    /// account list, and returns a normalised filter with a concrete From/To window.
    /// </summary>
    /// <exception cref="SnapshotGridFilterValidationException">
    /// Thrown when no non-blank account id is supplied, or when the resolved window is inverted.
    /// </exception>
    public static SnapshotGridFilter Resolve(SnapshotGridFilter filter, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var accountIds = (filter.AccountIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToArray();

        if (accountIds.Length == 0)
        {
            throw new SnapshotGridFilterValidationException(
                "At least one accountId is required; an unfiltered all-rows query is not allowed.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var toDate = filter.ToDate ?? now;
        var fromDate = filter.FromDate ?? toDate - DefaultWindow;

        if (fromDate > toDate)
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
