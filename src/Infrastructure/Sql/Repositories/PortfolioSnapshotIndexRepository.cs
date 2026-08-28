using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Domain.Entities;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;

/// <summary>
/// EF Core data access for the snapshot_index table - both the write-side UPSERT
/// (IPortfolioSnapshotIndexRepository, used by SqlPortfolioSnapshotIndexStore) and the Load-snapshots
/// grid read query plus the snapshot-detail AdlsPath lookup (IPortfolioSnapshotIndexQuery, solution
/// design sections 7 and 10, used directly by the Api composition root). One class owns all
/// dbo.PortfolioSnapshotIndex data access rather than splitting read and write into separate
/// Infrastructure classes.
/// <para>
/// The read path uses Database.SqlQueryRaw&lt;PortfolioSnapshotIndexRow&gt; onto the keyless read DTO
/// so the DisplayData column comes back as its raw JSON string - this deliberately bypasses
/// the write-side PortfolioSnapshotIndexEntity value converter, which would otherwise round-trip
/// the JSON through the typed POCO and drop any display key not modelled on it. Every
/// account id becomes its own @pN parameter - ids are never concatenated into the SQL text.
/// </para>
/// Stateless - one short-lived DbContext per call via the factory, await using-scoped so
/// it is disposed on every path. A new context per call does NOT mean a new physical SQL
/// connection per call: EF opens the pooled ADO.NET connection only for the query duration
/// and returns it to the pool on dispose, so this is connection-efficient on a hot consume
/// loop. Do not reintroduce a shared/held context.
/// </summary>
internal sealed class PortfolioSnapshotIndexRepository : IPortfolioSnapshotIndexRepository, IPortfolioSnapshotIndexQuery
{
    private readonly IDbContextFactory<SnapshotDbContext> _contextFactory;

    public PortfolioSnapshotIndexRepository(IDbContextFactory<SnapshotDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task UpsertAsync(
        string snapshotId,
        Func<PortfolioSnapshotIndexEntity?, PortfolioSnapshotIndexEntity> apply,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.PortfolioSnapshotIndex
            .SingleOrDefaultAsync(e => e.SnapshotId == snapshotId, cancellationToken);

        var result = apply(existing);

        if (existing is null)
        {
            context.PortfolioSnapshotIndex.Add(result);
        }
        else if (!ReferenceEquals(result, existing))
        {
            throw new InvalidOperationException(
                "apply must mutate and return the tracked instance on the update path.");
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PortfolioSnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var sql = BuildQuery(filter, out var parameters);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Database
            .SqlQueryRaw<PortfolioSnapshotIndexRow>(sql, parameters)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetAdlsPathAsync(string snapshotId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // EF parameterises snapshotId, so the caller-supplied value is never in the SQL text.
        // A point lookup on the nonclustered primary key, and the single-column projection
        // keeps the DisplayData value converter out of the query entirely - unlike the grid
        // read, nothing here needs the raw JSON column.
        return await context.PortfolioSnapshotIndex
            .AsNoTracking()
            .Where(e => e.SnapshotId == snapshotId)
            .Select(e => e.AdlsPath)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the parameterised grid query. Each account id is bound to a positional
    /// @pN placeholder generated from its index (never from its value), and the optional
    /// filters are bound as named parameters - so no caller-supplied value is ever
    /// interpolated into the SQL text. The lower date bound, the upper date bound and the
    /// event type are three independent conditionals: each clause and its parameter appear
    /// together only when the caller supplied that value, and are omitted entirely otherwise.
    /// An absent bound is never bound as SQL NULL - a NULL comparison is never true, so that
    /// would silently return no rows at all. Internal for direct unit testing of the
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

        var sql =
            "SELECT SnapshotId, AccountId, SnapshotDate, EventType, AdlsPath, " +
            "DisplayData AS DisplayDataJson, CreatedAt " +
            "FROM dbo.PortfolioSnapshotIndex " +
            $"WHERE AccountId IN ({string.Join(", ", placeholders)})";

        if (filter.FromDate is not null)
        {
            sql += " AND SnapshotDate >= @from";
            sqlParameters.Add(new SqlParameter("@from", filter.FromDate.Value));
        }

        if (filter.ToDate is not null)
        {
            sql += " AND SnapshotDate <= @to";
            sqlParameters.Add(new SqlParameter("@to", filter.ToDate.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            sql += " AND EventType LIKE @event ESCAPE '~'";
            sqlParameters.Add(new SqlParameter("@event", $"%{EscapeLikePattern(filter.EventType)}%"));
        }

        sql += " ORDER BY SnapshotDate DESC";

        parameters = sqlParameters.ToArray();
        return sql;
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            .Replace("[", "~[", StringComparison.Ordinal);
}
