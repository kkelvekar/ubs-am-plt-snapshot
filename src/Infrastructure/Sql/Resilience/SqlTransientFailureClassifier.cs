using System.Collections.Frozen;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Resilience;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.Resilience;

/// <summary>
/// Classifies a <see cref="SqlException"/> anywhere in the exception's inner-exception chain.
/// EF Core wraps <see cref="SqlException"/> in <see cref="DbUpdateException"/> on
/// <c>SaveChangesAsync</c>, so the chain walk (via <see cref="ExceptionChainWalker"/>) is what
/// makes a real SQL outage classify correctly instead of falling through to poison.
/// </summary>
/// <remarks>
/// Inverted polarity, deliberately: a recognised <see cref="SqlException"/> is transient UNLESS
/// its error is one a single message's own content can cause (a bad value, a constraint
/// violation) — those are enumerable against a fixed hand-written schema. Every other SQL
/// failure affects every message equally, so parking and restarting is the correct, loud
/// response rather than an allow-list that can never be complete and silently commits an
/// unrecognised infrastructure blip past a message forever.
/// </remarks>
public sealed class SqlTransientFailureClassifier : ITransientFailureClassifier
{
    /// <summary>
    /// SQL errors a single message's own content can cause. These are deterministic: the same
    /// bytes fail identically forever, so they are poison and the offset must move past them.
    /// EVERY other SqlException number is treated as transient — including numbers not listed
    /// anywhere here (258 "wait operation timed out", -2, 1205, 10053/10054/10060,
    /// 10928/10929, 40197/40501/40613, 4060, 49918/49919/49920, 233, 121, 64, 20, and any
    /// future or local-SQL-Server-specific code). An unknown SQL failure must never be
    /// committed away: losing a message to a transient blip is the exact defect this
    /// classifier exists to prevent, and an allow-list of transient numbers cannot be
    /// complete.
    /// </summary>
    private static readonly FrozenSet<int> ContentCausedNumbers = FrozenSet.ToFrozenSet(
    [
        245,  // Conversion failed when converting a value
        515,  // Cannot insert NULL into a non-nullable column
        547,  // Constraint violation (check / foreign key)
        2601, // Cannot insert duplicate key row in an index with a unique index
        2627, // Violation of PRIMARY KEY or UNIQUE constraint
        2628, // String or binary data would be truncated (with column detail)
        8114, // Error converting data type
        8152, // String or binary data would be truncated
    ]);

    public bool IsTransient(Exception exception)
        => ExceptionChainWalker.AnyInChain(exception, static ex => ex is SqlException sqlEx && IsTransientSqlException(sqlEx));

    /// <summary>
    /// Non-transient if ANY entry in <see cref="SqlException.Errors"/> is content-caused;
    /// otherwise transient. The whole collection is scanned, not just
    /// <see cref="SqlException.Number"/> (which is only <c>Errors[0].Number</c>): a truncation
    /// error accompanied by transport noise must not park-and-retry forever, and a genuinely
    /// unrecognised error must not hide behind an unrelated first entry.
    /// </summary>
    private static bool IsTransientSqlException(SqlException sqlException)
        => IsTransientErrorNumbers(sqlException.Errors.Cast<SqlError>().Select(static error => error.Number));

    /// <summary>Extracted for direct unit testing — constructing a real <see cref="SqlException"/> is impractical.</summary>
    internal static bool IsTransientNumber(int number) => !ContentCausedNumbers.Contains(number);

    /// <summary>
    /// The multi-error aggregation rule over a plain sequence of error numbers, extracted
    /// alongside <see cref="IsTransientNumber"/> so it too is directly unit-testable with a
    /// stand-in sequence rather than a real, unconstructible <see cref="SqlErrorCollection"/>.
    /// </summary>
    internal static bool IsTransientErrorNumbers(IEnumerable<int> numbers) => numbers.All(IsTransientNumber);
}
