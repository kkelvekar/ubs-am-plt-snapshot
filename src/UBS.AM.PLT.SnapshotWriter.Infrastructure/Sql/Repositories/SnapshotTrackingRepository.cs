using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.SnapshotWriter.Domain.Entities;

namespace UBS.AM.PLT.SnapshotWriter.Infrastructure.Sql.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISnapshotTrackingRepository"/>. Stateless — one
/// short-lived DbContext per call via the factory, scoped by <c>await using</c> so it is
/// disposed on every path (throwing query, throwing <c>apply</c>, failed save). A new
/// context per call does NOT mean a new physical SQL connection per call: EF opens the
/// pooled ADO.NET connection only for the query duration and returns it to the pool on
/// dispose, so this is connection-efficient on a hot consume loop. Do not reintroduce a
/// shared/held context.
/// </summary>
internal sealed class SnapshotTrackingRepository : ISnapshotTrackingRepository
{
    private readonly IDbContextFactory<SnapshotWriterDbContext> _contextFactory;

    public SnapshotTrackingRepository(IDbContextFactory<SnapshotWriterDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<SnapshotTrackingEntity> UpsertAsync(
        string snapshotId,
        Func<SnapshotTrackingEntity?, SnapshotTrackingEntity> apply,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.SnapshotTracking
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId, cancellationToken);

        var result = apply(existing);

        if (existing is null)
        {
            context.SnapshotTracking.Add(result);
        }
        else if (!ReferenceEquals(result, existing))
        {
            throw new InvalidOperationException(
                "apply must mutate and return the tracked instance on the update path.");
        }

        await context.SaveChangesAsync(cancellationToken);

        return result;
    }

    public async Task<string?> FindRootPathAsync(string snapshotId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.SnapshotTracking
            .AsNoTracking()
            .Where(e => e.SnapshotId == snapshotId)
            .Select(e => e.AdlsRootPath)
            .SingleOrDefaultAsync(cancellationToken);
    }
}
