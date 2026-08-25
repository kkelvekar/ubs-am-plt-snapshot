namespace UBS.AM.PLT.Snapshot.Application.Resilience;

/// <summary>
/// Shared inner-exception-chain walk for every <c>ITransientFailureClassifier</c>
/// implementation (this project's <see cref="NetworkTransientFailureClassifier"/>, plus the
/// SQL and blob classifiers in Infrastructure.Sql / Infrastructure.Adls, both of which already
/// reference this project). One copy, so the walk is written once rather than three times.
/// EF Core wraps <c>SqlException</c> in <c>DbUpdateException</c> on <c>SaveChangesAsync</c>, so
/// classifying only the top-level exception would misclassify a real SQL outage as poison.
/// </summary>
public static class ExceptionChainWalker
{
    /// <summary>
    /// True when <paramref name="exception"/> or any exception in its
    /// <see cref="Exception.InnerException"/> chain satisfies <paramref name="predicate"/>.
    /// </summary>
    public static bool AnyInChain(Exception exception, Func<Exception, bool> predicate)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (predicate(current))
            {
                return true;
            }
        }

        return false;
    }
}
