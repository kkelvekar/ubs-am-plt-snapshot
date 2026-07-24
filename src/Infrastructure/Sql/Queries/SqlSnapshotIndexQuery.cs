using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using UBS.AM.PLT.Snapshot.Application.Contracts.Infrastructure;
using UBS.AM.PLT.Snapshot.Application.Models;

namespace UBS.AM.PLT.Snapshot.Infrastructure.Sql.Queries;

/// <summary>
/// SQL adapter for <see cref="ISnapshotIndexQuery"/> (solution design §7 grid query). Reads
/// via <c>Database.SqlQueryRaw&lt;SnapshotIndexRow&gt;</c> onto the keyless read DTO so the
/// <c>DisplayData</c> column comes back as its raw JSON string — this deliberately bypasses
/// the write-side <c>SnapshotIndexEntity</c> value converter, which would otherwise round-trip
/// the JSON through the typed POCO and drop any display key not modelled on it.
/// <para>
/// Stateless singleton over the pooled <see cref="IDbContextFactory{TContext}"/>: one
/// short-lived context per call, <c>await using</c>-scoped, same pattern as the write
/// repositories. Every account id becomes its own <c>@pN</c> parameter — ids are never
/// concatenated into the SQL text.
/// </para>
/// </summary>
internal sealed class SqlSnapshotIndexQuery : ISnapshotIndexQuery
{
    private readonly IDbContextFactory<SnapshotDbContext> _contextFactory;

    public SqlSnapshotIndexQuery(IDbContextFactory<SnapshotDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<IReadOnlyList<SnapshotIndexRow>> QueryAsync(SnapshotGridFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var sql = BuildQuery(filter, out var parameters);

        await using var context = await _contextFactory.CreateDbContextAsync(ct);

        return await context.Database
            .SqlQueryRaw<SnapshotIndexRow>(sql, parameters)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Builds the parameterised grid query. Each account id is bound to a positional
    /// <c>@pN</c> placeholder generated from its index (never from its value), and the date
    /// window / optional event type are bound as named parameters — so no caller-supplied
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
