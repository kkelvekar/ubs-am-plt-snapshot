using Azure;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Resilience;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Adls.Resilience;

/// <summary>
/// Classifies a <see cref="RequestFailedException"/> anywhere in the exception's
/// inner-exception chain by its HTTP <see cref="RequestFailedException.Status"/>.
/// </summary>
/// <remarks>
/// Inverted polarity, deliberately, matching <c>SqlTransientFailureClassifier</c>: a recognised
/// <see cref="RequestFailedException"/> is transient UNLESS its status is one this message or
/// its snapshot's own state can cause. Every other status — including 403 — is transient: an
/// expired or misconfigured identity fails every message identically, and marching through the
/// topic writing FAILED rows for each one would be mass loss from a single config fault.
/// Parking and restarting is loud, loses nothing, and is the correct response.
/// </remarks>
public sealed class BlobTransientFailureClassifier : ITransientFailureClassifier
{
    /// <summary>
    /// Blob statuses caused by this message or its snapshot's own state, deterministic and
    /// therefore poison. Every other <see cref="RequestFailedException"/> status is transient.
    /// </summary>
    private static readonly HashSet<int> ContentCausedStatuses = [400, 404];

    public bool IsTransient(Exception exception)
        => ExceptionChainWalker.AnyInChain(
            exception,
            static ex => ex is RequestFailedException requestFailedEx && IsTransientStatus(requestFailedEx.Status));

    /// <summary>
    /// 400 — an unusable blob path built from this message's envelope. 404 — the header blob
    /// absent at completion time, this snapshot's own data state. Everything else, including
    /// 401/403/407 (identity/config faults affecting every message) and 429/5xx (throttling and
    /// server-side faults), is transient.
    /// </summary>
    internal static bool IsTransientStatus(int status) => !ContentCausedStatuses.Contains(status);
}
