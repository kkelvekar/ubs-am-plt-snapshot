using Microsoft.Data.SqlClient;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Verifies SnapshotIndexRepository.BuildQuery parameterises the account filter -
/// one @pN placeholder per id, generated from the index, with the raw id carried as a
/// parameter value and never concatenated into the SQL text. This is the SQL-injection guard.
/// </summary>
public sealed class SnapshotIndexRepositoryQueryTests
{
    private static readonly DateTime From = new(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Account_ids_are_bound_as_parameters_not_concatenated()
    {
        const string malicious = "'; DROP TABLE dbo.PortfolioSnapshotIndex; --";
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A", malicious],
            FromDate = From,
            ToDate = To,
        };

        var sql = SnapshotIndexRepository.BuildQuery(filter, out var parameters);

        // Placeholders are positional, derived from the index - never from the value.
        Assert.Contains("AccountId IN (@p0, @p1)", sql);

        // The malicious literal must NOT appear anywhere in the SQL text.
        Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain(malicious, sql);

        // It is carried as a parameter value instead.
        Assert.Equal("A", ParameterValue(parameters, "@p0"));
        Assert.Equal(malicious, ParameterValue(parameters, "@p1"));

        // Date window bound as named parameters.
        Assert.Equal(From, ParameterValue(parameters, "@from"));
        Assert.Equal(To, ParameterValue(parameters, "@to"));

        // No event filter supplied -> no @event parameter and no clause.
        Assert.DoesNotContain("@event", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@event");
    }

    [Fact]
    public void Event_type_adds_a_single_parameterised_clause()
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A"],
            FromDate = From,
            ToDate = To,
            EventType = "ModelChange",
        };

        var sql = SnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.Contains("AND EventType = @event", sql);
        Assert.Equal("ModelChange", ParameterValue(parameters, "@event"));
    }

    [Fact]
    public void Query_selects_display_data_aliased_and_orders_by_date_desc()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["A"], FromDate = From, ToDate = To };

        var sql = SnapshotIndexRepository.BuildQuery(filter, out _);

        Assert.Contains("DisplayData AS DisplayDataJson", sql);
        Assert.Contains("FROM dbo.PortfolioSnapshotIndex", sql);
        Assert.EndsWith("ORDER BY SnapshotDate DESC", sql);
    }

    private static object? ParameterValue(SqlParameter[] parameters, string name) =>
        Array.Find(parameters, p => p.ParameterName == name)?.Value;
}
