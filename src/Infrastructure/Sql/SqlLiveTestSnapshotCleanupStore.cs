using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.LiveTestCleanup;
using UBS.AM.PLT.Snapshot.Domain;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql;

public sealed class SqlLiveTestSnapshotCleanupStore(IDbContextFactory<SnapshotDbContext> contextFactory)
    : ILiveTestSnapshotCleanupStore
{
    public async Task<IReadOnlyList<LiveTestSnapshotLocation>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var tracking = await context.SnapshotTracking.AsNoTracking()
            .Where(row => row.SnapshotId.StartsWith(LiveTestSnapshot.IdPrefix))
            .Select(row => new LiveTestSnapshotLocation(row.SnapshotId, row.AccountId, row.AdlsRootPath))
            .ToListAsync(cancellationToken);
        var index = await context.PortfolioSnapshotIndex.AsNoTracking()
            .Where(row => row.SnapshotId.StartsWith(LiveTestSnapshot.IdPrefix))
            .Select(row => new LiveTestSnapshotLocation(row.SnapshotId, row.AccountId, row.AdlsPath))
            .ToListAsync(cancellationToken);
        return tracking.Concat(index)
            .Where(location => LiveTestSnapshot.IsTestId(location.SnapshotId)).Distinct().ToArray();
    }

    public async Task<int> DeleteAsync(string snapshotId, CancellationToken cancellationToken)
    {
        if (!LiveTestSnapshot.IsTestId(snapshotId))
        {
            throw new InvalidOperationException("Refusing to delete a non-test snapshot.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Binary collation keeps SQL's identity check as strict as the storage path check.
        var rows = await context.PortfolioSnapshotIndex
            .Where(row => EF.Functions.Collate(row.SnapshotId, "Latin1_General_100_BIN2") == snapshotId
                && EF.Functions.DataLength(row.SnapshotId) == snapshotId.Length)
            .ExecuteDeleteAsync(cancellationToken);
        rows += await context.SnapshotTracking
            .Where(row => EF.Functions.Collate(row.SnapshotId, "Latin1_General_100_BIN2") == snapshotId
                && EF.Functions.DataLength(row.SnapshotId) == snapshotId.Length)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
