using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;

namespace UBS.AM.PLT.Snapshot.Application.Resilience;

/// <summary>
/// BCL-only transient classifier: no vendor package reference, so it belongs beside the
/// <see cref="ITransientFailureClassifier"/> port itself rather than in a vendor adapter.
/// Covers timeouts and cancellations raised by generic .NET I/O (not by <c>Microsoft.Data.SqlClient</c>
/// or <c>Azure.*</c>, which have their own vendor-specific classifiers).
/// </summary>
public sealed class NetworkTransientFailureClassifier : ITransientFailureClassifier
{
    public bool IsTransient(Exception exception)
        => ExceptionChainWalker.AnyInChain(exception, static ex => ex is TimeoutException or TaskCanceledException);
}
