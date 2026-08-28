using Microsoft.Data.SqlClient;
using UBS.AM.PLT.Snapshot.Application.Features.PortfolioSnapshotGrid;
using UBS.AM.PLT.Snapshot.Infrastructure.Sql.Repositories;
using Xunit;

namespace UBS.AM.PLT.Snapshot.UnitTests;

/// <summary>
/// Verifies PortfolioSnapshotIndexRepository.BuildQuery parameterises the account filter -
/// one @pN placeholder per id, generated from the index, with the raw id carried as a
/// parameter value and never concatenated into the SQL text. This is the SQL-injection guard.
/// </summary>
public sealed class PortfolioSnapshotIndexRepositoryQueryTests
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

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

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
    public void Event_type_adds_a_single_parameterised_contains_clause()
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A"],
            FromDate = From,
            ToDate = To,
            EventType = "ModelChange",
        };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.Contains("AND EventType LIKE @event ESCAPE '~'", sql);
        Assert.DoesNotContain("CHARINDEX", sql);
        var eventParameter = Assert.Single(parameters, parameter => parameter.ParameterName == "@event");
        Assert.Equal("%ModelChange%", eventParameter.Value);
    }

    [Fact]
    public void Event_type_like_metacharacters_remain_a_literal_parameter_value()
    {
        const string eventType = "%Model_Change[+]~";
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A"],
            EventType = eventType,
        };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.Contains("AND EventType LIKE @event ESCAPE '~'", sql);
        Assert.DoesNotContain("CHARINDEX", sql);
        Assert.DoesNotContain(eventType, sql);
        var eventParameter = Assert.Single(parameters, parameter => parameter.ParameterName == "@event");
        Assert.Equal("%~%Model~_Change~[+]~~%", eventParameter.Value);
    }

    /// <summary>
    /// The accountIds-only request reaches BuildQuery with no event type at all. Neither a null
    /// nor a blank one may leave an <c>EventType LIKE @event</c> clause behind - the clause and the
    /// parameter have to appear or disappear together, or the command fails on a missing
    /// parameter at execution time.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_event_type_adds_no_clause_and_no_parameter(string? eventType)
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A"],
            FromDate = From,
            ToDate = To,
            EventType = eventType,
        };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.DoesNotContain("EventType LIKE @event", sql);
        Assert.DoesNotContain("CHARINDEX", sql);
        Assert.DoesNotContain("@event", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@event");

        // Only the account placeholder and the two window parameters remain.
        Assert.Equal(3, parameters.Length);
    }

    /// <summary>
    /// The accountIds-only request reaches BuildQuery with no date window at all. Neither bound
    /// may leave a <c>SnapshotDate</c> clause behind, and neither may be bound as SQL NULL - a
    /// NULL comparison is never true, so a bound NULL would silently return zero rows instead of
    /// leaving the range open.
    /// </summary>
    [Fact]
    public void Absent_dates_add_no_clause_and_no_parameter()
    {
        var filter = new SnapshotGridFilter
        {
            AccountIds = ["A"],
            FromDate = null,
            ToDate = null,
        };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.DoesNotContain("SnapshotDate >=", sql);
        Assert.DoesNotContain("SnapshotDate <=", sql);
        Assert.DoesNotContain("@from", sql);
        Assert.DoesNotContain("@to", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName is "@from" or "@to");

        // Only the single account placeholder remains.
        Assert.Single(parameters);
    }

    [Fact]
    public void From_only_emits_the_lower_bound_clause_alone()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["A"], FromDate = From };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.Contains("AND SnapshotDate >= @from", sql);
        Assert.Equal(From, ParameterValue(parameters, "@from"));

        Assert.DoesNotContain("@to", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@to");
    }

    [Fact]
    public void To_only_emits_the_upper_bound_clause_alone()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["A"], ToDate = To };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out var parameters);

        Assert.Contains("AND SnapshotDate <= @to", sql);
        Assert.Equal(To, ParameterValue(parameters, "@to"));

        Assert.DoesNotContain("@from", sql);
        Assert.DoesNotContain(parameters, p => p.ParameterName == "@from");
    }

    [Fact]
    public void Query_selects_display_data_aliased_and_orders_by_date_desc()
    {
        var filter = new SnapshotGridFilter { AccountIds = ["A"], FromDate = From, ToDate = To };

        var sql = PortfolioSnapshotIndexRepository.BuildQuery(filter, out _);

        Assert.Contains("DisplayData AS DisplayDataJson", sql);
        Assert.Contains("FROM dbo.PortfolioSnapshotIndex", sql);
        Assert.EndsWith("ORDER BY SnapshotDate DESC", sql);
    }

    private static object? ParameterValue(SqlParameter[] parameters, string name) =>
        Array.Find(parameters, p => p.ParameterName == name)?.Value;
}
