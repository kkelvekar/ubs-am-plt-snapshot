using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="ISnapshotIndexRepository"/>. Stateless — one
/// short-lived DbContext per call via the factory, scoped by <c>await using</c> so it is
/// disposed on every path (throwing query, throwing <c>apply</c>, failed save). A new
/// context per call does NOT mean a new physical SQL connection per call: EF opens the
/// pooled ADO.NET connection only for the query duration and returns it to the pool on
/// dispose, so this is connection-efficient on a hot consume loop. Do not reintroduce a
/// shared/held context.
/// </summary>
internal sealed class SnapshotIndexRepository : ISnapshotIndexRepository
{
    private readonly IDbContextFactory<SnapshotWriterDbContext> _contextFactory;

    public SnapshotIndexRepository(IDbContextFactory<SnapshotWriterDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task UpsertAsync(
        string snapshotId,
        Func<SnapshotIndexEntry?, SnapshotIndexEntry> apply,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.SnapshotIndex
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId, cancellationToken);

        var result = apply(existing);

        if (existing is null)
        {
            context.SnapshotIndex.Add(result);
        }
        else if (!ReferenceEquals(result, existing))
        {
            throw new InvalidOperationException(
                "apply must mutate and return the tracked instance on the update path.");
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
