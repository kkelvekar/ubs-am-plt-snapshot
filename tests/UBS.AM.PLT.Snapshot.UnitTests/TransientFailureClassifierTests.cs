using Azure;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Resilience;
using UBS.AM.PLT.Snapshot.Infrastructure.Adls.Resilience;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Resilience;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Covers every registered <see cref="ITransientFailureClassifier"/> under the settled
/// inverted-polarity rule: a recognised infrastructure exception (SQL, blob) is transient
/// UNLESS its error is one a single message's own content can cause; every other exception type
/// (not a recognised infrastructure exception at all) still defaults to poison. SQL error
/// numbers are exercised via <see cref="SqlTransientFailureClassifier.IsTransientNumber"/> and
/// <see cref="SqlTransientFailureClassifier.IsTransientErrorNumbers"/> — constructing a real
/// <c>SqlException</c> is impractical, since its constructors are internal to
/// <c>Microsoft.Data.SqlClient</c> and not a stable surface to depend on from a test — while the
/// blob classifier and the shared inner-exception-chain walk are exercised directly, since
/// <c>RequestFailedException</c> is trivially constructible.
/// </summary>
public sealed class TransientFailureClassifierTests
{
    // Test 1: every number the old allow-list carried, still transient under the new default.
    [Theory]
    [InlineData(-2)]
    [InlineData(20)]
    [InlineData(64)]
    [InlineData(121)]
    [InlineData(233)]
    [InlineData(258)] // "wait operation timed out" -- the defect the inversion exists to fix
    [InlineData(1205)]
    [InlineData(4060)]
    [InlineData(10053)]
    [InlineData(10054)]
    [InlineData(10060)]
    [InlineData(10928)]
    [InlineData(10929)]
    [InlineData(40197)]
    [InlineData(40501)]
    [InlineData(40613)]
    [InlineData(49918)]
    [InlineData(49919)]
    [InlineData(49920)]
    public void Sql_KnownInfrastructureNumbers_AreTransient(int number)
        => Assert.True(SqlTransientFailureClassifier.IsTransientNumber(number));

    // Test 2: the regression test for the inversion itself. An unlisted number must default to
    // transient -- must fail the moment anyone reintroduces an allow-list.
    [Theory]
    [InlineData(615)]
    [InlineData(926)]
    [InlineData(4221)]
    [InlineData(17142)]
    [InlineData(42108)]
    [InlineData(42109)]
    [InlineData(987654)]
    public void Sql_UnlistedNumbers_DefaultToTransient(int number)
        => Assert.True(SqlTransientFailureClassifier.IsTransientNumber(number));

    // Test 3: the fixed, enumerable content-caused set is the only thing that classifies false.
    [Theory]
    [InlineData(245)]
    [InlineData(515)]
    [InlineData(547)]
    [InlineData(2601)]
    [InlineData(2627)]
    [InlineData(2628)]
    [InlineData(8114)]
    [InlineData(8152)]
    public void Sql_ContentCausedNumbers_AreNotTransient(int number)
        => Assert.False(SqlTransientFailureClassifier.IsTransientNumber(number));

    // Test 4: multi-error rule -- ANY content-caused number in the set makes the whole
    // exception non-transient; driven through the internal seam with a stand-in sequence
    // standing in for SqlException.Errors, since SqlException itself is not constructible here.
    [Fact]
    public void Sql_MultiError_AnyContentCausedNumberIsNonTransient()
        => Assert.False(SqlTransientFailureClassifier.IsTransientErrorNumbers([10054, 2627]));

    [Fact]
    public void Sql_MultiError_AllTransientNumbersIsTransient()
        => Assert.True(SqlTransientFailureClassifier.IsTransientErrorNumbers([10054, 10060]));

    // Test 5 (replaces old test 12): the shared ExceptionChainWalker, proven with a
    // discriminating pair so the inverted default can't make the assertion vacuous. SQL's own
    // exception type isn't constructible here (see the class summary), so the walk -- which
    // both classifiers share -- is proven via the blob classifier, whose exception type is
    // trivially constructible.
    [Fact]
    public void ChainWalk_DbUpdateExceptionWrappingTransientBlobStatus_IsTransient()
    {
        var classifier = new BlobTransientFailureClassifier();
        var inner = new RequestFailedException(503, "throttled");
        var wrapped = new DbUpdateException("save failed", inner);

        Assert.True(classifier.IsTransient(wrapped));
    }

    [Fact]
    public void ChainWalk_DbUpdateExceptionWrappingContentCausedBlobStatus_IsNotTransient()
    {
        var classifier = new BlobTransientFailureClassifier();
        var inner = new RequestFailedException(404, "header blob absent");
        var wrapped = new DbUpdateException("save failed", inner);

        Assert.False(classifier.IsTransient(wrapped));
    }

    // Test 6 (rewrite of old test 10): blob statuses under the inverted default.
    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(403, true)] // open default: an identity/config fault affects every message
    [InlineData(401, true)]
    [InlineData(507, true)]
    [InlineData(400, false)] // content-caused: an unusable path built from this message
    [InlineData(404, false)] // content-caused: this snapshot's own data state
    public void Blob_ClassifiesByStatus(int status, bool expectedTransient)
    {
        var classifier = new BlobTransientFailureClassifier();
        var exception = new RequestFailedException(status, "blob request failed");

        Assert.Equal(expectedTransient, classifier.IsTransient(exception));
    }

    [Fact]
    public void Network_TimeoutExceptionIsTransient()
    {
        var classifier = new NetworkTransientFailureClassifier();
        Assert.True(classifier.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void Network_TaskCanceledExceptionIsTransient()
    {
        var classifier = new NetworkTransientFailureClassifier();
        Assert.True(classifier.IsTransient(new TaskCanceledException()));
    }

    [Fact]
    public void Network_UnrelatedExceptionIsNotTransient()
    {
        var classifier = new NetworkTransientFailureClassifier();
        Assert.False(classifier.IsTransient(new InvalidOperationException("not network related")));
    }

    // Test 7 (old test 13, unchanged): the settled default survives the inversion. An exception
    // that is not a recognised infrastructure exception at all -- JsonException,
    // NullReferenceException, InvalidOperationException, KeyNotFoundException, ArgumentException,
    // and so on -- is still not transient and still reaches poison.
    [Fact]
    public void Aggregation_ExceptionNotARecognisedInfrastructureException_DefaultsToPoison()
    {
        ITransientFailureClassifier[] classifiers =
        [
            new NetworkTransientFailureClassifier(),
            new SqlTransientFailureClassifier(),
            new BlobTransientFailureClassifier(),
        ];

        var unmatched = new InvalidOperationException("plain code defect, no infrastructure exception in its chain");

        Assert.DoesNotContain(classifiers, c => c.IsTransient(unmatched));
    }

    [Fact]
    public void Aggregation_TransientBlobFailure_IsTransient()
    {
        ITransientFailureClassifier[] classifiers =
        [
            new NetworkTransientFailureClassifier(),
            new SqlTransientFailureClassifier(),
            new BlobTransientFailureClassifier(),
        ];

        var transientBlobFailure = new RequestFailedException(503, "throttled");

        Assert.Contains(classifiers, c => c.IsTransient(transientBlobFailure));
    }
}
