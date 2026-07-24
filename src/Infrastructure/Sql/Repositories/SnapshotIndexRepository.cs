using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

/// <summary>
/// EF Core data access for the snapshot_index table - both the write-side UPSERT
/// (ISnapshotIndexRepository, used by SqlSnapshotIndexStore) and the Load-snapshots
/// grid read query (ISnapshotIndexQuery, solution design section 7, used directly by the
/// Api composition root). One class owns all dbo.SnapshotIndex data access rather than
/// splitting read and write into separate Infrastructure classes.
/// <para>
/// The read path uses Database.SqlQueryRaw&lt;SnapshotIndexRow&gt; onto the keyless read DTO
/// so the DisplayData column comes back as its raw JSON string - this deliberately bypasses
/// the write-side SnapshotIndexEntity value converter, which would otherwise round-trip
/// the JSON through the typed POCO and drop any display key not modelled on it. Every
/// account id becomes its own @pN parameter - ids are never concatenated into the SQL text.
/// </para>
/// Stateless - one short-lived DbContext per call via the factory, await using-scoped so
/// it is disposed on every path. A new context per call does NOT mean a new physical SQL
/// connection per call: EF opens the pooled ADO.NET connection only for the query duration
/// and returns it to the pool on dispose, so this is connection-efficient on a hot consume
/// loop. Do not reintroduce a shared/held context.
/// </summary>
internal sealed class SnapshotIndexRepository : ISnapshotIndexRepository, ISnapshotIndexQuery
{
    private readonly IDbContextFactory<SnapshotDbContext> _contextFactory;

    public SnapshotIndexRepository(IDbContextFactory<SnapshotDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task UpsertAsync(
        string snapshotId,
        Func<SnapshotIndexEntity?, SnapshotIndexEntity> apply,
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

    public async Task<IReadOnlyList<SnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var sql = BuildQuery(filter, out var parameters);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Database
            .SqlQueryRaw<SnapshotIndexRow>(sql, parameters)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the parameterised grid query. Each account id is bound to a positional
    /// @pN placeholder generated from its index (never from its value), and the date
    /// window / optional event type are bound as named parameters - so no caller-supplied
    /// value is ever interpolated into the SQL text. Internal for direct unit testing of the
    /// parameterisation.
    /// </summary>
    internal static string BuildQuery(SnapshotGridFilter filter, out SqlParameter[] parameters)
    {
        var accountIds = filter.AccountIds.ToArray();
        if (accountIds.Length == 0)
        {
            throw new ArgumentException("Resolved filter must contain at least one accountId.", nameof(filter));
        }

        var placeholders = new string[accountIds.Length];
        var sqlParameters = new List<SqlParameter>(accountIds.Length + 3);
        for (var i = 0; i < accountIds.Length; i++)
        {
            var name = $"@p{i}";
            placeholders[i] = name;
            sqlParameters.Add(new SqlParameter(name, accountIds[i]));
        }

        sqlParameters.Add(new SqlParameter("@from", filter.FromDate ?? (object)DBNull.Value));
        sqlParameters.Add(new SqlParameter("@to", filter.ToDate ?? (object)DBNull.Value));

        var sql =
            "SELECT SnapshotId, AccountId, SnapshotDate, EventType, AdlsPath, " +
            "DisplayData AS DisplayDataJson, CreatedAt " +
            "FROM dbo.SnapshotIndex " +
            $"WHERE AccountId IN ({string.Join(", ", placeholders)}) " +
            "AND SnapshotDate >= @from AND SnapshotDate <= @to";

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            sql += " AND EventType = @event";
            sqlParameters.Add(new SqlParameter("@event", filter.EventType));
        }

        sql += " ORDER BY SnapshotDate DESC";

        parameters = sqlParameters.ToArray();
        return sql;
    }
}
